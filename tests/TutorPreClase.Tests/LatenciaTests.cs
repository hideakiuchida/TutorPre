using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TutorPreClase.Application.Contenido;
using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Tutor;
using TutorPreClase.Domain.Reglas;
using TutorPreClase.Infrastructure.Llm;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

/// <summary>
/// El tutor responde rápido (RNF-02): se emite en vivo aunque la ampliación esté apagada,
/// el modelo no razona en el chat y a una clase grande solo van las páginas pertinentes.
/// </summary>
public class EmisorConCorteTests
{
    private static string Emitir(bool retener, params string[] fragmentos)
    {
        var emisor = new EmisorConCorte(retener);
        return string.Concat(fragmentos.Select(emisor.Agregar)) + emisor.Cerrar();
    }

    [Fact]
    public void Sin_marca_llega_todo_el_texto_y_empieza_antes_del_final()
    {
        var largo = new string('a', 200);
        var emisor = new EmisorConCorte(retenerAmpliacion: true);

        var primero = emisor.Agregar(largo);

        Assert.NotEmpty(primero);
        Assert.Equal(largo + "fin", primero + emisor.Agregar("fin") + emisor.Cerrar());
    }

    [Fact]
    public void La_marca_partida_entre_fragmentos_no_deja_pasar_la_ampliacion()
    {
        var texto = Emitir(true,
            "La sigmoide satura en los extremos [Clase03.pdf, p. 11]. ",
            "Ampliación fue", "ra del material: ademas existe GELU, que suaviza.");

        Assert.StartsWith("La sigmoide satura", texto);
        Assert.DoesNotContain("GELU", texto);
        Assert.EndsWith(Salvaguardas.SinCobertura, texto);
    }

    [Fact]
    public void Con_la_marca_al_inicio_solo_llega_el_aviso()
    {
        Assert.Equal(Salvaguardas.SinCobertura,
            Emitir(true, "Ampliación fuera del material: la respuesta es ReLU."));
    }

    [Fact]
    public void Con_la_ampliacion_permitida_no_retiene_nada()
    {
        var emisor = new EmisorConCorte(retenerAmpliacion: false);

        Assert.Equal("Ampliación fuera del material: GELU.", emisor.Agregar("Ampliación fuera del material: GELU."));
        Assert.False(emisor.Cortado);
        Assert.Equal("", emisor.Cerrar());
    }
}

public class ChatEnVivoTests
{
    [Fact]
    public async Task Con_la_ampliacion_apagada_el_texto_llega_en_varios_trozos()
    {
        using var banco = new BancoDePruebas().Sembrar(ampliacionPermitida: false);
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

        banco.Proveedor.Responde(
            new TextoParcial("La sigmoide satura en los extremos y por eso reduce el gradiente de las capas. "),
            new TextoParcial("ReLU mantiene gradiente uno para entradas positivas [Clase03.pdf, p. 12]. "),
            new TextoParcial("Por eso se usa en redes profundas."),
            new Fin(20, 40));

        var eventos = await banco.EnviarAsync(conversacion.Id, "por que ReLU?");
        var tokens = eventos.OfType<EventoToken>().ToList();

        Assert.True(tokens.Count > 1, "El texto debe llegar mientras se genera, no de una vez al final.");
        Assert.Equal(
            "La sigmoide satura en los extremos y por eso reduce el gradiente de las capas. " +
            "ReLU mantiene gradiente uno para entradas positivas [Clase03.pdf, p. 12]. " +
            "Por eso se usa en redes profundas.",
            string.Concat(tokens.Select(t => t.Texto)));
    }

    [Fact]
    public async Task Una_respuesta_cortada_por_el_tope_se_avisa()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

        banco.Proveedor.Responde(
            new TextoParcial("La sigmoide satura porque"),
            new Fin(20, 2048, CortadaPorTope: true));

        var eventos = await banco.EnviarAsync(conversacion.Id, "explica la sigmoide");

        Assert.Equal("respuesta_incompleta", eventos.OfType<EventoAviso>().Single().Codigo);
    }
}

public class RazonamientoOpenAiTests
{
    private static async Task<JsonElement> CuerpoEnviado(
        string? razonamientoAgente, string? razonamientoSolicitud = null, string? esfuerzoDesactivado = "minimal",
        string? esfuerzoBajo = null, bool deRazonamiento = false, int? maxTokens = null)
    {
        var simulado = new HttpSimulado
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n") }
        };
        var proveedor = new OpenAiProvider(simulado.CreateClient("llm:openai"),
            new OpcionesAgente
            {
                Modelo = "gpt-5-mini", ApiKey = "sk-proj-x",
                Razonamiento = razonamientoAgente, EsfuerzoDesactivado = esfuerzoDesactivado,
                EsfuerzoBajo = esfuerzoBajo, ModeloDeRazonamiento = deRazonamiento
            },
            NullLogger<OpenAiProvider>.Instance);

        var solicitud = new LlmSolicitud("gpt-5-mini", "Eres el tutor.",
            [new LlmMensaje(RolLlm.Usuario, "hola")], [], Temperatura: 0.7, MaxTokens: maxTokens,
            Razonamiento: razonamientoSolicitud);

        await foreach (var _ in proveedor.StreamAsync(solicitud, CancellationToken.None)) { }
        return JsonDocument.Parse(simulado.Recibidas.Single().Cuerpo).RootElement;
    }

    [Fact]
    public async Task En_el_chat_apaga_el_razonamiento_y_no_pone_tope_de_salida()
    {
        var cuerpo = await CuerpoEnviado(NivelRazonamiento.Desactivado);

        Assert.Equal("minimal", cuerpo.GetProperty("reasoning_effort").GetString());
        // El chat no pone tope: el modelo responde completo.
        Assert.False(cuerpo.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task Si_la_solicitud_pide_razonamiento_bajo_decide_el_modelo()
    {
        var cuerpo = await CuerpoEnviado(NivelRazonamiento.Desactivado, NivelRazonamiento.Bajo);

        Assert.False(cuerpo.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task Un_modelo_de_razonamiento_recibe_solo_los_parametros_que_OpenAI_le_admite()
    {
        // Lo que pasó en producción: OpenAI respondía 400 a `temperature` y a `max_tokens`.
        var cuerpo = await CuerpoEnviado(
            NivelRazonamiento.Desactivado, NivelRazonamiento.Bajo, esfuerzoBajo: "low", deRazonamiento: true, maxTokens: 2500);

        Assert.Equal(2500, cuerpo.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(cuerpo.TryGetProperty("max_tokens", out _));
        Assert.False(cuerpo.TryGetProperty("temperature", out _));
        Assert.Equal("low", cuerpo.GetProperty("reasoning_effort").GetString());
        // Una lista de herramientas vacía también es un 400: sin herramientas el campo no va.
        Assert.False(cuerpo.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Un_modelo_que_no_es_de_razonamiento_conserva_el_tope_y_la_temperatura()
    {
        var cuerpo = await CuerpoEnviado(null, maxTokens: 2500);

        Assert.Equal(2500, cuerpo.GetProperty("max_tokens").GetInt32());
        Assert.False(cuerpo.TryGetProperty("max_completion_tokens", out _));
        Assert.Equal(0.7, cuerpo.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task Un_proveedor_que_no_admite_apagarlo_no_recibe_el_parametro()
    {
        var cuerpo = await CuerpoEnviado(NivelRazonamiento.Desactivado, esfuerzoDesactivado: null);

        Assert.False(cuerpo.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task Una_respuesta_que_llega_al_tope_se_marca_como_cortada()
    {
        var simulado = new HttpSimulado
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"La sigmoide\"}}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n")
            }
        };
        var proveedor = new OpenAiProvider(simulado.CreateClient("llm:openai"),
            new OpcionesAgente { Modelo = "m", ApiKey = "sk-proj-x" }, NullLogger<OpenAiProvider>.Instance);

        var eventos = new List<LlmEvento>();
        await foreach (var e in proveedor.StreamAsync(
                           new LlmSolicitud("m", "p", [new LlmMensaje(RolLlm.Usuario, "hola")], []), CancellationToken.None))
            eventos.Add(e);

        Assert.True(eventos.OfType<Fin>().Single().CortadaPorTope);
    }
}

public class MaterialPreparadoTests
{
    private static readonly OpcionesContextoClase Opciones = new() { TokensParaSeleccionar = 100, TokensSeleccionados = 60 };

    private static ContextoClase Contexto(params PaginaContexto[] paginas) => new(
        "M4",
        ArmadorContextoClase.Bloque("M4", paginas, seleccion: false),
        paginas.Sum(p => p.Tokens),
        false,
        paginas.Select(p => (p.Archivo, p.Pagina)).ToHashSet(),
        paginas.Select(p => (p.Archivo, p.Pagina)).ToHashSet(),
        paginas);

    private static readonly ContextoClase Grande = Contexto(
        new PaginaContexto("M4.pdf", 1, "El capital de trabajo neto es el activo corriente menos el pasivo corriente.", 40),
        new PaginaContexto("M4.pdf", 2, "El flujo de caja libre mide el efectivo disponible para los accionistas.", 40),
        new PaginaContexto("M4.pdf", 3, "El interés compuesto capitaliza los intereses de cada periodo.", 40));

    [Fact]
    public void En_una_clase_grande_solo_van_las_paginas_de_la_pregunta()
    {
        var elegido = SelectorPaginas.Seleccionar(Grande, "¿Cómo se calcula el capital de trabajo?", Opciones);

        Assert.Contains("[M4.pdf, p. 1]", elegido.Texto);
        Assert.DoesNotContain("[M4.pdf, p. 3]", elegido.Texto);
        Assert.Contains("seleccion=", elegido.Texto);
        Assert.True(elegido.Tokens <= Opciones.TokensSeleccionados);
    }

    [Fact]
    public void Las_citas_valen_contra_toda_la_clase_aunque_la_pagina_no_se_enviara()
    {
        var elegido = SelectorPaginas.Seleccionar(Grande, "capital de trabajo", Opciones);

        Assert.DoesNotContain(("M4.pdf", 3), elegido.PaginasIncluidas);

        var cita = Salvaguardas.ExtraerCitas("Ver [M4.pdf, p. 3].", elegido.PaginasDeLaClase).Single();
        Assert.True(cita.Valida);
    }

    [Fact]
    public void Una_clase_pequena_o_una_pregunta_sin_coincidencias_recibe_todo_el_material()
    {
        var pequena = SelectorPaginas.Seleccionar(Grande, "capital de trabajo",
            new OpcionesContextoClase { TokensParaSeleccionar = 1_000 });
        var sinCoincidencias = SelectorPaginas.Seleccionar(Grande, "hola", Opciones);

        Assert.Same(Grande, pequena);
        Assert.Same(Grande, sinCoincidencias);
    }

    [Fact]
    public void Singular_y_plural_coinciden()
    {
        var elegido = SelectorPaginas.Seleccionar(Grande, "intereses compuestos", Opciones);

        Assert.Contains("[M4.pdf, p. 3]", elegido.Texto);
    }

    [Fact]
    public void Quita_encabezados_repetidos_y_numeros_de_pagina_de_un_pdf()
    {
        var paginas = Enumerable.Range(1, 4)
            .Select(n => (n, $"MDSTI · Finanzas empresariales\nContenido propio de la página {n}.\n{n}"));

        var limpias = LimpiadorTexto.Limpiar("M1.pdf", paginas);

        Assert.All(limpias, p => Assert.DoesNotContain("MDSTI", p.Texto));
        Assert.Equal("Contenido propio de la página 2.", limpias[1].Texto);
    }

    [Fact]
    public void En_una_hoja_de_calculo_las_lineas_repetidas_y_los_numeros_son_datos()
    {
        var hojas = Enumerable.Range(1, 3).Select(n => (n, $"Total\n{n * 1000}"));

        var limpias = LimpiadorTexto.Limpiar("Anexo.xlsx", hojas);

        Assert.All(limpias, p => Assert.Contains("Total", p.Texto));
        Assert.Contains("2000", limpias[1].Texto);
    }
}

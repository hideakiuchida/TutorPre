using System.Text.Json;
using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Tutor;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

public class FlujoExamenTests
{
    [Fact]
    public async Task El_examen_completo_por_chat_califica_y_fija_el_nivel()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        // Pregunta a pregunta: la segunda se genera cuando el tutor la pide.
        // El alumno responde bien la primera y mal la segunda.
        await Responder(banco, conversacion.Id, Pregunta(banco, 1), "A", "creo que es la A");
        await PedirSiguiente(banco, conversacion.Id);
        await Responder(banco, conversacion.Id, Pregunta(banco, 2), "B", "la B");

        banco.Proveedor.LlamaHerramienta(Herramientas.FinalizarExamen, new { });
        banco.Proveedor.RespondeTexto("Obtuviste 10.0. Revisemos el fallo [Clase03.pdf, p. 11].");

        var eventos = await banco.EnviarAsync(conversacion.Id, "ya terminé");

        var intento = banco.Db.Intentos.Single();
        Assert.Equal(EstadoIntento.EnRevision, intento.Estado);
        Assert.Equal(10.0m, intento.Puntaje);
        Assert.Equal(50, intento.Porcentaje);

        // El chat pasa solo a modo Revision (SDD §5).
        Assert.Equal(ModoConversacion.Revision, banco.Db.Conversaciones.Single().Modo);
        Assert.Contains(eventos.OfType<EventoModo>(), e => e.Modo == ModoConversacion.Revision);

        // Y el nivel queda calculado por el servidor, no por el agente.
        var nivel = banco.Db.Niveles.Single();
        Assert.Equal(NivelAlumnoValor.Inicial, nivel.Nivel);
        Assert.Equal(OrigenNivel.Automatico, nivel.Origen);
    }

    [Fact]
    public async Task En_modo_AlFinal_el_agente_no_recibe_si_acerto()
    {
        using var banco = new BancoDePruebas().Sembrar(feedback: ModoFeedback.AlFinal);
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        var pregunta = banco.Db.Preguntas.OrderBy(p => p.Orden).First();
        await Responder(banco, conversacion.Id, pregunta.Id, "A", "la A");

        var resultado = ResultadoHerramientaJson(banco, Herramientas.RegistrarRespuesta);

        Assert.True(resultado.GetProperty("registrada").GetBoolean());
        Assert.Equal(JsonValueKind.Null, resultado.GetProperty("es_correcta").ValueKind);
        Assert.Equal(JsonValueKind.Null, resultado.GetProperty("justificacion").ValueKind);
    }

    [Fact]
    public async Task En_modo_Inmediato_el_agente_recibe_la_correccion_al_registrar()
    {
        using var banco = new BancoDePruebas().Sembrar(feedback: ModoFeedback.Inmediato);
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        var pregunta = banco.Db.Preguntas.OrderBy(p => p.Orden).First();
        await Responder(banco, conversacion.Id, pregunta.Id, "B", "la B");

        var resultado = ResultadoHerramientaJson(banco, Herramientas.RegistrarRespuesta);

        Assert.False(resultado.GetProperty("es_correcta").GetBoolean());
        Assert.Equal("Esta en el material de la clase.", resultado.GetProperty("justificacion").GetString());
    }

    [Fact]
    public async Task La_siguiente_pregunta_no_expone_cual_es_la_correcta()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        banco.Proveedor.LlamaHerramienta(Herramientas.ObtenerSiguientePregunta, new { });
        banco.Proveedor.RespondeTexto("Pregunta 1 de 2…");
        await banco.EnviarAsync(conversacion.Id, "vamos");

        var json = ResultadoHerramientaJson(banco, Herramientas.ObtenerSiguientePregunta).GetRawText();

        Assert.Contains("alternativas", json);
        Assert.DoesNotContain("es_correcta", json);
        Assert.DoesNotContain("esCorrecta", json);
    }

    [Fact]
    public async Task Una_respuesta_registrada_es_inmutable()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        var pregunta = banco.Db.Preguntas.OrderBy(p => p.Orden).First();
        await Responder(banco, conversacion.Id, pregunta.Id, "A", "la A");
        await Responder(banco, conversacion.Id, pregunta.Id, "B", "mejor la B");

        var resultado = UltimoResultadoHerramienta(banco, Herramientas.RegistrarRespuesta);
        Assert.Contains("PreguntaYaRespondida", resultado);

        var respuesta = banco.Db.Respuestas.Single();
        Assert.True(respuesta.EsCorrecta);
    }

    [Fact]
    public async Task El_tutor_registra_las_dudas_que_el_material_no_cubre()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");

        banco.Proveedor.LlamaHerramienta(Herramientas.RegistrarDudaSinCobertura, new
        {
            texto = "Que es GELU?",
            tema = "Funciones de activacion",
            con_ampliacion = true
        });
        banco.Proveedor.RespondeTexto("Ampliación fuera del material: GELU es…");

        await banco.EnviarAsync(conversacion.Id, "que es GELU?");

        var duda = banco.Db.DudasSinCobertura.Single();
        Assert.Equal("Que es GELU?", duda.Texto);
        Assert.True(duda.RespondidaConAmpliacion);
        Assert.Equal(banco.ClaseId, duda.ClaseId);
    }

    [Fact]
    public async Task El_diagnostico_del_tutor_llena_temas_debiles_sin_mover_el_nivel()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        await Responder(banco, conversacion.Id, Pregunta(banco, 1), "A", "A");
        await PedirSiguiente(banco, conversacion.Id);
        await Responder(banco, conversacion.Id, Pregunta(banco, 2), "A", "A");

        banco.Proveedor.LlamaHerramienta(Herramientas.FinalizarExamen, new { });
        banco.Proveedor.RespondeTexto("Excelente.");
        await banco.EnviarAsync(conversacion.Id, "listo");

        var nivelTrasExamen = banco.Db.Niveles.Single().Nivel;

        banco.Proveedor.LlamaHerramienta(Herramientas.ProponerDiagnostico, new
        {
            temas_debiles = new[] { "Descenso de gradiente" }
        });
        banco.Proveedor.RespondeTexto("Anotado.");
        await banco.EnviarAsync(conversacion.Id, "en que debo reforzar?");

        var nivel = banco.Db.Niveles.Single();

        Assert.Equal(nivelTrasExamen, nivel.Nivel);
        Assert.Contains("Descenso de gradiente", nivel.TemasDebiles);
        Assert.Equal(OrigenNivel.Tutor, nivel.Origen);
    }

    [Fact]
    public async Task La_correccion_del_docente_no_la_pisa_el_calculo_automatico()
    {
        using var banco = new BancoDePruebas().Sembrar();

        await banco.Nivel.CorregirAsync(banco.AlumnoId, banco.ClaseId, NivelAlumnoValor.Avanzado);
        await banco.Nivel.RecalcularAsync(banco.AlumnoId, banco.ClaseId);

        var nivel = banco.Db.Niveles.Single();

        Assert.Equal(NivelAlumnoValor.Avanzado, nivel.Nivel);
        Assert.Equal(OrigenNivel.Docente, nivel.Origen);
    }

    [Fact]
    public async Task La_siguiente_pregunta_se_genera_al_pedirla_sin_repetir_la_anterior()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        await Responder(banco, conversacion.Id, Pregunta(banco, 1), "A", "la A");
        var progreso = await banco.Examen.ProgresoAsync(banco.Db.Intentos.Single().Id);
        Assert.Equal((1, 2), (progreso.Respondidas, progreso.Total)); // la segunda aún no existe

        await PedirSiguiente(banco, conversacion.Id);

        var resultado = JsonDocument.Parse(UltimoResultadoHerramienta(banco, Herramientas.ObtenerSiguientePregunta)).RootElement;
        Assert.Equal(2, resultado.GetProperty("numero").GetInt32());
        Assert.Contains(banco.Generador.UltimasYaPreguntadas!, p => p.Contains("desvanecimiento del gradiente"));
        Assert.Contains(banco.Db.Intentos.Single().Id, banco.Preparador.Pedidos);
    }

    [Fact]
    public async Task Si_la_siguiente_no_se_puede_generar_el_avance_se_conserva_y_se_puede_reintentar()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);
        await Responder(banco, conversacion.Id, Pregunta(banco, 1), "A", "la A");

        banco.Generador.Falla = new ErrorProveedor("429", true, LimiteDeUso: true, Saturado: true);
        await PedirSiguiente(banco, conversacion.Id);

        var error = JsonDocument.Parse(UltimoResultadoHerramienta(banco, Herramientas.ObtenerSiguientePregunta)).RootElement;
        Assert.Equal("pregunta_no_generada", error.GetProperty("error").GetString());
        Assert.Contains("saturado", error.GetProperty("mensaje").GetString());
        Assert.Single(banco.Db.Respuestas);

        banco.Generador.Falla = null;
        await PedirSiguiente(banco, conversacion.Id);
        Assert.Equal(2, banco.Db.Preguntas.Count());
    }

    [Fact]
    public async Task La_nota_se_calcula_sobre_las_preguntas_planeadas()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        // Responde bien la única generada y termina antes: la que faltaba cuenta como no acertada.
        await Responder(banco, conversacion.Id, Pregunta(banco, 1), "A", "la A");
        var resultado = await banco.Examen.FinalizarAsync(banco.Db.Intentos.Single().Id);

        Assert.Equal((1, 2, 10.0m), (resultado.Correctas, resultado.Total, resultado.Nota));
    }

    [Fact]
    public async Task No_se_genera_mas_de_una_adelantada_ni_mas_de_las_planeadas()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);
        var intentoId = banco.Db.Intentos.Single().Id;

        await banco.Preguntas.GenerarSiguienteAsync(intentoId); // 2 de 2: la adelantada
        await banco.Preguntas.GenerarSiguienteAsync(intentoId); // ya están todas

        Assert.Equal(2, banco.Db.Preguntas.Count());
        Assert.Equal(2, banco.Generador.Llamadas);
    }

    private static Guid Pregunta(BancoDePruebas banco, int orden) =>
        banco.Db.Preguntas.Single(p => p.Orden == orden).Id;

    private static async Task PedirSiguiente(BancoDePruebas banco, Guid conversacionId)
    {
        banco.Proveedor.LlamaHerramienta(Herramientas.ObtenerSiguientePregunta, new { });
        banco.Proveedor.RespondeTexto("Siguiente pregunta.");
        await banco.EnviarAsync(conversacionId, "siguiente");
    }

    private static async Task Responder(
        BancoDePruebas banco, Guid conversacionId, Guid preguntaId, string letra, string texto)
    {
        banco.Proveedor.LlamaHerramienta(Herramientas.RegistrarRespuesta, new
        {
            pregunta_id = preguntaId.ToString(),
            alternativa = letra,
            texto_original = texto
        });
        banco.Proveedor.RespondeTexto("Registrada.");

        await banco.EnviarAsync(conversacionId, texto);
    }

    private static JsonElement ResultadoHerramientaJson(BancoDePruebas banco, string herramienta) =>
        JsonDocument.Parse(banco.Db.Mensajes
            .Where(m => m.Herramienta == herramienta)
            .OrderBy(m => m.CreadoEn)
            .First().Texto).RootElement;

    private static string UltimoResultadoHerramienta(BancoDePruebas banco, string herramienta) =>
        banco.Db.Mensajes
            .Where(m => m.Herramienta == herramienta)
            .OrderBy(m => m.CreadoEn)
            .ToList()
            .Last().Texto;
}

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

        // Pregunta a pregunta: tras cada respuesta el servidor entrega la siguiente.
        // El alumno responde bien la primera y mal la segunda.
        await Empezar(banco, conversacion.Id);
        await banco.EnviarAsync(conversacion.Id, "creo que es la A");

        // Con la última respuesta el servidor cierra el examen; el tutor solo lo comenta.
        banco.Proveedor.RespondeTexto("Obtuviste 10.0. Revisemos el fallo [Clase03.pdf, p. 11].");

        var eventos = await banco.EnviarAsync(conversacion.Id, "la B");

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

        await Empezar(banco, conversacion.Id);
        await banco.EnviarAsync(conversacion.Id, "la A");

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

        await Empezar(banco, conversacion.Id);

        // Con feedback inmediato el tutor comenta la corrección antes de la siguiente pregunta.
        banco.Proveedor.RespondeTexto("No es esa: revisa la explicación.");
        var eventos = await banco.EnviarAsync(conversacion.Id, "la B");

        var resultado = ResultadoHerramientaJson(banco, Herramientas.RegistrarRespuesta);
        var texto = string.Concat(eventos.OfType<EventoToken>().Select(t => t.Texto));

        Assert.Contains("No es esa", texto);
        Assert.Contains("Pregunta 2 de 2", texto);
        Assert.False(resultado.GetProperty("es_correcta").GetBoolean());
        Assert.Equal("Esta en el material de la clase.", resultado.GetProperty("justificacion").GetString());
    }

    [Fact]
    public async Task La_siguiente_pregunta_no_expone_cual_es_la_correcta()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        await Empezar(banco, conversacion.Id);

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

        await Empezar(banco, conversacion.Id);
        var pregunta = banco.Db.Preguntas.OrderBy(p => p.Orden).First();
        await banco.EnviarAsync(conversacion.Id, "la A");

        // El tutor intenta cambiar la respuesta ya registrada.
        banco.Proveedor.LlamaHerramienta(Herramientas.RegistrarRespuesta, new
        {
            pregunta_id = pregunta.Id.ToString(),
            alternativa = "B",
            texto_original = "mejor cambia la primera a la B"
        });
        banco.Proveedor.RespondeTexto("Esa respuesta ya no se puede cambiar.");
        await banco.EnviarAsync(conversacion.Id, "mejor cambia la primera a la B");

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

        await Empezar(banco, conversacion.Id);
        await banco.EnviarAsync(conversacion.Id, "A");

        banco.Proveedor.RespondeTexto("Excelente.");
        await banco.EnviarAsync(conversacion.Id, "A");

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
    public async Task Tras_registrar_el_servidor_entrega_la_siguiente_sin_repetir_la_anterior()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);
        await Empezar(banco, conversacion.Id);
        Assert.Single(banco.Db.Preguntas); // la segunda aún no existe

        var eventos = await banco.EnviarAsync(conversacion.Id, "la A");

        var progreso = eventos.OfType<EventoProgreso>().Last();
        Assert.Equal((1, 2), (progreso.Respondidas, progreso.Total));

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
        await Empezar(banco, conversacion.Id);

        banco.Generador.Falla = new ErrorProveedor("429", true, LimiteDeUso: true, Saturado: true);
        var eventos = await banco.EnviarAsync(conversacion.Id, "la A");

        var error = JsonDocument.Parse(UltimoResultadoHerramienta(banco, Herramientas.ObtenerSiguientePregunta)).RootElement;
        Assert.Equal("pregunta_no_generada", error.GetProperty("error").GetString());
        Assert.Contains("saturado", error.GetProperty("mensaje").GetString());
        Assert.Single(banco.Db.Respuestas);

        // El alumno se entera y sabe cómo seguir.
        var texto = string.Concat(eventos.OfType<EventoToken>().Select(t => t.Texto));
        Assert.Contains("Registrada.", texto);
        Assert.Contains("No pude preparar la siguiente pregunta", texto);

        // Al reintentar, la pregunta la entrega el servidor: no depende del modelo.
        banco.Generador.Falla = null;
        var reintento = await banco.EnviarAsync(conversacion.Id, "siguiente");
        Assert.Equal(2, banco.Db.Preguntas.Count());
        Assert.Contains("Pregunta 2 de 2", string.Concat(reintento.OfType<EventoToken>().Select(t => t.Texto)));
        Assert.Empty(banco.Proveedor.Solicitudes);
    }

    [Fact]
    public async Task La_nota_se_calcula_sobre_las_preguntas_planeadas()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        // Responde bien la primera y termina antes: la que faltaba cuenta como no acertada.
        await Empezar(banco, conversacion.Id);
        await banco.EnviarAsync(conversacion.Id, "la A");
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

    /// <summary>Al empezar, el servidor presenta la primera pregunta sin esperar al modelo.</summary>
    internal static Task<List<EventoTutor>> Empezar(BancoDePruebas banco, Guid conversacionId) =>
        banco.EnviarAsync(conversacionId, "Estoy listo para empezar el examen.");

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

/// <summary>
/// El examen es confiable con cualquier modelo (SDD §6.4): lo que se registra, lo que se
/// pregunta y cuándo se cierra lo decide el servidor, no el texto del modelo.
/// </summary>
public class ExamenConfiableTests
{
    private static async Task<(BancoDePruebas Banco, Guid Conversacion)> EnExamen(ModoFeedback feedback = ModoFeedback.AlFinal)
    {
        var banco = new BancoDePruebas().Sembrar(feedback: feedback);
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);
        return (banco, conversacion.Id);
    }

    private static string Texto(IEnumerable<EventoTutor> eventos) =>
        string.Concat(eventos.OfType<EventoToken>().Select(t => t.Texto));

    [Fact]
    public async Task La_pregunta_la_presenta_el_servidor_tal_como_esta_guardada()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;

        var eventos = await FlujoExamenTests.Empezar(banco, conversacion);
        var texto = Texto(eventos);

        Assert.Contains("**Pregunta 1 de 2**", texto);
        Assert.Contains("Que funcion evita el desvanecimiento del gradiente?", texto);
        Assert.Contains("- **A)** ReLU", texto);
        Assert.Contains("- **D)** Ninguna", texto);

        // No hace falta el modelo para mostrarla: un modelo saturado no deja al alumno sin pregunta.
        Assert.Empty(banco.Proveedor.Solicitudes);
        Assert.Contains("Que funcion evita", banco.Db.Mensajes.Single(m => m.Rol == RolMensaje.Agente).Texto);
    }

    [Fact]
    public async Task Una_letra_sola_la_registra_el_servidor_sin_esperar_al_modelo()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;
        await FlujoExamenTests.Empezar(banco, conversacion);
        var llamadasAntes = banco.Proveedor.Solicitudes.Count;

        var eventos = await banco.EnviarAsync(conversacion, "creo que es la B");

        Assert.Equal(llamadasAntes, banco.Proveedor.Solicitudes.Count);
        Assert.False(banco.Db.Respuestas.Single().EsCorrecta);
        Assert.Equal("creo que es la B", banco.Db.Respuestas.Single().TextoOriginalAlumno);

        var texto = Texto(eventos);
        Assert.StartsWith("Registrada.", texto);
        Assert.Contains("**Pregunta 2 de 2**", texto);
        Assert.Contains("Que problema tiene la sigmoide", texto);

        // La hoja de respuestas llega al abrir el turno y se actualiza al registrar.
        var progresos = eventos.OfType<EventoProgreso>().Select(p => p.Respondidas).ToList();
        Assert.Equal(0, progresos.First());
        Assert.Equal(1, progresos.Last());
    }

    [Fact]
    public async Task Un_modelo_que_dice_Registrada_sin_registrar_no_llega_al_alumno()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;
        await FlujoExamenTests.Empezar(banco, conversacion);

        // Lo que pasó en producción: "Registrada" y una pregunta inventada, sin herramienta.
        banco.Proveedor.RespondeTexto("Registrada. Aquí va la siguiente:\n\nPregunta 2 de 2:\n\nUna pregunta inventada\n- A) x\n- B) y");
        banco.Proveedor.LlamaHerramienta(Herramientas.RegistrarRespuesta, new { alternativa = "A", texto_original = "la que no deja morir el gradiente" });

        var eventos = await banco.EnviarAsync(conversacion, "la que no deja morir el gradiente");
        var texto = Texto(eventos);

        Assert.DoesNotContain("inventada", texto);
        Assert.True(banco.Db.Respuestas.Single().EsCorrecta);
        Assert.Contains("Que problema tiene la sigmoide", texto);
        Assert.DoesNotContain("inventada", banco.Db.Mensajes.Where(m => m.Rol == RolMensaje.Agente).ToList().Last().Texto);

        // Al modelo se le recordó usar las herramientas antes de volver a preguntarle.
        Assert.Contains("registrar_respuesta", banco.Proveedor.Solicitudes.Last().Mensajes.Last().Texto);
    }

    [Fact]
    public async Task Si_el_modelo_insiste_el_servidor_responde_por_el_y_no_registra_nada()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;
        await FlujoExamenTests.Empezar(banco, conversacion);

        for (var i = 0; i < 3; i++) banco.Proveedor.RespondeTexto("Registrada. Sigamos.");

        var texto = Texto(await banco.EnviarAsync(conversacion, "la que no deja morir el gradiente"));

        Assert.Equal(GuionExamen.SoloLaLetra, texto);
        Assert.Empty(banco.Db.Respuestas);
    }

    [Fact]
    public async Task Una_pista_que_cita_el_material_no_llega_al_alumno()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;
        await FlujoExamenTests.Empezar(banco, conversacion);

        for (var i = 0; i < 3; i++)
            banco.Proveedor.RespondeTexto("No puedo darte pistas, pero el material dice que mantiene gradiente 1 [Clase03.pdf, p. 12].");

        var eventos = await banco.EnviarAsync(conversacion, "dame una pista");
        var texto = Texto(eventos);

        Assert.DoesNotContain("gradiente 1", texto);
        Assert.Empty(eventos.OfType<EventoFuentes>());
        Assert.Empty(banco.Db.Respuestas);
    }

    [Fact]
    public async Task Con_feedback_al_final_el_material_no_viaja_en_el_prompt_del_examen()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;

        await FlujoExamenTests.Empezar(banco, conversacion);
        banco.Proveedor.RespondeTexto("Durante el examen no puedo ayudarte con eso.");
        await banco.EnviarAsync(conversacion, "explícame el tema");

        Assert.DoesNotContain("ReLU mantiene gradiente 1", banco.Proveedor.Solicitudes.Single().PromptSistema);
    }

    [Fact]
    public async Task El_modelo_no_puede_cerrar_el_examen_con_preguntas_pendientes()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;
        await FlujoExamenTests.Empezar(banco, conversacion);

        banco.Proveedor.LlamaHerramienta(Herramientas.FinalizarExamen, new { });
        banco.Proveedor.RespondeTexto("Sigamos con el examen.");
        var eventos = await banco.EnviarAsync(conversacion, "no estoy seguro");

        Assert.Equal("error", eventos.OfType<EventoHerramienta>().Single().Estado);
        Assert.Contains("quedan_preguntas", banco.Db.Mensajes.Single(m => m.Herramienta == Herramientas.FinalizarExamen).Texto);
        Assert.Equal(EstadoIntento.EnCurso, banco.Db.Intentos.Single().Estado);
        Assert.Equal(ModoConversacion.Evaluacion, banco.Db.Conversaciones.Single().Modo);
    }

    [Fact]
    public async Task Si_el_alumno_pide_terminar_antes_el_examen_se_cierra()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;
        await FlujoExamenTests.Empezar(banco, conversacion);

        banco.Proveedor.LlamaHerramienta(Herramientas.FinalizarExamen, new { });
        banco.Proveedor.RespondeTexto("Tu nota es 0.");
        var eventos = await banco.EnviarAsync(conversacion, "quiero terminar el examen ya");

        Assert.Equal(EstadoIntento.EnRevision, banco.Db.Intentos.Single().Estado);
        Assert.Contains(eventos.OfType<EventoModo>(), e => e.Modo == ModoConversacion.Revision);
    }

    [Fact]
    public async Task Con_la_ultima_respuesta_el_servidor_cierra_el_examen_y_el_tutor_recibe_el_resultado()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;
        await FlujoExamenTests.Empezar(banco, conversacion);
        await banco.EnviarAsync(conversacion, "A");

        banco.Proveedor.RespondeTexto("Evaluación final: tu nota es 20.");
        var eventos = await banco.EnviarAsync(conversacion, "A");

        Assert.Equal(20.0m, banco.Db.Intentos.Single().Puntaje);
        Assert.Equal(ModoConversacion.Revision, banco.Db.Conversaciones.Single().Modo);
        Assert.Contains("Tu nota es **20.0** sobre 20: 2 de 2 correctas.", Texto(eventos));
        Assert.Contains("Evaluación final", Texto(eventos));

        // La evaluación final no tiene citas: no se le pide sustento.
        Assert.DoesNotContain(eventos.OfType<EventoAviso>(), a => a.Codigo == "sin_sustento");

        // El tutor escribe la evaluación con la nota del servidor y con las reglas de revisión.
        var solicitud = banco.Proveedor.Solicitudes.Last();
        Assert.Contains("\"nota\":20", solicitud.Mensajes.Last().Texto);
        Assert.Contains("MODO: Revision", solicitud.PromptSistema);
        Assert.Contains(solicitud.Herramientas, h => h.Nombre == Herramientas.ProponerDiagnostico);
    }

    [Fact]
    public async Task Si_el_tutor_falla_al_cerrar_el_alumno_ve_igual_su_nota_y_queda_guardada()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;
        await FlujoExamenTests.Empezar(banco, conversacion);
        await banco.EnviarAsync(conversacion, "A");

        // Lo que pasó en producción: sin cupo justo al pedirle la evaluación final.
        banco.Proveedor.Responde(new ErrorProveedor("429", true, LimiteDeUso: true));
        var eventos = await banco.EnviarAsync(conversacion, "B");

        Assert.Contains("Tu nota es **10.0** sobre 20: 1 de 2 correctas.", Texto(eventos));
        Assert.Contains(eventos.OfType<EventoAviso>(), a => a.Codigo == "limite_de_uso");
        Assert.Contains("Tu nota es **10.0**", banco.Db.Mensajes.Where(m => m.Rol == RolMensaje.Agente).ToList().Last().Texto);
        Assert.Equal(NivelAlumnoValor.Inicial, banco.Db.Niveles.Single().Nivel);
    }

    [Fact]
    public async Task Si_la_IA_del_examen_se_queda_sin_cupo_la_pregunta_sale_de_otra_IA_conectada()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;
        banco.Db.Agentes.Add(new AgenteIA
        {
            Id = "openai", NombreVisible = "ChatGPT", Proveedor = "OpenAI", Modelo = "gpt-5-mini", Habilitado = true
        });
        banco.Db.SaveChanges();
        await banco.Boveda.ConectarAsync(banco.AlumnoId, "openai", "sk-proj-de-prueba-0000-9999");
        await FlujoExamenTests.Empezar(banco, conversacion);

        // Solo la IA del intento está sin cupo.
        banco.Generador.FallaCon = modelo => modelo == "qwen/qwen3.8-27b:free"
            ? new ErrorProveedor("429", true, LimiteDeUso: true)
            : null;

        var texto = Texto(await banco.EnviarAsync(conversacion, "A"));

        Assert.Contains("Pregunta 2 de 2", texto);
        Assert.Equal("gpt-5-mini", banco.Generador.UltimoModelo);
        Assert.Equal(EstadoCredencial.Valida, banco.Db.Credenciales.Single(c => c.AgenteId == "openrouter").Estado);
    }

    [Theory]
    [InlineData("la de la salud económica y financiera", "C")]
    [InlineData("el estratégico", "B")]
    [InlineData("Creo que es el tiempo de operación", "A")]
    [InlineData("¿qué es la salud financiera?", null)]      // una duda no es una respuesta
    [InlineData("no es la salud financiera", null)]
    [InlineData("la salud financiera o el estratégico", null)]
    [InlineData("indicador", null)]                          // está en más de una
    [InlineData("la rentabilidad", null)]                    // no está en ninguna
    [InlineData("el", null)]
    public void El_texto_de_una_unica_alternativa_tambien_se_registra_directo(string mensaje, string? letra)
    {
        (string, string)[] alternativas =
        [
            ("A", "Indicador de eficiencia: el tiempo de operación de cada proceso"),
            ("B", "Indicador estratégico, porque guía los logros a largo plazo"),
            ("C", "La salud económica y financiera empresarial"),
            ("D", "Indicador de satisfacción del personal")
        ];

        Assert.Equal(letra, GuionExamen.LetraPorContenido(mensaje, alternativas));
    }

    [Fact]
    public async Task Una_respuesta_con_el_texto_de_la_alternativa_no_depende_del_modelo()
    {
        var (banco, conversacion) = await EnExamen();
        using var _ = banco;
        await FlujoExamenTests.Empezar(banco, conversacion);

        var eventos = await banco.EnviarAsync(conversacion, "yo diría que ReLU");

        Assert.Empty(banco.Proveedor.Solicitudes);
        Assert.True(banco.Db.Respuestas.Single().EsCorrecta);
        Assert.StartsWith("Registrada.", Texto(eventos));
    }

    [Theory]
    [InlineData("B", "B")]
    [InlineData("la c", "C")]
    [InlineData("creo que es la A", "A")]
    [InlineData("me quedo con la alternativa d.", "D")]
    [InlineData("(b)", "B")]
    [InlineData("el estratégico", null)]
    [InlineData("no sé, dame una pista", null)]
    [InlineData("la A o la B", null)]
    public void Solo_una_letra_sin_nada_que_interpretar_se_registra_directo(string mensaje, string? letra) =>
        Assert.Equal(letra, GuionExamen.LetraDirecta(mensaje));
}

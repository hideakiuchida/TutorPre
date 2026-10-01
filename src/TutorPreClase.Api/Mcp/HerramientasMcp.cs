using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using TutorPreClase.Api.Seguridad;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Contenido;
using TutorPreClase.Application.Evaluacion;
using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Tutor;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Api.Mcp;

/// <summary>
/// El Tutor Pre-Clase como servidor MCP (RF-39): el asistente del alumno (Claude, ChatGPT…)
/// lee el material y rinde el examen con estas herramientas. Son las mismas reglas que en
/// la web: todo contra el alumno del token, la nota la calcula el servidor y la respuesta
/// correcta no sale hasta finalizar.
/// </summary>
[McpServerToolType]
public sealed class HerramientasMcp(
    IHttpContextAccessor http,
    IAppDbContext db,
    IContextoClaseService contexto,
    IAgenteTutorService tutor,
    IEjecutorHerramientas ejecutor,
    IBovedaCredenciales credenciales)
{
    public const string Instrucciones = """
        Eres el tutor pre-clase del alumno. Usa solo estas herramientas y el material que devuelven.
        - Explica con el material de la clase (leer_material o buscar_en_material) y cita la fuente
          tal como viene: [archivo, p. N]. Si algo no está en el material, dilo y, si amplías, empieza
          esa parte con "Ampliación fuera del material:".
        - En el examen presenta una pregunta a la vez con obtener_siguiente_pregunta, registra lo que
          el alumno elija con registrar_respuesta y no des pistas ni la respuesta correcta hasta
          finalizar_examen. La nota la calcula el servidor: comunícala tal cual.
        - Al terminar, revisa los errores con ver_resultado y propone los temas a reforzar.
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Guid AlumnoId => http.HttpContext!.User.Id();

    [McpServerTool(Name = "listar_clases", ReadOnly = true)]
    [Description("Clases de los cursos del alumno, con su id, fecha y si el examen está publicado.")]
    public async Task<string> ListarClases(CancellationToken ct)
    {
        var alumnoId = AlumnoId;
        var cursos = db.Matriculas.Where(m => m.UsuarioId == alumnoId).Select(m => m.CursoId);

        var clases = await db.Clases.AsNoTracking()
            .Where(c => cursos.Contains(c.CursoId))
            .OrderBy(c => c.Inicio)
            .Select(c => new
            {
                clase_id = c.Id,
                titulo = c.Titulo,
                inicio = c.Inicio,
                examen_publicado = c.Examen != null && c.Examen.Publicado,
                examen_abre = c.Examen == null ? (DateTimeOffset?)null : c.Examen.AbreEn,
                examen_cierra = c.Examen == null ? (DateTimeOffset?)null : c.Examen.CierraEn
            })
            .ToListAsync(ct);

        return JsonSerializer.Serialize(clases, Json);
    }

    [McpServerTool(Name = "leer_material", ReadOnly = true)]
    [Description("Material de la clase preparado para estudiar, con las citas [archivo, p. N].")]
    public async Task<string> LeerMaterial(
        [Description("clase_id de listar_clases")] Guid clase_id, CancellationToken ct)
    {
        await ExigirClaseAsync(clase_id, ct);
        var material = await contexto.ObtenerAsync(clase_id, ct);

        return material.Tokens == 0 ? "Esta clase todavía no tiene material." : material.Texto;
    }

    [McpServerTool(Name = "buscar_en_material", ReadOnly = true)]
    [Description("Solo las páginas del material que responden a una pregunta. Úsala en clases largas.")]
    public async Task<string> BuscarEnMaterial(
        [Description("clase_id de listar_clases")] Guid clase_id,
        [Description("La pregunta o el tema a buscar")] string pregunta,
        CancellationToken ct)
    {
        await ExigirClaseAsync(clase_id, ct);
        var material = await contexto.ObtenerParaPreguntaAsync(clase_id, pregunta, ct);

        return material.Tokens == 0 ? "Esta clase todavía no tiene material." : material.Texto;
    }

    [McpServerTool(Name = "iniciar_examen")]
    [Description("Empieza un intento del examen de la clase. Gasta un intento; las preguntas las arma la IA conectada del alumno.")]
    public async Task<string> IniciarExamen(
        [Description("clase_id de listar_clases")] Guid clase_id, CancellationToken ct)
    {
        await ExigirClaseAsync(clase_id, ct);

        try
        {
            var agenteId = await AgenteConectadoAsync(ct);
            if (agenteId is null)
                return Error("sin_credencial",
                    "El alumno no tiene una IA conectada en el Tutor Pre-Clase. Que entre a la web, abra " +
                    "\"Mi tutor\" → Cambiar y conecte una (su cuenta de Google o una clave de API).");

            var conversacion = await tutor.AbrirConversacionAsync(clase_id, AlumnoId, agenteId, ct);
            conversacion = await tutor.IniciarExamenAsync(conversacion.Id, ct);

            return JsonSerializer.Serialize(new
            {
                examen_iniciado = true,
                intento_id = conversacion.IntentoId,
                siguiente_paso = "Llama a obtener_siguiente_pregunta y presenta una pregunta a la vez."
            }, Json);
        }
        catch (ExamenException ex)
        {
            return Error(ex.Codigo, ex.Message);
        }
    }

    [McpServerTool(Name = "obtener_siguiente_pregunta")]
    [Description("La siguiente pregunta del examen en curso, sin la respuesta correcta.")]
    public Task<string> SiguientePregunta(
        [Description("clase_id del examen")] Guid clase_id, CancellationToken ct) =>
        EjecutarAsync(clase_id, Herramientas.ObtenerSiguientePregunta, new { }, ct);

    [McpServerTool(Name = "registrar_respuesta")]
    [Description("Guarda la alternativa que eligió el alumno para la pregunta en curso. Es inmutable.")]
    public Task<string> RegistrarRespuesta(
        [Description("clase_id del examen")] Guid clase_id,
        [Description("Letra elegida: A, B, C o D")] string alternativa,
        [Description("Lo que escribió el alumno, tal cual")] string? texto_original = null,
        CancellationToken ct = default) =>
        EjecutarAsync(clase_id, Herramientas.RegistrarRespuesta,
            new { alternativa, texto_original = texto_original ?? alternativa }, ct);

    [McpServerTool(Name = "ver_progreso", ReadOnly = true)]
    [Description("Respondidas, total y tiempo restante del examen en curso.")]
    public Task<string> VerProgreso(
        [Description("clase_id del examen")] Guid clase_id, CancellationToken ct) =>
        EjecutarAsync(clase_id, Herramientas.ObtenerProgreso, new { }, ct);

    [McpServerTool(Name = "finalizar_examen")]
    [Description("Cierra y califica el intento en el servidor: nota sobre 20 y detalle de fallos.")]
    public Task<string> FinalizarExamen(
        [Description("clase_id del examen")] Guid clase_id, CancellationToken ct) =>
        EjecutarAsync(clase_id, Herramientas.FinalizarExamen, new { }, ct);

    [McpServerTool(Name = "ver_resultado", ReadOnly = true)]
    [Description("Nota y detalle de fallos del último intento enviado, para revisar los errores.")]
    public Task<string> VerResultado(
        [Description("clase_id del examen")] Guid clase_id, CancellationToken ct) =>
        EjecutarAsync(clase_id, Herramientas.ObtenerResultado, new { }, ct);

    [McpServerTool(Name = "proponer_temas_a_reforzar")]
    [Description("Tras revisar los errores, aporta los temas donde viste vacíos. No cambia la nota ni el nivel.")]
    public Task<string> ProponerTemas(
        [Description("clase_id del examen")] Guid clase_id,
        [Description("Temas a reforzar")] string[] temas_debiles,
        CancellationToken ct) =>
        EjecutarAsync(clase_id, Herramientas.ProponerDiagnostico, new { temas_debiles }, ct);

    /// <summary>
    /// Las herramientas del examen pasan por el mismo ejecutor del chat: comprueba el modo,
    /// la ventana y los intentos, y nunca devuelve la correcta antes de finalizar.
    /// </summary>
    private async Task<string> EjecutarAsync(Guid claseId, string herramienta, object argumentos, CancellationToken ct)
    {
        await ExigirClaseAsync(claseId, ct);

        var alumnoId = AlumnoId;
        var conversacion = await db.Conversaciones.FirstOrDefaultAsync(c => c.ClaseId == claseId && c.AlumnoId == alumnoId, ct);
        if (conversacion is null)
            return Error("sin_examen", "No hay un examen de esta clase: empieza con iniciar_examen.");

        // Al finalizar, el ejecutor pasa la conversación a Revisión, como en la web.
        var resultado = await ejecutor.EjecutarAsync(
            conversacion, herramienta, JsonSerializer.SerializeToElement(argumentos, Json), ct);

        return resultado.Json;
    }

    /// <summary>Solo clases de los cursos del alumno (SDD §9.1).</summary>
    private async Task ExigirClaseAsync(Guid claseId, CancellationToken ct)
    {
        var alumnoId = AlumnoId;
        var matriculado = await db.Clases.AnyAsync(c => c.Id == claseId &&
            db.Matriculas.Any(m => m.CursoId == c.CursoId && m.UsuarioId == alumnoId), ct);

        if (!matriculado)
            throw new ModelContextProtocol.McpException("Esa clase no existe o no es de tus cursos.");
    }

    /// <summary>La IA con la que se generan las preguntas: la que el alumno tiene conectada.</summary>
    private async Task<string?> AgenteConectadoAsync(CancellationToken ct)
    {
        var alumnoId = AlumnoId;
        var existente = await db.Conversaciones.AsNoTracking()
            .Where(c => c.AlumnoId == alumnoId)
            .OrderByDescending(c => c.CreadaEn)
            .Select(c => c.AgenteId)
            .FirstOrDefaultAsync(ct);

        var validas = (await credenciales.ListarAsync(alumnoId, ct))
            .Where(c => c.Estado == EstadoCredencial.Valida)
            .Select(c => c.AgenteId)
            .ToList();

        return existente is not null && validas.Contains(existente) ? existente : validas.FirstOrDefault();
    }

    private static string Error(string codigo, string mensaje) =>
        JsonSerializer.Serialize(new { error = codigo, mensaje }, Json);
}

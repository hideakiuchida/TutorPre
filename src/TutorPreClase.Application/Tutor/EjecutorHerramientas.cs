using System.Text.Json;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Evaluacion;
using TutorPreClase.Application.Nivel;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Application.Tutor;

public sealed record ResultadoHerramienta(string Nombre, string Json, bool Ok, bool CierraExamen = false);

public interface IEjecutorHerramientas
{
    Task<ResultadoHerramienta> EjecutarAsync(
        Conversacion conversacion, string nombre, JsonElement argumentos, CancellationToken ct = default);
}

/// <summary>
/// Ejecuta en el servidor las herramientas que pide el agente (SDD §5.7). Rechaza
/// cualquiera que no corresponda al modo de la conversacion: la seguridad del examen
/// no depende de que el modelo obedezca el prompt (SDD §6.4).
/// </summary>
public sealed class EjecutorHerramientas(
    IAppDbContext db,
    IServicioExamen examen,
    INivelService nivel,
    IRelojSistema reloj,
    IPreguntasDelIntento preguntas,
    IPreparadorPreguntas preparador) : IEjecutorHerramientas
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ResultadoHerramienta> EjecutarAsync(
        Conversacion conversacion, string nombre, JsonElement argumentos, CancellationToken ct = default)
    {
        if (!Herramientas.Permitida(conversacion.Modo, nombre))
            return Error(nombre, "herramienta_no_permitida",
                $"La herramienta {nombre} no esta disponible en modo {conversacion.Modo}.");

        try
        {
            return nombre switch
            {
                Herramientas.ObtenerSiguientePregunta => await SiguientePreguntaAsync(conversacion, ct),
                Herramientas.RegistrarRespuesta => await RegistrarRespuestaAsync(conversacion, argumentos, ct),
                Herramientas.ObtenerProgreso => await ProgresoAsync(conversacion, ct),
                Herramientas.FinalizarExamen => await FinalizarAsync(conversacion, ct),
                Herramientas.ObtenerResultado => await ResultadoAsync(conversacion, ct),
                Herramientas.RegistrarDudaSinCobertura => await RegistrarDudaAsync(conversacion, argumentos, ct),
                Herramientas.ProponerDiagnostico => await ProponerDiagnosticoAsync(conversacion, argumentos, ct),
                _ => Error(nombre, "herramienta_desconocida", "Esa herramienta no existe.")
            };
        }
        catch (ExamenException ex)
        {
            return Error(nombre, ex.Codigo, ex.Message);
        }
    }

    private async Task<ResultadoHerramienta> SiguientePreguntaAsync(Conversacion c, CancellationToken ct)
    {
        var intentoId = ExigirIntento(c);
        var pregunta = await examen.SiguientePreguntaAsync(intentoId, ct);

        // Pregunta a pregunta (RF-04): si la siguiente no llegó a prepararse, se genera ahora.
        if (pregunta is null)
        {
            var generacion = await preguntas.GenerarSiguienteAsync(intentoId, ct);
            if (!generacion.Ok)
            {
                // Sin pregunta que responder, el reloj se detiene: la espera no es del alumno.
                await examen.PausarRelojAsync(intentoId, pausar: true, ct);
                return Error(Herramientas.ObtenerSiguientePregunta, "pregunta_no_generada", generacion.Mensaje!);
            }

            pregunta = await examen.SiguientePreguntaAsync(intentoId, ct);
        }

        if (pregunta is not null)
        {
            // Ya tiene pregunta: el reloj sigue desde donde quedó.
            await examen.PausarRelojAsync(intentoId, pausar: false, ct);

            // Mientras responde esta, se prepara la siguiente.
            preparador.Preparar(intentoId);
        }

        return pregunta is null
            ? Ok(Herramientas.ObtenerSiguientePregunta, new { quedan_preguntas = false })
            : Ok(Herramientas.ObtenerSiguientePregunta, new
            {
                quedan_preguntas = true,
                pregunta_id = pregunta.Id,
                numero = pregunta.Numero,
                total = pregunta.Total,
                enunciado = pregunta.Enunciado,
                alternativas = pregunta.Alternativas.Select(a => new { letra = a.Letra, texto = a.Texto })
            });
    }

    private async Task<ResultadoHerramienta> RegistrarRespuestaAsync(
        Conversacion c, JsonElement args, CancellationToken ct)
    {
        var intentoId = ExigirIntento(c);

        // El historial que ve el modelo no arrastra los ids entre turnos, asi que el
        // servidor resuelve la pregunta en curso cuando no viene un pregunta_id usable.
        Guid preguntaId;

        if (Texto(args, "pregunta_id", out var preguntaTexto) && Guid.TryParse(preguntaTexto, out var recibida))
        {
            preguntaId = recibida;
        }
        else
        {
            var pendiente = await examen.SiguientePreguntaAsync(intentoId, ct);
            if (pendiente is null)
                return Error(Herramientas.RegistrarRespuesta, "sin_preguntas", "No quedan preguntas por responder.");

            preguntaId = pendiente.Id;
        }

        if (!Texto(args, "alternativa", out var letra) || letra.Length == 0)
            return Error(Herramientas.RegistrarRespuesta, "alternativa_invalida", "Falta la alternativa elegida.");

        Texto(args, "texto_original", out var original);

        var registro = await examen.RegistrarRespuestaAsync(intentoId, preguntaId, letra, original ?? "", ct);

        return Ok(Herramientas.RegistrarRespuesta, new
        {
            registrada = registro.Registrada,
            es_correcta = registro.EsCorrecta,
            justificacion = registro.Justificacion,
            quedan_preguntas = registro.QuedanPreguntas
        });
    }

    private async Task<ResultadoHerramienta> ProgresoAsync(Conversacion c, CancellationToken ct)
    {
        var progreso = await examen.ProgresoAsync(ExigirIntento(c), ct);

        return Ok(Herramientas.ObtenerProgreso, new
        {
            respondidas = progreso.Respondidas,
            total = progreso.Total,
            segundos_restantes = progreso.SegundosRestantes,
            reloj_en_pausa = progreso.EnPausa
        });
    }

    private async Task<ResultadoHerramienta> FinalizarAsync(Conversacion c, CancellationToken ct)
    {
        var resultado = await examen.FinalizarAsync(ExigirIntento(c), ct);

        // El chat pasa a Revision: cambian las herramientas y las reglas del prompt.
        c.Modo = ModoConversacion.Revision;
        await db.SaveChangesAsync(ct);

        return new ResultadoHerramienta(
            Herramientas.FinalizarExamen, Serializar(Resultado(resultado)), Ok: true, CierraExamen: true);
    }

    private async Task<ResultadoHerramienta> ResultadoAsync(Conversacion c, CancellationToken ct)
    {
        var resultado = await examen.ResultadoAsync(ExigirIntento(c), ct);
        return Ok(Herramientas.ObtenerResultado, Resultado(resultado));
    }

    private async Task<ResultadoHerramienta> RegistrarDudaAsync(
        Conversacion c, JsonElement args, CancellationToken ct)
    {
        if (!Texto(args, "texto", out var texto) || texto.Trim().Length == 0)
            return Error(Herramientas.RegistrarDudaSinCobertura, "texto_requerido", "Falta el texto de la duda.");

        Texto(args, "tema", out var tema);

        db.DudasSinCobertura.Add(new DudaSinCobertura
        {
            ClaseId = c.ClaseId,
            ConversacionId = c.Id,
            Texto = texto.Trim(),
            Tema = tema,
            RespondidaConAmpliacion = Booleano(args, "con_ampliacion"),
            CreadoEn = reloj.Ahora
        });

        await db.SaveChangesAsync(ct);
        return Ok(Herramientas.RegistrarDudaSinCobertura, new { registrada = true });
    }

    private async Task<ResultadoHerramienta> ProponerDiagnosticoAsync(
        Conversacion c, JsonElement args, CancellationToken ct)
    {
        var temas = new List<string>();

        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("temas_debiles", out var arreglo) &&
            arreglo.ValueKind == JsonValueKind.Array)
        {
            temas.AddRange(arreglo.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .Where(t => t.Trim().Length > 0));
        }

        if (temas.Count == 0)
            return Error(Herramientas.ProponerDiagnostico, "temas_requeridos", "Envia al menos un tema debil.");

        await nivel.RegistrarTemasDebilesAsync(c.AlumnoId, c.ClaseId, temas, ct);

        // El tutor no mueve el nivel: solo aporta el diagnostico (SDD §5.6).
        return Ok(Herramientas.ProponerDiagnostico, new { registrado = true, nivel_modificado = false });
    }

    private static object Resultado(ResultadoDto r) => new
    {
        nota = r.Nota,
        porcentaje = r.Porcentaje,
        correctas = r.Correctas,
        total = r.Total,
        fallos = r.Fallos.Select(f => new
        {
            pregunta_id = f.PreguntaId,
            enunciado = f.Enunciado,
            alternativa_elegida = f.AlternativaElegida,
            alternativa_correcta = f.AlternativaCorrecta,
            justificacion = f.Justificacion,
            tema = f.Tema
        })
    };

    private static Guid ExigirIntento(Conversacion c) =>
        c.IntentoId ?? throw new ExamenException("sin_intento", "La conversacion no tiene un intento asociado.");

    private static bool Texto(JsonElement args, string propiedad, out string valor)
    {
        valor = "";
        if (args.ValueKind != JsonValueKind.Object) return false;
        if (!args.TryGetProperty(propiedad, out var p) || p.ValueKind != JsonValueKind.String) return false;

        valor = p.GetString() ?? "";
        return true;
    }

    private static bool Booleano(JsonElement args, string propiedad) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(propiedad, out var p) &&
        p.ValueKind == JsonValueKind.True;

    private static ResultadoHerramienta Ok(string nombre, object contenido) =>
        new(nombre, Serializar(contenido), Ok: true);

    private static ResultadoHerramienta Error(string nombre, string codigo, string mensaje) =>
        new(nombre, Serializar(new { error = codigo, mensaje }), Ok: false);

    private static string Serializar(object contenido) => JsonSerializer.Serialize(contenido, Json);
}

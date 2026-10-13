using Microsoft.EntityFrameworkCore;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Nivel;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Domain.Reglas;

namespace TutorPreClase.Application.Evaluacion;

public interface IServicioExamen
{
    /// <summary>
    /// Valida ventana e intentos y solo entonces pide las preguntas a <paramref name="generar"/>
    /// (la IA, RF-04). Si la generacion falla, el intento no se crea ni se consume.
    /// </summary>
    Task<Intento> IniciarIntentoAsync(
        Guid claseId, Guid alumnoId, string agenteId, string modelo,
        Func<Examen, CancellationToken, Task<IReadOnlyList<Pregunta>>> generar,
        CancellationToken ct = default);
    Task<PreguntaDto?> SiguientePreguntaAsync(Guid intentoId, CancellationToken ct = default);
    Task<RegistroRespuestaDto> RegistrarRespuestaAsync(Guid intentoId, Guid preguntaId, string letra, string textoOriginal, CancellationToken ct = default);
    Task<ProgresoDto> ProgresoAsync(Guid intentoId, CancellationToken ct = default);

    /// <summary>
    /// Borra los intentos de un alumno en el examen de una clase, con sus preguntas y
    /// respuestas, para que vuelva a tener todos sus intentos. Devuelve cuántos borró.
    /// </summary>
    Task<int> ReiniciarIntentosAsync(Guid claseId, Guid alumnoId, CancellationToken ct = default);

    /// <summary>
    /// Borra un intento en curso con sus preguntas y respuestas, como si no se hubiera
    /// empezado: no consume intento ni deja nota. Uno ya enviado no se puede cancelar.
    /// </summary>
    Task CancelarIntentoAsync(Guid intentoId, CancellationToken ct = default);

    /// <summary>
    /// Detiene el reloj del intento (la siguiente pregunta no se pudo preparar) o lo pone a
    /// correr otra vez (ya hay pregunta). El tiempo en pausa no se le descuenta al alumno.
    /// </summary>
    Task PausarRelojAsync(Guid intentoId, bool pausar, CancellationToken ct = default);
    Task<ResultadoDto> FinalizarAsync(Guid intentoId, CancellationToken ct = default);
    Task<ResultadoDto> ResultadoAsync(Guid intentoId, CancellationToken ct = default);
}

/// <summary>
/// Motor del examen: registro y calificación deterministas, sin IA (RNF-01). El agente
/// solo comunica lo que este servicio decide.
/// </summary>
public sealed class ServicioExamen(IAppDbContext db, IRelojSistema reloj, INivelService nivel) : IServicioExamen
{
    public async Task<Intento> IniciarIntentoAsync(
        Guid claseId, Guid alumnoId, string agenteId, string modelo,
        Func<Examen, CancellationToken, Task<IReadOnlyList<Pregunta>>> generar,
        CancellationToken ct = default)
    {
        var examen = await CargarExamenPorClaseAsync(claseId, ct)
            ?? throw new ExamenException("sin_examen", "La clase no tiene examen publicado.");

        var previos = await db.Intentos
            .Where(i => i.ExamenId == examen.Id && i.AlumnoId == alumnoId)
            .ToListAsync(ct);

        var validacion = ReglasExamen.PuedeIniciar(examen, previos, reloj.Ahora);
        if (!validacion.Permitido)
            throw new ExamenException(validacion.Motivo.ToString(), MensajeDe(validacion.Motivo));

        var preguntas = await generar(examen, ct);

        var intento = new Intento
        {
            ExamenId = examen.Id,
            AlumnoId = alumnoId,
            AgenteId = agenteId,
            Modelo = modelo,
            Estado = EstadoIntento.EnCurso,
            Inicio = reloj.Ahora,
            // El resto de preguntas se genera de a una mientras responde (PreguntasDelIntento).
            PreguntasPlaneadas = Math.Max(examen.PreguntasPorIntento, preguntas.Count)
        };

        db.Intentos.Add(intento);

        foreach (var pregunta in preguntas)
        {
            pregunta.ExamenId = examen.Id;
            pregunta.IntentoId = intento.Id;
            db.Preguntas.Add(pregunta);
        }

        await db.SaveChangesAsync(ct);
        return intento;
    }

    public async Task<PreguntaDto?> SiguientePreguntaAsync(Guid intentoId, CancellationToken ct = default)
    {
        var (examen, intento) = await CargarAsync(intentoId, ct);

        var respondidas = intento.Respuestas.Select(r => r.PreguntaId).ToHashSet();
        var aprobadas = PreguntasVigentes(examen, intento);

        var siguiente = aprobadas.FirstOrDefault(p => !respondidas.Contains(p.Id));
        if (siguiente is null) return null;

        return new PreguntaDto(
            siguiente.Id,
            siguiente.Enunciado,
            aprobadas.IndexOf(siguiente) + 1,
            Total(examen, intento),
            siguiente.Alternativas
                .OrderBy(a => a.Letra, StringComparer.Ordinal)
                .Select(a => new AlternativaDto(a.Id, a.Letra, a.Texto))
                .ToList());
    }

    public async Task<RegistroRespuestaDto> RegistrarRespuestaAsync(
        Guid intentoId, Guid preguntaId, string letra, string textoOriginal, CancellationToken ct = default)
    {
        var (examen, intento) = await CargarAsync(intentoId, ct);

        var validacion = ReglasExamen.PuedeRegistrarRespuesta(examen, intento, preguntaId, reloj.Ahora);
        if (!validacion.Permitido)
            throw new ExamenException(validacion.Motivo.ToString(), MensajeDe(validacion.Motivo));

        var pregunta = examen.Preguntas.First(p => p.Id == preguntaId);

        var alternativa = pregunta.Alternativas
            .FirstOrDefault(a => string.Equals(a.Letra, letra.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ExamenException("alternativa_invalida", "La pregunta no tiene esa alternativa.");

        var respuesta = new RespuestaIntento
        {
            IntentoId = intento.Id,
            PreguntaId = preguntaId,
            AlternativaId = alternativa.Id,
            EsCorrecta = alternativa.EsCorrecta,
            TextoOriginalAlumno = textoOriginal,
            CreadoEn = reloj.Ahora
        };

        // Solo al DbSet: EF hace el fixup y la agrega a intento.Respuestas. Agregarla
        // tambien a mano la duplicaria en memoria y falsearia el conteo de correctas.
        db.Respuestas.Add(respuesta);
        await db.SaveChangesAsync(ct);

        // Quedan aunque aún no estén generadas: se preparan mientras tanto.
        var quedan = Total(examen, intento) > intento.Respuestas.Count;

        // En modo AlFinal el agente no recibe la corrección: solo "registrada" (RF-15).
        if (examen.ModoFeedback != ModoFeedback.Inmediato)
            return new RegistroRespuestaDto(true, null, null, quedan);

        var correcta = pregunta.Alternativas.First(a => a.EsCorrecta);
        return new RegistroRespuestaDto(
            true, alternativa.EsCorrecta, pregunta.Justificacion, quedan, $"{correcta.Letra}) {correcta.Texto}");
    }

    public async Task<int> ReiniciarIntentosAsync(Guid claseId, Guid alumnoId, CancellationToken ct = default)
    {
        var intentos = await db.Intentos
            .Include(i => i.Respuestas)
            .Where(i => i.AlumnoId == alumnoId && i.Examen!.ClaseId == claseId)
            .ToListAsync(ct);

        if (intentos.Count == 0) return 0;

        var ids = intentos.Select(i => i.Id).ToList();

        // El chat vuelve a consulta: sin intento no hay examen en curso ni nada que revisar.
        var conversaciones = await db.Conversaciones
            .Where(c => c.AlumnoId == alumnoId && c.ClaseId == claseId)
            .ToListAsync(ct);
        foreach (var conversacion in conversaciones)
        {
            conversacion.IntentoId = null;
            conversacion.Modo = ModoConversacion.Consulta;
        }

        // El nivel salía de esas notas; el que fijó el docente a mano se respeta.
        db.Niveles.RemoveRange(await db.Niveles
            .Where(n => n.AlumnoId == alumnoId && n.ClaseId == claseId && n.Origen != OrigenNivel.Docente)
            .ToListAsync(ct));

        // Primero las respuestas, que apuntan a las preguntas; luego las preguntas del intento.
        db.Respuestas.RemoveRange(intentos.SelectMany(i => i.Respuestas));
        await db.SaveChangesAsync(ct);

        db.Preguntas.RemoveRange(await db.Preguntas
            .Where(p => p.IntentoId != null && ids.Contains(p.IntentoId.Value))
            .ToListAsync(ct));
        db.Intentos.RemoveRange(intentos);
        await db.SaveChangesAsync(ct);

        return intentos.Count;
    }

    public async Task CancelarIntentoAsync(Guid intentoId, CancellationToken ct = default)
    {
        var intento = await db.Intentos
            .Include(i => i.Respuestas)
            .FirstOrDefaultAsync(i => i.Id == intentoId, ct)
            ?? throw new ExamenException("intento_no_encontrado", "No existe el intento.");

        if (intento.Estado != EstadoIntento.EnCurso)
            throw new ExamenException("examen_cerrado", "El examen ya terminó: no se puede cancelar.");

        // Primero las respuestas, que apuntan a las preguntas; luego las preguntas del intento.
        db.Respuestas.RemoveRange(intento.Respuestas);
        await db.SaveChangesAsync(ct);

        db.Preguntas.RemoveRange(await db.Preguntas.Where(p => p.IntentoId == intentoId).ToListAsync(ct));
        db.Intentos.Remove(intento);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProgresoDto> ProgresoAsync(Guid intentoId, CancellationToken ct = default)
    {
        var (examen, intento) = await CargarAsync(intentoId, ct);

        return new ProgresoDto(
            intento.Respuestas.Count,
            Total(examen, intento),
            ReglasExamen.SegundosRestantes(examen, intento, reloj.Ahora),
            EnPausa: intento.PausadoDesde is not null);
    }

    public async Task PausarRelojAsync(Guid intentoId, bool pausar, CancellationToken ct = default)
    {
        var intento = await db.Intentos.FirstOrDefaultAsync(i => i.Id == intentoId, ct);
        if (intento is null || intento.Estado != EstadoIntento.EnCurso) return;

        if (pausar)
        {
            if (intento.PausadoDesde is not null) return;
            intento.PausadoDesde = reloj.Ahora;
        }
        else
        {
            if (intento.PausadoDesde is not DateTimeOffset desde) return;
            intento.SegundosEnPausa += Math.Max(0, (int)(reloj.Ahora - desde).TotalSeconds);
            intento.PausadoDesde = null;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<ResultadoDto> FinalizarAsync(Guid intentoId, CancellationToken ct = default)
    {
        var (examen, intento) = await CargarAsync(intentoId, ct);

        if (intento.Estado == EstadoIntento.EnCurso)
        {
            // Sobre las planeadas: lo que no llegó a responder cuenta como no acertado.
            var total = Total(examen, intento);
            var correctas = intento.Respuestas.Count(r => r.EsCorrecta);

            intento.Puntaje = Calificacion.Nota(correctas, total);
            intento.Porcentaje = Calificacion.Porcentaje(correctas, total);
            intento.Estado = EstadoIntento.EnRevision;
            intento.Envio = reloj.Ahora;

            await db.SaveChangesAsync(ct);
            // Los temas a reforzar no dependen de que el tutor los proponga (RF-22).
            await nivel.RecalcularAsync(intento.AlumnoId, examen.ClaseId, TemasAReforzar(examen, intento), ct);
        }

        return Armar(examen, intento);
    }

    public async Task<ResultadoDto> ResultadoAsync(Guid intentoId, CancellationToken ct = default)
    {
        var (examen, intento) = await CargarAsync(intentoId, ct);

        if (intento.Estado == EstadoIntento.EnCurso)
            throw new ExamenException("intento_en_curso", "El intento todavia no ha sido enviado.");

        return Armar(examen, intento);
    }

    /// <summary>Las preguntas que la IA genero para este intento (RF-04).</summary>
    private static List<Pregunta> PreguntasVigentes(Examen examen, Intento intento) =>
        examen.Preguntas.Where(p => p.IntentoId == intento.Id).OrderBy(p => p.Orden).ToList();

    /// <summary>Preguntas del intento, generadas o por generar.</summary>
    private static int Total(Examen examen, Intento intento) =>
        Math.Max(intento.PreguntasPlaneadas, PreguntasVigentes(examen, intento).Count);

    /// <summary>
    /// Los temas de lo que el alumno no acertó: las preguntas falladas y las que tuvo
    /// preparadas y no respondió, sin repetir y sin el genérico "General".
    /// </summary>
    private static List<string> TemasAReforzar(Examen examen, Intento intento)
    {
        var acertadas = intento.Respuestas.Where(r => r.EsCorrecta).Select(r => r.PreguntaId).ToHashSet();

        return PreguntasVigentes(examen, intento)
            .Where(p => !acertadas.Contains(p.Id))
            .Select(p => p.Tema.Trim())
            .Where(t => t.Length > 0 && !t.Equals("General", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ResultadoDto Armar(Examen examen, Intento intento)
    {
        var total = Total(examen, intento);
        var correctas = intento.Respuestas.Count(r => r.EsCorrecta);

        var fallos = intento.Respuestas
            .Where(r => !r.EsCorrecta)
            .Select(r =>
            {
                var pregunta = examen.Preguntas.First(p => p.Id == r.PreguntaId);
                var elegida = pregunta.Alternativas.First(a => a.Id == r.AlternativaId);
                var correcta = pregunta.Alternativas.First(a => a.EsCorrecta);

                return new FalloDto(
                    pregunta.Id,
                    pregunta.Enunciado,
                    elegida.Letra + ". " + elegida.Texto,
                    correcta.Letra + ". " + correcta.Texto,
                    pregunta.Justificacion,
                    pregunta.Tema);
            })
            .ToList();

        return new ResultadoDto(
            intento.Id,
            intento.Puntaje ?? 0m,
            intento.Porcentaje ?? 0,
            correctas,
            total,
            fallos);
    }

    private async Task<(Examen Examen, Intento Intento)> CargarAsync(Guid intentoId, CancellationToken ct)
    {
        var intento = await db.Intentos
            .Include(i => i.Respuestas)
            .FirstOrDefaultAsync(i => i.Id == intentoId, ct)
            ?? throw new ExamenException("intento_no_encontrado", "No existe el intento.");

        // Solo las preguntas de este intento: las de otros alumnos no hacen falta.
        var examen = await db.Examenes
            .Include(e => e.Preguntas.Where(p => p.IntentoId == intentoId)).ThenInclude(p => p.Alternativas)
            .FirstAsync(e => e.Id == intento.ExamenId, ct);

        return (examen, intento);
    }

    private Task<Examen?> CargarExamenPorClaseAsync(Guid claseId, CancellationToken ct) =>
        db.Examenes.FirstOrDefaultAsync(e => e.ClaseId == claseId, ct);

    private static string MensajeDe(MotivoRechazo motivo) => motivo switch
    {
        MotivoRechazo.NoPublicado => "El examen todavia no esta publicado.",
        MotivoRechazo.SinIntentosDisponibles => "Ya usaste todos tus intentos.",
        MotivoRechazo.IntentoEnCurso => "Ya tienes un intento en curso.",
        MotivoRechazo.TiempoVencido => "Se acabo el tiempo del examen.",
        MotivoRechazo.PreguntaAjenaAlIntento => "Esa pregunta no pertenece a este examen.",
        MotivoRechazo.PreguntaYaRespondida => "Esa pregunta ya fue respondida.",
        MotivoRechazo.IntentoNoEnCurso => "El intento ya fue enviado.",
        _ => "Operacion no permitida."
    };
}

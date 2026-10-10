using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Domain.Reglas;

public enum MotivoRechazo
{
    Ninguno,
    NoPublicado,
    SinIntentosDisponibles,
    IntentoEnCurso,
    TiempoVencido,
    PreguntaAjenaAlIntento,
    PreguntaYaRespondida,
    IntentoNoEnCurso
}

public readonly record struct Validacion(bool Permitido, MotivoRechazo Motivo)
{
    public static Validacion Ok => new(true, MotivoRechazo.Ninguno);
    public static Validacion No(MotivoRechazo motivo) => new(false, motivo);
}

/// <summary>
/// Reglas de intentos y tiempo (RF-08, SDD §6.4). El examen no tiene fechas: se puede rendir
/// en cualquier momento, mientras queden intentos.
/// </summary>
public static class ReglasExamen
{
    public static Validacion PuedeIniciar(Examen examen, IReadOnlyCollection<Intento> intentosPrevios, DateTimeOffset ahora)
    {
        if (!examen.Publicado) return Validacion.No(MotivoRechazo.NoPublicado);
        if (intentosPrevios.Any(i => i.Estado == EstadoIntento.EnCurso)) return Validacion.No(MotivoRechazo.IntentoEnCurso);
        if (intentosPrevios.Count >= examen.MaxIntentos) return Validacion.No(MotivoRechazo.SinIntentosDisponibles);
        return Validacion.Ok;
    }

    public static Validacion PuedeRegistrarRespuesta(
        Examen examen, Intento intento, Guid preguntaId, DateTimeOffset ahora)
    {
        if (intento.Estado != EstadoIntento.EnCurso) return Validacion.No(MotivoRechazo.IntentoNoEnCurso);
        if (TiempoVencido(examen, intento, ahora)) return Validacion.No(MotivoRechazo.TiempoVencido);
        // Solo cuentan las preguntas generadas para este intento, no las de otro alumno.
        if (!examen.Preguntas.Any(p => p.Id == preguntaId && p.IntentoId == intento.Id))
            return Validacion.No(MotivoRechazo.PreguntaAjenaAlIntento);
        if (intento.Respuestas.Any(r => r.PreguntaId == preguntaId)) return Validacion.No(MotivoRechazo.PreguntaYaRespondida);
        return Validacion.Ok;
    }

    public static bool TiempoVencido(Examen examen, Intento intento, DateTimeOffset ahora)
    {
        if (examen.MinutosLimite is not int minutos) return false;
        return Transcurrido(intento, ahora) > TimeSpan.FromMinutes(minutos);
    }

    public static int? SegundosRestantes(Examen examen, Intento intento, DateTimeOffset ahora)
    {
        if (examen.MinutosLimite is not int minutos) return null;

        var restante = TimeSpan.FromMinutes(minutos) - Transcurrido(intento, ahora);
        return restante < TimeSpan.Zero ? 0 : (int)restante.TotalSeconds;
    }

    /// <summary>
    /// El tiempo que el reloj del intento estuvo corriendo: no cuenta lo que pasó en pausa,
    /// ni la pausa en curso (SDD §5.2).
    /// </summary>
    public static TimeSpan Transcurrido(Intento intento, DateTimeOffset ahora) =>
        (intento.PausadoDesde ?? ahora) - intento.Inicio - TimeSpan.FromSeconds(intento.SegundosEnPausa);
}

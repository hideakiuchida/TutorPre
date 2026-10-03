using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Domain.Reglas;

public enum MotivoRechazo
{
    Ninguno,
    NoPublicado,
    FueraDeVentana,
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

/// <summary>Reglas de ventana, intentos y tiempo (RF-08, SDD §6.4).</summary>
public static class ReglasExamen
{
    public static Validacion PuedeIniciar(Examen examen, IReadOnlyCollection<Intento> intentosPrevios, DateTimeOffset ahora)
    {
        if (!examen.Publicado) return Validacion.No(MotivoRechazo.NoPublicado);
        if (ahora < examen.AbreEn || Cerrado(examen, ahora)) return Validacion.No(MotivoRechazo.FueraDeVentana);
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

    /// <summary>Un examen sin fecha de cierre no se cierra nunca: solo lo limitan los intentos.</summary>
    private static bool Cerrado(Examen examen, DateTimeOffset ahora) =>
        examen.CierraEn is DateTimeOffset cierre && ahora > cierre;

    public static bool TiempoVencido(Examen examen, Intento intento, DateTimeOffset ahora)
    {
        if (Cerrado(examen, ahora)) return true;
        if (examen.MinutosLimite is not int minutos) return false;
        return ahora > intento.Inicio.AddMinutes(minutos);
    }

    public static int? SegundosRestantes(Examen examen, Intento intento, DateTimeOffset ahora)
    {
        if (examen.MinutosLimite is not int minutos) return null;
        var fin = intento.Inicio.AddMinutes(minutos);
        if (examen.CierraEn is DateTimeOffset cierre && fin > cierre) fin = cierre;
        var restante = (int)(fin - ahora).TotalSeconds;
        return restante < 0 ? 0 : restante;
    }
}

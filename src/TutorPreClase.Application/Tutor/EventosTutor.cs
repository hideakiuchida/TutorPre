using TutorPreClase.Domain.Entidades;
using TutorPreClase.Domain.Reglas;

namespace TutorPreClase.Application.Tutor;

/// <summary>
/// Eventos que la API reenvia por SSE (SDD §7.2). `modo` y `progreso` los emite el
/// servidor, no el modelo, para que la cabecera del chat sea confiable con cualquier agente.
/// </summary>
public abstract record EventoTutor;

public sealed record EventoModo(ModoConversacion Modo, bool AmpliacionPermitida) : EventoTutor;

public sealed record EventoToken(string Texto) : EventoTutor;

public sealed record EventoHerramienta(string Nombre, string Estado, string? Detalle = null) : EventoTutor;

public sealed record EventoProgreso(int Respondidas, int Total, int? SegundosRestantes, bool EnPausa = false) : EventoTutor;

public sealed record EventoFuentes(IReadOnlyList<Cita> Citas) : EventoTutor;

public sealed record EventoAviso(string Codigo, string Mensaje) : EventoTutor;

public sealed record EventoFin(string AgenteId, bool UsaAmpliacion, int TokensEntrada, int TokensSalida) : EventoTutor;

public sealed record EventoError(string Mensaje, bool Reintentable) : EventoTutor;

namespace TutorPreClase.Domain.Entidades;

public enum RolUsuario { Alumno, Docente, Admin }

/// <summary>Estado de extracción de texto de un archivo de contenido (SDD §4).</summary>
public enum EstadoArchivo { Pendiente, Procesando, Listo, Error }

/// <summary>
/// SinCuenta: el profesor aún no conectó OpenRouter; el tutor lee el original hasta
/// que lo haga (SDD §6.2).
/// </summary>
public enum EstadoCondensado { Pendiente, Procesando, Listo, SinCuenta, Error }

public enum ModoFeedback { Inmediato, AlFinal }

public enum EstadoIntento { EnCurso, Enviado, EnRevision }

/// <summary>Modo del chat del alumno (SDD §5).</summary>
public enum ModoConversacion { Consulta, Evaluacion, Revision }

public enum RolMensaje { Alumno, Agente, Herramienta }

public enum NivelAlumnoValor { Inicial, Basico, Intermedio, Avanzado }

public enum OrigenNivel { Automatico, Tutor, Docente }

public enum OrigenPregunta { IA, Docente }

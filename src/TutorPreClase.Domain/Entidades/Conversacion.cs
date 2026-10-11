namespace TutorPreClase.Domain.Entidades;

public class AgenteIA
{
    /// <summary>"gemini", "gemini-clave", "openai" o "anthropic": el mismo id de la configuración.</summary>
    public string Id { get; set; } = "";
    public string NombreVisible { get; set; } = "";
    public string Proveedor { get; set; } = "";
    public string Modelo { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? Descripcion { get; set; }
    /// <summary>Consola del proveedor donde el alumno genera su credencial.</summary>
    public string? UrlConsola { get; set; }
    public bool Habilitado { get; set; } = true;
}

public class Intento
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExamenId { get; set; }
    public Examen? Examen { get; set; }
    public Guid AlumnoId { get; set; }
    public string AgenteId { get; set; } = "";
    public string Modelo { get; set; } = "";
    public EstadoIntento Estado { get; set; } = EstadoIntento.EnCurso;
    public DateTimeOffset Inicio { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Envio { get; set; }
    public decimal? Puntaje { get; set; }
    public int? Porcentaje { get; set; }

    /// <summary>
    /// Cuántas preguntas tendrá el intento. La IA las genera de a una mientras el alumno
    /// responde (RF-04), así que al empezar existe solo la primera; 0 en intentos antiguos,
    /// que tenían todas desde el inicio.
    /// </summary>
    public int PreguntasPlaneadas { get; set; }

    /// <summary>
    /// Desde cuándo está detenido el reloj del intento, o null si corre. Se detiene cuando la
    /// siguiente pregunta no se pudo generar: ese rato de espera no es culpa del alumno.
    /// </summary>
    public DateTimeOffset? PausadoDesde { get; set; }

    /// <summary>Lo que el reloj ya estuvo detenido en pausas anteriores.</summary>
    public int SegundosEnPausa { get; set; }

    public List<RespuestaIntento> Respuestas { get; set; } = [];
}

public class RespuestaIntento
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IntentoId { get; set; }
    public Intento? Intento { get; set; }
    public Guid PreguntaId { get; set; }
    public Pregunta? Pregunta { get; set; }
    public Guid AlternativaId { get; set; }
    public bool EsCorrecta { get; set; }
    public string TextoOriginalAlumno { get; set; } = "";
    public DateTimeOffset CreadoEn { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Hilo único del alumno por clase (SDD §4): pasa de Consulta a Evaluación al iniciar
/// el examen y a Revisión al finalizarlo.
/// </summary>
public class Conversacion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClaseId { get; set; }
    public Clase? Clase { get; set; }
    public Guid AlumnoId { get; set; }
    public string AgenteId { get; set; } = "";
    public ModoConversacion Modo { get; set; } = ModoConversacion.Consulta;
    public Guid? IntentoId { get; set; }
    public Intento? Intento { get; set; }
    public DateTimeOffset CreadaEn { get; set; } = DateTimeOffset.UtcNow;

    public List<MensajeChat> Mensajes { get; set; } = [];
}

public class MensajeChat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversacionId { get; set; }
    public Conversacion? Conversacion { get; set; }
    public Guid? PreguntaId { get; set; }
    public string AgenteId { get; set; } = "";
    public RolMensaje Rol { get; set; }
    public string Texto { get; set; } = "";
    public string? Herramienta { get; set; }
    /// <summary>Fuentes citadas y resueltas contra pagina_contenido, en JSON.</summary>
    public string? Fuentes { get; set; }
    public bool UsaAmpliacion { get; set; }
    public int TokensEntrada { get; set; }
    public int TokensSalida { get; set; }
    public DateTimeOffset CreadoEn { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>RF-13: duda que el material del docente no cubrió.</summary>
public class DudaSinCobertura
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClaseId { get; set; }
    public Guid ConversacionId { get; set; }
    public string Texto { get; set; } = "";
    public string? Tema { get; set; }
    public bool RespondidaConAmpliacion { get; set; }
    public DateTimeOffset CreadoEn { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>RF-21: nivel vigente del alumno en una clase.</summary>
public class NivelAlumno
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AlumnoId { get; set; }
    public Guid ClaseId { get; set; }
    public NivelAlumnoValor Nivel { get; set; }
    /// <summary>Temas débiles en JSON, aportados por el tutor (proponer_diagnostico).</summary>
    public string TemasDebiles { get; set; } = "[]";
    public OrigenNivel Origen { get; set; } = OrigenNivel.Automatico;
    public DateTimeOffset ActualizadoEn { get; set; } = DateTimeOffset.UtcNow;
}

public enum EstadoCredencial { Valida, Invalida }

/// <summary>Como llego la clave: pegada por el alumno u obtenida por OAuth (RF-29).</summary>
public enum OrigenCredencial { Pegada, OAuth }

/// <summary>
/// Credencial BYOK del alumno (SDD §4). La clave se guarda cifrada y nunca se devuelve:
/// la web solo conoce el agente, los ultimos 4 caracteres y la fecha de conexion.
/// </summary>
public class CredencialAgente
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsuarioId { get; set; }
    public Usuario? Usuario { get; set; }
    public string AgenteId { get; set; } = "";
    public string ClaveCifrada { get; set; } = "";
    public string Ultimos4 { get; set; } = "";
    public OrigenCredencial Origen { get; set; } = OrigenCredencial.Pegada;
    public EstadoCredencial Estado { get; set; } = EstadoCredencial.Valida;
    public DateTimeOffset CreadaEn { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UltimoUsoEn { get; set; }
}

/// <summary>
/// Token personal con el que el asistente del alumno (Claude, ChatGPT…) usa el Tutor
/// como servidor MCP (RF-39). Solo se guarda su hash: el token se muestra una vez.
/// </summary>
public class TokenMcp
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsuarioId { get; set; }
    public Usuario? Usuario { get; set; }
    /// <summary>SHA-256 del token, en hexadecimal.</summary>
    public string Hash { get; set; } = "";
    /// <summary>Últimos 4 caracteres, para que el alumno lo reconozca.</summary>
    public string Ultimos4 { get; set; } = "";
    public DateTimeOffset CreadoEn { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UltimoUsoEn { get; set; }
    public DateTimeOffset? RevocadoEn { get; set; }
}

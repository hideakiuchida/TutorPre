namespace TutorPreClase.Domain.Entidades;

public class ArchivoContenido
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClaseId { get; set; }
    public Clase? Clase { get; set; }
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "";
    public string BlobUrl { get; set; } = "";
    public string HashSha256 { get; set; } = "";
    public EstadoArchivo Estado { get; set; } = EstadoArchivo.Pendiente;
    public string? Error { get; set; }
    public DateTimeOffset CreadoEn { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// La IA reescribe cada página como apuntes compactos para que el tutor lea menos
    /// (SDD §6.2). Se hace una vez, con la IA que el profesor tenga conectada.
    /// </summary>
    public EstadoCondensado Condensado { get; set; } = EstadoCondensado.Pendiente;
    public string? ErrorCondensado { get; set; }

    /// <summary>El profesor puede preferir que el tutor lea el texto original.</summary>
    public bool UsarOriginal { get; set; }

    public List<PaginaContenido> Paginas { get; set; } = [];
}

/// <summary>
/// Una página (o diapositiva) de texto extraído. El contexto de clase se arma
/// concatenando estas páginas en orden (SDD §6.2); no hay chunks ni embeddings.
/// </summary>
public class PaginaContenido
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ArchivoId { get; set; }
    public ArchivoContenido? Archivo { get; set; }
    public Guid ClaseId { get; set; }
    public int Pagina { get; set; }
    public string Texto { get; set; } = "";
    public int Tokens { get; set; }

    /// <summary>Apuntes de esta página hechos por la IA; null si se usa el original.</summary>
    public string? TextoCondensado { get; set; }
    public int TokensCondensado { get; set; }
}

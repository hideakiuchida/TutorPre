using System.Text.Json;

namespace TutorPreClase.Application.Llm;

/// <summary>
/// Contrato común de los proveedores (SDD §5.8). El resto del sistema no sabe con
/// cuál está hablando: el prompt, las herramientas y el historial son los mismos.
/// </summary>
public interface ILlmProvider
{
    /// <summary>"openrouter"; la interfaz deja abierta la puerta a otro gateway.</summary>
    string Id { get; }

    IAsyncEnumerable<LlmEvento> StreamAsync(LlmSolicitud solicitud, CancellationToken ct);
}

public sealed record LlmSolicitud(
    string Modelo,
    string PromptSistema,
    IReadOnlyList<LlmMensaje> Mensajes,
    IReadOnlyList<DefinicionHerramienta> Herramientas,
    double Temperatura = 0.2,
    int MaxTokens = 2048,
    string? Razonamiento = null);

public enum RolLlm { Usuario, Asistente, Herramienta }

public sealed record LlmMensaje(
    RolLlm Rol,
    string? Texto,
    string? HerramientaId = null,
    string? HerramientaNombre = null,
    string? HerramientaResultado = null,
    JsonElement? HerramientaArgumentos = null);

public sealed record DefinicionHerramienta(string Nombre, string Descripcion, JsonElement EsquemaEntrada);

public abstract record LlmEvento;
public sealed record TextoParcial(string Texto) : LlmEvento;
public sealed record LlamadaHerramienta(string Id, string Nombre, JsonElement Argumentos) : LlmEvento;
/// <summary>`CortadaPorTope`: la respuesta llegó a MaxTokens y quedó incompleta.</summary>
public sealed record Fin(int TokensEntrada, int TokensSalida, bool CortadaPorTope = false) : LlmEvento;
/// <summary>
/// Fallo del proveedor. `LimiteDeUso` es un 429: la credencial sigue siendo buena y el
/// alumno puede volver cuando indique `ReintentarEn`, si el proveedor lo dijo (RF-30).
/// `Saturado` distingue el 429 del modelo gratuito saturado para todos (se reintenta en
/// segundos) del cupo propio del alumno agotado.
/// </summary>
public sealed record ErrorProveedor(
    string Mensaje,
    bool Reintentable,
    bool CredencialRechazada = false,
    bool LimiteDeUso = false,
    DateTimeOffset? ReintentarEn = null,
    bool Saturado = false) : LlmEvento;

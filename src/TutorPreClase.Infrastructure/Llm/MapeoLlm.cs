using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TutorPreClase.Application.Llm;

namespace TutorPreClase.Infrastructure.Llm;

/// <summary>Utilidades compartidas por los adaptadores de proveedor.</summary>
internal static class MapeoLlm
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonElement ObjetoVacio { get; } = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Lee un flujo SSE y devuelve el contenido de cada linea `data:`.</summary>
    public static async IAsyncEnumerable<string> LeerSseAsync(
        Stream flujo, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var lector = new StreamReader(flujo);

        // Se lee hasta que ReadLineAsync devuelva null: EndOfStream bloquearia el hilo
        // con una lectura sincrona sobre la conexion del proveedor.
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var linea = await lector.ReadLineAsync(ct);
            if (linea is null) break;
            if (!linea.StartsWith("data:", StringComparison.Ordinal)) continue;

            var dato = linea[5..].Trim();
            if (dato.Length == 0 || dato == "[DONE]") continue;

            yield return dato;
        }
    }

    public static JsonElement Argumentos(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ObjetoVacio;

        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return ObjetoVacio;
        }
    }

    public static string Texto(JsonElement raiz, params string[] ruta)
    {
        var actual = raiz;
        foreach (var paso in ruta)
        {
            if (actual.ValueKind != JsonValueKind.Object || !actual.TryGetProperty(paso, out actual))
                return "";
        }

        return actual.ValueKind == JsonValueKind.String ? actual.GetString() ?? "" : "";
    }

    public static int Entero(JsonElement raiz, params string[] ruta)
    {
        var actual = raiz;
        foreach (var paso in ruta)
        {
            if (actual.ValueKind != JsonValueKind.Object || !actual.TryGetProperty(paso, out actual))
                return 0;
        }

        return actual.ValueKind == JsonValueKind.Number && actual.TryGetInt32(out var valor) ? valor : 0;
    }

    /// <summary>Un 429 o un 5xx se reintenta; un 4xx de la peticion, no (RF-17).</summary>
    public static bool EsReintentable(HttpStatusCode codigo) =>
        codigo == HttpStatusCode.TooManyRequests || (int)codigo >= 500;

    /// <summary>
    /// Lo que lee el alumno cuando el proveedor falla: qué puede hacer, no el código HTTP
    /// (ese queda en el log).
    /// </summary>
    public static string MensajeDeFallo(HttpStatusCode codigo) => EsReintentable(codigo)
        ? "El tutor no pudo responder ahora mismo. Vuelve a enviar tu mensaje en unos segundos."
        : "El tutor no pudo responder a ese mensaje. Si se repite, cambia de tutor en «Mi tutor» o avisa a tu docente.";

    public readonly record struct Envio(HttpResponseMessage? Respuesta, ErrorProveedor? Error);

    /// <summary>
    /// El envio va aparte porque C# no admite `yield return` dentro de un `catch`.
    /// </summary>
    public static async Task<Envio> EnviarAsync(
        HttpClient http, HttpRequestMessage peticion, string proveedor, ILogger log, CancellationToken ct)
    {
        try
        {
            var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, ct);
            return new Envio(respuesta, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "{Proveedor} no respondio", proveedor);
            return new Envio(null, new ErrorProveedor("No se pudo contactar al proveedor.", Reintentable: true));
        }
    }

    public static async Task<ErrorProveedor?> RevisarEstadoAsync(
        HttpResponseMessage respuesta, string proveedor, ILogger log, CancellationToken ct)
    {
        if (respuesta.IsSuccessStatusCode) return null;

        var detalle = await respuesta.Content.ReadAsStringAsync(ct);
        log.LogWarning("{Proveedor} devolvio {Codigo}: {Detalle}", proveedor, (int)respuesta.StatusCode, detalle);

        var credencialRechazada =
            respuesta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        // Una cuenta de pago sin créditos (OpenAI responde 429; Anthropic, 400): la clave es
        // buena, pero no hay nada que esperar: hay que cargar saldo o usar otra IA.
        var sinSaldo = EsFaltaDeSaldo(detalle);

        // Un 429 es cupo agotado, no clave mala: no se invalida la credencial (RF-30).
        var limiteDeUso = respuesta.StatusCode == HttpStatusCode.TooManyRequests || sinSaldo;

        return new ErrorProveedor(
            MensajeDeFallo(respuesta.StatusCode),
            EsReintentable(respuesta.StatusCode),
            credencialRechazada,
            limiteDeUso,
            limiteDeUso ? ReintentarEn(respuesta) : null,
            limiteDeUso && EsSaturacion(detalle),
            sinSaldo);
    }

    public static bool EsFaltaDeSaldo(string detalle) =>
        detalle.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
        detalle.Contains("no credits remaining", StringComparison.OrdinalIgnoreCase) ||
        detalle.Contains("credit balance is too low", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// OpenRouter marca con `limit_source` de dónde viene el 429: si es del proveedor que
    /// sirve el modelo gratuito (pool compartido), el cupo del alumno sigue intacto.
    /// </summary>
    public static bool EsSaturacion(string detalle)
    {
        try
        {
            using var doc = JsonDocument.Parse(detalle);
            var fuente = Texto(doc.RootElement, "error", "metadata", "limit_source");
            if (fuente.Length > 0) return fuente.StartsWith("upstream", StringComparison.OrdinalIgnoreCase);

            return Texto(doc.RootElement, "error", "metadata", "raw")
                .Contains("rate-limited upstream", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Cuando se puede volver a llamar, si el proveedor lo dice: `Retry-After` (estandar)
    /// o `X-RateLimit-Reset` en milisegundos Unix (OpenRouter).
    /// </summary>
    public static DateTimeOffset? ReintentarEn(HttpResponseMessage respuesta)
    {
        var reintento = respuesta.Headers.RetryAfter;
        if (reintento?.Date is DateTimeOffset fecha) return fecha;
        if (reintento?.Delta is TimeSpan espera) return DateTimeOffset.UtcNow + espera;

        if (respuesta.Headers.TryGetValues("X-RateLimit-Reset", out var valores) &&
            long.TryParse(valores.FirstOrDefault(), out var milisegundos) &&
            milisegundos > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milisegundos);
        }

        return null;
    }
}

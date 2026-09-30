using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TutorPreClase.Application.Llm;

namespace TutorPreClase.Infrastructure.Llm;

/// <summary>Resultado de pedir un token de acceso a Google.</summary>
public readonly record struct TokenGoogle(string? Acceso, bool Revocado)
{
    public static TokenGoogle Rechazado { get; } = new(null, true);
    public static TokenGoogle SinRespuesta { get; } = new(null, false);
}

/// <summary>
/// Canjea códigos y renueva accesos de Google (RF-37). La bóveda guarda el refresh token
/// cifrado; el token de acceso, que dura una hora, vive solo en memoria y se renueva antes
/// de vencer. En la caché la clave es un hash: el token en claro no se usa como clave.
/// </summary>
public sealed class TokensGoogle(
    IHttpClientFactory http,
    IMemoryCache cache,
    IOptions<OpcionesAgentes> agentes,
    ILogger<TokensGoogle> log)
{
    public const string Cliente = "google-oauth";
    private const string UrlTokenPorDefecto = "https://oauth2.googleapis.com/token";

    /// <summary>
    /// Canjea el código del retorno. Devuelve el refresh token, o null si Google lo rechaza;
    /// `PermisoIncompleto` si el usuario no marcó el permiso que exige Gemini (en la
    /// pantalla de Google cada permiso sensible es una casilla).
    /// </summary>
    public async Task<(string? Renovacion, bool PermisoIncompleto)> CanjearAsync(
        string agenteId, string codigo, string verificador, CancellationToken ct)
    {
        var opciones = Opciones(agenteId);

        var respuesta = await PedirAsync(opciones, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = codigo,
            ["code_verifier"] = verificador,
            ["redirect_uri"] = opciones.UrlRetorno!,
            ["client_id"] = opciones.ClientId!,
            ["client_secret"] = opciones.ClientSecret!
        }, ct);

        if (respuesta?.AccessToken is not { Length: > 0 } acceso || respuesta.RefreshToken is not { Length: > 0 } renovacion)
            return (null, false);

        // Google devuelve los permisos concedidos separados por espacios; hacen falta todos.
        var concedidos = (respuesta.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pedidos = (opciones.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (pedidos.Except(concedidos).Any())
        {
            log.LogWarning("Google no concedio el permiso {Permiso}; concedidos: {Concedidos}", opciones.Scope, respuesta.Scope);
            return (null, true);
        }

        Guardar(renovacion, acceso, respuesta.ExpiresIn);
        return (renovacion, false);
    }

    /// <summary>Token de acceso vigente para ese refresh token, renovándolo si hace falta.</summary>
    public async Task<TokenGoogle> AccesoAsync(string agenteId, string renovacion, CancellationToken ct)
    {
        if (cache.TryGetValue(Clave(renovacion), out string? vigente) && vigente is not null)
            return new TokenGoogle(vigente, false);

        var opciones = Opciones(agenteId);
        var respuesta = await PedirAsync(opciones, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = renovacion,
            ["client_id"] = opciones.ClientId!,
            ["client_secret"] = opciones.ClientSecret!
        }, ct);

        if (respuesta is null) return TokenGoogle.SinRespuesta;

        // invalid_grant: el usuario revocó el acceso o el refresh token caducó (RF-28).
        if (respuesta.Error == "invalid_grant") return TokenGoogle.Rechazado;
        if (respuesta.AccessToken is not { Length: > 0 } acceso) return TokenGoogle.SinRespuesta;

        Guardar(renovacion, acceso, respuesta.ExpiresIn);
        return new TokenGoogle(acceso, false);
    }

    public void Olvidar(string renovacion) => cache.Remove(Clave(renovacion));

    private void Guardar(string renovacion, string acceso, int segundos) =>
        cache.Set(Clave(renovacion), acceso, TimeSpan.FromSeconds(Math.Max(60, segundos - 300)));

    private async Task<RespuestaToken?> PedirAsync(OpcionesAgente opciones, Dictionary<string, string> formulario, CancellationToken ct)
    {
        try
        {
            using var respuesta = await http.CreateClient(Cliente).PostAsync(
                opciones.UrlToken ?? UrlTokenPorDefecto, new FormUrlEncodedContent(formulario), ct);

            // El cuerpo no se registra: si sale bien, trae los tokens.
            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaToken>(ct);
            if (!respuesta.IsSuccessStatusCode)
                log.LogWarning("Google rechazo el token con {Codigo} ({Error})", (int)respuesta.StatusCode, cuerpo?.Error);

            return cuerpo ?? new RespuestaToken();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Google no respondio al pedir un token");
            return null;
        }
    }

    private OpcionesAgente Opciones(string agenteId)
    {
        if (!agentes.Value.TryGetValue(agenteId, out var opciones) ||
            opciones.Conexion != ConexionAgente.OAuthGoogle ||
            string.IsNullOrWhiteSpace(opciones.ClientId) ||
            string.IsNullOrWhiteSpace(opciones.ClientSecret))
        {
            throw new CredencialException("oauth_no_disponible",
                "La conexión con Google aún no está configurada en el servidor.");
        }

        return opciones;
    }

    private static string Clave(string renovacion) =>
        "google-acceso:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(renovacion)));

    private sealed class RespuestaToken
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
    }
}

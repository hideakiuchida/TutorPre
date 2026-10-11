using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Llm;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Infrastructure.Llm;

/// <summary>
/// El usuario conecta Gemini iniciando sesión con su cuenta de Google (RF-37): OAuth con
/// PKCE, con el mismo cliente web del inicio de sesión y un permiso más (`cloud-platform`,
/// el que exige la API de Gemini). El verificador vive en el servidor, ligado al usuario y
/// de un solo uso; el canje lo hace la API y el refresh token se guarda cifrado en la
/// bóveda, sin pasar nunca por el navegador. Google cuenta el uso contra el proyecto de
/// Google Cloud de la app, no contra el usuario (SDD §5.8).
/// </summary>
public sealed class ConexionGoogleGemini(
    IMemoryCache cache,
    TokensGoogle tokens,
    IBovedaCredenciales boveda,
    IAppDbContext db,
    IOptions<OpcionesAgentes> agentes) : IConexionOAuth
{
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(10);

    public async Task<string> IniciarAsync(Guid usuarioId, string agenteId, CancellationToken ct = default)
    {
        var opciones = OpcionesOAuth(agenteId);

        var verificador = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var desafio = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verificador)));
        cache.Set(ClaveCache(usuarioId, agenteId), verificador, Vigencia);

        // Se sugiere la misma cuenta con la que entró a la plataforma.
        var correo = await db.Usuarios.Where(u => u.Id == usuarioId).Select(u => u.Email).FirstOrDefaultAsync(ct);

        var parametros = new Dictionary<string, string?>
        {
            ["client_id"] = opciones.ClientId,
            ["redirect_uri"] = opciones.UrlRetorno,
            ["response_type"] = "code",
            ["scope"] = opciones.Scope,
            ["code_challenge"] = desafio,
            ["code_challenge_method"] = "S256",
            // Sin offline no hay refresh token, y el acceso dura una hora.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
            ["login_hint"] = correo
        };

        var consulta = string.Join('&', parametros
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));

        return $"{opciones.UrlAutorizacion}?{consulta}";
    }

    public async Task<CredencialResumen> CanjearAsync(
        Guid usuarioId, string agenteId, string codigo, CancellationToken ct = default)
    {
        OpcionesOAuth(agenteId);

        if (string.IsNullOrWhiteSpace(codigo))
            throw new CredencialException("oauth_sin_codigo", "Google no devolvió ningún código de autorización.");

        var claveCache = ClaveCache(usuarioId, agenteId);
        if (!cache.TryGetValue(claveCache, out string? verificador) || verificador is null)
            throw new CredencialException("oauth_vencido",
                "La conexión con Google venció o ya se usó. Vuelve a pulsar «Entrar con Gemini».");

        cache.Remove(claveCache);

        var (renovacion, permisoIncompleto) = await tokens.CanjearAsync(agenteId, codigo.Trim(), verificador, ct);

        if (permisoIncompleto)
            throw new CredencialException("oauth_permiso_incompleto",
                "Google no concedió el permiso que necesita Gemini. Vuelve a pulsar «Entrar con Gemini» y, " +
                "en la pantalla de Google, marca la casilla del permiso de Google Cloud antes de continuar.");

        if (renovacion is null)
            throw new CredencialException("oauth_rechazado",
                "Google no aceptó la autorización. Vuelve a pulsar «Entrar con Gemini» y acepta el permiso.");

        // Desde aquí es una credencial más: se valida, se cifra y se guarda (RF-25, RF-26).
        return await boveda.ConectarAsync(usuarioId, agenteId, renovacion, OrigenCredencial.OAuth, ct);
    }

    private OpcionesAgente OpcionesOAuth(string agenteId)
    {
        if (!agentes.Value.TryGetValue(agenteId, out var opciones) ||
            opciones.Conexion != ConexionAgente.OAuthGoogle ||
            string.IsNullOrWhiteSpace(opciones.UrlAutorizacion) ||
            string.IsNullOrWhiteSpace(opciones.UrlRetorno) ||
            string.IsNullOrWhiteSpace(opciones.Scope))
        {
            throw new CredencialException("oauth_no_disponible", "Ese agente no se conecta con Google.");
        }

        if (string.IsNullOrWhiteSpace(opciones.ClientId) || string.IsNullOrWhiteSpace(opciones.ClientSecret))
            throw new CredencialException("oauth_no_disponible",
                "La conexión con Google aún no está configurada en el servidor.");

        return opciones;
    }

    private static string ClaveCache(Guid usuarioId, string agenteId) => $"oauth:{agenteId}:{usuarioId}";
}

/// <summary>
/// Puerta de las conexiones por inicio de sesión (RF-29, RF-37): hoy solo Google, para
/// Gemini. Un agente que ya no está vigente en la configuración no se puede conectar.
/// </summary>
public sealed class ConexionesOAuth(
    ConexionGoogleGemini google,
    IOptions<OpcionesAgentes> agentes) : IConexionOAuth
{
    public Task<string> IniciarAsync(Guid usuarioId, string agenteId, CancellationToken ct = default) =>
        Para(agenteId).IniciarAsync(usuarioId, agenteId, ct);

    public Task<CredencialResumen> CanjearAsync(Guid usuarioId, string agenteId, string codigo, CancellationToken ct = default) =>
        Para(agenteId).CanjearAsync(usuarioId, agenteId, codigo, ct);

    private IConexionOAuth Para(string agenteId)
    {
        if (!agentes.Value.TryGetValue(agenteId, out var opciones) || !opciones.Vigente)
            throw new CredencialException("agente_no_disponible", "Esa opción de conexión ya no está disponible.");

        // Si el agente no es de Google, la propia conexión responde que no se conecta así.
        return google;
    }
}

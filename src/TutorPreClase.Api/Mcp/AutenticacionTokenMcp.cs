using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TutorPreClase.Application.Mcp;

namespace TutorPreClase.Api.Mcp;

public sealed class OpcionesAutenticacionTokenMcp : AuthenticationSchemeOptions;

/// <summary>
/// Autentica al asistente del alumno en /mcp con su token personal (RF-39). Solo vale
/// ahí: el token no abre la API de la web, y la sesión de la web no abre /mcp.
/// </summary>
public sealed class ManejadorTokenMcp(
    IOptionsMonitor<OpcionesAutenticacionTokenMcp> opciones,
    ILoggerFactory logs,
    UrlEncoder codificador,
    IServicioTokenMcp tokens)
    : AuthenticationHandler<OpcionesAutenticacionTokenMcp>(opciones, logs, codificador)
{
    public const string Esquema = "TokenMcp";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cabecera = Request.Headers.Authorization.ToString();
        if (!cabecera.StartsWith("Bearer ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        var usuario = await tokens.ValidarAsync(cabecera["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (usuario is null) return AuthenticateResult.Fail("Token MCP inexistente o revocado.");

        var identidad = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
            new Claim(ClaimTypes.Role, usuario.Rol.ToString())
        ], Esquema);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidad), Esquema));
    }

    /// <summary>Así el cliente MCP sabe que tiene que mandar el token.</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"tutor-preclase-mcp\"";
        return Task.CompletedTask;
    }
}

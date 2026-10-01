using TutorPreClase.Api.Seguridad;
using TutorPreClase.Application.Mcp;

namespace TutorPreClase.Api.Mcp;

/// <summary>
/// El Tutor como servidor MCP (RF-39): el alumno genera su token en la web y lo pone en su
/// asistente, que llama a /mcp. El token sale en claro solo al generarlo.
/// </summary>
public static class EndpointsMcp
{
    public const string Ruta = "/mcp";

    public static void MapearMcp(this WebApplication app)
    {
        var grupo = app.MapGroup("/api/v1/alumno/token-mcp").RequireAuthorization();

        grupo.MapGet("/", async (HttpContext http, IServicioTokenMcp tokens, CancellationToken ct) =>
            Results.Ok(new { token = await tokens.ActualAsync(http.User.Id(), ct), url = UrlServidor(http) }));

        grupo.MapPost("/", async (HttpContext http, IServicioTokenMcp tokens, CancellationToken ct) =>
        {
            var nuevo = await tokens.GenerarAsync(http.User.Id(), ct);
            return Results.Ok(new { valor = nuevo.Token, token = nuevo.Resumen, url = UrlServidor(http) });
        });

        grupo.MapDelete("/", async (HttpContext http, IServicioTokenMcp tokens, CancellationToken ct) =>
        {
            await tokens.RevocarAsync(http.User.Id(), ct);
            return Results.NoContent();
        });

        // Solo con el token MCP: la sesión de la web no sirve aquí.
        app.MapMcp(Ruta).RequireAuthorization(p => p
            .AddAuthenticationSchemes(ManejadorTokenMcp.Esquema)
            .RequireAuthenticatedUser());
    }

    /// <summary>La dirección pública de /mcp; en Railway llega por el proxy como https.</summary>
    private static string UrlServidor(HttpContext http)
    {
        var esquema = http.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? http.Request.Scheme;
        return $"{esquema}://{http.Request.Host}{Ruta}";
    }
}

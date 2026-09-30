using Microsoft.Extensions.Options;
using TutorPreClase.Api.Seguridad;
using TutorPreClase.Application.Llm;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Api.Endpoints;

public sealed record CanjeOAuthPeticion(string Code);

public sealed record ClavePegadaPeticion(string Clave);

/// <summary>
/// Credenciales BYOK del alumno (SDD §7). Los agentes OAuth se conectan iniciando sesion
/// en el proveedor (RF-29); los de tipo Clave aceptan la clave de API del propio alumno,
/// validada contra el proveedor y guardada cifrada (RF-38). Todo va contra el usuario del
/// token, y la clave nunca se devuelve.
/// </summary>
public static class EndpointsCredenciales
{
    public static void MapearCredenciales(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/v1/alumno/credenciales").RequireAuthorization();

        grupo.MapGet("/", async (HttpContext http, IBovedaCredenciales boveda, CancellationToken ct) =>
        {
            var credenciales = await boveda.ListarAsync(http.User.Id(), ct);

            return Results.Ok(credenciales.Select(c => new
            {
                agenteId = c.AgenteId,
                ultimos4 = c.Ultimos4,
                origen = c.Origen.ToString(),
                estado = c.Estado.ToString(),
                creadaEn = c.CreadaEn,
                ultimoUsoEn = c.UltimoUsoEn
            }));
        });

        // Clave pegada (RF-38): solo en agentes de tipo Clave; en los OAuth sigue sin aceptarse.
        grupo.MapPut("/{agenteId}", async (
            string agenteId,
            ClavePegadaPeticion peticion,
            HttpContext http,
            IOptions<OpcionesAgentes> opciones,
            IBovedaCredenciales boveda,
            CancellationToken ct) =>
        {
            if (!opciones.Value.TryGetValue(agenteId, out var agente) || agente.Conexion != ConexionAgente.Clave)
                throw new CredencialException("conexion_por_oauth",
                    "Este agente se conecta iniciando sesión en el proveedor, no pegando una clave.");

            var clave = (peticion.Clave ?? "").Trim();

            if (clave.Length > 512 ||
                (!string.IsNullOrEmpty(agente.PrefijoClave) &&
                 !clave.StartsWith(agente.PrefijoClave, StringComparison.Ordinal)))
                throw new CredencialException("clave_invalida",
                    $"Esa no parece una clave de {agente.Nombre}: debería empezar por \"{agente.PrefijoClave}\".");

            var credencial = await boveda.ConectarAsync(http.User.Id(), agenteId, clave, OrigenCredencial.Pegada, ct);
            return Results.Ok(Resumen(credencial));
        });

        // Conexion por inicio de sesion en el proveedor, sin pegar clave (RF-29, SDD §5.1).
        grupo.MapPost("/{agenteId}/oauth/inicio", async (
            string agenteId, HttpContext http, IConexionOAuth oauth, CancellationToken ct) =>
        {
            var url = await oauth.IniciarAsync(http.User.Id(), agenteId, ct);
            return Results.Ok(new { url });
        });

        grupo.MapPost("/{agenteId}/oauth/canje", async (
            string agenteId,
            CanjeOAuthPeticion peticion,
            HttpContext http,
            IConexionOAuth oauth,
            CancellationToken ct) =>
        {
            var credencial = await oauth.CanjearAsync(http.User.Id(), agenteId, peticion.Code, ct);
            return Results.Ok(Resumen(credencial));
        });

        grupo.MapDelete("/{agenteId}", async (
            string agenteId, HttpContext http, IBovedaCredenciales boveda, CancellationToken ct) =>
        {
            await boveda.DesconectarAsync(http.User.Id(), agenteId, ct);
            return Results.NoContent();
        });
    }

    private static object Resumen(CredencialResumen c) => new
    {
        agenteId = c.AgenteId,
        ultimos4 = c.Ultimos4,
        origen = c.Origen.ToString(),
        estado = c.Estado.ToString(),
        creadaEn = c.CreadaEn
    };
}

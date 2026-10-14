using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TutorPreClase.Api.Seguridad;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Api.Endpoints;

public sealed record CanjeAccesoPeticion(string Code, string State);
public sealed record AccesoDesarrolloPeticion(Guid UsuarioId);
public sealed record InicioAccesoPeticion(string? Perfil);
public sealed record RegistroPeticion(string Token, string Rol, Guid? ProfesorId);

/// <summary>
/// Pagina de entrada de todos los roles (RF-31): la web pide la URL del proveedor, el
/// usuario inicia sesion alli y la web entrega el codigo de vuelta para recibir su sesion.
/// </summary>
public static class EndpointsAcceso
{
    /// <summary>Política de límite de intentos de las rutas con contraseña.</summary>
    public const string LimiteClave = "clave";

    public static void MapearAcceso(this IEndpointRouteBuilder app, IHostEnvironment entorno)
    {
        var grupo = app.MapGroup("/api/v1/acceso");

        grupo.MapGet("/proveedores", (ConexionIdentidad identidad) => Results.Ok(new
        {
            proveedores = identidad.Disponibles().Select(p => new { id = p.Id, nombre = p.Nombre, configurado = p.Configurado }),
            conClave = identidad.ConClave,
            desarrollo = !entorno.IsProduction()
        })).AllowAnonymous();

        // Cuenta con correo y contraseña (RF-41). Limitadas por IP contra el adivinado de claves.
        grupo.MapPost("/cuenta", async (CuentaNuevaPeticion peticion, ConexionIdentidad identidad, CancellationToken ct) =>
            Results.Ok(await identidad.CrearCuentaAsync(peticion, ct)))
            .AllowAnonymous().RequireRateLimiting(LimiteClave);

        grupo.MapPost("/clave", async (EntradaConClavePeticion peticion, ConexionIdentidad identidad, CancellationToken ct) =>
            Results.Ok(await identidad.EntrarConClaveAsync(peticion, ct)))
            .AllowAnonymous().RequireRateLimiting(LimiteClave);

        // Olvidé mi contraseña (RF-42): con el enlace que generó el administrador.
        grupo.MapPost("/clave/restablecer", async (RestablecerClavePeticion peticion, ConexionIdentidad identidad, CancellationToken ct) =>
            Results.Ok(await identidad.RestablecerClaveAsync(peticion, ct)))
            .AllowAnonymous().RequireRateLimiting(LimiteClave);

        grupo.MapPost("/{proveedor}/inicio", (string proveedor, InicioAccesoPeticion? peticion, ConexionIdentidad identidad) =>
            Results.Ok(new { url = identidad.Iniciar(proveedor, peticion?.Perfil) })).AllowAnonymous();

        grupo.MapPost("/{proveedor}/canje", async (
            string proveedor, CanjeAccesoPeticion peticion, ConexionIdentidad identidad, CancellationToken ct) =>
            Results.Ok(await identidad.CanjearAsync(proveedor, peticion.Code, peticion.State, ct))).AllowAnonymous();

        // Primer acceso: la persona elige si es alumno (y de qué profesor) o profesor (RF-33).
        grupo.MapPost("/registro", async (RegistroPeticion peticion, ConexionIdentidad identidad, CancellationToken ct) =>
            Results.Ok(await identidad.RegistrarAsync(peticion.Token, peticion.Rol, peticion.ProfesorId, ct))).AllowAnonymous();

        // Profesores entre los que elige el alumno: con un registro en curso o ya con sesión.
        grupo.MapGet("/profesores", async (
            HttpContext http, string? registro, ConexionIdentidad identidad, IAppDbContext db, CancellationToken ct) =>
        {
            if (http.User.Identity?.IsAuthenticated != true && !identidad.RegistroVigente(registro))
                return Results.Unauthorized();

            var profesores = await db.Usuarios.AsNoTracking()
                .Where(u => u.Rol == RolUsuario.Docente)
                .OrderBy(u => u.Nombre)
                .Select(u => new
                {
                    id = u.Id,
                    nombre = u.Nombre,
                    cursos = db.Cursos.Where(c => c.DocenteId == u.Id).OrderBy(c => c.Codigo)
                        .Select(c => c.Codigo + " - " + c.Nombre).ToList()
                })
                .ToListAsync(ct);

            return Results.Ok(profesores);
        }).AllowAnonymous();

        // La web lo usa al recargar la pagina para saber si la sesion sigue viva.
        grupo.MapGet("/yo", async (HttpContext http, IAppDbContext db, CancellationToken ct) =>
        {
            var usuario = await db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Id == http.User.Id(), ct);
            return usuario is null
                ? Results.Unauthorized()
                : Results.Ok(new UsuarioSesion(usuario.Id, usuario.Nombre, usuario.Email,
                    http.User.FindFirstValue(ClaimTypes.Role) ?? usuario.Rol.ToString(), ServicioSesion.VistasDe(usuario)));
        }).RequireAuthorization();

        if (entorno.IsProduction()) return;

        // Solo fuera de produccion: entrar como un usuario ya registrado, sin proveedor.
        grupo.MapGet("/desarrollo/usuarios", async (IAppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Usuarios.AsNoTracking()
                .OrderBy(u => u.Rol).ThenBy(u => u.Nombre)
                .Select(u => new { id = u.Id, nombre = u.Nombre, email = u.Email, rol = u.Rol.ToString() })
                .ToListAsync(ct))).AllowAnonymous();

        grupo.MapPost("/desarrollo", async (
            AccesoDesarrolloPeticion peticion, IAppDbContext db, ServicioSesion sesiones, CancellationToken ct) =>
        {
            var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == peticion.UsuarioId, ct);
            return usuario is null ? Results.NotFound() : Results.Ok(sesiones.Emitir(usuario));
        }).AllowAnonymous();
    }
}

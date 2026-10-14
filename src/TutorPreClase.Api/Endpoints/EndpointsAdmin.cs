using Microsoft.EntityFrameworkCore;
using TutorPreClase.Api.Seguridad;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Api.Endpoints;

public sealed record ConfigurarAgentePeticion(string? Modelo, bool? Habilitado, string? Descripcion);

public sealed record UsuarioPeticion(string Email, string Nombre, string Rol, List<Guid> Cursos);

public sealed record LoteUsuariosPeticion(List<UsuarioPeticion> Usuarios);

public static class EndpointsAdmin
{
    public static void MapearAdmin(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/v1/admin")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // RF-33: cada persona elige su rol al entrar; el administrador puede corregirlo o
        // dar de alta a alguien de antemano. El correo es su identidad al iniciar sesion.
        grupo.MapGet("/usuarios", async (IAppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Usuarios.AsNoTracking()
                .OrderBy(u => u.Rol).ThenBy(u => u.Nombre)
                .Select(u => new
                {
                    id = u.Id,
                    email = u.Email,
                    nombre = u.Nombre,
                    rol = u.Rol.ToString(),
                    conClave = u.ClaveHash != null,
                    cursos = u.Matriculas.Select(m => m.CursoId)
                })
                .ToListAsync(ct)));

        // Olvidé mi contraseña (RF-42): sin servicio de correo, el administrador genera el
        // enlace y se lo pasa a la persona. Solo se muestra en esta respuesta.
        grupo.MapPost("/usuarios/{usuarioId:guid}/restablecer-clave", async (
            Guid usuarioId, ConexionIdentidad identidad, CancellationToken ct) =>
            Results.Ok(await identidad.GenerarEnlaceRestablecerAsync(usuarioId, ct)));

        grupo.MapGet("/cursos", async (IAppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Cursos.AsNoTracking()
                .OrderBy(c => c.Codigo)
                .Select(c => new { id = c.Id, codigo = c.Codigo, nombre = c.Nombre })
                .ToListAsync(ct)));

        grupo.MapPut("/usuarios", async (UsuarioPeticion peticion, IAppDbContext db, CancellationToken ct) =>
        {
            var usuario = await GuardarAsync(db, peticion, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = usuario.Id, email = usuario.Email, rol = usuario.Rol.ToString() });
        });

        // Por lista: una fila por persona (correo, nombre, rol), todas al mismo curso.
        grupo.MapPost("/usuarios/lote", async (LoteUsuariosPeticion peticion, IAppDbContext db, CancellationToken ct) =>
        {
            foreach (var fila in peticion.Usuarios)
                await GuardarAsync(db, fila, ct);

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { guardados = peticion.Usuarios.Count });
        });

        grupo.MapGet("/agentes", async (IAppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Agentes.AsNoTracking().ToListAsync(ct)));

        grupo.MapPut("/agentes/{agenteId}", async (
            string agenteId, ConfigurarAgentePeticion peticion, IAppDbContext db, CancellationToken ct) =>
        {
            var agente = await db.Agentes.FirstOrDefaultAsync(a => a.Id == agenteId, ct);
            if (agente is null) return Results.NotFound();

            if (peticion.Modelo is not null) agente.Modelo = peticion.Modelo;
            if (peticion.Habilitado is not null) agente.Habilitado = peticion.Habilitado.Value;
            if (peticion.Descripcion is not null) agente.Descripcion = peticion.Descripcion;

            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                id = agente.Id,
                modelo = agente.Modelo,
                habilitado = agente.Habilitado
            });
        });
    }

    /// <summary>Crea o actualiza por correo y deja sus matriculas igual a las indicadas.</summary>
    private static async Task<Usuario> GuardarAsync(IAppDbContext db, UsuarioPeticion peticion, CancellationToken ct)
    {
        var email = peticion.Email.Trim().ToLowerInvariant();

        if (!email.Contains('@') || email.Length > 320)
            throw new AccesoException("correo_invalido", $"«{peticion.Email}» no es un correo válido.");

        if (!Enum.TryParse<RolUsuario>(peticion.Rol, ignoreCase: true, out var rol))
            throw new AccesoException("rol_invalido", $"El rol «{peticion.Rol}» no existe: usa Alumno, Docente o Admin.");

        var usuario = await db.Usuarios.Include(u => u.Matriculas).FirstOrDefaultAsync(u => u.Email == email, ct);
        if (usuario is null)
        {
            usuario = new Usuario { Email = email };
            db.Usuarios.Add(usuario);
        }

        usuario.Nombre = string.IsNullOrWhiteSpace(peticion.Nombre) ? email : peticion.Nombre.Trim();
        usuario.Rol = rol;

        var cursos = await db.Cursos.Where(c => peticion.Cursos.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);

        foreach (var sobrante in usuario.Matriculas.Where(m => !cursos.Contains(m.CursoId)).ToList())
            db.Matriculas.Remove(sobrante);

        foreach (var cursoId in cursos.Where(c => usuario.Matriculas.All(m => m.CursoId != c)))
            db.Matriculas.Add(new Matricula { UsuarioId = usuario.Id, CursoId = cursoId, RolEnCurso = rol });

        return usuario;
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TutorPreClase.Api.Seguridad;
using TutorPreClase.Api.Sse;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Academico;
using TutorPreClase.Application.Evaluacion;
using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Tutor;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Infrastructure.Contenido;

namespace TutorPreClase.Api.Endpoints;

public sealed record AbrirConversacionPeticion(string AgenteId);
public sealed record MensajePeticion(string Texto);
public sealed record CambiarAgentePeticion(string AgenteId);
public sealed record ElegirProfesorPeticion(Guid? ProfesorId);

public static class EndpointsAlumno
{
    public static void MapearAlumno(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/v1").RequireAuthorization();

        grupo.MapGet("/agentes", async (
            HttpContext http,
            IAppDbContext db,
            IBovedaCredenciales boveda,
            IOptions<OpcionesAgentes> opciones,
            IProveedorLlmFactory proveedores,
            CancellationToken ct) =>
        {
            var conectados = await boveda.AgentesConectadosAsync(http.User.Id(), ct);

            var agentes = await db.Agentes
                .AsNoTracking()
                .Where(a => a.Habilitado)
                .Select(a => new { id = a.Id, nombre = a.NombreVisible, descripcion = a.Descripcion, consola = a.UrlConsola, modelo = a.Modelo })
                .ToListAsync(ct);

            // Se listan los habilitados que tienen proveedor: el alumno necesita ver cual puede
            // conectar, y un agente retirado que siga en la base no debe ofrecerse.
            return Results.Ok(agentes.Where(a => proveedores.Existe(a.id)).Select(a => new
            {
                a.id,
                a.nombre,
                a.descripcion,
                a.consola,
                // El alumno ve qué modelo responde con su cuenta.
                a.modelo,
                // "OAuth": la web ofrece iniciar sesion en lugar del campo para pegar la clave,
                // sea con OpenRouter o con Google (RF-29, RF-37).
                conexion = opciones.Value.TryGetValue(a.id, out var o) && ConexionAgente.EsOAuth(o.Conexion)
                    ? ConexionAgente.OAuth
                    : ConexionAgente.Clave,
                conectado = conectados.Contains(a.id)
            }));
        });

        grupo.MapGet("/alumno/clases", async (HttpContext http, IAppDbContext db, IRelojSistema reloj, CancellationToken ct) =>
        {
            var alumnoId = http.User.Id();

            var cursos = await db.Matriculas
                .AsNoTracking()
                .Where(m => m.UsuarioId == alumnoId)
                .Select(m => m.CursoId)
                .ToListAsync(ct);

            var clases = await db.Clases
                .AsNoTracking()
                .Where(c => cursos.Contains(c.CursoId))
                .OrderBy(c => c.Inicio)
                .Select(c => new
                {
                    claseId = c.Id,
                    titulo = c.Titulo,
                    inicio = c.Inicio,
                    examen = c.Examen == null ? null : new
                    {
                        publicado = c.Examen.Publicado,
                        abreEn = c.Examen.AbreEn,
                        cierraEn = c.Examen.CierraEn
                    }
                })
                .ToListAsync(ct);

            return Results.Ok(clases);
        });

        // El alumno ve solo los cursos del profesor que eligio (RF-33) y puede cambiarlo.
        grupo.MapGet("/alumno/profesor", async (HttpContext http, IAppDbContext db, CancellationToken ct) =>
        {
            var profesorId = await db.Usuarios.Where(u => u.Id == http.User.Id()).Select(u => u.ProfesorId).FirstOrDefaultAsync(ct);
            var profesor = profesorId is null ? null
                : await db.Usuarios.AsNoTracking().Where(u => u.Id == profesorId)
                    .Select(u => new { id = u.Id, nombre = u.Nombre }).FirstOrDefaultAsync(ct);

            return Results.Ok(new { profesor });
        }).RequireAuthorization(p => p.RequireRole(Roles.Alumno));

        grupo.MapPut("/alumno/profesor", async (
            HttpContext http, ElegirProfesorPeticion peticion, ConexionIdentidad identidad, IAppDbContext db, CancellationToken ct) =>
        {
            await identidad.ValidarProfesorAsync(peticion.ProfesorId, ct);

            var alumno = await db.Usuarios.FirstAsync(u => u.Id == http.User.Id(), ct);
            alumno.ProfesorId = peticion.ProfesorId;
            await MatriculasPorProfesor.AlinearAlumnoAsync(db, alumno, ct);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { profesorId = alumno.ProfesorId });
        }).RequireAuthorization(p => p.RequireRole(Roles.Alumno));

        // La web escucha aqui y se actualiza solo cuando el docente cambia el material de
        // un curso del alumno (SDD §6.1). El latido mantiene viva la conexion en proxies.
        grupo.MapGet("/alumno/novedades", async (
            HttpContext http, IAppDbContext db, IAvisosContenido avisos, CancellationToken ct) =>
        {
            var alumnoId = http.User.Id();
            EscritorSse.PrepararCabeceras(http.Response);

            var cambios = avisos.EscucharAsync(ct).GetAsyncEnumerator(ct);
            var siguiente = cambios.MoveNextAsync().AsTask();

            try
            {
                await http.Response.WriteAsync(": conectado\n\n", ct);
                await http.Response.Body.FlushAsync(ct);

                while (true)
                {
                    if (await Task.WhenAny(siguiente, Task.Delay(TimeSpan.FromSeconds(25), ct)) != siguiente)
                    {
                        await http.Response.WriteAsync(": latido\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        continue;
                    }

                    if (!await siguiente) break;
                    var cambio = cambios.Current;

                    if (await db.Matriculas.AnyAsync(m => m.UsuarioId == alumnoId && m.CursoId == cambio.CursoId, ct))
                    {
                        var datos = JsonSerializer.Serialize(new { cursoId = cambio.CursoId, clases = cambio.Clases },
                            new JsonSerializerOptions(JsonSerializerDefaults.Web));

                        await http.Response.WriteAsync($"event: contenido\ndata: {datos}\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                    }

                    siguiente = cambios.MoveNextAsync().AsTask();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // El alumno cerro la pagina.
            }
            finally
            {
                // Un iterador async no se puede liberar con un MoveNextAsync en curso: se
                // espera a que termine (al cerrar la pagina lo cancela el mismo token).
                try { await siguiente; } catch (OperationCanceledException) { }
                await cambios.DisposeAsync();
            }

            return Results.Empty;
        });

        grupo.MapPost("/clases/{claseId:guid}/conversacion", async (
            Guid claseId,
            AbrirConversacionPeticion peticion,
            HttpContext http,
            IAgenteTutorService tutor,
            CancellationToken ct) =>
        {
            var conversacion = await tutor.AbrirConversacionAsync(claseId, http.User.Id(), peticion.AgenteId, ct);

            return Results.Ok(new
            {
                conversacionId = conversacion.Id,
                modo = conversacion.Modo.ToString(),
                agenteId = conversacion.AgenteId
            });
        });

        grupo.MapPost("/conversaciones/{conversacionId:guid}/mensajes", async (
            Guid conversacionId,
            [FromBody] MensajePeticion peticion,
            HttpContext http,
            IAgenteTutorService tutor,
            IAppDbContext db,
            CancellationToken ct) =>
        {
            if (!await EsDueñoAsync(db, conversacionId, http.User.Id(), ct))
                return Results.Forbid();

            EscritorSse.PrepararCabeceras(http.Response);

            await foreach (var evento in tutor.ProcesarMensajeAsync(conversacionId, peticion.Texto, ct))
                await EscritorSse.EscribirAsync(http.Response, evento, ct);

            return Results.Empty;
        });

        grupo.MapGet("/conversaciones/{conversacionId:guid}/mensajes", async (
            Guid conversacionId, HttpContext http, IAppDbContext db, CancellationToken ct) =>
        {
            if (!await EsDueñoAsync(db, conversacionId, http.User.Id(), ct))
                return Results.Forbid();

            var mensajes = await db.Mensajes
                .AsNoTracking()
                .Where(m => m.ConversacionId == conversacionId && m.Rol != RolMensaje.Herramienta)
                .OrderBy(m => m.CreadoEn)
                .Select(m => new
                {
                    rol = m.Rol.ToString(),
                    texto = m.Texto,
                    agenteId = m.AgenteId,
                    fuentes = m.Fuentes,
                    usaAmpliacion = m.UsaAmpliacion,
                    creadoEn = m.CreadoEn
                })
                .ToListAsync(ct);

            return Results.Ok(mensajes);
        });

        grupo.MapPut("/conversaciones/{conversacionId:guid}/agente", async (
            Guid conversacionId,
            CambiarAgentePeticion peticion,
            HttpContext http,
            IAgenteTutorService tutor,
            IAppDbContext db,
            CancellationToken ct) =>
        {
            if (!await EsDueñoAsync(db, conversacionId, http.User.Id(), ct))
                return Results.Forbid();

            var conversacion = await tutor.CambiarAgenteAsync(conversacionId, peticion.AgenteId, ct);
            return Results.Ok(new { agenteId = conversacion.AgenteId });
        });

        grupo.MapPost("/conversaciones/{conversacionId:guid}/examen", async (
            Guid conversacionId, HttpContext http, IAgenteTutorService tutor, IAppDbContext db, CancellationToken ct) =>
        {
            if (!await EsDueñoAsync(db, conversacionId, http.User.Id(), ct))
                return Results.Forbid();

            var conversacion = await tutor.IniciarExamenAsync(conversacionId, ct);

            return Results.Ok(new
            {
                conversacionId = conversacion.Id,
                intentoId = conversacion.IntentoId,
                modo = conversacion.Modo.ToString()
            });
        });

        grupo.MapGet("/intentos/{intentoId:guid}/resultado", async (
            Guid intentoId, HttpContext http, IServicioExamen examen, IAppDbContext db, CancellationToken ct) =>
        {
            var intento = await db.Intentos.AsNoTracking().FirstOrDefaultAsync(i => i.Id == intentoId, ct);
            if (intento is null) return Results.NotFound();
            if (intento.AlumnoId != http.User.Id() && !http.User.EsDocenteOAdmin()) return Results.Forbid();

            return Results.Ok(await examen.ResultadoAsync(intentoId, ct));
        });
    }

    private static Task<bool> EsDueñoAsync(IAppDbContext db, Guid conversacionId, Guid alumnoId, CancellationToken ct) =>
        db.Conversaciones.AnyAsync(c => c.Id == conversacionId && c.AlumnoId == alumnoId, ct);
}

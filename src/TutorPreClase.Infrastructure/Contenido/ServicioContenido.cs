using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Contenido;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Infrastructure.Contenido;

public interface IAlmacenArchivos
{
    Task<string> GuardarAsync(string ruta, Stream contenido, CancellationToken ct = default);
    Task<Stream> AbrirAsync(string ruta, CancellationToken ct = default);
    Task<bool> ExisteAsync(string ruta, CancellationToken ct = default);
    Task EliminarAsync(string ruta, CancellationToken ct = default);
}

public sealed record ResultadoSubida(Guid ArchivoId, string Nombre, bool Duplicado);

public interface IServicioContenido
{
    Task<ResultadoSubida> SubirAsync(Guid cursoId, Guid claseId, string nombre, Stream contenido, CancellationToken ct = default);
    Task ProcesarAsync(Guid archivoId, CancellationToken ct = default);
    Task EliminarAsync(Guid archivoId, CancellationToken ct = default);
}

/// <summary>
/// Ingesta de la carpeta de clase (SDD §6.1): guarda el archivo, calcula su SHA-256 y
/// deja el texto listo pagina por pagina. La API no espera a la extraccion.
/// </summary>
public sealed class ServicioContenido(
    IAppDbContext db,
    IAlmacenArchivos almacen,
    IExtractorTextoFactory extractores,
    IContextoClaseService contexto,
    IColaCondensacion condensacion,
    IRelojSistema reloj,
    ILogger<ServicioContenido> log) : IServicioContenido
{
    public async Task<ResultadoSubida> SubirAsync(
        Guid cursoId, Guid claseId, string nombre, Stream contenidoArchivo, CancellationToken ct = default)
    {
        using var memoria = new MemoryStream();
        await contenidoArchivo.CopyToAsync(memoria, ct);
        memoria.Position = 0;

        var hash = Convert.ToHexString(await SHA256.HashDataAsync(memoria, ct)).ToLowerInvariant();
        memoria.Position = 0;

        var existente = await db.Archivos
            .FirstOrDefaultAsync(a => a.ClaseId == claseId && a.HashSha256 == hash, ct);

        if (existente is not null)
        {
            // El texto ya se extrajo, pero el archivo puede faltar en el almacén (un despliegue
            // nuevo empieza con el disco vacío): se repone para que se pueda descargar.
            if (!await almacen.ExisteAsync(existente.BlobUrl, ct))
                await almacen.GuardarAsync(existente.BlobUrl, memoria, ct);

            return new ResultadoSubida(existente.Id, existente.Nombre, Duplicado: true);
        }

        var ruta = $"cursos/{cursoId}/clases/{claseId}/{nombre}";
        var url = await almacen.GuardarAsync(ruta, memoria, ct);

        var archivo = new ArchivoContenido
        {
            ClaseId = claseId,
            Nombre = nombre,
            Tipo = Path.GetExtension(nombre).TrimStart('.').ToLowerInvariant(),
            BlobUrl = url,
            HashSha256 = hash,
            Estado = EstadoArchivo.Pendiente,
            CreadoEn = reloj.Ahora
        };

        db.Archivos.Add(archivo);
        await db.SaveChangesAsync(ct);

        return new ResultadoSubida(archivo.Id, archivo.Nombre, Duplicado: false);
    }

    public async Task ProcesarAsync(Guid archivoId, CancellationToken ct = default)
    {
        var archivo = await db.Archivos
            .Include(a => a.Paginas)
            .FirstOrDefaultAsync(a => a.Id == archivoId, ct);

        if (archivo is null)
        {
            log.LogWarning("Archivo {Archivo} ya no existe; se omite la extraccion", archivoId);
            return;
        }

        archivo.Estado = EstadoArchivo.Procesando;
        archivo.Error = null;
        await db.SaveChangesAsync(ct);

        try
        {
            var extractor = extractores.Para(archivo.Nombre)
                ?? throw new NotSupportedException($"No hay extractor para '{archivo.Nombre}'.");

            await using var flujo = await almacen.AbrirAsync(archivo.BlobUrl, ct);
            var paginas = extractor.Extraer(flujo);

            db.Paginas.RemoveRange(archivo.Paginas);

            var utiles = paginas
                .Where(p => !string.IsNullOrWhiteSpace(p.Texto))
                .Select(p => new PaginaContenido
                {
                    ArchivoId = archivo.Id,
                    ClaseId = archivo.ClaseId,
                    Pagina = p.Pagina,
                    Texto = p.Texto.Trim(),
                    Tokens = ContadorTokens.Estimar(p.Texto)
                })
                .ToList();

            if (utiles.Count == 0)
            {
                // PDF escaneado o archivo sin texto: el docente necesita saberlo (SDD §9.3).
                archivo.Estado = EstadoArchivo.Error;
                archivo.Error = "No se encontro texto. Si es un PDF escaneado, requiere OCR (fuera de v1).";
            }
            else
            {
                db.Paginas.AddRange(utiles);
                archivo.Estado = EstadoArchivo.Listo;
                archivo.Condensado = EstadoCondensado.Pendiente;
                archivo.ErrorCondensado = null;
            }

            await db.SaveChangesAsync(ct);

            // Texto nuevo, apuntes nuevos: la IA los rehace en segundo plano (SDD §6.2).
            if (archivo.Estado == EstadoArchivo.Listo) await condensacion.EncolarAsync(archivo.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Fallo la extraccion del archivo {Archivo}", archivoId);
            archivo.Estado = EstadoArchivo.Error;
            archivo.Error = ex.Message;
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            contexto.Invalidar(archivo.ClaseId);
        }
    }

    public async Task EliminarAsync(Guid archivoId, CancellationToken ct = default)
    {
        var archivo = await db.Archivos.FirstOrDefaultAsync(a => a.Id == archivoId, ct);
        if (archivo is null) return;

        // Las paginas caen en cascada; el contexto de clase se rearma (SDD §6.1).
        db.Archivos.Remove(archivo);
        await db.SaveChangesAsync(ct);
        await almacen.EliminarAsync(archivo.BlobUrl, ct);

        contexto.Invalidar(archivo.ClaseId);
    }
}

/// <summary>
/// Almacen local en disco. En produccion se cambia por Blob Storage o S3 sin tocar
/// el resto del sistema.
/// </summary>
public sealed class AlmacenArchivosLocal(string raiz) : IAlmacenArchivos
{
    public async Task<string> GuardarAsync(string ruta, Stream contenido, CancellationToken ct = default)
    {
        var destino = Combinar(ruta);
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

        await using var archivo = File.Create(destino);
        await contenido.CopyToAsync(archivo, ct);

        return ruta;
    }

    public Task<Stream> AbrirAsync(string ruta, CancellationToken ct = default) =>
        Task.FromResult<Stream>(File.OpenRead(Combinar(ruta)));

    public Task<bool> ExisteAsync(string ruta, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(Combinar(ruta)));

    public Task EliminarAsync(string ruta, CancellationToken ct = default)
    {
        var destino = Combinar(ruta);
        if (File.Exists(destino)) File.Delete(destino);
        return Task.CompletedTask;
    }

    private string Combinar(string ruta)
    {
        var completa = Path.GetFullPath(Path.Combine(raiz, ruta));
        var baseCompleta = Path.GetFullPath(raiz);

        // Evita que un nombre de archivo con ".." escriba fuera del almacen.
        if (!completa.StartsWith(baseCompleta, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Ruta de archivo fuera del almacen.");

        return completa;
    }
}

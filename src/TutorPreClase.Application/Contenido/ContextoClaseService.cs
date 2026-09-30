using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Application.Contenido;

public interface IContextoClaseService
{
    Task<ContextoClase> ObtenerAsync(Guid claseId, CancellationToken ct = default);

    /// <summary>El material para una pregunta: completo, o sus páginas pertinentes si es grande.</summary>
    Task<ContextoClase> ObtenerParaPreguntaAsync(Guid claseId, string consulta, CancellationToken ct = default);

    void Invalidar(Guid claseId);
}

/// <summary>
/// Arma el contexto de clase una vez y lo cachea (SDD §6.2). La entrada guarda el hash
/// del contenido: si cambia un archivo, se rearma aunque nadie llame a Invalidar.
/// </summary>
public sealed class ContextoClaseService(
    IAppDbContext db,
    IMemoryCache cache,
    IOptions<OpcionesContextoClase> opciones) : IContextoClaseService
{
    private readonly OpcionesContextoClase _opciones = opciones.Value;

    private sealed record Entrada(string Hash, ContextoClase Contexto);

    public async Task<ContextoClase> ObtenerAsync(Guid claseId, CancellationToken ct = default)
    {
        var clase = await db.Clases.AsNoTracking().FirstOrDefaultAsync(c => c.Id == claseId, ct);
        if (clase is null) return ContextoClase.Vacio;

        var archivos = await db.Archivos
            .AsNoTracking()
            .Where(a => a.ClaseId == claseId && a.Estado == EstadoArchivo.Listo)
            .Include(a => a.Paginas)
            .ToListAsync(ct);

        var hash = HashContenido(archivos);

        if (cache.TryGetValue(Clave(claseId), out Entrada? entrada) && entrada?.Hash == hash)
            return entrada.Contexto;

        var contexto = ArmadorContextoClase.Armar(clase.Titulo, archivos, _opciones);

        cache.Set(Clave(claseId), new Entrada(hash, contexto), new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromHours(2)
        });

        return contexto;
    }

    public async Task<ContextoClase> ObtenerParaPreguntaAsync(Guid claseId, string consulta, CancellationToken ct = default) =>
        SelectorPaginas.Seleccionar(await ObtenerAsync(claseId, ct), consulta, _opciones);

    public void Invalidar(Guid claseId) => cache.Remove(Clave(claseId));

    private static string Clave(Guid claseId) => $"contexto:{claseId}";

    /// <summary>Cambia si cambia cualquier archivo de la clase o su número de páginas.</summary>
    private static string HashContenido(IEnumerable<ArchivoContenido> archivos)
    {
        var partes = archivos
            .OrderBy(a => a.Nombre, StringComparer.OrdinalIgnoreCase)
            .Select(a => $"{a.Nombre}:{a.HashSha256}:{a.Paginas.Count}");

        var texto = string.Join('|', partes);
        if (texto.Length == 0) return "vacio";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto)))[..16];
    }
}

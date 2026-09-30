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
    /// <summary>Lo que lee el tutor: el material condensado donde lo haya (SDD §6.2).</summary>
    Task<ContextoClase> ObtenerAsync(Guid claseId, CancellationToken ct = default);

    /// <summary>El texto original: para generar el examen, donde el detalle manda.</summary>
    Task<ContextoClase> ObtenerOriginalAsync(Guid claseId, CancellationToken ct = default);

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

    public Task<ContextoClase> ObtenerAsync(Guid claseId, CancellationToken ct = default) =>
        ArmarAsync(claseId, condensado: true, ct);

    public Task<ContextoClase> ObtenerOriginalAsync(Guid claseId, CancellationToken ct = default) =>
        ArmarAsync(claseId, condensado: false, ct);

    private async Task<ContextoClase> ArmarAsync(Guid claseId, bool condensado, CancellationToken ct)
    {
        var clase = await db.Clases.AsNoTracking().FirstOrDefaultAsync(c => c.Id == claseId, ct);
        if (clase is null) return ContextoClase.Vacio;

        var archivos = await db.Archivos
            .AsNoTracking()
            .Where(a => a.ClaseId == claseId && a.Estado == EstadoArchivo.Listo)
            .Include(a => a.Paginas)
            .ToListAsync(ct);

        var hash = HashContenido(archivos);

        if (cache.TryGetValue(Clave(claseId, condensado), out Entrada? entrada) && entrada?.Hash == hash)
            return entrada.Contexto;

        var contexto = ArmadorContextoClase.Armar(clase.Titulo, archivos, _opciones, condensado);

        cache.Set(Clave(claseId, condensado), new Entrada(hash, contexto), new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromHours(2)
        });

        return contexto;
    }

    public async Task<ContextoClase> ObtenerParaPreguntaAsync(Guid claseId, string consulta, CancellationToken ct = default) =>
        SelectorPaginas.Seleccionar(await ObtenerAsync(claseId, ct), consulta, _opciones);

    public void Invalidar(Guid claseId)
    {
        cache.Remove(Clave(claseId, condensado: true));
        cache.Remove(Clave(claseId, condensado: false));
    }

    private static string Clave(Guid claseId, bool condensado) =>
        $"contexto:{claseId}:{(condensado ? "condensado" : "original")}";

    /// <summary>
    /// Cambia si cambia cualquier archivo de la clase, su número de páginas, sus apuntes
    /// condensados o la elección del profesor de usar el original.
    /// </summary>
    private static string HashContenido(IEnumerable<ArchivoContenido> archivos)
    {
        var partes = archivos
            .OrderBy(a => a.Nombre, StringComparer.OrdinalIgnoreCase)
            .Select(a => $"{a.Nombre}:{a.HashSha256}:{a.Paginas.Count}:{a.UsarOriginal}:" +
                         $"{a.Paginas.Count(p => p.TextoCondensado != null)}:{a.Paginas.Sum(p => p.TokensCondensado)}");

        var texto = string.Join('|', partes);
        if (texto.Length == 0) return "vacio";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto)))[..16];
    }
}

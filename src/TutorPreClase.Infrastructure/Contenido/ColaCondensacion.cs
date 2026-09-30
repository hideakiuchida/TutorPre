using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Contenido;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Infrastructure.Contenido;

/// <summary>Cola en proceso de archivos a condensar, con el mismo patrón que la de extracción.</summary>
public sealed class ColaCondensacionEnMemoria : IColaCondensacion
{
    private readonly Channel<Guid> _canal = Channel.CreateUnbounded<Guid>();

    public ValueTask EncolarAsync(Guid archivoId, CancellationToken ct = default) =>
        _canal.Writer.WriteAsync(archivoId, ct);

    public IAsyncEnumerable<Guid> LeerAsync(CancellationToken ct) =>
        _canal.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Condensa en segundo plano (SDD §6.2), de a un archivo: el cupo gratuito del profesor es
/// por minuto y por día. Al arrancar retoma lo que quedó pendiente o a medias. Si el modelo
/// gratuito está saturado, vuelve a intentarlo solo, cada vez más espaciado.
/// </summary>
public sealed class ServicioCondensacionEnSegundoPlano(
    IColaCondensacion cola,
    IServiceScopeFactory ambitos,
    ILogger<ServicioCondensacionEnSegundoPlano> log) : BackgroundService
{
    /// <summary>Esperas antes de cada reintento por saturación; después, queda en Error.</summary>
    private static readonly TimeSpan[] Esperas =
    [
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(60)
    ];

    private readonly Dictionary<Guid, int> _reintentos = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await RetomarPendientesAsync(ct);

        await foreach (var archivoId in cola.LeerAsync(ct))
        {
            try
            {
                using var ambito = ambitos.CreateScope();
                var resultado = await ambito.ServiceProvider.GetRequiredService<ICondensadorMaterial>().CondensarAsync(archivoId, ct);

                if (resultado == ResultadoCondensacion.Reintentar) await ProgramarReintentoAsync(ambito, archivoId, ct);
                else _reintentos.Remove(archivoId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // La página sigue con su original; la cola no debe caerse por un archivo.
                log.LogError(ex, "Fallo la condensacion del archivo {Archivo}", archivoId);
            }
        }
    }

    private async Task ProgramarReintentoAsync(IServiceScope ambito, Guid archivoId, CancellationToken ct)
    {
        var intento = _reintentos.GetValueOrDefault(archivoId);

        if (intento >= Esperas.Length)
        {
            _reintentos.Remove(archivoId);
            var db = ambito.ServiceProvider.GetRequiredService<IAppDbContext>();
            var archivo = await db.Archivos.FirstOrDefaultAsync(a => a.Id == archivoId, ct);
            if (archivo is null) return;

            archivo.Condensado = EstadoCondensado.Error;
            archivo.ErrorCondensado = "El modelo gratuito siguió saturado. Pulsa «Condensar de nuevo» más tarde.";
            await db.SaveChangesAsync(ct);
            return;
        }

        _reintentos[archivoId] = intento + 1;
        log.LogInformation("Modelo saturado: el archivo {Archivo} se condensará en {Minutos} min", archivoId, Esperas[intento].TotalMinutes);

        // No bloquea la cola: los demás archivos siguen mientras este espera.
        _ = Task.Delay(Esperas[intento], ct)
            .ContinueWith(_ => cola.EncolarAsync(archivoId, ct).AsTask(), ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default)
            .Unwrap();
    }

    private async Task RetomarPendientesAsync(CancellationToken ct)
    {
        try
        {
            using var ambito = ambitos.CreateScope();
            var db = ambito.ServiceProvider.GetRequiredService<IAppDbContext>();

            var pendientes = await db.Archivos
                .Where(a => a.Estado == EstadoArchivo.Listo &&
                            (a.Condensado == EstadoCondensado.Pendiente || a.Condensado == EstadoCondensado.Procesando))
                .Select(a => a.Id)
                .ToListAsync(ct);

            foreach (var id in pendientes) await cola.EncolarAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin base todavía (primer arranque): lo nuevo se encola al extraerlo.
            log.LogWarning(ex, "No se pudieron retomar las condensaciones pendientes");
        }
    }
}

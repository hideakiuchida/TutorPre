using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Llm;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Infrastructure.Llm;

/// <summary>
/// Cifra con Data Protection usando un proposito distinto por usuario: la credencial
/// de un alumno no se puede descifrar en el contexto de otro, aunque se filtre la fila.
/// </summary>
public sealed class BovedaCredenciales(
    IAppDbContext db,
    IDataProtectionProvider protector,
    IValidadorCredencial validador,
    IRelojSistema reloj,
    ILogger<BovedaCredenciales> log) : IBovedaCredenciales
{
    // Solo las de agentes que siguen habilitados: la cuenta de un agente retirado queda
    // guardada, pero no cuenta como IA conectada ni se ofrece para conversar.
    public async Task<IReadOnlyList<CredencialResumen>> ListarAsync(Guid usuarioId, CancellationToken ct = default) =>
        await db.Credenciales
            .AsNoTracking()
            .Where(c => c.UsuarioId == usuarioId && db.Agentes.Any(a => a.Id == c.AgenteId && a.Habilitado))
            .OrderBy(c => c.AgenteId)
            .Select(c => new CredencialResumen(c.AgenteId, c.Ultimos4, c.Origen, c.Estado, c.CreadaEn, c.UltimoUsoEn))
            .ToListAsync(ct);

    public async Task<CredencialResumen> ConectarAsync(
        Guid usuarioId,
        string agenteId,
        string clave,
        OrigenCredencial origen = OrigenCredencial.Pegada,
        CancellationToken ct = default)
    {
        clave = clave.Trim();

        if (clave.Length < 16)
            throw new CredencialException("clave_invalida", "Esa credencial no tiene la forma de una clave de API.");

        var agente = await db.Agentes.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agenteId && a.Habilitado, ct)
            ?? throw new CredencialException("agente_no_disponible", "Ese agente no está habilitado.");

        // Se valida contra el proveedor antes de guardarla (RF-25).
        if (!await validador.EsUsableAsync(agente, clave, ct))
            throw new CredencialException("clave_rechazada",
                $"{agente.NombreVisible} rechazó esa credencial. Revísala y vuelve a intentarlo.");

        var credencial = await db.Credenciales
            .FirstOrDefaultAsync(c => c.UsuarioId == usuarioId && c.AgenteId == agenteId, ct);

        if (credencial is null)
        {
            credencial = new CredencialAgente { UsuarioId = usuarioId, AgenteId = agenteId, CreadaEn = reloj.Ahora };
            db.Credenciales.Add(credencial);
        }

        credencial.ClaveCifrada = Protector(usuarioId).Protect(clave);
        credencial.Ultimos4 = clave[^4..];
        credencial.Origen = origen;
        credencial.Estado = EstadoCredencial.Valida;
        credencial.UltimoUsoEn = null;

        await db.SaveChangesAsync(ct);

        // Se registra el hecho, nunca el valor (SDD §9.1).
        log.LogInformation("Credencial de {Agente} conectada por el usuario {Usuario}", agenteId, usuarioId);

        return new CredencialResumen(
            credencial.AgenteId, credencial.Ultimos4, credencial.Origen, credencial.Estado,
            credencial.CreadaEn, credencial.UltimoUsoEn);
    }

    public async Task DesconectarAsync(Guid usuarioId, string agenteId, CancellationToken ct = default)
    {
        var credencial = await db.Credenciales
            .FirstOrDefaultAsync(c => c.UsuarioId == usuarioId && c.AgenteId == agenteId, ct);

        if (credencial is null) return;

        db.Credenciales.Remove(credencial);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Credencial de {Agente} desconectada por el usuario {Usuario}", agenteId, usuarioId);
    }

    public async Task<string?> ClaveParaAsync(Guid usuarioId, string agenteId, CancellationToken ct = default)
    {
        var credencial = await db.Credenciales
            .FirstOrDefaultAsync(c => c.UsuarioId == usuarioId && c.AgenteId == agenteId, ct);

        if (credencial is null || credencial.Estado == EstadoCredencial.Invalida) return null;

        string clave;
        try
        {
            clave = Protector(usuarioId).Unprotect(credencial.ClaveCifrada);
        }
        catch (Exception ex)
        {
            // Clave de cifrado rotada o fila manipulada: se obliga a reconectar.
            log.LogWarning(ex, "No se pudo descifrar la credencial de {Agente} del usuario {Usuario}", agenteId, usuarioId);
            credencial.Estado = EstadoCredencial.Invalida;
            await db.SaveChangesAsync(ct);
            return null;
        }

        credencial.UltimoUsoEn = reloj.Ahora;
        await db.SaveChangesAsync(ct);

        return clave;
    }

    public async Task<IReadOnlyCollection<string>> AgentesConectadosAsync(Guid usuarioId, CancellationToken ct = default) =>
        await db.Credenciales
            .AsNoTracking()
            .Where(c => c.UsuarioId == usuarioId && c.Estado == EstadoCredencial.Valida &&
                        db.Agentes.Any(a => a.Id == c.AgenteId && a.Habilitado))
            .Select(c => c.AgenteId)
            .ToListAsync(ct);

    public async Task MarcarInvalidaAsync(Guid usuarioId, string agenteId, CancellationToken ct = default)
    {
        var credencial = await db.Credenciales
            .FirstOrDefaultAsync(c => c.UsuarioId == usuarioId && c.AgenteId == agenteId, ct);

        if (credencial is null || credencial.Estado == EstadoCredencial.Invalida) return;

        credencial.Estado = EstadoCredencial.Invalida;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Credencial de {Agente} del usuario {Usuario} marcada como invalida", agenteId, usuarioId);
    }

    private IDataProtector Protector(Guid usuarioId) =>
        protector.CreateProtector("TutorPreClase.Credenciales", usuarioId.ToString());
}

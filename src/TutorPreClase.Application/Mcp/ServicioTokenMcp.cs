using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Application.Mcp;

public sealed record TokenMcpResumen(string Ultimos4, DateTimeOffset CreadoEn, DateTimeOffset? UltimoUsoEn);

/// <summary>El token recién generado: es la única vez que sale en claro.</summary>
public sealed record TokenMcpNuevo(string Token, TokenMcpResumen Resumen);

public sealed record UsuarioMcp(Guid Id, RolUsuario Rol);

public interface IServicioTokenMcp
{
    Task<TokenMcpResumen?> ActualAsync(Guid usuarioId, CancellationToken ct = default);

    /// <summary>Genera uno nuevo y revoca el anterior: hay un solo token vivo por alumno.</summary>
    Task<TokenMcpNuevo> GenerarAsync(Guid usuarioId, CancellationToken ct = default);

    Task RevocarAsync(Guid usuarioId, CancellationToken ct = default);

    /// <summary>El dueño de un token vivo, o null si no existe o está revocado.</summary>
    Task<UsuarioMcp?> ValidarAsync(string token, CancellationToken ct = default);
}

/// <summary>
/// Tokens personales para usar el Tutor desde el asistente del alumno como servidor MCP
/// (RF-39). Se guarda solo el SHA-256: aunque se filtre la tabla, no sirve para entrar.
/// </summary>
public sealed class ServicioTokenMcp(IAppDbContext db, IRelojSistema reloj) : IServicioTokenMcp
{
    /// <summary>Distingue el token del de sesión y deja que los escáneres de secretos lo reconozcan.</summary>
    public const string Prefijo = "tpc_mcp_";

    public async Task<TokenMcpResumen?> ActualAsync(Guid usuarioId, CancellationToken ct = default) =>
        await db.TokensMcp.AsNoTracking()
            .Where(t => t.UsuarioId == usuarioId && t.RevocadoEn == null)
            .Select(t => new TokenMcpResumen(t.Ultimos4, t.CreadoEn, t.UltimoUsoEn))
            .FirstOrDefaultAsync(ct);

    public async Task<TokenMcpNuevo> GenerarAsync(Guid usuarioId, CancellationToken ct = default)
    {
        await RevocarVivosAsync(usuarioId, ct);

        var token = Prefijo + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var registro = new TokenMcp
        {
            UsuarioId = usuarioId,
            Hash = Hash(token),
            Ultimos4 = token[^4..],
            CreadoEn = reloj.Ahora
        };

        db.TokensMcp.Add(registro);
        await db.SaveChangesAsync(ct);

        return new TokenMcpNuevo(token, new TokenMcpResumen(registro.Ultimos4, registro.CreadoEn, null));
    }

    public async Task RevocarAsync(Guid usuarioId, CancellationToken ct = default)
    {
        await RevocarVivosAsync(usuarioId, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<UsuarioMcp?> ValidarAsync(string token, CancellationToken ct = default)
    {
        if (!token.StartsWith(Prefijo, StringComparison.Ordinal) || token.Length > 128) return null;

        var hash = Hash(token);
        var registro = await db.TokensMcp
            .Include(t => t.Usuario)
            .FirstOrDefaultAsync(t => t.Hash == hash && t.RevocadoEn == null, ct);

        if (registro?.Usuario is null) return null;

        // El alumno ve cuándo lo usó su asistente por última vez; basta con precisión de minutos.
        if (registro.UltimoUsoEn is null || reloj.Ahora - registro.UltimoUsoEn > TimeSpan.FromMinutes(1))
        {
            registro.UltimoUsoEn = reloj.Ahora;
            await db.SaveChangesAsync(ct);
        }

        return new UsuarioMcp(registro.UsuarioId, registro.Usuario.Rol);
    }

    private async Task RevocarVivosAsync(Guid usuarioId, CancellationToken ct)
    {
        var vivos = await db.TokensMcp.Where(t => t.UsuarioId == usuarioId && t.RevocadoEn == null).ToListAsync(ct);
        foreach (var t in vivos) t.RevocadoEn = reloj.Ahora;
    }

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

using Microsoft.EntityFrameworkCore;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Infrastructure.Persistencia;

/// <summary>
/// Agente de fabrica (SDD §5.8): OpenRouter, la unica via para que el alumno conecte su
/// cuenta. Se agrega si falta, sin tocar el existente: el administrador puede haber
/// cambiado su modelo.
/// </summary>
public static class DatosIniciales
{
    /// <summary>
    /// Modelos que el proveedor retiró, con el que los reemplaza. Solo se cambia un agente
    /// que todavía tiene el retirado: un modelo que eligió el administrador no se toca.
    /// </summary>
    private static readonly Dictionary<string, string> Retirados = new()
    {
        // Google: "gemini-2.5-flash is no longer available to new users" (2026-09-30).
        ["gemini-2.5-flash"] = "gemini-3.8-flash"
    };

    public static async Task SembrarAsync(AppDbContext db, CancellationToken ct = default)
    {
        var agentes = await db.Agentes.ToListAsync(ct);

        foreach (var agente in agentes)
            if (Retirados.TryGetValue(agente.Modelo, out var nuevo)) agente.Modelo = nuevo;

        var existentes = agentes.Select(a => a.Id).ToHashSet();
        db.Agentes.AddRange(Fabrica().Where(a => !existentes.Contains(a.Id)));

        await db.SaveChangesAsync(ct);
    }

    private static IEnumerable<AgenteIA> Fabrica() =>
    [
        // Sin consola: la clave llega por OAuth al iniciar sesion (RF-24, RF-29).
        new AgenteIA
        {
            Id = "openrouter",
            NombreVisible = "OpenRouter (gratis)",
            Proveedor = "OpenRouter",
            Modelo = "qwen/qwen3.8-27b:free",
            BaseUrl = "https://openrouter.ai",
            Descripcion = "Sin costo: entra con tu cuenta de OpenRouter. Tiene un límite de mensajes por día.",
            Habilitado = true
        },
        // Con la cuenta de Google del usuario (RF-37); el uso cuenta en el proyecto de la app.
        new AgenteIA
        {
            Id = "gemini",
            NombreVisible = "Gemini (tu cuenta de Google)",
            Proveedor = "Google",
            Modelo = "gemini-3.8-flash",
            BaseUrl = "https://generativelanguage.googleapis.com",
            Descripcion = "Entra con tu cuenta de Google. Sin claves ni tarjeta.",
            Habilitado = true
        }
    ];
}

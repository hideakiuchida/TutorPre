using Microsoft.EntityFrameworkCore;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Infrastructure.Persistencia;

/// <summary>
/// Agentes de fabrica (SDD §5.8). Se agregan si faltan, sin tocar los existentes: el
/// administrador puede haber cambiado su modelo. Los que ya no estan vigentes en la
/// configuracion quedan deshabilitados: no se ofrecen ni se usan, aunque sigan en la base
/// porque de ellos cuelgan conversaciones e intentos antiguos.
/// </summary>
public static class DatosIniciales
{
    /// <summary>
    /// Modelos que se dejaron de usar, con el que los reemplaza: los que el proveedor retiró y
    /// los que se cambiaron por el más económico de cada API (2026-10-12). Solo se cambia un
    /// agente que todavía tiene el anterior: un modelo que eligió el administrador no se toca.
    /// </summary>
    private static readonly Dictionary<string, string> Retirados = new()
    {
        // Google: "gemini-2.5-flash is no longer available to new users" (2026-09-30).
        ["gemini-2.5-flash"] = "gemini-3.1-flash-lite",
        ["gemini-3.8-flash"] = "gemini-3.1-flash-lite",
        ["gpt-5-mini"] = "gpt-6-luna",
        ["claude-haiku-4-5"] = "claude-haiku-5-5"
    };

    public static async Task SembrarAsync(
        AppDbContext db, IEnumerable<string>? vigentes = null, CancellationToken ct = default)
    {
        var enUso = vigentes?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var agentes = await db.Agentes.ToListAsync(ct);

        foreach (var agente in agentes)
            if (Retirados.TryGetValue(agente.Modelo, out var nuevo)) agente.Modelo = nuevo;

        var existentes = agentes.Select(a => a.Id).ToHashSet();
        var nuevos = Fabrica().Where(a => !existentes.Contains(a.Id)).ToList();
        db.Agentes.AddRange(nuevos);

        // Un agente que la configuracion ya no trae, o trae con "Habilitado": false, deja de
        // ofrecerse aunque existiera en la base con cuentas conectadas. Sin lista (pruebas)
        // no se deshabilita ninguno.
        if (enUso is not null)
            foreach (var agente in agentes.Concat(nuevos))
                if (!enUso.Contains(agente.Id)) agente.Habilitado = false;

        await db.SaveChangesAsync(ct);
    }

    private static IEnumerable<AgenteIA> Fabrica() =>
    [
        // Con la cuenta de Google del usuario (RF-37); el uso cuenta en el proyecto de la app.
        new AgenteIA
        {
            Id = "gemini",
            NombreVisible = "Gemini (tu cuenta de Google)",
            Proveedor = "Google",
            Modelo = "gemini-3.1-flash-lite",
            BaseUrl = "https://generativelanguage.googleapis.com",
            Descripcion = "Entra con tu cuenta de Google. Sin claves ni tarjeta.",
            Habilitado = true
        },
        // Con la clave de API del propio alumno (RF-38): la pega una vez y se guarda cifrada.
        new AgenteIA
        {
            Id = "openai",
            NombreVisible = "ChatGPT (tu clave de OpenAI)",
            Proveedor = "OpenAI",
            Modelo = "gpt-6-luna",
            BaseUrl = "https://api.openai.com",
            UrlConsola = "https://platform.openai.com/api-keys",
            Descripcion = "Pega una clave de API de OpenAI. El uso se cobra en tu cuenta de OpenAI (la suscripción de ChatGPT no incluye la API).",
            Habilitado = true
        },
        new AgenteIA
        {
            Id = "anthropic",
            NombreVisible = "Claude (tu clave de Anthropic)",
            Proveedor = "Anthropic",
            Modelo = "claude-haiku-5-5",
            BaseUrl = "https://api.anthropic.com",
            UrlConsola = "https://console.anthropic.com/settings/keys",
            Descripcion = "Pega una clave de API de Anthropic. El uso se cobra en tu cuenta de Anthropic (la suscripción de Claude no incluye la API).",
            Habilitado = true
        },
        new AgenteIA
        {
            Id = "gemini-clave",
            NombreVisible = "Gemini (tu clave de AI Studio)",
            Proveedor = "GoogleAIStudio",
            Modelo = "gemini-3.1-flash-lite",
            BaseUrl = "https://generativelanguage.googleapis.com",
            UrlConsola = "https://aistudio.google.com/apikey",
            Descripcion = "Pega una clave gratuita de Google AI Studio: usa el cupo gratis de tu propia cuenta.",
            Habilitado = true
        }
    ];
}

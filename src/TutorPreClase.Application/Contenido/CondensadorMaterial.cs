using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Tutor;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Application.Contenido;

public interface ICondensadorMaterial
{
    Task<ResultadoCondensacion> CondensarAsync(Guid archivoId, CancellationToken ct = default);
}

/// <summary>`Reintentar`: el modelo gratuito estaba saturado; conviene volver en unos minutos.</summary>
public enum ResultadoCondensacion { Hecho, Omitido, SinCuenta, Reintentar, Error }

/// <summary>Archivos a condensar; la API no espera a la IA (SDD §6.2).</summary>
public interface IColaCondensacion
{
    ValueTask EncolarAsync(Guid archivoId, CancellationToken ct = default);
    IAsyncEnumerable<Guid> LeerAsync(CancellationToken ct);
}

/// <summary>
/// Reescribe cada página del material como apuntes compactos, una sola vez, para que el
/// tutor lea menos tokens en cada pregunta (SDD §6.2). Trabaja página por página, así que
/// las citas [archivo, p. N] siguen valiendo. Usa la cuenta de OpenRouter del profesor del
/// curso: la plataforma no tiene clave propia. Nada se pierde: una página que la IA no
/// devuelve, o devuelve más larga, se queda con su texto original.
/// </summary>
public sealed class CondensadorMaterial(
    IAppDbContext db,
    IBovedaCredenciales credenciales,
    IProveedorLlmFactory proveedores,
    IContextoClaseService contexto,
    ILogger<CondensadorMaterial> log) : ICondensadorMaterial
{
    /// <summary>Tokens de texto original por llamada: pocas llamadas y respuestas completas.</summary>
    public const int TokensPorLote = 3_000;

    /// <summary>Una página más corta que esto ya es un apunte: no vale la pena condensarla.</summary>
    public const int TokensMinimos = 60;

    public async Task<ResultadoCondensacion> CondensarAsync(Guid archivoId, CancellationToken ct = default)
    {
        var archivo = await db.Archivos
            .Include(a => a.Paginas)
            .Include(a => a.Clase)
            .FirstOrDefaultAsync(a => a.Id == archivoId, ct);

        if (archivo is null || archivo.Estado != EstadoArchivo.Listo) return ResultadoCondensacion.Omitido;

        var docenteId = await db.Cursos
            .Where(c => c.Id == archivo.Clase!.CursoId)
            .Select(c => c.DocenteId)
            .FirstOrDefaultAsync(ct);

        // Con la cuenta que el profesor tenga conectada: OpenRouter o Gemini (RF-37).
        AgenteIA? agente = null;
        string? clave = null;

        if (docenteId is Guid profesor)
        {
            foreach (var candidato in await db.Agentes.Where(a => a.Habilitado).OrderBy(a => a.Id).ToListAsync(ct))
            {
                clave = await credenciales.ClaveParaAsync(profesor, candidato.Id, ct);
                if (clave is null) continue;

                agente = candidato;
                break;
            }
        }

        if (clave is null)
        {
            await TerminarAsync(archivo, EstadoCondensado.SinCuenta,
                "Conecta Gemini u OpenRouter en tu panel para que la IA condense el material.", ct);
            return ResultadoCondensacion.SinCuenta;
        }

        archivo.Condensado = EstadoCondensado.Procesando;
        archivo.ErrorCondensado = null;
        await db.SaveChangesAsync(ct);

        var proveedor = proveedores.Obtener(agente!.Id, clave);
        var paginas = archivo.Paginas.OrderBy(p => p.Pagina).ToList();

        foreach (var pagina in paginas)
        {
            pagina.TextoCondensado = null;
            pagina.TokensCondensado = 0;
        }

        foreach (var lote in Lotes(paginas.Where(p => p.Tokens >= TokensMinimos)))
        {
            var (apuntes, error) = await PedirAsync(proveedor, agente.Modelo, archivo.Nombre, lote, ct);

            if (error is not null)
            {
                if (error.CredencialRechazada) await credenciales.MarcarInvalidaAsync(docenteId!.Value, agente.Id, ct);

                // Saturación del pool gratuito: pasa sola; se deja pendiente para reintentar.
                if (error.Saturado)
                {
                    await TerminarAsync(archivo, EstadoCondensado.Pendiente,
                        "El modelo gratuito está saturado; se reintentará solo en unos minutos.", ct);
                    return ResultadoCondensacion.Reintentar;
                }

                // Lo condensado hasta aquí se conserva; el resto sigue en original.
                await TerminarAsync(archivo, EstadoCondensado.Error, MensajeError(error), ct);
                return ResultadoCondensacion.Error;
            }

            foreach (var pagina in lote)
            {
                if (!apuntes.TryGetValue(pagina.Pagina, out var texto)) continue;

                var tokens = ContadorTokens.Estimar(texto);
                if (tokens == 0 || tokens >= pagina.Tokens) continue;

                pagina.TextoCondensado = texto;
                pagina.TokensCondensado = tokens;
            }
        }

        log.LogInformation(
            "Material condensado: {Archivo}, {Original} → {Condensado} tokens",
            archivo.Nombre,
            paginas.Sum(p => p.Tokens),
            paginas.Sum(p => p.TextoCondensado is null ? p.Tokens : p.TokensCondensado));

        await TerminarAsync(archivo, EstadoCondensado.Listo, null, ct);
        return ResultadoCondensacion.Hecho;
    }

    private async Task TerminarAsync(ArchivoContenido archivo, EstadoCondensado estado, string? error, CancellationToken ct)
    {
        archivo.Condensado = estado;
        archivo.ErrorCondensado = error;
        await db.SaveChangesAsync(ct);
        contexto.Invalidar(archivo.ClaseId);
    }

    /// <summary>Agrupa páginas seguidas hasta <see cref="TokensPorLote"/>.</summary>
    internal static IEnumerable<List<PaginaContenido>> Lotes(IEnumerable<PaginaContenido> paginas)
    {
        var lote = new List<PaginaContenido>();
        var tokens = 0;

        foreach (var pagina in paginas)
        {
            if (lote.Count > 0 && tokens + pagina.Tokens > TokensPorLote)
            {
                yield return lote;
                lote = [];
                tokens = 0;
            }

            lote.Add(pagina);
            tokens += pagina.Tokens;
        }

        if (lote.Count > 0) yield return lote;
    }

    private static async Task<(Dictionary<int, string> Apuntes, ErrorProveedor? Error)> PedirAsync(
        ILlmProvider proveedor, string modelo, string archivo, List<PaginaContenido> lote, CancellationToken ct)
    {
        var material = new StringBuilder();
        foreach (var pagina in lote)
            material.Append("[p. ").Append(pagina.Pagina).Append("]\n").Append(pagina.Texto.Trim()).Append("\n\n");

        var solicitud = new LlmSolicitud(
            modelo,
            Prompt(archivo),
            [new LlmMensaje(RolLlm.Usuario, material.ToString())],
            [],
            Temperatura: 0.1,
            MaxTokens: lote.Sum(p => p.Tokens) + 400,
            Razonamiento: NivelRazonamiento.Desactivado);

        var respuesta = new StringBuilder();

        await foreach (var evento in proveedor.StreamAsync(solicitud, ct))
        {
            switch (evento)
            {
                case TextoParcial t:
                    respuesta.Append(t.Texto);
                    break;
                case ErrorProveedor e:
                    return ([], e);
            }
        }

        return (Interpretar(respuesta.ToString()), null);
    }

    /// <summary>{"paginas":[{"pagina":N,"apuntes":"…"}]}, tolerando texto o ``` alrededor.</summary>
    public static Dictionary<int, string> Interpretar(string respuesta)
    {
        var apuntes = new Dictionary<int, string>();

        var inicio = respuesta.IndexOf('{');
        var fin = respuesta.LastIndexOf('}');
        if (inicio < 0 || fin <= inicio) return apuntes;

        try
        {
            var raiz = JsonDocument.Parse(respuesta[inicio..(fin + 1)]).RootElement;
            if (!raiz.TryGetProperty("paginas", out var lista) || lista.ValueKind != JsonValueKind.Array)
                return apuntes;

            foreach (var item in lista.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("pagina", out var numero) || !numero.TryGetInt32(out var pagina)) continue;
                if (!item.TryGetProperty("apuntes", out var texto) || texto.ValueKind != JsonValueKind.String) continue;

                var limpio = texto.GetString()!.Trim();
                if (limpio.Length > 0) apuntes[pagina] = limpio;
            }
        }
        catch (JsonException)
        {
            // Respuesta ilegible: todas las páginas del lote se quedan con su original.
        }

        return apuntes;
    }

    private static string MensajeError(ErrorProveedor error) => error switch
    {
        { CredencialRechazada: true } => "OpenRouter rechazó tu cuenta. Vuelve a conectarla y condensa de nuevo.",
        { Saturado: true } => "El modelo gratuito estaba saturado. Vuelve a intentarlo en unos minutos.",
        { LimiteDeUso: true } => "Se acabó el cupo de tu cuenta de OpenRouter por hoy. Vuelve a intentarlo más tarde.",
        _ => "La IA no respondió. Vuelve a intentarlo."
    };

    private static string Prompt(string archivo) => $$"""
        Condensas material de clase de un curso universitario para que un tutor de IA lo lea
        rápido. Archivo: "{{archivo}}". Recibes varias páginas, cada una tras su marca [p. N].

        Para CADA página escribe apuntes compactos en español:
        - Conserva todas las definiciones, cifras, fórmulas, pasos, clasificaciones y ejemplos
          numéricos, con los mismos términos del material.
        - Quita redacción de relleno, repeticiones, frases de transición y textos decorativos.
        - No agregues nada que no esté en la página ni mezcles contenido entre páginas.
        - Usa viñetas cortas. Si la página no tiene contenido útil, devuelve apuntes vacíos.

        Responde SOLO con JSON, sin texto antes ni después:
        {"paginas":[{"pagina":N,"apuntes":"- …\n- …"}]}
        """;
}

using System.Text.Json;
using TutorPreClase.Application.Tutor;

namespace TutorPreClase.Api.Sse;

/// <summary>
/// Traduce los eventos del tutor al formato SSE del SDD §7.2. Los eventos `modo` y
/// `progreso` los emite el servidor, no el modelo.
/// </summary>
public static class EscritorSse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task EscribirAsync(HttpResponse respuesta, EventoTutor evento, CancellationToken ct)
    {
        var (nombre, datos) = Traducir(evento);

        await respuesta.WriteAsync($"event: {nombre}\n", ct);
        await respuesta.WriteAsync($"data: {JsonSerializer.Serialize(datos, Json)}\n\n", ct);
        await respuesta.Body.FlushAsync(ct);
    }

    private static (string Nombre, object Datos) Traducir(EventoTutor evento) => evento switch
    {
        EventoModo e => ("modo", new { modo = e.Modo.ToString(), ampliacionPermitida = e.AmpliacionPermitida }),

        EventoToken e => ("token", new { texto = e.Texto }),

        EventoHerramienta e => ("herramienta", new { nombre = e.Nombre, estado = e.Estado, detalle = e.Detalle }),

        EventoProgreso e => ("progreso", new
        {
            respondidas = e.Respondidas,
            total = e.Total,
            segundosRestantes = e.SegundosRestantes,
            enPausa = e.EnPausa
        }),

        EventoFuentes e => ("fuentes", e.Citas.Select(c => new
        {
            archivo = c.Archivo,
            pagina = c.Pagina,
            valida = c.Valida
        })),

        EventoAviso e => ("aviso", new { codigo = e.Codigo, mensaje = e.Mensaje }),

        EventoFin e => ("fin", new
        {
            agenteId = e.AgenteId,
            usaAmpliacion = e.UsaAmpliacion,
            tokensEntrada = e.TokensEntrada,
            tokensSalida = e.TokensSalida
        }),

        EventoError e => ("error", new { mensaje = e.Mensaje, reintentable = e.Reintentable }),

        _ => ("desconocido", new { })
    };

    public static void PrepararCabeceras(HttpResponse respuesta)
    {
        respuesta.Headers.ContentType = "text/event-stream";
        respuesta.Headers.CacheControl = "no-cache";
        respuesta.Headers.Connection = "keep-alive";
        // Evita que un proxy intermedio acumule el stream y rompa RNF-02.
        respuesta.Headers["X-Accel-Buffering"] = "no";
    }
}

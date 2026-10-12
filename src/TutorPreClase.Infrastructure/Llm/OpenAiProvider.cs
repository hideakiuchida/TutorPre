using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TutorPreClase.Application.Llm;

namespace TutorPreClase.Infrastructure.Llm;

/// <summary>
/// Adaptador del formato de chat de OpenAI (function calling, streaming). Lo comparten los
/// agentes de clave propia: OpenAI, Anthropic y Gemini de AI Studio, cada uno en su ruta
/// (SDD §5.8).
/// </summary>
public class OpenAiProvider(
    HttpClient http,
    OpcionesAgente opciones,
    ILogger logger,
    string id = "openai") : ILlmProvider
{
    public string Id { get; } = id;

    protected OpcionesAgente Opciones { get; } = opciones;

    /// <summary>Ruta del chat relativa a la BaseUrl; cada compatible la puede mover (RF-38).</summary>
    protected virtual string RutaChat =>
        string.IsNullOrWhiteSpace(Opciones.RutaChat) ? "/v1/chat/completions" : Opciones.RutaChat;

    /// <summary>`reasoning_effort` del formato OpenAI, solo si el agente lo configura.</summary>
    protected virtual string? Esfuerzo(string? nivel) => nivel switch
    {
        NivelRazonamiento.Desactivado when !string.IsNullOrWhiteSpace(Opciones.EsfuerzoDesactivado) => Opciones.EsfuerzoDesactivado,
        NivelRazonamiento.Bajo when !string.IsNullOrWhiteSpace(Opciones.EsfuerzoBajo) => Opciones.EsfuerzoBajo,
        _ => null
    };

    public async IAsyncEnumerable<LlmEvento> StreamAsync(
        LlmSolicitud solicitud, [EnumeratorCancellation] CancellationToken ct)
    {
        var reloj = Stopwatch.StartNew();
        long? primerTokenMs = null;

        using var peticion = new HttpRequestMessage(HttpMethod.Post, RutaChat)
        {
            Content = JsonContent.Create(Cuerpo(solicitud), options: MapeoLlm.Json)
        };

        if (!string.IsNullOrEmpty(Opciones.ApiKey))
            peticion.Headers.Authorization = new("Bearer", Opciones.ApiKey);

        var envio = await MapeoLlm.EnviarAsync(http, peticion, Id, logger, ct);

        if (envio.Error is not null)
        {
            yield return envio.Error;
            yield break;
        }

        using (var respuesta = envio.Respuesta!)
        {
            var fallo = await MapeoLlm.RevisarEstadoAsync(respuesta, Id, logger, ct);
            if (fallo is not null)
            {
                yield return fallo;
                yield break;
            }

            var entrada = 0;
            var salida = 0;
            var razonamiento = 0;
            var modeloQueRespondio = solicitud.Modelo;
            var razonFin = "";
            string? herramientaId = null;
            string? herramientaNombre = null;
            var argumentos = new StringBuilder();

            await using var flujo = await respuesta.Content.ReadAsStreamAsync(ct);

            await foreach (var dato in MapeoLlm.LeerSseAsync(flujo, ct))
            {
                JsonElement evento;
                try
                {
                    evento = JsonDocument.Parse(dato).RootElement;
                }
                catch (JsonException)
                {
                    continue;
                }

                entrada += MapeoLlm.Entero(evento, "usage", "prompt_tokens");
                salida += MapeoLlm.Entero(evento, "usage", "completion_tokens");
                razonamiento += MapeoLlm.Entero(evento, "usage", "completion_tokens_details", "reasoning_tokens");

                // El proveedor dice qué versión concreta del modelo respondió.
                var modelo = MapeoLlm.Texto(evento, "model");
                if (modelo.Length > 0) modeloQueRespondio = modelo;

                if (!evento.TryGetProperty("choices", out var opcionesJson) ||
                    opcionesJson.ValueKind != JsonValueKind.Array ||
                    opcionesJson.GetArrayLength() == 0)
                {
                    continue;
                }

                var opcion = opcionesJson[0];
                if (!opcion.TryGetProperty("delta", out var delta)) continue;

                var texto = MapeoLlm.Texto(delta, "content");
                if (texto.Length > 0)
                {
                    primerTokenMs ??= reloj.ElapsedMilliseconds;
                    yield return new TextoParcial(texto);
                }

                if (delta.TryGetProperty("tool_calls", out var llamadas) &&
                    llamadas.ValueKind == JsonValueKind.Array)
                {
                    foreach (var llamada in llamadas.EnumerateArray())
                    {
                        var idParcial = MapeoLlm.Texto(llamada, "id");
                        if (idParcial.Length > 0) herramientaId = idParcial;

                        var nombre = MapeoLlm.Texto(llamada, "function", "name");
                        if (nombre.Length > 0) herramientaNombre = nombre;

                        argumentos.Append(MapeoLlm.Texto(llamada, "function", "arguments"));
                    }
                }

                var razon = MapeoLlm.Texto(opcion, "finish_reason");
                if (razon.Length > 0) razonFin = razon;

                if (razon == "tool_calls" && herramientaId is not null && herramientaNombre is not null)
                {
                    yield return new LlamadaHerramienta(
                        herramientaId, herramientaNombre, MapeoLlm.Argumentos(argumentos.ToString()));

                    herramientaId = null;
                    herramientaNombre = null;
                    argumentos.Clear();
                }
            }

            // Sin el texto del alumno (RNF-06): solo tiempos y consumo, para medir la latencia.
            logger.LogInformation(
                "LLM {Proveedor} {Modelo}: primer token {PrimerTokenMs} ms, total {TotalMs} ms, " +
                "entrada {Entrada}, salida {Salida}, razonamiento {Razonamiento}, fin {RazonFin}",
                Id, modeloQueRespondio, primerTokenMs, reloj.ElapsedMilliseconds,
                entrada, salida, razonamiento, razonFin);

            yield return new Fin(entrada, salida, CortadaPorTope: razonFin == "length");
        }
    }

    private object Cuerpo(LlmSolicitud s)
    {
        var mensajes = new List<object> { new { role = "system", content = s.PromptSistema } };

        foreach (var m in s.Mensajes)
        {
            switch (m.Rol)
            {
                case RolLlm.Usuario:
                    mensajes.Add(new { role = "user", content = m.Texto ?? "" });
                    break;

                case RolLlm.Asistente when m.HerramientaId is not null:
                    mensajes.Add(new
                    {
                        role = "assistant",
                        content = string.IsNullOrWhiteSpace(m.Texto) ? null : m.Texto,
                        tool_calls = new object[]
                        {
                            new
                            {
                                id = m.HerramientaId,
                                type = "function",
                                function = new
                                {
                                    name = m.HerramientaNombre,
                                    arguments = (m.HerramientaArgumentos ?? MapeoLlm.ObjetoVacio).GetRawText()
                                }
                            }
                        }
                    });
                    break;

                case RolLlm.Asistente:
                    mensajes.Add(new { role = "assistant", content = m.Texto ?? "" });
                    break;

                case RolLlm.Herramienta:
                    mensajes.Add(new
                    {
                        role = "tool",
                        tool_call_id = m.HerramientaId,
                        content = m.HerramientaResultado ?? ""
                    });
                    break;
            }
        }

        // Los modelos de razonamiento de OpenAI rechazan con 400 `max_tokens` y `temperature`.
        var deRazonamiento = Opciones.ModeloDeRazonamiento;

        return new
        {
            model = s.Modelo,
            // null se omite del JSON: sin tope pedido, el modelo usa su máximo.
            max_tokens = deRazonamiento ? null : s.MaxTokens,
            max_completion_tokens = deRazonamiento ? s.MaxTokens : null,
            reasoning_effort = Esfuerzo(s.Razonamiento ?? Opciones.Razonamiento),
            temperature = deRazonamiento ? (double?)null : s.Temperatura,
            stream = true,
            stream_options = new { include_usage = true },
            messages = mensajes,
            // Sin herramientas el campo no va: OpenAI rechaza con 400 una lista vacía.
            tools = s.Herramientas.Count == 0 ? null : s.Herramientas.Select(h => new
            {
                type = "function",
                function = new
                {
                    name = h.Nombre,
                    description = h.Descripcion,
                    parameters = h.EsquemaEntrada
                }
            })
        };
    }
}

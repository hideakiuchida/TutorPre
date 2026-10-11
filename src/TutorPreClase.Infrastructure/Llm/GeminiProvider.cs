using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TutorPreClase.Application.Llm;

namespace TutorPreClase.Infrastructure.Llm;

/// <summary>
/// Gemini con la cuenta de Google del usuario (RF-37). Habla la API REST de Gemini
/// (`streamGenerateContent`, SSE) con un token de acceso OAuth que renueva
/// <see cref="TokensGoogle"/> a partir del refresh token guardado en la bóveda. El resto
/// del sistema no nota la diferencia con los agentes de clave: mismos eventos, herramientas y avisos.
/// </summary>
public sealed class GeminiProvider(
    HttpClient http,
    OpcionesAgente opciones,
    TokensGoogle tokens,
    ILogger<GeminiProvider> logger,
    string id = "gemini") : ILlmProvider
{
    public string Id { get; } = id;

    /// <summary>
    /// Razonamiento según el nivel; null deja decidir al modelo. Gemini 3 usa
    /// `thinkingLevel` con su mínimo "low": gemini-3.8-flash rechaza "minimal" con
    /// un 400. Los modelos anteriores usan `thinkingBudget`.
    /// </summary>
    private static JsonObject? Razonamiento(string modelo, string? nivel)
    {
        if (nivel is not (NivelRazonamiento.Desactivado or NivelRazonamiento.Bajo)) return null;

        return modelo.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject { ["thinkingLevel"] = "low" }
            : new JsonObject { ["thinkingBudget"] = nivel == NivelRazonamiento.Desactivado ? 0 : 512 };
    }

    public async IAsyncEnumerable<LlmEvento> StreamAsync(
        LlmSolicitud solicitud, [EnumeratorCancellation] CancellationToken ct)
    {
        var reloj = Stopwatch.StartNew();
        long? primerTokenMs = null;

        // La "clave" de este agente es el refresh token: se cambia por un acceso vigente.
        var token = await tokens.AccesoAsync(Id, opciones.ApiKey ?? "", ct);
        if (token.Revocado)
        {
            yield return new ErrorProveedor("Google revocó el acceso.", Reintentable: false, CredencialRechazada: true);
            yield break;
        }

        if (token.Acceso is null)
        {
            yield return new ErrorProveedor("No se pudo contactar a Google.", Reintentable: true);
            yield break;
        }

        using var peticion = new HttpRequestMessage(
            HttpMethod.Post, $"/v1beta/models/{solicitud.Modelo}:streamGenerateContent?alt=sse")
        {
            Content = JsonContent.Create(Cuerpo(solicitud))
        };
        peticion.Headers.Authorization = new("Bearer", token.Acceso);

        var envio = await MapeoLlm.EnviarAsync(http, peticion, Id, logger, ct);
        if (envio.Error is not null)
        {
            yield return envio.Error;
            yield break;
        }

        using var respuesta = envio.Respuesta!;
        var fallo = await RevisarEstadoAsync(respuesta, ct);
        if (fallo is not null)
        {
            if (fallo.CredencialRechazada) tokens.Olvidar(opciones.ApiKey ?? "");
            yield return fallo;
            yield break;
        }

        var entrada = 0;
        var salida = 0;
        var razonamiento = 0;
        var modelo = solicitud.Modelo;
        var razonFin = "";

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

            // Gemini manda el uso acumulado en cada trozo: vale el último, no la suma.
            if (evento.TryGetProperty("usageMetadata", out var uso))
            {
                entrada = MapeoLlm.Entero(uso, "promptTokenCount");
                salida = MapeoLlm.Entero(uso, "candidatesTokenCount");
                razonamiento = MapeoLlm.Entero(uso, "thoughtsTokenCount");
            }

            var version = MapeoLlm.Texto(evento, "modelVersion");
            if (version.Length > 0) modelo = version;

            if (!evento.TryGetProperty("candidates", out var candidatos) ||
                candidatos.ValueKind != JsonValueKind.Array || candidatos.GetArrayLength() == 0)
            {
                continue;
            }

            var candidato = candidatos[0];
            var razon = MapeoLlm.Texto(candidato, "finishReason");
            if (razon.Length > 0) razonFin = razon;

            if (!candidato.TryGetProperty("content", out var contenido) ||
                !contenido.TryGetProperty("parts", out var partes) ||
                partes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var parte in partes.EnumerateArray())
            {
                // Los pensamientos del modelo no se muestran ni se guardan.
                if (parte.TryGetProperty("thought", out var pensado) && pensado.ValueKind == JsonValueKind.True) continue;

                var texto = MapeoLlm.Texto(parte, "text");
                if (texto.Length > 0)
                {
                    primerTokenMs ??= reloj.ElapsedMilliseconds;
                    yield return new TextoParcial(texto);
                }

                if (parte.TryGetProperty("functionCall", out var llamada))
                {
                    var argumentos = llamada.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object
                        ? args.Clone()
                        : MapeoLlm.ObjetoVacio;

                    // Gemini no numera las llamadas: el id lo pone el servidor. La firma del
                    // razonamiento viaja en la misma parte y hay que devolverla tal cual.
                    var firma = MapeoLlm.Texto(parte, "thoughtSignature");

                    yield return new LlamadaHerramienta(
                        $"gemini_{Guid.NewGuid():N}", MapeoLlm.Texto(llamada, "name"), argumentos,
                        firma.Length > 0 ? firma : null);
                }
            }
        }

        logger.LogInformation(
            "LLM {Proveedor} {Modelo}: primer token {PrimerTokenMs} ms, total {TotalMs} ms, " +
            "entrada {Entrada}, salida {Salida}, razonamiento {Razonamiento}, fin {RazonFin}",
            Id, modelo, primerTokenMs, reloj.ElapsedMilliseconds, entrada, salida, razonamiento, razonFin);

        yield return new Fin(entrada, salida, CortadaPorTope: razonFin == "MAX_TOKENS");
    }

    /// <summary>
    /// 401 o permiso insuficiente: hay que reconectar. Un 403 porque la API no está activada
    /// en el proyecto es de la plataforma, no del usuario: su credencial no se invalida. Un
    /// 429 es la cuota del proyecto de la app (RF-30).
    /// </summary>
    private async Task<ErrorProveedor?> RevisarEstadoAsync(HttpResponseMessage respuesta, CancellationToken ct)
    {
        if (respuesta.IsSuccessStatusCode) return null;

        var detalle = await respuesta.Content.ReadAsStringAsync(ct);
        logger.LogWarning("{Proveedor} devolvio {Codigo}: {Detalle}", Id, (int)respuesta.StatusCode, detalle);

        if (respuesta.StatusCode == HttpStatusCode.Forbidden &&
            (detalle.Contains("SERVICE_DISABLED", StringComparison.Ordinal) ||
             detalle.Contains("has not been used", StringComparison.OrdinalIgnoreCase)))
        {
            return new ErrorProveedor(
                "La API de Gemini no está activada en el proyecto de la plataforma. Avisa al administrador.",
                Reintentable: false);
        }

        if (respuesta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new ErrorProveedor("Google rechazó el acceso a tu cuenta.", false, CredencialRechazada: true);

        // 503 / UNAVAILABLE: el modelo está saturado para todos; no es el cupo del alumno.
        if (respuesta.StatusCode == HttpStatusCode.ServiceUnavailable)
            return new ErrorProveedor(MapeoLlm.MensajeDeFallo(respuesta.StatusCode), true, LimiteDeUso: true, Saturado: true);

        var limite = respuesta.StatusCode == HttpStatusCode.TooManyRequests;
        return new ErrorProveedor(
            MapeoLlm.MensajeDeFallo(respuesta.StatusCode),
            MapeoLlm.EsReintentable(respuesta.StatusCode),
            LimiteDeUso: limite,
            ReintentarEn: limite ? MapeoLlm.ReintentarEn(respuesta) ?? ReintentarSegunDetalle(detalle) : null);
    }

    /// <summary>
    /// Google no manda `Retry-After`: dice cuándo volver en el cuerpo, como
    /// `"retryDelay": "5594s"` (RetryInfo). Con eso el alumno sabe cuánto falta.
    /// </summary>
    internal static DateTimeOffset? ReintentarSegunDetalle(string detalle)
    {
        var espera = System.Text.RegularExpressions.Regex.Match(detalle, """
            "retryDelay"\s*:\s*"(\d+)(?:\.\d+)?s"
            """.Trim());
        return espera.Success ? DateTimeOffset.UtcNow.AddSeconds(double.Parse(espera.Groups[1].Value)) : null;
    }

    internal object Cuerpo(LlmSolicitud s)
    {
        var generacion = new JsonObject { ["temperature"] = s.Temperatura };

        // Sin tope pedido, Gemini usa el máximo del modelo.
        if (s.MaxTokens is int tope) generacion["maxOutputTokens"] = tope;

        if (Razonamiento(s.Modelo, s.Razonamiento ?? opciones.Razonamiento) is JsonObject razonamiento)
            generacion["thinkingConfig"] = razonamiento;

        var cuerpo = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = s.PromptSistema }) },
            ["contents"] = Contenidos(s.Mensajes),
            ["generationConfig"] = generacion
        };

        if (s.Herramientas.Count > 0)
        {
            var declaraciones = new JsonArray();
            foreach (var h in s.Herramientas)
            {
                var declaracion = new JsonObject { ["name"] = h.Nombre, ["description"] = h.Descripcion };
                if (Esquema(h.EsquemaEntrada) is JsonObject parametros) declaracion["parameters"] = parametros;
                declaraciones.Add(declaracion);
            }

            cuerpo["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declaraciones });
        }

        return cuerpo;
    }

    /// <summary>
    /// Historial en turnos `user` / `model`, que Gemini exige alternados y empezando por el
    /// usuario. Los turnos seguidos del mismo rol se juntan; las respuestas de herramientas
    /// van como `functionResponse` en un turno del usuario.
    /// </summary>
    private static JsonArray Contenidos(IReadOnlyList<LlmMensaje> mensajes)
    {
        var turnos = new List<(string Rol, JsonArray Partes)>();

        void Agregar(string rol, JsonObject parte)
        {
            if (turnos.Count > 0 && turnos[^1].Rol == rol) turnos[^1].Partes.Add(parte);
            else turnos.Add((rol, new JsonArray(parte)));
        }

        foreach (var m in mensajes)
        {
            switch (m.Rol)
            {
                case RolLlm.Usuario when !string.IsNullOrWhiteSpace(m.Texto):
                    Agregar("user", new JsonObject { ["text"] = m.Texto });
                    break;

                case RolLlm.Asistente:
                    if (!string.IsNullOrWhiteSpace(m.Texto)) Agregar("model", new JsonObject { ["text"] = m.Texto });
                    if (m.HerramientaNombre is not null)
                    {
                        var llamada = new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = m.HerramientaNombre,
                                ["args"] = JsonNode.Parse((m.HerramientaArgumentos ?? MapeoLlm.ObjetoVacio).GetRawText())
                            }
                        };

                        // Gemini 3 rechaza con 400 una llamada del turno en curso sin su firma.
                        if (m.HerramientaFirma is not null) llamada["thoughtSignature"] = m.HerramientaFirma;

                        Agregar("model", llamada);
                    }
                    break;

                case RolLlm.Herramienta:
                    Agregar("user", new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = m.HerramientaNombre,
                            ["response"] = Respuesta(m.HerramientaResultado)
                        }
                    });
                    break;
            }
        }

        // Un historial que empieza con el saludo del tutor necesita un turno del usuario antes.
        if (turnos.Count > 0 && turnos[0].Rol == "model")
            turnos.Insert(0, ("user", new JsonArray(new JsonObject { ["text"] = "(inicio de la conversación)" })));

        var contenidos = new JsonArray();
        foreach (var (rol, partes) in turnos) contenidos.Add(new JsonObject { ["role"] = rol, ["parts"] = partes });
        return contenidos;
    }

    /// <summary>`functionResponse.response` tiene que ser un objeto.</summary>
    private static JsonNode Respuesta(string? json)
    {
        try
        {
            var nodo = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return nodo is JsonObject objeto ? objeto : new JsonObject { ["resultado"] = nodo };
        }
        catch (JsonException)
        {
            return new JsonObject { ["resultado"] = json };
        }
    }

    /// <summary>
    /// Gemini acepta un subconjunto de JSON Schema: se quitan las claves que rechaza, y un
    /// objeto sin propiedades se omite (una herramienta sin parámetros va sin `parameters`).
    /// </summary>
    internal static JsonObject? Esquema(JsonElement esquema)
    {
        if (JsonNode.Parse(esquema.GetRawText()) is not JsonObject raiz) return null;

        Limpiar(raiz);
        return raiz["properties"] is JsonObject { Count: > 0 } ? raiz : null;
    }

    private static readonly string[] NoAdmitidas = ["additionalProperties", "$schema", "$id", "examples", "default"];

    private static void Limpiar(JsonObject nodo)
    {
        foreach (var clave in NoAdmitidas) nodo.Remove(clave);

        if (nodo["required"] is JsonArray { Count: 0 }) nodo.Remove("required");

        foreach (var (clave, hijo) in nodo.ToList())
        {
            switch (hijo)
            {
                // "properties" es un mapa nombre → esquema: se limpian los esquemas, no los nombres.
                case JsonObject propiedades when clave == "properties":
                    foreach (var esquema in propiedades.Select(p => p.Value).OfType<JsonObject>()) Limpiar(esquema);
                    break;
                case JsonObject objeto:
                    Limpiar(objeto);
                    break;
                case JsonArray lista:
                    foreach (var elemento in lista.OfType<JsonObject>()) Limpiar(elemento);
                    break;
            }
        }
    }
}

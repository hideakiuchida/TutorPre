using System.Net;
using Microsoft.Extensions.Logging;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Infrastructure.Llm;

/// <summary>Comprueba contra el proveedor que la credencial sirve, antes de guardarla (RF-25).</summary>
public interface IValidadorCredencial
{
    Task<bool> EsUsableAsync(AgenteIA agente, string clave, CancellationToken ct = default);
}

public sealed class ValidadorCredencial(
    IHttpClientFactory http,
    TokensGoogle tokensGoogle,
    ILogger<ValidadorCredencial> log) : IValidadorCredencial
{
    public async Task<bool> EsUsableAsync(AgenteIA agente, string clave, CancellationToken ct = default)
    {
        var cliente = http.CreateClient($"llm:{agente.Id}");
        cliente.Timeout = TimeSpan.FromSeconds(20);

        // Con Google la clave es un refresh token: primero se cambia por un acceso (RF-37).
        if (agente.Proveedor == ProveedorGoogle)
        {
            var token = await tokensGoogle.AccesoAsync(agente.Id, clave, ct);
            if (token.Revocado) return false;
            if (token.Acceso is null) return true;
            clave = token.Acceso;
        }

        using var peticion = Armar(agente, clave);

        try
        {
            using var respuesta = await cliente.SendAsync(peticion, ct);

            // Un 401/403 es credencial mala; un 429 o un 5xx es del proveedor, no de la clave.
            if (respuesta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // El cuerpo de error de Google o de OpenRouter explica el motivo y no trae secretos.
                var detalle = await respuesta.Content.ReadAsStringAsync(ct);
                log.LogWarning("Validacion de {Agente} rechazada con {Codigo}: {Detalle}",
                    agente.Id, (int)respuesta.StatusCode, detalle.Length > 600 ? detalle[..600] : detalle);
                return false;
            }

            if (respuesta.IsSuccessStatusCode) return true;

            log.LogWarning("Validacion de {Agente} devolvio {Codigo}; se acepta la credencial",
                agente.Id, (int)respuesta.StatusCode);

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Sin red no se puede afirmar que la clave sea mala: se deja pasar y ya fallara al usarla.
            log.LogWarning(ex, "No se pudo validar la credencial de {Agente}", agente.Id);
            return true;
        }
    }

    public const string ProveedorGoogle = "Google";
    public const string ProveedorOpenAi = "OpenAI";
    public const string ProveedorAnthropic = "Anthropic";
    public const string ProveedorGoogleAiStudio = "GoogleAIStudio";

    /// <summary>
    /// Una llamada que autentica sin gastar cupo: la propia clave en OpenRouter, o listar
    /// los modelos en Gemini, OpenAI y Anthropic, cada uno con su cabecera.
    /// </summary>
    public static HttpRequestMessage Armar(AgenteIA agente, string clave)
    {
        switch (agente.Proveedor)
        {
            case ProveedorAnthropic:
            {
                var peticion = new HttpRequestMessage(HttpMethod.Get, "/v1/models?limit=1");
                peticion.Headers.Add("x-api-key", clave);
                peticion.Headers.Add("anthropic-version", "2023-06-01");
                return peticion;
            }
            case ProveedorGoogleAiStudio:
            {
                var peticion = new HttpRequestMessage(HttpMethod.Get, "/v1beta/models?pageSize=1");
                peticion.Headers.Add("x-goog-api-key", clave);
                return peticion;
            }
            default:
            {
                var ruta = agente.Proveedor switch
                {
                    ProveedorGoogle => "/v1beta/models?pageSize=1",
                    ProveedorOpenAi => "/v1/models",
                    _ => "/api/v1/key"
                };
                var peticion = new HttpRequestMessage(HttpMethod.Get, ruta);
                peticion.Headers.Authorization = new("Bearer", clave);
                return peticion;
            }
        }
    }
}

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
                return false;

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

    /// <summary>
    /// Consultar la propia clave en OpenRouter, o listar los modelos en Gemini, autentica
    /// sin gastar cupo.
    /// </summary>
    private static HttpRequestMessage Armar(AgenteIA agente, string clave)
    {
        var ruta = agente.Proveedor == ProveedorGoogle ? "/v1beta/models?pageSize=1" : "/api/v1/key";
        var peticion = new HttpRequestMessage(HttpMethod.Get, ruta);
        peticion.Headers.Authorization = new("Bearer", clave);
        return peticion;
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TutorPreClase.Application.Llm;

namespace TutorPreClase.Infrastructure.Llm;

/// <summary>
/// Resuelve el ILlmProvider del agente elegido. Agregar un proveedor nuevo es una
/// implementacion mas y una entrada de configuracion (RNF-09).
/// </summary>
public sealed class ProveedorLlmFactory(
    IHttpClientFactory http,
    IOptions<OpcionesAgentes> opciones,
    TokensGoogle tokensGoogle,
    ILoggerFactory logs) : IProveedorLlmFactory
{
    private readonly OpcionesAgentes _agentes = opciones.Value;

    public bool Existe(string agenteId) => _agentes.ContainsKey(agenteId);

    public ILlmProvider Obtener(string agenteId, string apiKey)
    {
        if (!_agentes.TryGetValue(agenteId, out var plantilla))
            throw new InvalidOperationException($"No hay configuracion para el agente '{agenteId}'.");

        // La configuracion aporta modelo y BaseUrl; la credencial la trae el alumno.
        var configAgente = new OpcionesAgente
        {
            Nombre = plantilla.Nombre,
            BaseUrl = plantilla.BaseUrl,
            Modelo = plantilla.Modelo,
            ModelosAlternativos = plantilla.ModelosAlternativos,
            Razonamiento = plantilla.Razonamiento,
            RutaChat = plantilla.RutaChat,
            EsfuerzoDesactivado = plantilla.EsfuerzoDesactivado,
            Descripcion = plantilla.Descripcion,
            Habilitado = plantilla.Habilitado,
            Conexion = plantilla.Conexion,
            ApiKey = apiKey
        };

        var cliente = http.CreateClient($"llm:{agenteId}");

        // Con Google la "clave" es el refresh token del usuario; el provider pide el acceso.
        return plantilla.Conexion switch
        {
            ConexionAgente.OAuthGoogle => new GeminiProvider(
                cliente, configAgente, tokensGoogle, logs.CreateLogger<GeminiProvider>(), agenteId),
            _ when agenteId == "openrouter" => new OpenRouterProvider(cliente, configAgente, logs.CreateLogger<OpenRouterProvider>()),
            // Clave pegada por el alumno (RF-38): OpenAI, Anthropic o Gemini, en formato OpenAI.
            ConexionAgente.Clave => new OpenAiProvider(cliente, configAgente, logs.CreateLogger<OpenAiProvider>(), agenteId),
            _ => throw new InvalidOperationException($"Agente '{agenteId}' no soportado.")
        };
    }
}

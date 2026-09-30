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
            Descripcion = plantilla.Descripcion,
            Habilitado = plantilla.Habilitado,
            Conexion = plantilla.Conexion,
            ApiKey = apiKey
        };

        var cliente = http.CreateClient($"llm:{agenteId}");

        return agenteId switch
        {
            "openrouter" => new OpenRouterProvider(cliente, configAgente, logs.CreateLogger<OpenRouterProvider>()),
            _ => throw new InvalidOperationException($"Agente '{agenteId}' no soportado.")
        };
    }
}

namespace TutorPreClase.Application.Llm;

/// <summary>Sección "Agentes" de configuración (SDD §5.8).</summary>
public sealed class OpcionesAgentes : Dictionary<string, OpcionesAgente>
{
    public const string Seccion = "Agentes";
}

public sealed class OpcionesAgente
{
    public string Nombre { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Modelo { get; set; } = "";
    public string? Descripcion { get; set; }
    public bool Habilitado { get; set; } = true;
    public string? ApiKey { get; set; }

    /// <summary>
    /// Modelos de respaldo, en orden, por si el principal está saturado o caído: el
    /// gateway los prueba en la misma llamada (OpenRouter `models`). Solo modelos que
    /// hayan pasado la suite de paridad (SDD §5.8).
    /// </summary>
    public List<string> ModelosAlternativos { get; set; } = [];

    /// <summary>
    /// Cuánto razona el modelo antes de contestar: "desactivado", "bajo" o vacío (lo que
    /// decida el modelo). El razonamiento no se ve y retrasa el primer token, así que en el
    /// chat va desactivado; cada solicitud puede pedir otro nivel (SDD §5.8).
    /// </summary>
    public string? Razonamiento { get; set; }

    /// <summary>"Clave" (el alumno la pega) u "OAuth" (inicia sesion en el proveedor, RF-29).</summary>
    public string Conexion { get; set; } = ConexionAgente.Clave;

    /// <summary>Solo OAuth: pagina de autorizacion del proveedor.</summary>
    public string? UrlAutorizacion { get; set; }

    /// <summary>
    /// Solo OAuth: ruta de la web a la que vuelve el proveedor. Es fija por entorno y
    /// nunca se toma de la peticion, para que nadie la desvie (SDD §9.1).
    /// </summary>
    public string? UrlRetorno { get; set; }
}

public static class NivelRazonamiento
{
    public const string Desactivado = "desactivado";
    public const string Bajo = "bajo";
}

public static class ConexionAgente
{
    public const string Clave = "Clave";
    public const string OAuth = "OAuth";
}

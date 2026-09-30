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

    /// <summary>Solo OAuthGoogle: donde se canjea el código y se renueva el acceso.</summary>
    public string? UrlToken { get; set; }

    /// <summary>Solo OAuthGoogle: permiso que se pide a la cuenta de Google del usuario.</summary>
    public string? Scope { get; set; }

    /// <summary>
    /// Solo OAuthGoogle: el cliente OAuth web de la app, el mismo del inicio de sesión. No va
    /// en appsettings: se completa al arrancar desde Acceso:Proveedores:google (secrets/ o
    /// variables del servidor).
    /// </summary>
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

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

    /// <summary>OAuth con PKCE de OpenRouter: devuelve una clave a nombre del usuario.</summary>
    public const string OAuth = "OAuth";

    /// <summary>OAuth de Google: la cuenta de Google del usuario llama a Gemini (RF-37).</summary>
    public const string OAuthGoogle = "OAuthGoogle";

    /// <summary>Para la web ambas son "iniciar sesión en el proveedor", sin pegar claves.</summary>
    public static bool EsOAuth(string? conexion) => conexion is OAuth or OAuthGoogle;
}

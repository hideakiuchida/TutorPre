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
    /// Se puede usar: está habilitado y completo. Una entrada a medias (por ejemplo, la que
    /// deja una variable de entorno suelta de un agente que ya no existe) no cuenta.
    /// </summary>
    public bool Vigente =>
        Habilitado && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Modelo);

    /// <summary>
    /// Cuánto razona el modelo antes de contestar: "desactivado", "bajo" o vacío (lo que
    /// decida el modelo). El razonamiento no se ve y retrasa el primer token, así que en el
    /// chat va desactivado; cada solicitud puede pedir otro nivel (SDD §5.8).
    /// </summary>
    public string? Razonamiento { get; set; }

    /// <summary>
    /// Solo Clave: ruta del chat en formato OpenAI relativa a BaseUrl. OpenAI, Anthropic
    /// (compatibilidad OpenAI) y Gemini de AI Studio lo exponen en rutas distintas (RF-38).
    /// </summary>
    public string? RutaChat { get; set; }

    /// <summary>
    /// Solo Clave: cómo empieza una clave válida de ese proveedor ("sk-", "sk-ant-"). Gemini no
    /// lo fija: Google cambió el formato de sus claves (antes "AIza…", ahora también "AQ…") y
    /// la que decide si sirve es la comprobación con el proveedor.
    /// </summary>
    public string? PrefijoClave { get; set; }

    /// <summary>
    /// Solo Clave: valor de `reasoning_effort` con el razonamiento desactivado, si el
    /// proveedor lo admite (OpenAI: "none", obligatorio en gpt-6-luna cuando la llamada lleva
    /// herramientas). Vacío: no se envía.
    /// </summary>
    public string? EsfuerzoDesactivado { get; set; }

    /// <summary>
    /// Solo Clave: valor de `reasoning_effort` con el razonamiento bajo (OpenAI: "low"). Vacío:
    /// no se envía y el modelo usa su nivel por defecto, que gasta más tokens de salida.
    /// </summary>
    public string? EsfuerzoBajo { get; set; }

    /// <summary>
    /// Solo Clave: el modelo razona por su cuenta (OpenAI gpt-5 y gpt-6, Claude desde 4.7). Su
    /// API rechaza con 400 una `temperature` propia, y OpenAI además `max_tokens`: el tope va en
    /// `max_completion_tokens`, que incluye lo que razona, y la temperatura no se envía.
    /// </summary>
    public bool ModeloDeRazonamiento { get; set; }

    /// <summary>"Clave" (el alumno la pega) u "OAuthGoogle" (inicia sesion en Google, RF-29).</summary>
    public string Conexion { get; set; } = ConexionAgente.Clave;

    /// <summary>Solo OAuthGoogle: pagina de autorizacion del proveedor.</summary>
    public string? UrlAutorizacion { get; set; }

    /// <summary>Solo OAuthGoogle: donde se canjea el código y se renueva el acceso.</summary>
    public string? UrlToken { get; set; }

    /// <summary>
    /// Solo OAuthGoogle: permisos que se piden a la cuenta de Google del usuario, separados
    /// por espacios. La API de Gemini exige cloud-platform y generative-language.retriever:
    /// con solo el primero responde 403 "insufficient authentication scopes".
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// Solo OAuthGoogle: el cliente OAuth web de la app, el mismo del inicio de sesión. No va
    /// en appsettings: se completa al arrancar desde Acceso:Proveedores:google (secrets/ o
    /// variables del servidor).
    /// </summary>
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Solo OAuthGoogle: ruta de la web a la que vuelve el proveedor. Es fija por entorno y
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

    /// <summary>Lo que ve la web: "inicia sesión en el proveedor", sin pegar claves.</summary>
    public const string OAuth = "OAuth";

    /// <summary>OAuth de Google: la cuenta de Google del usuario llama a Gemini (RF-37).</summary>
    public const string OAuthGoogle = "OAuthGoogle";

    public static bool EsOAuth(string? conexion) => conexion is OAuthGoogle;
}

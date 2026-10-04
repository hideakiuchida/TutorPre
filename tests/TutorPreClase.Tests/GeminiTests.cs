using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TutorPreClase.Application.Llm;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Infrastructure.Llm;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

/// <summary>Google simulado: el token de OAuth y la API de Gemini, sin salir a la red.</summary>
public sealed class GoogleSimulado : HttpMessageHandler, IHttpClientFactory
{
    public List<(HttpRequestMessage Peticion, string Cuerpo)> Recibidas { get; } = [];

    public Func<HttpRequestMessage, string, HttpResponseMessage> Token { get; set; } = (_, _) =>
        Json(new
        {
            access_token = "ya29.acceso",
            refresh_token = "1//renovacion-del-usuario",
            expires_in = 3599,
            scope = "openid https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/generative-language.retriever"
        });

    public Func<HttpRequestMessage, HttpResponseMessage> Gemini { get; set; } = _ => Sse(
        """{"candidates":[{"content":{"role":"model","parts":[{"text":"Hola"}]}}]}""",
        """{"candidates":[{"content":{"role":"model","parts":[{"text":" alumno"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":120,"candidatesTokenCount":4},"modelVersion":"gemini-2.5-flash"}""");

    public static HttpResponseMessage Json(object cuerpo, HttpStatusCode estado = HttpStatusCode.OK) =>
        new(estado) { Content = JsonContent.Create(cuerpo) };

    public static HttpResponseMessage Sse(params string[] eventos) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(eventos.Select(e => $"data: {e}\n\n"))) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage peticion, CancellationToken ct)
    {
        var cuerpo = peticion.Content is null ? "" : await peticion.Content.ReadAsStringAsync(ct);
        Recibidas.Add((peticion, cuerpo));

        return peticion.RequestUri!.Host == "oauth2.googleapis.com" ? Token(peticion, cuerpo) : Gemini(peticion);
    }

    public HttpClient CreateClient(string nombre) =>
        new(this, disposeHandler: false) { BaseAddress = new Uri("https://generativelanguage.googleapis.com") };
}

public class GeminiTests
{
    private static readonly OpcionesAgentes Agentes = new()
    {
        ["gemini"] = new OpcionesAgente
        {
            Nombre = "Gemini (tu cuenta de Google)",
            BaseUrl = "https://generativelanguage.googleapis.com",
            Modelo = "gemini-2.5-flash",
            Razonamiento = NivelRazonamiento.Desactivado,
            Conexion = ConexionAgente.OAuthGoogle,
            UrlAutorizacion = "https://accounts.google.com/o/oauth2/v2/auth",
            UrlToken = "https://oauth2.googleapis.com/token",
            Scope = "https://www.googleapis.com/auth/cloud-platform",
            UrlRetorno = "http://localhost:4200/conectar/gemini",
            ClientId = "cliente.apps.googleusercontent.com",
            ClientSecret = "secreto-de-prueba"
        },
        ["openrouter"] = new OpcionesAgente { Conexion = ConexionAgente.OAuth }
    };

    private static TokensGoogle Tokens(GoogleSimulado google) =>
        new(google, new MemoryCache(new MemoryCacheOptions()), Options.Create(Agentes), NullLogger<TokensGoogle>.Instance);

    private static GeminiProvider Proveedor(GoogleSimulado google, TokensGoogle? tokens = null) =>
        new(google.CreateClient("llm:gemini"),
            new OpcionesAgente { Modelo = "gemini-2.5-flash", ApiKey = "1//renovacion-del-usuario", Razonamiento = NivelRazonamiento.Desactivado },
            tokens ?? Tokens(google),
            NullLogger<GeminiProvider>.Instance);

    private static async Task<List<LlmEvento>> Conversar(GeminiProvider proveedor, LlmSolicitud solicitud)
    {
        var eventos = new List<LlmEvento>();
        await foreach (var e in proveedor.StreamAsync(solicitud, CancellationToken.None)) eventos.Add(e);
        return eventos;
    }

    private static readonly LlmSolicitud Hola = new(
        "gemini-2.5-flash", "Eres el tutor.", [new LlmMensaje(RolLlm.Usuario, "hola")], []);

    [Fact]
    public async Task Llama_a_Gemini_con_el_acceso_de_la_cuenta_de_Google_y_emite_el_texto()
    {
        var google = new GoogleSimulado();

        var eventos = await Conversar(Proveedor(google), Hola);

        var (peticion, _) = google.Recibidas.Last();
        Assert.Equal("/v1beta/models/gemini-2.5-flash:streamGenerateContent", peticion.RequestUri!.AbsolutePath);
        Assert.Equal("ya29.acceso", peticion.Headers.Authorization!.Parameter);

        Assert.Equal("Hola alumno", string.Concat(eventos.OfType<TextoParcial>().Select(t => t.Texto)));
        var fin = eventos.OfType<Fin>().Single();
        Assert.Equal((120, 4, false), (fin.TokensEntrada, fin.TokensSalida, fin.CortadaPorTope));
    }

    [Fact]
    public async Task El_cuerpo_lleva_el_prompt_el_historial_alternado_y_las_herramientas_que_Gemini_acepta()
    {
        var google = new GoogleSimulado();

        var sinParametros = JsonDocument.Parse("""{"type":"object","properties":{},"required":[]}""").RootElement;
        var conParametros = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false,"properties":{"alternativa":{"type":"string","enum":["A","B"]}},"required":["alternativa"]}""").RootElement;

        var solicitud = new LlmSolicitud("gemini-2.5-flash", "Eres el tutor.",
        [
            new LlmMensaje(RolLlm.Asistente, "Hola, ¿qué quieres hacer?"),
            new LlmMensaje(RolLlm.Usuario, "la B"),
            new LlmMensaje(RolLlm.Asistente, null, "id1", "registrar_respuesta",
                HerramientaArgumentos: JsonDocument.Parse("""{"alternativa":"B"}""").RootElement),
            new LlmMensaje(RolLlm.Herramienta, null, "id1", "registrar_respuesta", """{"ok":true}""")
        ],
        [
            new DefinicionHerramienta("obtener_progreso", "Progreso", sinParametros),
            new DefinicionHerramienta("registrar_respuesta", "Registra", conParametros)
        ]);

        await Conversar(Proveedor(google), solicitud);

        var cuerpo = JsonDocument.Parse(google.Recibidas.Last().Cuerpo).RootElement;
        Assert.Equal("Eres el tutor.", cuerpo.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(0, cuerpo.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
        Assert.False(cuerpo.GetProperty("generationConfig").TryGetProperty("maxOutputTokens", out _));

        var roles = cuerpo.GetProperty("contents").EnumerateArray().Select(c => c.GetProperty("role").GetString()!).ToArray();
        Assert.Equal(["user", "model", "user", "model", "user"], roles);

        var respuesta = cuerpo.GetProperty("contents")[4].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("registrar_respuesta", respuesta.GetProperty("name").GetString());
        Assert.True(respuesta.GetProperty("response").GetProperty("ok").GetBoolean());

        var declaraciones = cuerpo.GetProperty("tools")[0].GetProperty("functionDeclarations");
        Assert.False(declaraciones[0].TryGetProperty("parameters", out _));
        Assert.False(declaraciones[1].GetProperty("parameters").TryGetProperty("additionalProperties", out _));
    }

    [Fact]
    public async Task Con_Gemini_3_el_razonamiento_va_como_thinkingLevel()
    {
        var google = new GoogleSimulado();

        await Conversar(Proveedor(google), Hola with { Modelo = "gemini-3.8-flash" });

        var pensamiento = JsonDocument.Parse(google.Recibidas.Last().Cuerpo).RootElement
            .GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.Equal("low", pensamiento.GetProperty("thinkingLevel").GetString());
        Assert.False(pensamiento.TryGetProperty("thinkingBudget", out _));
    }

    [Fact]
    public async Task Un_modelo_retirado_se_reemplaza_al_arrancar_y_uno_elegido_no_se_toca()
    {
        using var banco = new BancoDePruebas().Sembrar();
        banco.Db.Agentes.Add(new AgenteIA { Id = "gemini", NombreVisible = "Gemini", Proveedor = "Google", Modelo = "gemini-2.5-flash", Habilitado = true });
        banco.Db.SaveChanges();

        await TutorPreClase.Infrastructure.Persistencia.DatosIniciales.SembrarAsync(banco.Db);

        Assert.Equal("gemini-3.8-flash", banco.Db.Agentes.Single(a => a.Id == "gemini").Modelo);
        Assert.Equal("qwen/qwen3.8-27b:free", banco.Db.Agentes.Single(a => a.Id == "openrouter").Modelo);
    }

    [Fact]
    public async Task Una_llamada_a_herramienta_y_el_tope_de_salida_se_traducen()
    {
        var google = new GoogleSimulado
        {
            Gemini = _ => GoogleSimulado.Sse(
                """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"obtener_siguiente_pregunta","args":{}}}]}}]}""",
                """{"candidates":[{"content":{"parts":[{"text":"…"}]},"finishReason":"MAX_TOKENS"}]}""")
        };

        var eventos = await Conversar(Proveedor(google), Hola);

        Assert.Equal("obtener_siguiente_pregunta", eventos.OfType<LlamadaHerramienta>().Single().Nombre);
        Assert.True(eventos.OfType<Fin>().Single().CortadaPorTope);
    }

    [Fact]
    public async Task La_firma_de_razonamiento_de_una_llamada_vuelve_tal_cual_en_la_vuelta_siguiente()
    {
        // Gemini 3 responde 400 ("missing a thought_signature") si la llamada vuelve sin su firma.
        var google = new GoogleSimulado
        {
            Gemini = _ => GoogleSimulado.Sse(
                """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"registrar_duda_sin_cobertura","args":{"texto":"WACC"}},"thoughtSignature":"firma-opaca=="}]}}]}""")
        };

        var llamada = (await Conversar(Proveedor(google), Hola)).OfType<LlamadaHerramienta>().Single();
        Assert.Equal("firma-opaca==", llamada.Firma);

        await Conversar(Proveedor(google), Hola with
        {
            Mensajes =
            [
                new LlmMensaje(RolLlm.Usuario, "¿qué es el WACC?"),
                new LlmMensaje(RolLlm.Asistente, null, llamada.Id, llamada.Nombre,
                    HerramientaArgumentos: llamada.Argumentos, HerramientaFirma: llamada.Firma),
                new LlmMensaje(RolLlm.Herramienta, null, llamada.Id, llamada.Nombre, """{"registrada":true}""")
            ]
        });

        var parte = JsonDocument.Parse(google.Recibidas.Last().Cuerpo).RootElement.GetProperty("contents")[1].GetProperty("parts")[0];
        Assert.Equal("registrar_duda_sin_cobertura", parte.GetProperty("functionCall").GetProperty("name").GetString());
        Assert.Equal("firma-opaca==", parte.GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public async Task El_cupo_agotado_de_Gemini_dice_cuando_se_puede_volver()
    {
        // Respuesta real al agotar las solicitudes gratuitas del día.
        const string cuerpo = """
            {"error":{"code":429,"message":"You exceeded your current quota. Please retry in 1h33m14s.","status":"RESOURCE_EXHAUSTED",
             "details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"5594s"}]}}
            """;
        var google = new GoogleSimulado
        {
            Gemini = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(cuerpo) }
        };

        var error = (await Conversar(Proveedor(google), Hola)).OfType<ErrorProveedor>().Single();

        Assert.True(error.LimiteDeUso);
        Assert.InRange((error.ReintentarEn!.Value - DateTimeOffset.UtcNow).TotalMinutes, 92, 94);
    }

    [Fact]
    public async Task Un_503_es_saturacion_del_modelo_y_no_muestra_el_codigo_al_alumno()
    {
        var google = new GoogleSimulado
        {
            Gemini = _ => GoogleSimulado.Json(new { error = new { code = 503, status = "UNAVAILABLE" } }, HttpStatusCode.ServiceUnavailable)
        };

        var error = (await Conversar(Proveedor(google), Hola)).OfType<ErrorProveedor>().Single();

        Assert.True(error is { LimiteDeUso: true, Saturado: true, CredencialRechazada: false });
        Assert.DoesNotContain("503", error.Mensaje);
    }

    [Fact]
    public async Task La_API_desactivada_no_invalida_la_credencial_del_usuario()
    {
        var google = new GoogleSimulado
        {
            Gemini = _ => GoogleSimulado.Json(new { error = new { status = "PERMISSION_DENIED", message = "Generative Language API has not been used in project 1 before or it is disabled. SERVICE_DISABLED" } },
                HttpStatusCode.Forbidden)
        };

        var error = (await Conversar(Proveedor(google), Hola)).OfType<ErrorProveedor>().Single();

        Assert.False(error.CredencialRechazada);
        Assert.Contains("no está activada", error.Mensaje);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false, true)]
    public async Task Un_401_pide_reconectar_y_un_429_es_limite_de_uso(HttpStatusCode estado, bool rechazada, bool limite)
    {
        var google = new GoogleSimulado { Gemini = _ => GoogleSimulado.Json(new { error = new { code = (int)estado } }, estado) };

        var error = (await Conversar(Proveedor(google), Hola)).OfType<ErrorProveedor>().Single();

        Assert.Equal((rechazada, limite), (error.CredencialRechazada, error.LimiteDeUso));
    }

    [Fact]
    public async Task El_acceso_se_reutiliza_y_un_acceso_revocado_pide_reconectar()
    {
        var google = new GoogleSimulado();
        var tokens = Tokens(google);

        await Conversar(Proveedor(google, tokens), Hola);
        await Conversar(Proveedor(google, tokens), Hola);
        Assert.Single(google.Recibidas, r => r.Peticion.RequestUri!.Host == "oauth2.googleapis.com");

        var revocado = new GoogleSimulado
        {
            Token = (_, _) => GoogleSimulado.Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest)
        };
        var error = (await Conversar(Proveedor(revocado), Hola)).OfType<ErrorProveedor>().Single();

        Assert.True(error.CredencialRechazada);
        Assert.DoesNotContain(revocado.Recibidas, r => r.Peticion.RequestUri!.Host == "generativelanguage.googleapis.com");
    }
}

public class GeminiApiTests
{
    [Fact]
    public async Task Con_el_json_de_Google_en_secrets_funcionan_el_inicio_de_sesion_y_la_conexion_de_Gemini()
    {
        var carpeta = Directory.CreateTempSubdirectory("secrets-").FullName;
        File.WriteAllText(Path.Combine(carpeta, "client_secret_prueba.apps.googleusercontent.com.json"),
            """{"web":{"client_id":"prueba.apps.googleusercontent.com","client_secret":"secreto-de-prueba"}}""");

        using var api = new ApiDePruebas(new Dictionary<string, string>
        {
            ["Acceso:CarpetaSecretos"] = carpeta,
            // Como en el contenedor local: tutor.env define las variables, pero vacías.
            ["Acceso:Proveedores:google:ClientId"] = "",
            ["Acceso:Proveedores:google:ClientSecret"] = ""
        });
        var (_, alumnoId, _, _) = api.Sembrar();

        var proveedores = await api.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/acceso/proveedores");
        var google = proveedores.GetProperty("proveedores").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "google");
        Assert.True(google.GetProperty("configurado").GetBoolean());

        var inicio = await api.Como(alumnoId, "Alumno").PostAsync("/api/v1/alumno/credenciales/gemini/oauth/inicio", null);
        var cuerpo = await inicio.Content.ReadAsStringAsync();
        Assert.True(inicio.IsSuccessStatusCode, cuerpo);
        Assert.Contains("client_id=prueba.apps.googleusercontent.com", cuerpo);
    }
}

public class ConexionGoogleGeminiTests
{
    private static readonly OpcionesAgentes Agentes = new()
    {
        ["gemini"] = new OpcionesAgente
        {
            Conexion = ConexionAgente.OAuthGoogle,
            UrlAutorizacion = "https://accounts.google.com/o/oauth2/v2/auth",
            UrlToken = "https://oauth2.googleapis.com/token",
            Scope = "https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/generative-language.retriever",
            UrlRetorno = "http://localhost:4200/conectar/gemini",
            ClientId = "cliente.apps.googleusercontent.com",
            ClientSecret = "secreto-de-prueba"
        },
        ["openrouter"] = new OpcionesAgente { Conexion = ConexionAgente.OAuth }
    };

    private static (BancoDePruebas Banco, ConexionGoogleGemini Conexion, GoogleSimulado Google) Montar()
    {
        var banco = new BancoDePruebas().Sembrar();
        banco.Db.Agentes.Add(new AgenteIA { Id = "gemini", NombreVisible = "Gemini", Proveedor = "Google", Modelo = "gemini-2.5-flash", Habilitado = true });
        banco.Db.SaveChanges();

        var google = new GoogleSimulado();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tokens = new TokensGoogle(google, cache, Options.Create(Agentes), NullLogger<TokensGoogle>.Instance);

        return (banco, new ConexionGoogleGemini(cache, tokens, banco.Boveda, banco.Db, Options.Create(Agentes)), google);
    }

    private static Dictionary<string, string> Parametros(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public async Task La_url_pide_la_cuenta_de_Google_con_PKCE_acceso_offline_y_el_retorno_fijo()
    {
        var (banco, conexion, _) = Montar();
        using var _b = banco;

        var url = await conexion.IniciarAsync(banco.AlumnoId, "gemini");
        var p = Parametros(url);

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Equal("cliente.apps.googleusercontent.com", p["client_id"]);
        Assert.Equal("https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/generative-language.retriever", p["scope"]);
        Assert.Equal("http://localhost:4200/conectar/gemini", p["redirect_uri"]);
        Assert.Equal(("offline", "S256", "code"), (p["access_type"], p["code_challenge_method"], p["response_type"]));
        Assert.Equal("alumna@uni.edu", p["login_hint"]);
    }

    [Fact]
    public async Task El_canje_envia_el_verificador_y_guarda_el_refresh_token_una_sola_vez()
    {
        var (banco, conexion, google) = Montar();
        using var _b = banco;

        var desafio = Parametros(await conexion.IniciarAsync(banco.AlumnoId, "gemini"))["code_challenge"];

        var resumen = await conexion.CanjearAsync(banco.AlumnoId, "gemini", "4/codigo-de-google");

        var formulario = google.Recibidas.First(r => r.Peticion.RequestUri!.Host == "oauth2.googleapis.com").Cuerpo
            .Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("authorization_code", formulario["grant_type"]);
        Assert.Equal(desafio, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(formulario["code_verifier"]))));

        Assert.Equal(OrigenCredencial.OAuth, resumen.Origen);
        Assert.Equal("1//renovacion-del-usuario", await banco.Boveda.ClaveParaAsync(banco.AlumnoId, "gemini"));

        var repetido = await Assert.ThrowsAsync<CredencialException>(
            () => conexion.CanjearAsync(banco.AlumnoId, "gemini", "4/codigo-de-google"));
        Assert.Equal("oauth_vencido", repetido.Codigo);
    }

    [Fact]
    public async Task Si_el_usuario_no_marca_el_permiso_de_Google_Cloud_se_le_explica_y_no_se_guarda_nada()
    {
        var (banco, conexion, google) = Montar();
        using var _b = banco;

        // En la pantalla de Google el permiso sensible es una casilla: sin marcarla, no viene.
        google.Token = (_, _) => GoogleSimulado.Json(new
        {
            // Marcó un permiso pero no el otro: igual falta.
            access_token = "ya29.acceso", refresh_token = "1//renovacion", expires_in = 3599,
            scope = "openid https://www.googleapis.com/auth/cloud-platform"
        });

        await conexion.IniciarAsync(banco.AlumnoId, "gemini");
        var error = await Assert.ThrowsAsync<CredencialException>(
            () => conexion.CanjearAsync(banco.AlumnoId, "gemini", "4/codigo"));

        Assert.Equal("oauth_permiso_incompleto", error.Codigo);
        Assert.Contains("marca la casilla", error.Message);
        Assert.Null(await banco.Boveda.ClaveParaAsync(banco.AlumnoId, "gemini"));
    }

    [Fact]
    public async Task El_despachador_elige_Google_para_Gemini_y_OpenRouter_para_el_resto()
    {
        var (banco, google, _) = Montar();
        using var _b = banco;

        var openRouter = new ConexionOpenRouter(
            new MemoryCache(new MemoryCacheOptions()), new OpenRouterSimulado(), banco.Boveda,
            Options.Create(new OpcionesAgentes
            {
                ["openrouter"] = new OpcionesAgente
                {
                    Conexion = ConexionAgente.OAuth,
                    UrlAutorizacion = "https://openrouter.ai/auth",
                    UrlRetorno = "http://localhost:4200/conectar/openrouter"
                }
            }),
            NullLogger<ConexionOpenRouter>.Instance);

        var conexiones = new ConexionesOAuth(openRouter, google, Options.Create(Agentes));

        Assert.StartsWith("https://accounts.google.com/", await conexiones.IniciarAsync(banco.AlumnoId, "gemini"));
        Assert.StartsWith("https://openrouter.ai/auth", await conexiones.IniciarAsync(banco.AlumnoId, "openrouter"));
    }
}

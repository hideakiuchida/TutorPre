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
using TutorPreClase.Application.Tutor;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Infrastructure.Llm;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

/// <summary>OpenRouter simulado: responde lo que el test le pida y guarda lo que recibio.</summary>
public sealed class OpenRouterSimulado : HttpMessageHandler, IHttpClientFactory
{
    public List<(HttpRequestMessage Peticion, string Cuerpo)> Recibidas { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { key = "sk-or-v1-clave-obtenida-por-oauth-7777" })
        };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage peticion, CancellationToken ct)
    {
        var cuerpo = peticion.Content is null ? "" : await peticion.Content.ReadAsStringAsync(ct);
        Recibidas.Add((peticion, cuerpo));
        return Responder(peticion);
    }

    public HttpClient CreateClient(string nombre) =>
        new(this, disposeHandler: false) { BaseAddress = new Uri("https://openrouter.ai") };
}

public class ConexionOpenRouterTests
{
    private const string Retorno = "http://localhost:4200/conectar/openrouter";

    private static (BancoDePruebas Banco, ConexionOpenRouter Oauth, OpenRouterSimulado OpenRouter) Montar()
    {
        // El banco deja la cuenta conectada; aqui se parte de un alumno sin conectar.
        var banco = new BancoDePruebas().Sembrar();
        banco.Boveda.DesconectarAsync(banco.AlumnoId, "openrouter").GetAwaiter().GetResult();

        var agentes = new OpcionesAgentes
        {
            ["openrouter"] = new OpcionesAgente
            {
                Nombre = "OpenRouter (gratis)",
                BaseUrl = "https://openrouter.ai",
                Conexion = ConexionAgente.OAuth,
                UrlAutorizacion = "https://openrouter.ai/auth",
                UrlRetorno = Retorno
            },
            ["sin-oauth"] = new OpcionesAgente { Nombre = "Sin OAuth" }
        };

        var openRouter = new OpenRouterSimulado();
        var oauth = new ConexionOpenRouter(
            new MemoryCache(new MemoryCacheOptions()),
            openRouter,
            banco.Boveda,
            Options.Create(agentes),
            NullLogger<ConexionOpenRouter>.Instance);

        return (banco, oauth, openRouter);
    }

    private static Dictionary<string, string> Parametros(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public async Task La_url_de_autorizacion_lleva_el_retorno_fijo_y_el_desafio_S256()
    {
        var (banco, oauth, _) = Montar();
        using var _b = banco;

        var url = await oauth.IniciarAsync(banco.AlumnoId, "openrouter");
        var parametros = Parametros(url);

        Assert.StartsWith("https://openrouter.ai/auth?", url);
        Assert.Equal(Retorno, parametros["callback_url"]);
        Assert.Equal("S256", parametros["code_challenge_method"]);
        Assert.Equal(43, parametros["code_challenge"].Length);
    }

    [Fact]
    public async Task El_canje_envia_el_verificador_del_alumno_y_guarda_la_clave_como_OAuth()
    {
        var (banco, oauth, openRouter) = Montar();
        using var _b = banco;

        var desafio = Parametros(await oauth.IniciarAsync(banco.AlumnoId, "openrouter"))["code_challenge"];

        var resumen = await oauth.CanjearAsync(banco.AlumnoId, "openrouter", "codigo-de-openrouter");

        var (peticion, cuerpo) = openRouter.Recibidas.Single();
        Assert.Equal("/api/v1/auth/keys", peticion.RequestUri!.AbsolutePath);

        // El verificador enviado corresponde al desafio que vio OpenRouter.
        var json = JsonDocument.Parse(cuerpo).RootElement;
        Assert.Equal("codigo-de-openrouter", json.GetProperty("code").GetString());
        var verificador = json.GetProperty("code_verifier").GetString()!;
        Assert.Equal(desafio, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verificador))));

        Assert.Equal("7777", resumen.Ultimos4);
        Assert.Equal(OrigenCredencial.OAuth, resumen.Origen);
        Assert.Equal("sk-or-v1-clave-obtenida-por-oauth-7777",
            await banco.Boveda.ClaveParaAsync(banco.AlumnoId, "openrouter"));
    }

    [Fact]
    public async Task Un_codigo_no_se_puede_canjear_dos_veces()
    {
        var (banco, oauth, _) = Montar();
        using var _b = banco;

        await oauth.IniciarAsync(banco.AlumnoId, "openrouter");
        await oauth.CanjearAsync(banco.AlumnoId, "openrouter", "codigo");

        var error = await Assert.ThrowsAsync<CredencialException>(
            () => oauth.CanjearAsync(banco.AlumnoId, "openrouter", "codigo"));

        Assert.Equal("oauth_vencido", error.Codigo);
    }

    [Fact]
    public async Task Otro_alumno_no_puede_canjear_un_codigo_que_no_inicio_el()
    {
        var (banco, oauth, openRouter) = Montar();
        using var _b = banco;

        await oauth.IniciarAsync(banco.AlumnoId, "openrouter");

        var error = await Assert.ThrowsAsync<CredencialException>(
            () => oauth.CanjearAsync(Guid.NewGuid(), "openrouter", "codigo-robado"));

        Assert.Equal("oauth_vencido", error.Codigo);
        Assert.Empty(openRouter.Recibidas);
    }

    [Fact]
    public async Task Si_OpenRouter_rechaza_el_canje_no_se_guarda_nada_y_el_verificador_ya_no_sirve()
    {
        var (banco, oauth, openRouter) = Montar();
        using var _b = banco;
        openRouter.Responder = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

        await oauth.IniciarAsync(banco.AlumnoId, "openrouter");

        var error = await Assert.ThrowsAsync<CredencialException>(
            () => oauth.CanjearAsync(banco.AlumnoId, "openrouter", "codigo"));

        Assert.Equal("oauth_rechazado", error.Codigo);
        Assert.Empty(banco.Db.Credenciales.Where(c => c.AgenteId == "openrouter"));

        var repetido = await Assert.ThrowsAsync<CredencialException>(
            () => oauth.CanjearAsync(banco.AlumnoId, "openrouter", "codigo"));
        Assert.Equal("oauth_vencido", repetido.Codigo);
    }

    [Fact]
    public async Task Un_agente_sin_OAuth_configurado_no_se_puede_conectar()
    {
        var (banco, oauth, _) = Montar();
        using var _b = banco;

        var error = await Assert.ThrowsAsync<CredencialException>(
            () => oauth.IniciarAsync(banco.AlumnoId, "sin-oauth"));

        Assert.Equal("oauth_no_disponible", error.Codigo);
    }
}

public class OpenRouterProviderTests
{
    private static readonly LlmSolicitud Solicitud = new(
        "qwen/qwen3.8-27b:free", "Eres el tutor.", [new LlmMensaje(RolLlm.Usuario, "hola")], []);

    private static OpenRouterProvider Proveedor(OpenRouterSimulado simulado) =>
        new(simulado.CreateClient("llm:openrouter"),
            new OpcionesAgente { Modelo = "qwen/qwen3.8-27b:free", ApiKey = "sk-or-v1-clave-del-alumno" },
            NullLogger<OpenRouterProvider>.Instance);

    [Fact]
    public async Task Llama_al_chat_bajo_api_v1_con_la_clave_del_alumno()
    {
        var simulado = new OpenRouterSimulado
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"Hola\"}}]}\n\ndata: [DONE]\n\n")
            }
        };

        var eventos = new List<LlmEvento>();
        await foreach (var e in Proveedor(simulado).StreamAsync(Solicitud, CancellationToken.None)) eventos.Add(e);

        var (peticion, cuerpo) = simulado.Recibidas.Single();
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", peticion.RequestUri!.ToString());
        Assert.Equal("sk-or-v1-clave-del-alumno", peticion.Headers.Authorization!.Parameter);
        Assert.Equal("Hola", Assert.Single(eventos.OfType<TextoParcial>()).Texto);

        // Solo proveedores que no guardan los prompts (RNF-11), sin que el alumno configure nada.
        var proveedor = JsonDocument.Parse(cuerpo).RootElement.GetProperty("provider");
        Assert.Equal("deny", proveedor.GetProperty("data_collection").GetString());
    }

    [Fact]
    public async Task Un_429_es_limite_de_uso_con_la_hora_de_reinicio_y_no_credencial_rechazada()
    {
        var reinicio = DateTimeOffset.UtcNow.AddMinutes(30);
        var simulado = new OpenRouterSimulado
        {
            Responder = _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
                r.Headers.Add("X-RateLimit-Reset", reinicio.ToUnixTimeMilliseconds().ToString());
                return r;
            }
        };

        var eventos = new List<LlmEvento>();
        await foreach (var e in Proveedor(simulado).StreamAsync(Solicitud, CancellationToken.None)) eventos.Add(e);

        var error = Assert.Single(eventos.OfType<ErrorProveedor>());
        Assert.True(error.LimiteDeUso);
        Assert.False(error.CredencialRechazada);
        Assert.Equal(reinicio.ToUnixTimeMilliseconds(), error.ReintentarEn!.Value.ToUnixTimeMilliseconds());
        Assert.False(error.Saturado);
    }

    [Fact]
    public async Task Un_429_del_pool_compartido_del_modelo_gratuito_es_saturacion_no_cupo_del_alumno()
    {
        // Respuesta real de OpenRouter cuando el proveedor del modelo :free esta saturado.
        const string cuerpo = """
            {"error":{"message":"Provider returned error","code":429,"metadata":{"raw":"qwen/qwen3.8-27b:free is temporarily rate-limited upstream.","provider_name":"ModelRun","limit_source":"upstream_provider_shared_pool"}}}
            """;
        var simulado = new OpenRouterSimulado
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(cuerpo) }
        };

        var eventos = new List<LlmEvento>();
        await foreach (var e in Proveedor(simulado).StreamAsync(Solicitud, CancellationToken.None)) eventos.Add(e);

        var error = Assert.Single(eventos.OfType<ErrorProveedor>());
        Assert.True(error.LimiteDeUso);
        Assert.True(error.Saturado);
    }

    [Theory]
    // OpenAI responde 429 y Anthropic 400 cuando la cuenta no tiene créditos.
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"message":"You have no credits remaining. Add credits to continue using the API","type":"insufficient_quota"}}""", true)]
    [InlineData(HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API."}}""", true)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit reached for requests","type":"rate_limit_exceeded"}}""", false)]
    public async Task Una_cuenta_sin_creditos_no_se_confunde_con_un_limite_pasajero(HttpStatusCode estado, string cuerpo, bool sinSaldo)
    {
        var simulado = new OpenRouterSimulado
        {
            Responder = _ => new HttpResponseMessage(estado) { Content = new StringContent(cuerpo) }
        };

        var eventos = new List<LlmEvento>();
        await foreach (var e in Proveedor(simulado).StreamAsync(Solicitud, CancellationToken.None)) eventos.Add(e);

        var error = Assert.Single(eventos.OfType<ErrorProveedor>());
        Assert.Equal(sinSaldo, error.SinSaldo);
        Assert.True(error.LimiteDeUso);
        Assert.False(error.CredencialRechazada);
    }

    [Fact]
    public async Task Con_modelos_alternativos_pide_a_OpenRouter_que_pase_al_siguiente_si_falla_el_principal()
    {
        var simulado = new OpenRouterSimulado
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n") }
        };
        var proveedor = new OpenRouterProvider(simulado.CreateClient("llm:openrouter"),
            new OpcionesAgente
            {
                Modelo = "qwen/qwen3.8-27b:free",
                ModelosAlternativos = ["google/gemma-4-31b-it:free"],
                ApiKey = "sk-or-v1-clave-del-alumno"
            },
            NullLogger<OpenRouterProvider>.Instance);

        await foreach (var _ in proveedor.StreamAsync(Solicitud, CancellationToken.None)) { }

        var raiz = JsonDocument.Parse(simulado.Recibidas.Single().Cuerpo).RootElement;
        Assert.Equal("qwen/qwen3.8-27b:free", raiz.GetProperty("model").GetString());
        Assert.Equal(["qwen/qwen3.8-27b:free", "google/gemma-4-31b-it:free"],
            raiz.GetProperty("models").EnumerateArray().Select(m => m.GetString()!).ToArray());
    }

    [Fact]
    public async Task Sin_modelos_alternativos_no_envia_la_lista()
    {
        var simulado = new OpenRouterSimulado
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n") }
        };

        await foreach (var _ in Proveedor(simulado).StreamAsync(Solicitud, CancellationToken.None)) { }

        Assert.False(JsonDocument.Parse(simulado.Recibidas.Single().Cuerpo).RootElement.TryGetProperty("models", out _));
    }
}

public class LimiteDeUsoEnElChatTests
{
    [Fact]
    public async Task Un_limite_de_uso_se_avisa_sin_invalidar_la_credencial()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");

        banco.Proveedor.Responde(new ErrorProveedor(
            "El proveedor respondio 429.", true, LimiteDeUso: true, ReintentarEn: banco.Reloj.Ahora.AddMinutes(20)));

        var eventos = await banco.EnviarAsync(conversacion.Id, "hola");

        var aviso = eventos.OfType<EventoAviso>().Single();
        Assert.Equal("limite_de_uso", aviso.Codigo);
        Assert.Contains("20 min", aviso.Mensaje);
        Assert.DoesNotContain("credencial", aviso.Mensaje);
        Assert.Equal(EstadoCredencial.Valida, banco.Db.Credenciales.Single().Estado);
    }

    [Fact]
    public async Task Un_modelo_saturado_se_avisa_como_saturacion_y_no_como_cupo_agotado()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");

        banco.Proveedor.Responde(new ErrorProveedor(
            "El proveedor respondio 429.", true, LimiteDeUso: true, Saturado: true));

        var eventos = await banco.EnviarAsync(conversacion.Id, "hola");

        var aviso = eventos.OfType<EventoAviso>().Single();
        Assert.Equal("limite_de_uso", aviso.Codigo);
        Assert.Contains("saturado", aviso.Mensaje);
        Assert.DoesNotContain("Se acabó tu cupo", aviso.Mensaje);
        Assert.Equal(EstadoCredencial.Valida, banco.Db.Credenciales.Single().Estado);
    }
}

public class SinSaldoTests
{
    [Fact]
    public async Task El_chat_dice_que_falta_saldo_y_no_que_vuelva_mas_tarde()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");

        banco.Proveedor.Responde(new ErrorProveedor("sin saldo", false, LimiteDeUso: true, SinSaldo: true));
        var aviso = (await banco.EnviarAsync(conversacion.Id, "hola")).OfType<EventoAviso>().Single();

        Assert.Contains("no tiene saldo", aviso.Mensaje);
        Assert.DoesNotContain("más tarde", aviso.Mensaje);
        Assert.Equal(EstadoCredencial.Valida, banco.Db.Credenciales.Single().Estado);
    }
}

public class OpenRouterApiTests
{
    [Fact]
    public async Task El_inicio_devuelve_la_url_de_OpenRouter_con_el_retorno_de_la_configuracion()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();

        var respuesta = await api.Como(alumnoId, "Alumno")
            .PostAsync("/api/v1/alumno/credenciales/openrouter/oauth/inicio", null);
        var url = (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.StartsWith("https://openrouter.ai/auth?", url);
        Assert.Contains(Uri.EscapeDataString("http://localhost:4200/conectar/openrouter"), url);
    }

    [Fact]
    public async Task Canjear_sin_haber_iniciado_responde_400_sin_llamar_a_OpenRouter()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();

        var respuesta = await api.Como(alumnoId, "Alumno")
            .PostAsJsonAsync("/api/v1/alumno/credenciales/openrouter/oauth/canje", new { code = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Contains("oauth_vencido", await respuesta.Content.ReadAsStringAsync());
    }
}

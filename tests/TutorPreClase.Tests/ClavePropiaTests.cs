using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TutorPreClase.Application.Llm;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Infrastructure.Llm;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

/// <summary>
/// El alumno pega su propia clave de OpenAI, Anthropic o Gemini (RF-38): se valida contra
/// el proveedor con su cabecera, se guarda cifrada y nunca vuelve a salir.
/// </summary>
public class ClavePropiaTests
{
    private static AgenteIA Agente(string id, string proveedor) =>
        new() { Id = id, NombreVisible = id, Proveedor = proveedor, Modelo = "m", Habilitado = true };

    private static (ValidadorCredencial Validador, HttpSimulado Http) Validador(HttpStatusCode estado = HttpStatusCode.OK)
    {
        var http = new HttpSimulado { Responder = _ => new HttpResponseMessage(estado) { Content = new StringContent("{}") } };
        var tokens = new TokensGoogle(http, new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new OpcionesAgentes()), NullLogger<TokensGoogle>.Instance);

        return (new ValidadorCredencial(http, tokens, NullLogger<ValidadorCredencial>.Instance), http);
    }

    [Fact]
    public async Task Cada_proveedor_se_valida_con_su_cabecera_y_sin_gastar_cupo()
    {
        var (validador, http) = Validador();

        Assert.True(await validador.EsUsableAsync(Agente("openai", "OpenAI"), "sk-proj-clave-0001"));
        Assert.True(await validador.EsUsableAsync(Agente("anthropic", "Anthropic"), "sk-ant-api03-clave-0002"));
        Assert.True(await validador.EsUsableAsync(Agente("gemini-clave", "GoogleAIStudio"), "AIzaSy-clave-0003"));

        var (openai, anthropic, gemini) = (http.Recibidas[0].Peticion, http.Recibidas[1].Peticion, http.Recibidas[2].Peticion);

        Assert.All(http.Recibidas, r => Assert.Equal(HttpMethod.Get, r.Peticion.Method));

        Assert.Equal("/v1/models", openai.RequestUri!.AbsolutePath);
        Assert.Equal("sk-proj-clave-0001", openai.Headers.Authorization!.Parameter);

        Assert.Equal("/v1/models", anthropic.RequestUri!.AbsolutePath);
        Assert.Equal("sk-ant-api03-clave-0002", anthropic.Headers.GetValues("x-api-key").Single());
        Assert.True(anthropic.Headers.Contains("anthropic-version"));

        Assert.Equal("/v1beta/models", gemini.RequestUri!.AbsolutePath);
        Assert.Equal("AIzaSy-clave-0003", gemini.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task Una_clave_que_el_proveedor_rechaza_no_es_usable()
    {
        var (validador, _) = Validador(HttpStatusCode.Unauthorized);

        Assert.False(await validador.EsUsableAsync(Agente("openai", "OpenAI"), "sk-proj-mala-0000"));
    }

    [Fact]
    public async Task Google_rechaza_una_clave_mala_con_400_y_tampoco_es_usable()
    {
        var (validador, http) = Validador();
        http.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT","details":[{"reason":"API_KEY_INVALID"}]}}""")
        };

        Assert.False(await validador.EsUsableAsync(Agente("gemini-clave", "GoogleAIStudio"), "AIzaSy-clave-mala-0000"));

        // Otro 400 (p. ej. un parámetro que el proveedor ya no admite) no culpa a la clave.
        http.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"pageSize"}}""") };
        Assert.True(await validador.EsUsableAsync(Agente("gemini-clave", "GoogleAIStudio"), "AIzaSy-clave-0003"));
    }

    [Fact]
    public async Task El_chat_va_a_la_ruta_del_proveedor_y_apaga_el_razonamiento_si_lo_admite()
    {
        var http = new HttpSimulado
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n") }
        };
        var proveedor = new OpenAiProvider(http.CreateClient("llm:gemini-clave"),
            new OpcionesAgente
            {
                Modelo = "gemini-3.8-flash",
                ApiKey = "AIzaSy-clave",
                RutaChat = "/v1beta/openai/chat/completions",
                EsfuerzoDesactivado = "minimal",
                Razonamiento = NivelRazonamiento.Desactivado
            },
            NullLogger<OpenAiProvider>.Instance, "gemini-clave");

        await foreach (var _ in proveedor.StreamAsync(
                           new LlmSolicitud("gemini-3.8-flash", "p", [new LlmMensaje(RolLlm.Usuario, "hola")], []),
                           CancellationToken.None)) { }

        var (peticion, cuerpo) = http.Recibidas.Single();
        Assert.Equal("/v1beta/openai/chat/completions", peticion.RequestUri!.AbsolutePath);
        Assert.Equal("AIzaSy-clave", peticion.Headers.Authorization!.Parameter);

        var json = JsonDocument.Parse(cuerpo).RootElement;
        Assert.Equal("minimal", json.GetProperty("reasoning_effort").GetString());
        // Solo campos del formato OpenAI.
        Assert.False(json.TryGetProperty("provider", out _));
        Assert.False(json.TryGetProperty("reasoning", out _));
    }

    private static void HabilitarAgentes(ApiDePruebas api)
    {
        using var db = api.NuevoContexto();
        db.Agentes.AddRange(
            new AgenteIA { Id = "openai", NombreVisible = "ChatGPT (tu clave de OpenAI)", Proveedor = "OpenAI", Modelo = "gpt-5-mini", Habilitado = true },
            new AgenteIA { Id = "anthropic", NombreVisible = "Claude (tu clave de Anthropic)", Proveedor = "Anthropic", Modelo = "claude-haiku-4-5", Habilitado = true },
            new AgenteIA { Id = "gemini-clave", NombreVisible = "Gemini (tu clave de AI Studio)", Proveedor = "GoogleAIStudio", Modelo = "gemini-3.8-flash", Habilitado = true });
        db.SaveChanges();
    }

    [Fact]
    public async Task La_clave_pegada_se_guarda_y_la_api_solo_devuelve_los_ultimos_4()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();
        HabilitarAgentes(api);
        var alumno = api.Como(alumnoId, "Alumno");

        var respuesta = await alumno.PutAsJsonAsync("/api/v1/alumno/credenciales/openai",
            new { clave = "  sk-proj-mi-clave-secreta-9876  " });

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("mi-clave-secreta", cuerpo);
        Assert.Contains("\"origen\":\"Pegada\"", cuerpo);

        var listado = await alumno.GetStringAsync("/api/v1/alumno/credenciales");
        Assert.DoesNotContain("mi-clave-secreta", listado);
        Assert.Contains("9876", listado);
    }

    [Theory]
    [InlineData("AIzaSy-clave-antigua-0003")]
    [InlineData("AQ.Ab8-clave-nueva-de-ai-studio-0004")]
    public async Task Gemini_acepta_las_claves_de_AI_Studio_con_el_formato_antiguo_y_el_nuevo(string clave)
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();
        HabilitarAgentes(api);

        var respuesta = await api.Como(alumnoId, "Alumno")
            .PutAsJsonAsync("/api/v1/alumno/credenciales/gemini-clave", new { clave });

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(clave[^4..], (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ultimos4").GetString());
    }

    [Fact]
    public async Task Una_clave_con_la_forma_de_otro_proveedor_se_rechaza_sin_guardarla()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();
        HabilitarAgentes(api);
        var alumno = api.Como(alumnoId, "Alumno");

        var respuesta = await alumno.PutAsJsonAsync("/api/v1/alumno/credenciales/anthropic",
            new { clave = "sk-proj-esta-es-de-openai-5555" });

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Contains("clave_invalida", await respuesta.Content.ReadAsStringAsync());
        Assert.DoesNotContain("5555", await alumno.GetStringAsync("/api/v1/alumno/credenciales"));
    }

    [Fact]
    public async Task Un_agente_de_inicio_de_sesion_sigue_sin_aceptar_claves()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();

        var respuesta = await api.Como(alumnoId, "Alumno")
            .PutAsJsonAsync("/api/v1/alumno/credenciales/gemini", new { clave = "AIzaSy-pegada-a-mano-0000" });

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Contains("conexion_por_oauth", await respuesta.Content.ReadAsStringAsync());
    }
}

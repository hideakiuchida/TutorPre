using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Tutor;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Infrastructure.Llm;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

/// <summary>Proveedor HTTP simulado: responde lo que el test le pida y guarda lo que recibio.</summary>
public sealed class HttpSimulado : HttpMessageHandler, IHttpClientFactory
{
    public List<(HttpRequestMessage Peticion, string Cuerpo)> Recibidas { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage peticion, CancellationToken ct)
    {
        var cuerpo = peticion.Content is null ? "" : await peticion.Content.ReadAsStringAsync(ct);
        Recibidas.Add((peticion, cuerpo));
        return Responder(peticion);
    }

    public HttpClient CreateClient(string nombre) =>
        new(this, disposeHandler: false) { BaseAddress = new Uri("https://ia.prueba") };
}

public class OpenAiProviderTests
{
    private static readonly LlmSolicitud Solicitud = new(
        "gpt-5-mini", "Eres el tutor.", [new LlmMensaje(RolLlm.Usuario, "hola")], []);

    private static OpenAiProvider Proveedor(HttpSimulado simulado) =>
        new(simulado.CreateClient("llm:openai"),
            new OpcionesAgente { Modelo = "gpt-5-mini", ApiKey = "sk-proj-clave-del-alumno" },
            NullLogger<OpenAiProvider>.Instance);

    [Fact]
    public async Task Llama_al_chat_con_la_clave_del_alumno_y_solo_con_campos_del_formato_OpenAI()
    {
        var simulado = new HttpSimulado
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
        Assert.Equal("/v1/chat/completions", peticion.RequestUri!.AbsolutePath);
        Assert.Equal("sk-proj-clave-del-alumno", peticion.Headers.Authorization!.Parameter);
        Assert.Equal("Hola", Assert.Single(eventos.OfType<TextoParcial>()).Texto);

        var raiz = JsonDocument.Parse(cuerpo).RootElement;
        Assert.Equal("gpt-5-mini", raiz.GetProperty("model").GetString());
        Assert.False(raiz.TryGetProperty("models", out _));
        Assert.False(raiz.TryGetProperty("provider", out _));
    }

    [Fact]
    public async Task Un_429_es_limite_de_uso_con_la_espera_que_pide_el_proveedor_y_no_credencial_rechazada()
    {
        var simulado = new HttpSimulado
        {
            Responder = _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
                return r;
            }
        };

        var antes = DateTimeOffset.UtcNow;
        var eventos = new List<LlmEvento>();
        await foreach (var e in Proveedor(simulado).StreamAsync(Solicitud, CancellationToken.None)) eventos.Add(e);

        var error = Assert.Single(eventos.OfType<ErrorProveedor>());
        Assert.True(error.LimiteDeUso);
        Assert.False(error.CredencialRechazada);
        Assert.False(error.Saturado);
        Assert.InRange(error.ReintentarEn!.Value, antes.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));
    }

    [Theory]
    // OpenAI responde 429 y Anthropic 400 cuando la cuenta no tiene créditos.
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"message":"You have no credits remaining. Add credits to continue using the API","type":"insufficient_quota"}}""", true)]
    [InlineData(HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API."}}""", true)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit reached for requests","type":"rate_limit_exceeded"}}""", false)]
    public async Task Una_cuenta_sin_creditos_no_se_confunde_con_un_limite_pasajero(HttpStatusCode estado, string cuerpo, bool sinSaldo)
    {
        var simulado = new HttpSimulado
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
}

public class LimiteDeUsoEnElChatTests
{
    [Fact]
    public async Task Un_limite_de_uso_se_avisa_sin_invalidar_la_credencial()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

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
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

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
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

        banco.Proveedor.Responde(new ErrorProveedor("sin saldo", false, LimiteDeUso: true, SinSaldo: true));
        var aviso = (await banco.EnviarAsync(conversacion.Id, "hola")).OfType<EventoAviso>().Single();

        Assert.Contains("no tiene saldo", aviso.Mensaje);
        Assert.DoesNotContain("más tarde", aviso.Mensaje);
        Assert.Equal(EstadoCredencial.Valida, banco.Db.Credenciales.Single().Estado);
    }
}

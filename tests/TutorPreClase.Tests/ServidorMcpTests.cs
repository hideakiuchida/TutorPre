using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

/// <summary>
/// El Tutor como servidor MCP (RF-39): el asistente del alumno entra con un token personal
/// que se guarda hasheado, ve solo sus clases y rinde el examen con las reglas del servidor.
/// </summary>
public class ServidorMcpTests
{
    private static async Task<string> GenerarToken(ApiDePruebas api, Guid alumnoId)
    {
        var respuesta = await api.Como(alumnoId, "Alumno").PostAsync("/api/v1/alumno/token-mcp", null);
        respuesta.EnsureSuccessStatusCode();
        return (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("valor").GetString()!;
    }

    private static Task<McpClient> Conectar(ApiDePruebas api, string token)
    {
        var transporte = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(api.Server.BaseAddress, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" }
        }, api.CreateClient());

        return McpClient.CreateAsync(transporte);
    }

    private static string Texto(CallToolResult resultado) =>
        string.Concat(resultado.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task El_token_se_muestra_una_vez_y_solo_se_guarda_su_hash()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();

        var token = await GenerarToken(api, alumnoId);

        Assert.StartsWith("tpc_mcp_", token);
        using var db = api.NuevoContexto();
        var guardado = db.TokensMcp.Single();
        Assert.NotEqual(token, guardado.Hash);
        Assert.DoesNotContain(token, guardado.Hash);
        Assert.Equal(token[^4..], guardado.Ultimos4);

        var estado = await api.Como(alumnoId, "Alumno").GetStringAsync("/api/v1/alumno/token-mcp");
        Assert.DoesNotContain(token, estado);
        Assert.Contains("/mcp", estado);
    }

    [Fact]
    public async Task Sin_token_con_la_sesion_de_la_web_o_con_uno_revocado_no_se_entra()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();
        var token = await GenerarToken(api, alumnoId);

        async Task<HttpStatusCode> Llamar(Action<HttpRequestHeaders> cabeceras)
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" })
            };
            peticion.Headers.Accept.ParseAdd("application/json");
            peticion.Headers.Accept.ParseAdd("text/event-stream");
            cabeceras(peticion.Headers);
            return (await api.CreateClient().SendAsync(peticion)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await Llamar(_ => { }));
        // Las cabeceras de desarrollo (la "sesión" de las pruebas) no abren /mcp.
        Assert.Equal(HttpStatusCode.Unauthorized, await Llamar(h =>
        {
            h.Add("X-Usuario-Id", alumnoId.ToString());
            h.Add("X-Usuario-Rol", "Alumno");
        }));
        Assert.Equal(HttpStatusCode.Unauthorized, await Llamar(h => h.Authorization = new("Bearer", "tpc_mcp_inventado")));

        (await api.Como(alumnoId, "Alumno").DeleteAsync("/api/v1/alumno/token-mcp")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, await Llamar(h => h.Authorization = new("Bearer", token)));
    }

    [Fact]
    public async Task Generar_otro_token_revoca_el_anterior()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();

        var primero = await GenerarToken(api, alumnoId);
        await GenerarToken(api, alumnoId);

        await Assert.ThrowsAnyAsync<Exception>(() => Conectar(api, primero));
    }

    [Fact]
    public async Task El_asistente_ve_las_herramientas_y_solo_las_clases_del_alumno()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, claseId) = api.Sembrar();
        await using var cliente = await Conectar(api, await GenerarToken(api, alumnoId));

        var herramientas = (await cliente.ListToolsAsync()).Select(h => h.Name).ToList();
        Assert.Contains("leer_material", herramientas);
        Assert.Contains("iniciar_examen", herramientas);
        Assert.Contains("registrar_respuesta", herramientas);

        var clases = Texto(await cliente.CallToolAsync("listar_clases"));
        Assert.Contains(claseId.ToString(), clases);

        // Una clase de un curso ajeno no se lee.
        Guid ajena;
        using (var db = api.NuevoContexto())
        {
            var curso = new Curso { Codigo = "OTRO", Nombre = "Otro curso", Periodo = "2026-2", DocenteId = db.Usuarios.First().Id };
            var clase = new Clase { CursoId = curso.Id, Titulo = "Ajena", Inicio = DateTimeOffset.UtcNow, Orden = 1 };
            db.Cursos.Add(curso);
            db.Clases.Add(clase);
            db.SaveChanges();
            ajena = clase.Id;
        }

        var lectura = await cliente.CallToolAsync("leer_material", new Dictionary<string, object?> { ["clase_id"] = ajena });
        Assert.True(lectura.IsError);
        Assert.DoesNotContain(ajena.ToString(), clases);
    }

    [Fact]
    public async Task El_asistente_rinde_el_examen_y_la_nota_la_pone_el_servidor()
    {
        using var api = new ApiDePruebas
        {
            Servicios = s =>
            {
                s.RemoveAll<TutorPreClase.Application.Evaluacion.IGeneradorExamen>();
                s.AddSingleton<TutorPreClase.Application.Evaluacion.IGeneradorExamen, GeneradorFijo>();
            }
        };
        var (_, alumnoId, _, claseId) = api.Sembrar();

        using (var db = api.NuevoContexto())
        {
            var archivo = new ArchivoContenido
            {
                ClaseId = claseId, Nombre = "Clase03.pdf", Tipo = "pdf", BlobUrl = "x/Clase03.pdf",
                HashSha256 = "abc", Estado = EstadoArchivo.Listo
            };
            archivo.Paginas.Add(new PaginaContenido
            {
                ArchivoId = archivo.Id, ClaseId = claseId, Pagina = 11,
                Texto = "La sigmoide satura en los extremos y reduce el gradiente.", Tokens = 14
            });
            db.Archivos.Add(archivo);
            db.Examenes.Add(new Examen
            {
                ClaseId = claseId,
                MaxIntentos = 3, ModoFeedback = ModoFeedback.AlFinal, Publicado = true, PreguntasPorIntento = 2
            });
            db.SaveChanges();
        }

        await using var cliente = await Conectar(api, await GenerarToken(api, alumnoId));
        var clase = new Dictionary<string, object?> { ["clase_id"] = claseId };

        Assert.Contains("Clase03.pdf, p. 11", Texto(await cliente.CallToolAsync("leer_material", clase)));
        Assert.Contains("examen_iniciado", Texto(await cliente.CallToolAsync("iniciar_examen", clase)));

        // Dos preguntas, una a la vez; la correcta es la A y no sale antes de finalizar.
        foreach (var letra in new[] { "A", "C" })
        {
            var pregunta = Texto(await cliente.CallToolAsync("obtener_siguiente_pregunta", clase));
            Assert.Contains("enunciado", pregunta);
            Assert.DoesNotContain("correcta", pregunta);

            var registro = Texto(await cliente.CallToolAsync("registrar_respuesta",
                new Dictionary<string, object?> { ["clase_id"] = claseId, ["alternativa"] = letra }));
            Assert.Contains("\"registrada\":true", registro);
        }

        var final = Texto(await cliente.CallToolAsync("finalizar_examen", clase));
        var resultado = JsonDocument.Parse(final).RootElement;
        Assert.Equal(10, resultado.GetProperty("nota").GetDecimal());

        using var verificacion = api.NuevoContexto();
        Assert.Equal(10, verificacion.Intentos.Single().Puntaje);
    }

    [Fact]
    public async Task Las_herramientas_del_examen_piden_empezarlo_antes()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, claseId) = api.Sembrar();
        await using var cliente = await Conectar(api, await GenerarToken(api, alumnoId));

        var pregunta = Texto(await cliente.CallToolAsync("obtener_siguiente_pregunta",
            new Dictionary<string, object?> { ["clase_id"] = claseId }));
        Assert.Contains("sin_examen", pregunta);

        // Sin material la clase no tiene examen: no se gasta un intento.
        var inicio = Texto(await cliente.CallToolAsync("iniciar_examen",
            new Dictionary<string, object?> { ["clase_id"] = claseId }));
        Assert.Contains("\"error\"", inicio);
    }
}

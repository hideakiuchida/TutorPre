using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TutorPreClase.Application.Llm;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

public class ApiTests
{
    [Fact]
    public async Task La_api_responde_salud_sin_autenticacion()
    {
        using var api = new ApiDePruebas();
        api.Sembrar();

        var respuesta = await api.CreateClient().GetAsync("/salud");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task Sin_identidad_el_chat_no_es_accesible()
    {
        using var api = new ApiDePruebas();
        var (_, _, _, claseId) = api.Sembrar();

        var respuesta = await api.CreateClient()
            .PostAsJsonAsync($"/api/v1/clases/{claseId}/conversacion", new { agenteId = "openrouter" });

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    [Fact]
    public async Task Un_alumno_no_puede_usar_los_endpoints_del_docente()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, claseId) = api.Sembrar();

        var respuesta = await api.Como(alumnoId, "Alumno").GetAsync($"/api/v1/clases/{claseId}/reporte");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task Un_alumno_no_puede_leer_la_conversacion_de_otro()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, claseId) = api.Sembrar();

        var abierta = await api.Como(alumnoId, "Alumno")
            .PostAsJsonAsync($"/api/v1/clases/{claseId}/conversacion", new { agenteId = "openrouter" });

        var cuerpo = await abierta.Content.ReadFromJsonAsync<JsonElement>();
        var conversacionId = cuerpo.GetProperty("conversacionId").GetString();

        var intruso = await api.Como(Guid.NewGuid(), "Alumno")
            .GetAsync($"/api/v1/conversaciones/{conversacionId}/mensajes");

        Assert.Equal(HttpStatusCode.Forbidden, intruso.StatusCode);
    }

    [Fact]
    public async Task El_docente_arma_la_clase_y_el_alumno_conversa_por_sse()
    {
        using var api = new ApiDePruebas();
        var (docenteId, alumnoId, _, claseId) = api.Sembrar();

        var docente = api.Como(docenteId, "Docente");
        var alumno = api.Como(alumnoId, "Alumno");

        // 1. El docente configura y publica el examen.
        var examen = await docente.PutAsJsonAsync($"/api/v1/clases/{claseId}/examen", new
        {
            abreEn = DateTimeOffset.UtcNow.AddHours(-1),
            cierraEn = DateTimeOffset.UtcNow.AddHours(5),
            maxIntentos = 3,
            minutosLimite = (int?)null,
            modoFeedback = "AlFinal"
        });

        Assert.Equal(HttpStatusCode.OK, examen.StatusCode);
        var examenId = (await examen.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("examenId").GetString();

        // Sin preguntas: la IA genera las de cada alumno al empezar (RF-04).
        Assert.Equal(HttpStatusCode.OK,
            (await docente.PostAsync($"/api/v1/examenes/{examenId}/publicar", null)).StatusCode);

        // Y ya no hay forma de cargar ni aprobar preguntas a mano.
        var manual = await docente.PostAsJsonAsync($"/api/v1/examenes/{examenId}/preguntas", new { enunciado = "x" });
        Assert.False(manual.IsSuccessStatusCode);

        // 2. El alumno abre su chat.
        var abierta = await alumno.PostAsJsonAsync($"/api/v1/clases/{claseId}/conversacion", new { agenteId = "openrouter" });
        var conversacionId = (await abierta.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("conversacionId").GetString();

        // 3. Y conversa: la respuesta llega como stream SSE.
        api.Proveedor.Responde(
            new TextoParcial("Hola, revisemos la clase."),
            new Fin(12, 8));

        var mensaje = await alumno.PostAsJsonAsync(
            $"/api/v1/conversaciones/{conversacionId}/mensajes", new { texto = "hola" });

        Assert.Equal("text/event-stream", mensaje.Content.Headers.ContentType?.MediaType);

        var flujo = await mensaje.Content.ReadAsStringAsync();

        Assert.Contains("event: modo", flujo);
        Assert.Contains("event: token", flujo);
        Assert.Contains("Hola, revisemos la clase.", flujo);
        Assert.Contains("event: fin", flujo);

        // 4. El historial queda disponible para recargar la pagina.
        var historial = await alumno.GetFromJsonAsync<JsonElement>(
            $"/api/v1/conversaciones/{conversacionId}/mensajes");

        Assert.Equal(2, historial.GetArrayLength());
    }

    [Fact]
    public async Task El_profesor_ve_y_ajusta_el_examen_y_el_servidor_valida_lo_que_llega()
    {
        using var api = new ApiDePruebas();
        var (docenteId, alumnoId, _, claseId) = api.Sembrar();
        var docente = api.Como(docenteId, "Docente");

        var abre = DateTimeOffset.UtcNow.AddHours(-1);
        object Ajustes(int intentos = 2, int? minutos = 30, DateTimeOffset? cierra = null, string modo = "Inmediato") => new
        {
            abreEn = abre, cierraEn = cierra, maxIntentos = intentos, minutosLimite = minutos, modoFeedback = modo, preguntasPorIntento = 8
        };

        Assert.Equal(HttpStatusCode.OK, (await docente.PutAsJsonAsync($"/api/v1/clases/{claseId}/examen", Ajustes())).StatusCode);

        var examen = (await docente.GetFromJsonAsync<JsonElement>($"/api/v1/clases/{claseId}/contenido")).GetProperty("examen");
        Assert.Equal(2, examen.GetProperty("maxIntentos").GetInt32());
        Assert.Equal(30, examen.GetProperty("minutosLimite").GetInt32());
        Assert.Equal(8, examen.GetProperty("preguntasPorIntento").GetInt32());
        Assert.Equal("Inmediato", examen.GetProperty("modoFeedback").GetString());
        Assert.Equal(JsonValueKind.Null, examen.GetProperty("cierraEn").ValueKind);

        // Valores fuera de rango no se guardan.
        foreach (var malo in new[] { Ajustes(intentos: 0), Ajustes(intentos: 11), Ajustes(minutos: 2), Ajustes(cierra: abre.AddMinutes(-1)), Ajustes(modo: "Otro") })
            Assert.Equal(HttpStatusCode.BadRequest, (await docente.PutAsJsonAsync($"/api/v1/clases/{claseId}/examen", malo)).StatusCode);

        // El alumno ve cuántos intentos tiene y que aún no rindió.
        var clases = await api.Como(alumnoId, "Alumno").GetFromJsonAsync<JsonElement>("/api/v1/alumno/clases");
        var suExamen = clases.EnumerateArray().Single(c => c.GetProperty("claseId").GetGuid() == claseId).GetProperty("examen");
        Assert.Equal(2, suExamen.GetProperty("maxIntentos").GetInt32());
        Assert.Equal(0, suExamen.GetProperty("intentosUsados").GetInt32());
        Assert.Equal(JsonValueKind.Null, suExamen.GetProperty("mejorNota").ValueKind);
    }

    [Fact]
    public async Task El_reporte_del_docente_trae_niveles_y_dudas()
    {
        using var api = new ApiDePruebas();
        var (docenteId, _, _, claseId) = api.Sembrar();

        var reporte = await api.Como(docenteId, "Docente")
            .GetFromJsonAsync<JsonElement>($"/api/v1/clases/{claseId}/reporte");

        Assert.Equal("Clase 03 - Redes profundas", reporte.GetProperty("clase").GetString());
        Assert.True(reporte.TryGetProperty("distribucionNiveles", out _));
        Assert.True(reporte.TryGetProperty("dudasFueraDelMaterial", out _));
        Assert.True(reporte.TryGetProperty("temasMasFallados", out _));
    }

    [Fact]
    public async Task La_ampliacion_viene_apagada_y_el_docente_la_enciende_o_apaga_por_clase()
    {
        using var api = new ApiDePruebas();
        var (docenteId, _, _, claseId) = api.Sembrar();

        // Por defecto el tutor se limita al material (RF-20).
        using (var db = api.NuevoContexto())
            Assert.False(db.Clases.Single().AmpliacionPermitida);

        var docente = api.Como(docenteId, "Docente");

        var encendida = await docente.PutAsJsonAsync($"/api/v1/clases/{claseId}/ampliacion", new { permitida = true });
        Assert.Equal(HttpStatusCode.OK, encendida.StatusCode);
        Assert.True((await encendida.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ampliacionPermitida").GetBoolean());

        await docente.PutAsJsonAsync($"/api/v1/clases/{claseId}/ampliacion", new { permitida = false });

        using var despues = api.NuevoContexto();
        Assert.False(despues.Clases.Single().AmpliacionPermitida);
    }
}

using System.Text.Json;
using TutorPreClase.Application.Contenido;
using TutorPreClase.Application.Evaluacion;
using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Tutor;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

public class GeneradorExamenTests
{
    private static readonly ContextoClase Material = new(
        "M1",
        "<contenido clase=\"M1\">\n[M1.pdf, p. 43]\nLos ingresos se registran cuando se ganan.\n</contenido>",
        20, false,
        new HashSet<(string, int)> { ("M1.pdf", 43) },
        new HashSet<(string, int)> { ("M1.pdf", 43) },
        [new PaginaContexto("M1.pdf", 43, "Los ingresos se registran cuando se ganan.", 20)]);

    private static string Json(params object[] preguntas) => JsonSerializer.Serialize(new { preguntas });

    private static object Valida(string enunciado, int correcta = 0) => new
    {
        enunciado,
        tema = "Principio de devengado",
        justificacion = "Se registran al ganarse [M1.pdf, p. 43].",
        alternativas = Enumerable.Range(0, 4)
            .Select(i => new { texto = $"Opción {i} de {enunciado}", correcta = i == correcta })
    };

    [Fact]
    public async Task Pide_el_examen_con_el_material_y_devuelve_preguntas_de_la_IA_sin_aprobar_nada()
    {
        var proveedor = new ProveedorGuionado().RespondeTexto(Json(Valida("P1"), Valida("P2"), Valida("P3")));

        var preguntas = await new GeneradorExamen(barajar: false).GenerarAsync(proveedor, "modelo:free", Material, 3);

        Assert.Equal(3, preguntas.Count);
        Assert.All(preguntas, p =>
        {
            Assert.Equal(OrigenPregunta.IA, p.Origen);
            Assert.True(p.Aprobada);
            Assert.Equal(["A", "B", "C", "D"], p.Alternativas.Select(a => a.Letra));
            Assert.Single(p.Alternativas, a => a.EsCorrecta);
        });

        // Llamada aparte del chat: con el material, sin herramientas.
        var solicitud = proveedor.Solicitudes.Single();
        Assert.Contains("Los ingresos se registran cuando se ganan.", solicitud.PromptSistema);
        Assert.Empty(solicitud.Herramientas);
    }

    [Fact]
    public void Descarta_preguntas_mal_formadas_y_tolera_texto_alrededor_del_json()
    {
        var dosCorrectas = new
        {
            enunciado = "Mal",
            justificacion = "x",
            alternativas = Enumerable.Range(0, 4).Select(i => new { texto = $"t{i}", correcta = i < 2 })
        };
        var tresAlternativas = new
        {
            enunciado = "Corta",
            justificacion = "x",
            alternativas = Enumerable.Range(0, 3).Select(i => new { texto = $"t{i}", correcta = i == 0 })
        };

        var respuesta = "Aquí tienes:\n```json\n" + Json(Valida("Buena"), dosCorrectas, tresAlternativas) + "\n```";

        var preguntas = new GeneradorExamen(barajar: false).Interpretar(respuesta, 6);

        Assert.Equal("Buena", Assert.Single(preguntas).Enunciado);
    }

    [Fact]
    public void Barajar_mueve_la_correcta_sin_perderla()
    {
        var generador = new GeneradorExamen();
        var letras = Enumerable.Range(0, 40)
            .Select(_ => generador.Interpretar(Json(Valida("P", correcta: 0)), 1).Single())
            .Select(p => p.Alternativas.Single(a => a.EsCorrecta).Letra)
            .ToHashSet();

        Assert.True(letras.Count > 1, "La correcta cayó siempre en la misma letra");
    }

    [Fact]
    public async Task Si_la_IA_no_devuelve_un_examen_util_falla_sin_inventar_nada()
    {
        var proveedor = new ProveedorGuionado().RespondeTexto("No puedo ayudarte con eso.");

        await Assert.ThrowsAsync<GeneracionExamenException>(
            () => new GeneradorExamen().GenerarAsync(proveedor, "m", Material, 6));
    }

    [Fact]
    public async Task Un_error_del_proveedor_viaja_con_el_fallo()
    {
        var proveedor = new ProveedorGuionado().Responde(new ErrorProveedor("429", true, LimiteDeUso: true));

        var error = await Assert.ThrowsAsync<GeneracionExamenException>(
            () => new GeneradorExamen().GenerarAsync(proveedor, "m", Material, 6));

        Assert.True(error.Error!.LimiteDeUso);
    }
}

public class ExamenPorAlumnoTests
{
    [Fact]
    public async Task Cada_intento_recibe_sus_propias_preguntas_generadas()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");

        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        var intento = banco.Db.Intentos.Single();
        Assert.Equal(1, banco.Generador.Llamadas);
        Assert.Equal(2, banco.Db.Preguntas.Count(p => p.IntentoId == intento.Id));
    }

    [Fact]
    public async Task Si_la_generacion_falla_no_se_gasta_el_intento()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        banco.Generador.Falla = new ErrorProveedor("El proveedor respondio 502.", true);

        var error = await Assert.ThrowsAsync<ExamenException>(() => banco.Tutor.IniciarExamenAsync(conversacion.Id));

        Assert.Equal("examen_no_generado", error.Codigo);
        Assert.Empty(banco.Db.Intentos);
        Assert.Equal(ModoConversacion.Consulta, banco.Db.Conversaciones.Single().Modo);
    }

    [Fact]
    public async Task Sin_cupo_se_avisa_el_limite_y_la_credencial_sigue_valida()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        banco.Generador.Falla = new ErrorProveedor("429", true, LimiteDeUso: true);

        var error = await Assert.ThrowsAsync<ExamenException>(() => banco.Tutor.IniciarExamenAsync(conversacion.Id));

        Assert.Equal("limite_de_uso", error.Codigo);
        Assert.Equal(EstadoCredencial.Valida, banco.Db.Credenciales.Single().Estado);
    }

    [Fact]
    public async Task Si_el_proveedor_rechaza_la_clave_se_marca_invalida()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        banco.Generador.Falla = new ErrorProveedor("401", false, CredencialRechazada: true);

        var error = await Assert.ThrowsAsync<ExamenException>(() => banco.Tutor.IniciarExamenAsync(conversacion.Id));

        Assert.Equal("credencial_rechazada", error.Codigo);
        Assert.Equal(EstadoCredencial.Invalida, banco.Db.Credenciales.Single().Estado);
    }

    [Fact]
    public async Task Sin_material_no_hay_examen_que_generar()
    {
        using var banco = new BancoDePruebas().Sembrar();
        banco.Db.Archivos.RemoveRange(banco.Db.Archivos);
        banco.Db.SaveChanges();
        banco.Contexto.Invalidar(banco.ClaseId);

        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        var error = await Assert.ThrowsAsync<ExamenException>(() => banco.Tutor.IniciarExamenAsync(conversacion.Id));

        Assert.Equal("sin_material", error.Codigo);
        Assert.Equal(0, banco.Generador.Llamadas);
    }

    [Fact]
    public async Task Una_pregunta_de_otro_intento_no_se_puede_responder()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        // Una pregunta generada para el intento de otro alumno.
        var ajena = BancoDePruebas.Pregunta("Ajena", 1, "X");
        ajena.ExamenId = banco.ExamenId;
        banco.Db.Preguntas.Add(ajena);
        banco.Db.SaveChanges();

        var intento = banco.Db.Intentos.Single();
        var error = await Assert.ThrowsAsync<ExamenException>(
            () => banco.Examen.RegistrarRespuestaAsync(intento.Id, ajena.Id, "A", "la A"));

        Assert.Equal("PreguntaAjenaAlIntento", error.Codigo);
    }
}

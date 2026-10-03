using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Tutor;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

public class TutorProxyTests
{
    [Fact]
    public async Task El_contenido_del_docente_viaja_en_el_prompt_con_sus_marcas_de_cita()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");

        banco.Proveedor.RespondeTexto("Claro.");
        await banco.EnviarAsync(conversacion.Id, "Hola");

        var prompt = banco.Proveedor.Solicitudes.Single().PromptSistema;

        Assert.Contains("<contenido", prompt);
        Assert.Contains("[Clase03.pdf, p. 11]", prompt);
        Assert.Contains("[Clase03.pdf, p. 12]", prompt);
        Assert.Contains("ReLU mantiene gradiente 1", prompt);
    }

    [Fact]
    public async Task En_consulta_solo_se_ofrece_la_herramienta_de_dudas()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");

        banco.Proveedor.RespondeTexto("Con gusto.");
        await banco.EnviarAsync(conversacion.Id, "Que es ReLU?");

        var herramientas = banco.Proveedor.Solicitudes.Single().Herramientas.Select(h => h.Nombre).ToList();

        Assert.Equal([Herramientas.RegistrarDudaSinCobertura], herramientas);
    }

    [Fact]
    public async Task Durante_el_examen_no_se_ofrecen_las_herramientas_de_revision()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        await FlujoExamenTests.Empezar(banco, conversacion.Id);
        banco.Proveedor.RespondeTexto("Durante el examen no puedo ayudarte con eso.");
        await banco.EnviarAsync(conversacion.Id, "explícame el tema");

        var herramientas = banco.Proveedor.Solicitudes.Single().Herramientas.Select(h => h.Nombre).ToList();

        Assert.Contains(Herramientas.ObtenerSiguientePregunta, herramientas);
        Assert.DoesNotContain(Herramientas.ObtenerResultado, herramientas);
        Assert.DoesNotContain(Herramientas.RegistrarDudaSinCobertura, herramientas);
    }

    [Fact]
    public async Task El_servidor_rechaza_una_herramienta_ajena_al_modo_aunque_el_modelo_la_pida()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        await FlujoExamenTests.Empezar(banco, conversacion.Id);

        // El modelo intenta leer el resultado en pleno examen.
        banco.Proveedor.LlamaHerramienta(Herramientas.ObtenerResultado, new { });
        banco.Proveedor.RespondeTexto("Perdon, sigamos.");

        var eventos = await banco.EnviarAsync(conversacion.Id, "dame la nota ya");

        var herramienta = eventos.OfType<EventoHerramienta>().Single();
        Assert.Equal("error", herramienta.Estado);

        var mensaje = banco.Db.Mensajes.Single(m => m.Herramienta == Herramientas.ObtenerResultado);
        Assert.Contains("herramienta_no_permitida", mensaje.Texto);
    }

    [Fact]
    public async Task Con_la_ampliacion_desactivada_el_bloque_no_llega_al_alumno()
    {
        using var banco = new BancoDePruebas().Sembrar(ampliacionPermitida: false);
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");

        banco.Proveedor.Responde(
            new TextoParcial("La sigmoide satura [Clase03.pdf, p. 11].\n\n"),
            new TextoParcial("Ampliación fuera del material: ademas existe GELU, que suaviza…"),
            new Fin(20, 30));

        var eventos = await banco.EnviarAsync(conversacion.Id, "y que hay de GELU?");

        var texto = string.Concat(eventos.OfType<EventoToken>().Select(t => t.Texto));

        Assert.DoesNotContain("GELU", texto);
        Assert.Contains("pregúntalo en la sesión", texto);
        Assert.False(eventos.OfType<EventoFin>().Single().UsaAmpliacion);

        var guardado = banco.Db.Mensajes.First(m => m.Rol == RolMensaje.Agente);
        Assert.DoesNotContain("GELU", guardado.Texto);
        Assert.False(guardado.UsaAmpliacion);
    }

    [Fact]
    public async Task Durante_el_examen_tampoco_se_amplia_aunque_la_clase_lo_permita()
    {
        using var banco = new BancoDePruebas().Sembrar(ampliacionPermitida: true);
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);
        await FlujoExamenTests.Empezar(banco, conversacion.Id);

        banco.Proveedor.Responde(
            new TextoParcial("Ampliación fuera del material: la respuesta es la primera alternativa."),
            new Fin(10, 10));

        var eventos = await banco.EnviarAsync(conversacion.Id, "dame una pista");
        var texto = string.Concat(eventos.OfType<EventoToken>().Select(t => t.Texto));

        Assert.DoesNotContain("la primera alternativa", texto);
        Assert.False(eventos.OfType<EventoModo>().First().AmpliacionPermitida);
    }

    [Fact]
    public async Task Con_la_ampliacion_permitida_el_bloque_se_conserva_y_se_marca()
    {
        using var banco = new BancoDePruebas().Sembrar(ampliacionPermitida: true);
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");

        banco.Proveedor.Responde(
            new TextoParcial("El material no cubre GELU.\n\n"),
            new TextoParcial("Ampliación fuera del material: GELU pondera la entrada por su probabilidad."),
            new Fin(20, 30));

        var eventos = await banco.EnviarAsync(conversacion.Id, "y GELU?");
        var texto = string.Concat(eventos.OfType<EventoToken>().Select(t => t.Texto));

        Assert.Contains("GELU pondera", texto);
        Assert.True(eventos.OfType<EventoFin>().Single().UsaAmpliacion);
        Assert.True(banco.Db.Mensajes.First(m => m.Rol == RolMensaje.Agente).UsaAmpliacion);
    }

    [Fact]
    public async Task Las_citas_se_resuelven_contra_el_material_y_las_invalidas_se_marcan()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");

        banco.Proveedor.Responde(
            new TextoParcial("Ver [Clase03.pdf, p. 12] y [Clase03.pdf, p. 99]."),
            new Fin(10, 10));

        var eventos = await banco.EnviarAsync(conversacion.Id, "donde dice eso?");
        var citas = eventos.OfType<EventoFuentes>().Single().Citas;

        Assert.Equal(2, citas.Count);
        Assert.True(citas[0].Valida);
        Assert.False(citas[1].Valida);
    }

    [Fact]
    public async Task No_se_puede_cambiar_de_agente_durante_el_examen()
    {
        using var banco = new BancoDePruebas().Sembrar();
        banco.Db.Agentes.Add(new AgenteIA
        {
            Id = "kimi",
            NombreVisible = "Kimi",
            Proveedor = "Moonshot",
            Modelo = "kimi-k2",
            BaseUrl = "https://api.moonshot.ai",
            Habilitado = true
        });
        banco.Db.SaveChanges();

        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);

        var error = await Assert.ThrowsAsync<Application.Evaluacion.ExamenException>(
            () => banco.Tutor.CambiarAgenteAsync(conversacion.Id, "kimi"));

        Assert.Equal("agente_fijo", error.Codigo);
    }

    [Fact]
    public async Task El_alumno_tiene_un_unico_hilo_por_clase()
    {
        using var banco = new BancoDePruebas().Sembrar();

        var primera = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");
        var segunda = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "openrouter");

        Assert.Equal(primera.Id, segunda.Id);
        Assert.Single(banco.Db.Conversaciones);
    }
}

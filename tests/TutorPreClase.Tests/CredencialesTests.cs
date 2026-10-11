using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Tutor;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

public class BovedaCredencialesTests
{
    private const string Clave = "sk-prueba-secreto-de-la-alumna-9876";

    [Fact]
    public async Task La_clave_no_se_guarda_en_claro_y_solo_se_expone_por_sus_ultimos_4()
    {
        using var banco = new BancoDePruebas().Sembrar();

        var resumen = await banco.Boveda.ConectarAsync(banco.AlumnoId, "prueba", Clave);

        Assert.Equal("9876", resumen.Ultimos4);
        Assert.Equal(EstadoCredencial.Valida, resumen.Estado);

        var fila = banco.Db.Credenciales.Single(c => c.UsuarioId == banco.AlumnoId);
        Assert.DoesNotContain(Clave, fila.ClaveCifrada);
        Assert.NotEqual(Clave, fila.ClaveCifrada);

        // Y se recupera intacta para la llamada saliente.
        Assert.Equal(Clave, await banco.Boveda.ClaveParaAsync(banco.AlumnoId, "prueba"));
    }

    [Fact]
    public async Task La_credencial_de_un_alumno_no_sirve_en_el_contexto_de_otro()
    {
        using var banco = new BancoDePruebas().Sembrar();

        var otro = new Usuario { Email = "otro@uni.edu", Nombre = "Otro", Rol = RolUsuario.Alumno };
        banco.Db.Usuarios.Add(otro);
        banco.Db.SaveChanges();

        await banco.Boveda.ConectarAsync(banco.AlumnoId, "prueba", Clave);

        Assert.Null(await banco.Boveda.ClaveParaAsync(otro.Id, "prueba"));
    }

    [Fact]
    public async Task Reconectar_reemplaza_la_credencial_sin_duplicarla()
    {
        using var banco = new BancoDePruebas().Sembrar();

        await banco.Boveda.ConectarAsync(banco.AlumnoId, "prueba", Clave);
        await banco.Boveda.ConectarAsync(banco.AlumnoId, "prueba", "sk-prueba-la-nueva-clave-0001");

        Assert.Single(banco.Db.Credenciales.Where(c => c.UsuarioId == banco.AlumnoId && c.AgenteId == "prueba"));
        Assert.Equal("0001", banco.Db.Credenciales.Single(c => c.AgenteId == "prueba").Ultimos4);
    }

    [Fact]
    public async Task Una_credencial_marcada_invalida_deja_de_entregarse()
    {
        using var banco = new BancoDePruebas().Sembrar();
        await banco.Boveda.ConectarAsync(banco.AlumnoId, "prueba", Clave);

        await banco.Boveda.MarcarInvalidaAsync(banco.AlumnoId, "prueba");

        Assert.Null(await banco.Boveda.ClaveParaAsync(banco.AlumnoId, "prueba"));
        Assert.Empty(await banco.Boveda.AgentesConectadosAsync(banco.AlumnoId));
    }

    [Fact]
    public async Task Desconectar_borra_la_credencial()
    {
        using var banco = new BancoDePruebas().Sembrar();
        await banco.Boveda.ConectarAsync(banco.AlumnoId, "prueba", Clave);

        await banco.Boveda.DesconectarAsync(banco.AlumnoId, "prueba");

        Assert.Empty(banco.Db.Credenciales.Where(c => c.UsuarioId == banco.AlumnoId));
    }

    [Fact]
    public async Task Se_rechaza_algo_que_no_parece_una_clave()
    {
        using var banco = new BancoDePruebas().Sembrar();

        var error = await Assert.ThrowsAsync<CredencialException>(
            () => banco.Boveda.ConectarAsync(banco.AlumnoId, "prueba", "hola"));

        Assert.Equal("clave_invalida", error.Codigo);
    }
}

public class CredencialesEnElChatTests
{
    [Fact]
    public async Task Sin_credencial_el_chat_lo_avisa_y_no_llama_al_proveedor()
    {
        using var banco = new BancoDePruebas().Sembrar();
        await banco.Boveda.DesconectarAsync(banco.AlumnoId, "prueba");

        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");
        var eventos = await banco.EnviarAsync(conversacion.Id, "hola");

        var aviso = eventos.OfType<EventoAviso>().Single();
        Assert.Equal("sin_credencial", aviso.Codigo);
        Assert.Empty(banco.Proveedor.Solicitudes);
    }

    [Fact]
    public async Task Sin_credencial_no_se_puede_iniciar_el_examen()
    {
        using var banco = new BancoDePruebas().Sembrar();
        await banco.Boveda.DesconectarAsync(banco.AlumnoId, "prueba");

        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

        var error = await Assert.ThrowsAsync<Application.Evaluacion.ExamenException>(
            () => banco.Tutor.IniciarExamenAsync(conversacion.Id));

        Assert.Equal("sin_credencial", error.Codigo);
    }

    [Fact]
    public async Task Si_el_proveedor_rechaza_la_clave_se_marca_invalida_y_se_pide_reconectar()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

        banco.Proveedor.Responde(new ErrorProveedor("El proveedor respondio 401.", false, CredencialRechazada: true));

        var eventos = await banco.EnviarAsync(conversacion.Id, "hola");

        Assert.Equal("credencial_rechazada", eventos.OfType<EventoAviso>().Single().Codigo);
        Assert.Equal(EstadoCredencial.Invalida, banco.Db.Credenciales.Single().Estado);
    }

    [Fact]
    public async Task La_clave_del_alumno_no_entra_al_prompt_ni_al_historial()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

        banco.Proveedor.RespondeTexto("Hola.");
        await banco.EnviarAsync(conversacion.Id, "hola");

        var solicitud = banco.Proveedor.Solicitudes.Single();
        Assert.DoesNotContain("sk-prueba", solicitud.PromptSistema);
        Assert.All(banco.Db.Mensajes, m => Assert.DoesNotContain("sk-prueba", m.Texto));
    }
}

public class CredencialesApiTests
{
    [Fact]
    public async Task La_api_nunca_devuelve_la_clave()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();
        api.ConectarCredencial(alumnoId, "prueba", "sk-prueba-mi-clave-secreta-4321");

        var listado = await api.Como(alumnoId, "Alumno").GetStringAsync("/api/v1/alumno/credenciales");

        Assert.DoesNotContain("mi-clave-secreta", listado);
        Assert.Contains("4321", listado);
    }

    [Fact]
    public async Task Un_agente_de_inicio_de_sesion_no_acepta_claves_pegadas()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();

        // Gemini con la cuenta de Google solo se conecta iniciando sesion (RF-24, RF-29).
        var respuesta = await api.Como(alumnoId, "Alumno")
            .PutAsJsonAsync("/api/v1/alumno/credenciales/gemini", new { clave = "sk-prueba-pegada-a-mano-0000" });

        Assert.False(respuesta.IsSuccessStatusCode);

        var listado = await api.Como(alumnoId, "Alumno").GetStringAsync("/api/v1/alumno/credenciales");
        Assert.DoesNotContain("0000", listado);
    }

    [Fact]
    public async Task Un_alumno_no_ve_las_credenciales_de_otro()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();

        var ajeno = await api.Como(Guid.NewGuid(), "Alumno")
            .GetFromJsonAsync<JsonElement>("/api/v1/alumno/credenciales");

        Assert.Equal(0, ajeno.GetArrayLength());

        var propias = await api.Como(alumnoId, "Alumno")
            .GetFromJsonAsync<JsonElement>("/api/v1/alumno/credenciales");

        Assert.Equal(1, propias.GetArrayLength());
    }

    [Fact]
    public async Task El_listado_de_agentes_dice_cuales_tiene_conectados()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();

        var agentes = await api.Como(alumnoId, "Alumno").GetFromJsonAsync<JsonElement>("/api/v1/agentes");

        var conectado = agentes.EnumerateArray().Single(a => a.GetProperty("id").GetString() == "prueba");

        Assert.True(conectado.GetProperty("conectado").GetBoolean());
        Assert.Equal("Clave", conectado.GetProperty("conexion").GetString());
    }
}

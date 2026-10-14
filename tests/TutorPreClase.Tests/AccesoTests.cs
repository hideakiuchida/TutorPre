using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TutorPreClase.Api.Seguridad;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

/// <summary>Proveedor de identidad simulado: devuelve el id_token que el test le fije.</summary>
public sealed class ProveedorIdentidadSimulado : HttpMessageHandler, IHttpClientFactory
{
    public string IdToken { get; set; } = "";
    public HttpStatusCode Estado { get; set; } = HttpStatusCode.OK;
    public string? CuerpoRecibido { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage peticion, CancellationToken ct)
    {
        CuerpoRecibido = peticion.Content is null ? null : await peticion.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(Estado) { Content = JsonContent.Create(new { id_token = IdToken }) };
    }

    public HttpClient CreateClient(string nombre) => new(this, disposeHandler: false);
}

public class AccesoTests
{
    private static readonly OpcionesProveedorIdentidad Google = new()
    {
        Nombre = "Google",
        ClientId = "cliente-google",
        ClientSecret = "secreto",
        UrlAutorizacion = "https://accounts.google.com/o/oauth2/v2/auth",
        UrlToken = "https://oauth2.googleapis.com/token",
        Emisores = ["https://accounts.google.com"]
    };

    private static readonly OpcionesProveedorIdentidad Microsoft = new()
    {
        Nombre = "Microsoft",
        ClientId = "cliente-ms",
        ClientSecret = "secreto",
        UrlAutorizacion = "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize",
        UrlToken = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token",
        Emisores = ["https://login.microsoftonline.com/"]
    };

    /// <summary>Un id_token sin firma: la API lo recibe del proveedor por TLS y valida sus afirmaciones.</summary>
    public static string IdToken(object carga)
    {
        static string B64(object o) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)));
        return $"{B64(new { alg = "none", typ = "JWT" })}.{B64(carga)}.";
    }

    private static object Carga(string aud = "cliente-google", string iss = "https://accounts.google.com",
        string nonce = "n1", long? exp = null, string? email = "ana@upc.edu.pe", bool verificado = true) => new
    {
        iss, aud, nonce, email, email_verified = verificado, name = "Ana Pérez",
        exp = exp ?? DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
    };

    [Fact]
    public void Un_id_token_valido_da_el_correo_en_minusculas_y_el_nombre()
    {
        var (email, nombre) = ConexionIdentidad.Validar(Google, IdToken(Carga(email: "Ana@UPC.edu.pe")), "n1");

        Assert.Equal(("ana@upc.edu.pe", "Ana Pérez"), (email, nombre));
    }

    [Theory]
    [InlineData("otra-app", "https://accounts.google.com", "n1", 5, true)]     // audiencia ajena
    [InlineData("cliente-google", "https://evil.example", "n1", 5, true)]     // emisor ajeno
    [InlineData("cliente-google", "https://accounts.google.com", "x", 5, true)] // nonce repetido de otro inicio
    [InlineData("cliente-google", "https://accounts.google.com", "n1", -10, true)] // vencido
    [InlineData("cliente-google", "https://accounts.google.com", "n1", 5, false)]  // correo sin verificar
    public void Se_rechaza_una_identidad_que_no_cumple(string aud, string iss, string nonce, int minutos, bool verificado)
    {
        var token = IdToken(Carga(aud, iss, nonce, DateTimeOffset.UtcNow.AddMinutes(minutos).ToUnixTimeSeconds(), verificado: verificado));

        var error = Assert.Throws<AccesoException>(() => ConexionIdentidad.Validar(Google, token, "n1"));
        Assert.Equal("acceso_rechazado", error.Codigo);
    }

    [Fact]
    public void Microsoft_sin_email_usa_el_nombre_de_la_cuenta()
    {
        var token = IdToken(new
        {
            iss = "https://login.microsoftonline.com/1234-tenant/v2.0",
            aud = "cliente-ms",
            nonce = "n1",
            preferred_username = "Luis.Rojas@upc.edu.pe",
            name = "Luis Rojas",
            exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
        });

        Assert.Equal("luis.rojas@upc.edu.pe", ConexionIdentidad.Validar(Microsoft, token, "n1").Email);
    }

    private static (ConexionIdentidad Conexion, ProveedorIdentidadSimulado Proveedor) Montar(
        BancoDePruebas banco, string[]? administradores = null, bool registroAbierto = true)
    {
        var opciones = Options.Create(new OpcionesAcceso
        {
            Proveedores = new() { ["google"] = Google },
            Administradores = administradores ?? [],
            RegistroAbierto = registroAbierto,
            ClaveSesion = Convert.ToBase64String(new byte[32])
        });

        var proveedor = new ProveedorIdentidadSimulado();
        var conexion = new ConexionIdentidad(
            new MemoryCache(new MemoryCacheOptions()), proveedor, banco.Db,
            new ServicioSesion(opciones, new HostingEnvironment { EnvironmentName = "Testing" }),
            opciones, NullLogger<ConexionIdentidad>.Instance);

        return (conexion, proveedor);
    }

    private static Dictionary<string, string> Parametros(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    private static async Task<ResultadoAcceso> EntrarAsync(
        ConexionIdentidad conexion, ProveedorIdentidadSimulado proveedor, string email, string? perfil = null)
    {
        var url = Parametros(conexion.Iniciar("google", perfil));
        proveedor.IdToken = IdToken(Carga(email: email, nonce: url["nonce"]));
        return await conexion.CanjearAsync("google", "codigo", url["state"], CancellationToken.None);
    }

    /// <summary>Un profesor dueño del curso sembrado, como si tuviera su carpeta.</summary>
    private static Usuario ProfesorDelCurso(BancoDePruebas banco)
    {
        var profesor = new Usuario { Email = "profe@upc.edu.pe", Nombre = "Profe", Rol = RolUsuario.Docente };
        banco.Db.Usuarios.Add(profesor);
        banco.Db.Cursos.Single().DocenteId = profesor.Id;
        banco.Db.SaveChanges();
        return profesor;
    }

    [Fact]
    public async Task Un_usuario_registrado_entra_con_su_rol_de_la_plataforma()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var alumna = banco.Db.Usuarios.Single();
        var (conexion, proveedor) = Montar(banco);

        var url = Parametros(conexion.Iniciar("google"));
        Assert.Equal("S256", url["code_challenge_method"]);
        Assert.Equal("openid email profile", url["scope"]);

        proveedor.IdToken = IdToken(Carga(email: alumna.Email, nonce: url["nonce"]));
        var resultado = await conexion.CanjearAsync("google", "codigo", url["state"], CancellationToken.None);

        Assert.Null(resultado.Registro);
        Assert.Equal((alumna.Id, "Alumno"), (resultado.Sesion!.Usuario.Id, resultado.Sesion.Usuario.Rol));
        Assert.Contains("code_verifier=", proveedor.CuerpoRecibido);

        // El estado es de un solo uso.
        var repetido = await Assert.ThrowsAsync<AccesoException>(
            () => conexion.CanjearAsync("google", "codigo", url["state"], CancellationToken.None));
        Assert.Equal("acceso_vencido", repetido.Codigo);
    }

    [Fact]
    public async Task Con_registro_cerrado_quien_no_esta_registrado_no_entra()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var (conexion, proveedor) = Montar(banco, registroAbierto: false);

        var error = await Assert.ThrowsAsync<AccesoException>(() => EntrarAsync(conexion, proveedor, "intruso@gmail.com"));

        Assert.Equal(("no_registrado", 403), (error.Codigo, error.Estado));
        Assert.DoesNotContain(banco.Db.Usuarios, u => u.Email == "intruso@gmail.com");
    }

    [Fact]
    public async Task El_primer_administrador_sale_de_la_configuracion()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var (conexion, proveedor) = Montar(banco, administradores: ["Directora@upc.edu.pe"]);

        var resultado = await EntrarAsync(conexion, proveedor, "directora@upc.edu.pe");

        Assert.Equal("Admin", resultado.Sesion!.Usuario.Rol);
        Assert.Equal(RolUsuario.Admin, banco.Db.Usuarios.Single(u => u.Email == "directora@upc.edu.pe").Rol);
    }

    [Fact]
    public async Task La_primera_vez_no_se_crea_nadie_hasta_que_la_persona_elige_su_rol()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var (conexion, proveedor) = Montar(banco);

        var resultado = await EntrarAsync(conexion, proveedor, "nueva@gmail.com");

        Assert.Null(resultado.Sesion);
        Assert.Equal("nueva@gmail.com", resultado.Registro!.Email);
        Assert.True(conexion.RegistroVigente(resultado.Registro.Token));
        Assert.DoesNotContain(banco.Db.Usuarios, u => u.Email == "nueva@gmail.com");
    }

    [Fact]
    public async Task El_alumno_elige_a_su_profesor_y_solo_queda_en_los_cursos_de_ese_profesor()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var profesor = ProfesorDelCurso(banco);
        var otro = new Usuario { Email = "otro@upc.edu.pe", Nombre = "Otro", Rol = RolUsuario.Docente };
        var cursoAjeno = new Curso { Codigo = "ECO1", Nombre = "Economía", DocenteId = otro.Id };
        banco.Db.Usuarios.Add(otro);
        banco.Db.Cursos.Add(cursoAjeno);
        banco.Db.SaveChanges();

        var (conexion, proveedor) = Montar(banco);
        var registro = (await EntrarAsync(conexion, proveedor, "nueva@gmail.com")).Registro!;

        var sesion = await conexion.RegistrarAsync(registro.Token, "Alumno", profesor.Id, CancellationToken.None);

        Assert.Equal("Alumno", sesion.Usuario.Rol);
        var cursos = banco.Db.Matriculas.Where(m => m.UsuarioId == sesion.Usuario.Id).Select(m => m.CursoId).ToList();
        Assert.Equal([banco.Db.Cursos.Single(c => c.DocenteId == profesor.Id).Id], cursos);

        // El registro es de un solo uso.
        var repetido = await Assert.ThrowsAsync<AccesoException>(
            () => conexion.RegistrarAsync(registro.Token, "Alumno", profesor.Id, CancellationToken.None));
        Assert.Equal("acceso_vencido", repetido.Codigo);
    }

    [Fact]
    public async Task El_perfil_elegido_antes_de_entrar_decide_con_que_rol_se_entra()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var profesor = ProfesorDelCurso(banco);
        var alumna = banco.Db.Usuarios.Single(u => u.Rol == RolUsuario.Alumno);
        var (conexion, proveedor) = Montar(banco);

        // Primera vez como profesor: entra directo, sin más preguntas.
        var nuevo = await EntrarAsync(conexion, proveedor, "profe.nuevo@gmail.com", "Docente");
        Assert.Equal("Docente", nuevo.Sesion!.Usuario.Rol);

        // Primera vez como alumno: falta elegir profesor, y el registro lo recuerda.
        var registro = (await EntrarAsync(conexion, proveedor, "alumno.nuevo@gmail.com", "Alumno")).Registro!;
        Assert.Equal("Alumno", registro.Perfil);

        // El profesor puede entrar como alumno sin dejar de ser profesor.
        var comoAlumno = (await EntrarAsync(conexion, proveedor, profesor.Email, "Alumno")).Sesion!;
        Assert.Equal("Alumno", comoAlumno.Usuario.Rol);
        Assert.Equal(["Docente", "Alumno"], comoAlumno.Usuario.Vistas);
        Assert.Equal(RolUsuario.Docente, banco.Db.Usuarios.Single(u => u.Id == profesor.Id).Rol);

        // Un alumno que entra como profesor pasa a serlo.
        var ahoraProfesor = (await EntrarAsync(conexion, proveedor, alumna.Email, "Docente")).Sesion!;
        Assert.Equal("Docente", ahoraProfesor.Usuario.Rol);

        Assert.Equal("rol_invalido", Assert.Throws<AccesoException>(() => conexion.Iniciar("google", "Admin")).Codigo);
    }

    [Fact]
    public async Task Un_profesor_que_elige_otro_profesor_como_alumno_conserva_sus_cursos()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var profesor = ProfesorDelCurso(banco);
        var otro = new Usuario { Email = "otro@upc.edu.pe", Nombre = "Otro", Rol = RolUsuario.Docente };
        var ajeno = new Curso { Codigo = "ECO1", Nombre = "Economía", DocenteId = otro.Id };
        banco.Db.Usuarios.Add(otro);
        banco.Db.Cursos.Add(ajeno);
        banco.Db.SaveChanges();

        profesor.ProfesorId = otro.Id;
        await TutorPreClase.Application.Academico.MatriculasPorProfesor.AlinearAlumnoAsync(banco.Db, profesor, CancellationToken.None);
        banco.Db.SaveChanges();

        var cursos = banco.Db.Matriculas.Where(m => m.UsuarioId == profesor.Id).Select(m => m.CursoId).ToHashSet();
        Assert.Contains(ajeno.Id, cursos);
        Assert.Contains(banco.Db.Cursos.Single(c => c.DocenteId == profesor.Id).Id, cursos);
    }

    [Fact]
    public async Task Un_alumno_sin_profesor_valido_no_se_registra()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var (conexion, proveedor) = Montar(banco);
        var registro = (await EntrarAsync(conexion, proveedor, "nueva@gmail.com")).Registro!;

        var error = await Assert.ThrowsAsync<AccesoException>(
            () => conexion.RegistrarAsync(registro.Token, "Alumno", Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("profesor_invalido", error.Codigo);
        Assert.DoesNotContain(banco.Db.Usuarios, u => u.Email == "nueva@gmail.com");
    }

    [Fact]
    public async Task Quien_elige_ser_profesor_entra_como_docente_y_no_puede_elegir_ser_administrador()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var (conexion, proveedor) = Montar(banco);

        var admin = (await EntrarAsync(conexion, proveedor, "listo@gmail.com")).Registro!;
        var error = await Assert.ThrowsAsync<AccesoException>(
            () => conexion.RegistrarAsync(admin.Token, "Admin", null, CancellationToken.None));
        Assert.Equal("rol_invalido", error.Codigo);

        var registro = (await EntrarAsync(conexion, proveedor, "profe.nueva@gmail.com")).Registro!;
        var sesion = await conexion.RegistrarAsync(registro.Token, "Docente", null, CancellationToken.None);

        Assert.Equal("Docente", sesion.Usuario.Rol);
    }

    private static CuentaNuevaPeticion Cuenta(string email, string perfil, string clave = "secreta-123") =>
        new(email, "Persona Nueva", clave, perfil);

    [Fact]
    public async Task El_profesor_crea_su_cuenta_con_contrasena_y_vuelve_a_entrar_con_ella()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var (conexion, _) = Montar(banco);

        var creada = await conexion.CrearCuentaAsync(Cuenta("Profe.Clave@Gmail.com", "Docente"), CancellationToken.None);
        Assert.Equal("Docente", creada.Sesion!.Usuario.Rol);

        var guardado = banco.Db.Usuarios.Single(u => u.Email == "profe.clave@gmail.com");
        Assert.NotNull(guardado.ClaveHash);
        Assert.DoesNotContain("secreta-123", guardado.ClaveHash);
        Assert.False(guardado.CorreoVerificado);

        var sesion = await conexion.EntrarConClaveAsync(
            new EntradaConClavePeticion(" PROFE.clave@gmail.com ", "secreta-123", "Docente"), CancellationToken.None);
        Assert.Equal((guardado.Id, "Docente"), (sesion.Usuario.Id, sesion.Usuario.Rol));

        // También puede entrar como alumno, como con Google.
        var comoAlumno = await conexion.EntrarConClaveAsync(
            new EntradaConClavePeticion("profe.clave@gmail.com", "secreta-123", "Alumno"), CancellationToken.None);
        Assert.Equal("Alumno", comoAlumno.Usuario.Rol);

        var mala = await Assert.ThrowsAsync<AccesoException>(() => conexion.EntrarConClaveAsync(
            new EntradaConClavePeticion("profe.clave@gmail.com", "otra-clave", null), CancellationToken.None));
        Assert.Equal(("clave_incorrecta", 401), (mala.Codigo, mala.Estado));
    }

    [Fact]
    public async Task El_alumno_con_contrasena_elige_a_su_profesor_antes_de_que_se_cree_su_cuenta()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var profesor = ProfesorDelCurso(banco);
        var (conexion, _) = Montar(banco);

        var resultado = await conexion.CrearCuentaAsync(Cuenta("alumno.clave@gmail.com", "Alumno"), CancellationToken.None);
        Assert.Null(resultado.Sesion);
        Assert.Equal(("alumno.clave@gmail.com", "Alumno"), (resultado.Registro!.Email, resultado.Registro.Perfil));
        Assert.DoesNotContain(banco.Db.Usuarios, u => u.Email == "alumno.clave@gmail.com");

        var sesion = await conexion.RegistrarAsync(resultado.Registro.Token, "Alumno", profesor.Id, CancellationToken.None);
        Assert.Equal("Alumno", sesion.Usuario.Rol);
        Assert.Equal(profesor.Id, banco.Db.Usuarios.Single(u => u.Id == sesion.Usuario.Id).ProfesorId);

        var otra = await conexion.EntrarConClaveAsync(
            new EntradaConClavePeticion("alumno.clave@gmail.com", "secreta-123", "Alumno"), CancellationToken.None);
        Assert.Equal(sesion.Usuario.Id, otra.Usuario.Id);
    }

    [Theory]
    [InlineData("sin-arroba", "Alumno", "secreta-123", "correo_invalido")]
    [InlineData("a@b.com", "Alumno", "corta", "clave_corta")]
    [InlineData("a@b.com", "Admin", "secreta-123", "rol_invalido")]
    public async Task Una_cuenta_con_datos_invalidos_no_se_crea(string email, string perfil, string clave, string codigo)
    {
        using var banco = new BancoDePruebas().Sembrar();
        var (conexion, _) = Montar(banco);

        var error = await Assert.ThrowsAsync<AccesoException>(
            () => conexion.CrearCuentaAsync(Cuenta(email, perfil, clave), CancellationToken.None));

        Assert.Equal(codigo, error.Codigo);
    }

    [Fact]
    public async Task Nadie_toma_con_contrasena_una_cuenta_que_ya_existe_ni_una_de_administrador()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var alumna = banco.Db.Usuarios.Single();
        var (conexion, _) = Montar(banco, administradores: ["directora@upc.edu.pe"]);

        var existente = await Assert.ThrowsAsync<AccesoException>(
            () => conexion.CrearCuentaAsync(Cuenta(alumna.Email, "Docente"), CancellationToken.None));
        Assert.Equal(("cuenta_existente", 409), (existente.Codigo, existente.Estado));
        Assert.Null(banco.Db.Usuarios.Single(u => u.Id == alumna.Id).ClaveHash);

        var admin = await Assert.ThrowsAsync<AccesoException>(
            () => conexion.CrearCuentaAsync(Cuenta("directora@upc.edu.pe", "Docente"), CancellationToken.None));
        Assert.Equal("cuenta_institucional", admin.Codigo);

        // La cuenta sin contraseña indica que entre por su proveedor.
        var sinClave = await Assert.ThrowsAsync<AccesoException>(() => conexion.EntrarConClaveAsync(
            new EntradaConClavePeticion(alumna.Email, "lo-que-sea-1", null), CancellationToken.None));
        Assert.Equal("cuenta_sin_clave", sinClave.Codigo);
    }

    [Fact]
    public async Task Un_registro_de_alumno_con_contrasena_no_entra_a_una_cuenta_creada_mientras_tanto()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var profesor = ProfesorDelCurso(banco);
        var (conexion, proveedor) = Montar(banco);

        var registro = (await conexion.CrearCuentaAsync(Cuenta("carrera@gmail.com", "Alumno"), CancellationToken.None)).Registro!;
        await EntrarAsync(conexion, proveedor, "carrera@gmail.com", "Docente");

        var error = await Assert.ThrowsAsync<AccesoException>(
            () => conexion.RegistrarAsync(registro.Token, "Alumno", profesor.Id, CancellationToken.None));
        Assert.Equal("cuenta_existente", error.Codigo);
    }

    [Fact]
    public async Task Entrar_con_Google_descarta_una_contrasena_creada_sin_verificar_el_correo()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var (conexion, proveedor) = Montar(banco);

        // Alguien crea una cuenta con un correo que no es suyo...
        await conexion.CrearCuentaAsync(Cuenta("victima@gmail.com", "Docente"), CancellationToken.None);

        // ...y la dueña real entra con Google: la contraseña ajena deja de servir.
        var real = await EntrarAsync(conexion, proveedor, "victima@gmail.com", "Docente");
        Assert.NotNull(real.Sesion);

        var usuario = banco.Db.Usuarios.Single(u => u.Email == "victima@gmail.com");
        Assert.Null(usuario.ClaveHash);
        Assert.True(usuario.CorreoVerificado);

        var error = await Assert.ThrowsAsync<AccesoException>(() => conexion.EntrarConClaveAsync(
            new EntradaConClavePeticion("victima@gmail.com", "secreta-123", null), CancellationToken.None));
        Assert.Equal("cuenta_sin_clave", error.Codigo);
    }

    private static string TokenDe(EnlaceRestablecer enlace) => enlace.Url[(enlace.Url.IndexOf("#t=") + 3)..];

    [Fact]
    public async Task El_enlace_del_administrador_cambia_la_contrasena_una_sola_vez()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var (conexion, _) = Montar(banco);
        var creada = await conexion.CrearCuentaAsync(Cuenta("olvido@gmail.com", "Docente"), CancellationToken.None);

        var enlace = await conexion.GenerarEnlaceRestablecerAsync(creada.Sesion!.Usuario.Id, CancellationToken.None);
        Assert.StartsWith("http://localhost:4200/restablecer#t=", enlace.Url);
        Assert.InRange(enlace.Vence, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25));

        // Solo se guarda el hash del enlace.
        var guardado = banco.Db.Usuarios.Single(u => u.Email == "olvido@gmail.com");
        Assert.NotEqual(TokenDe(enlace), guardado.RestablecerClaveHash);

        var corta = await Assert.ThrowsAsync<AccesoException>(() => conexion.RestablecerClaveAsync(
            new RestablecerClavePeticion(TokenDe(enlace), "corta"), CancellationToken.None));
        Assert.Equal("clave_corta", corta.Codigo);

        var sesion = await conexion.RestablecerClaveAsync(
            new RestablecerClavePeticion(TokenDe(enlace), "nueva-clave-456"), CancellationToken.None);
        Assert.Equal(guardado.Id, sesion.Usuario.Id);

        await conexion.EntrarConClaveAsync(new EntradaConClavePeticion("olvido@gmail.com", "nueva-clave-456", null), CancellationToken.None);
        var vieja = await Assert.ThrowsAsync<AccesoException>(() => conexion.EntrarConClaveAsync(
            new EntradaConClavePeticion("olvido@gmail.com", "secreta-123", null), CancellationToken.None));
        Assert.Equal("clave_incorrecta", vieja.Codigo);

        var repetido = await Assert.ThrowsAsync<AccesoException>(() => conexion.RestablecerClaveAsync(
            new RestablecerClavePeticion(TokenDe(enlace), "otra-clave-789"), CancellationToken.None));
        Assert.Equal("enlace_vencido", repetido.Codigo);
    }

    [Fact]
    public async Task Un_enlace_nuevo_anula_el_anterior_y_uno_vencido_no_sirve()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var alumna = banco.Db.Usuarios.Single();
        var (conexion, _) = Montar(banco);

        var primero = await conexion.GenerarEnlaceRestablecerAsync(alumna.Id, CancellationToken.None);
        var segundo = await conexion.GenerarEnlaceRestablecerAsync(alumna.Id, CancellationToken.None);

        var anulado = await Assert.ThrowsAsync<AccesoException>(() => conexion.RestablecerClaveAsync(
            new RestablecerClavePeticion(TokenDe(primero), "nueva-clave-456"), CancellationToken.None));
        Assert.Equal("enlace_vencido", anulado.Codigo);

        banco.Db.Usuarios.Single(u => u.Id == alumna.Id).RestablecerClaveVence = DateTimeOffset.UtcNow.AddMinutes(-1);
        banco.Db.SaveChanges();
        var vencido = await Assert.ThrowsAsync<AccesoException>(() => conexion.RestablecerClaveAsync(
            new RestablecerClavePeticion(TokenDe(segundo), "nueva-clave-456"), CancellationToken.None));
        Assert.Equal("enlace_vencido", vencido.Codigo);

        var inventado = await Assert.ThrowsAsync<AccesoException>(() => conexion.RestablecerClaveAsync(
            new RestablecerClavePeticion("inventado", "nueva-clave-456"), CancellationToken.None));
        Assert.Equal("enlace_vencido", inventado.Codigo);
    }

    [Fact]
    public async Task Quien_solo_entraba_con_Google_puede_tener_contrasena_con_el_enlace()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var (conexion, proveedor) = Montar(banco);
        var google = (await EntrarAsync(conexion, proveedor, "solo.google@gmail.com", "Docente")).Sesion!;

        var enlace = await conexion.GenerarEnlaceRestablecerAsync(google.Usuario.Id, CancellationToken.None);
        await conexion.RestablecerClaveAsync(new RestablecerClavePeticion(TokenDe(enlace), "nueva-clave-456"), CancellationToken.None);

        var sesion = await conexion.EntrarConClaveAsync(
            new EntradaConClavePeticion("solo.google@gmail.com", "nueva-clave-456", null), CancellationToken.None);
        Assert.Equal(google.Usuario.Id, sesion.Usuario.Id);

        // Ya con el correo verificado por Google, volver a entrar con Google no la borra.
        await EntrarAsync(conexion, proveedor, "solo.google@gmail.com", "Docente");
        Assert.NotNull(banco.Db.Usuarios.Single(u => u.Id == google.Usuario.Id).ClaveHash);
    }

    [Fact]
    public async Task Con_registro_cerrado_no_se_crean_cuentas_con_contrasena()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var (conexion, _) = Montar(banco, registroAbierto: false);

        var error = await Assert.ThrowsAsync<AccesoException>(
            () => conexion.CrearCuentaAsync(Cuenta("nadie@gmail.com", "Docente"), CancellationToken.None));

        Assert.Equal(("no_registrado", 403), (error.Codigo, error.Estado));
    }
}

public class CredencialesEnCarpetaTests
{
    [Fact]
    public void Lee_el_json_de_Google_y_el_de_Microsoft_sin_pisar_lo_configurado()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "secretos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        try
        {
            File.WriteAllText(Path.Combine(carpeta, "client_secret_123-abc.apps.googleusercontent.com.json"),
                """{"web":{"client_id":"id-google-de-prueba","client_secret":"secreto-google-de-prueba"}}""");
            File.WriteAllText(Path.Combine(carpeta, "microsoft.json"),
                """{"client_id":"id-ms-de-prueba","client_secret":"secreto-ms-de-prueba"}""");

            var opciones = new OpcionesAcceso
            {
                CarpetaSecretos = carpeta,
                Proveedores = new()
                {
                    ["google"] = new OpcionesProveedorIdentidad { Nombre = "Google" },
                    ["microsoft"] = new OpcionesProveedorIdentidad { Nombre = "Microsoft", ClientId = "ya-configurado" }
                }
            };

            CredencialesEnCarpeta.Completar(opciones);

            Assert.Equal(("id-google-de-prueba", "secreto-google-de-prueba"),
                (opciones.Proveedores["google"].ClientId, opciones.Proveedores["google"].ClientSecret));
            Assert.Equal("ya-configurado", opciones.Proveedores["microsoft"].ClientId);
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }
}

public class SesionApiTests
{
    [Fact]
    public async Task La_sesion_emitida_abre_la_api_y_una_falsificada_no()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();

        var entrada = await api.CreateClient().PostAsJsonAsync("/api/v1/acceso/desarrollo", new { usuarioId = alumnoId });
        var sesion = await entrada.Content.ReadFromJsonAsync<JsonElement>();
        var token = sesion.GetProperty("token").GetString()!;

        var cliente = api.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var yo = await cliente.GetFromJsonAsync<JsonElement>("/api/v1/acceso/yo");
        Assert.Equal("Alumno", yo.GetProperty("rol").GetString());

        var falsa = api.CreateClient();
        falsa.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccesoTests.IdToken(new
        {
            sub = alumnoId, role = "Admin", iss = "tutorpreclase", aud = "tutorpreclase",
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        }));

        Assert.Equal(HttpStatusCode.Unauthorized, (await falsa.GetAsync("/api/v1/acceso/yo")).StatusCode);
    }

    [Fact]
    public async Task La_cuenta_con_contrasena_se_crea_y_abre_la_api_por_http()
    {
        using var api = new ApiDePruebas();
        api.Sembrar();
        var cliente = api.CreateClient();

        var proveedores = await cliente.GetFromJsonAsync<JsonElement>("/api/v1/acceso/proveedores");
        Assert.True(proveedores.GetProperty("conClave").GetBoolean());

        var creada = await cliente.PostAsJsonAsync("/api/v1/acceso/cuenta", new
        {
            email = "http.profe@gmail.com", nombre = "Profe Http", clave = "secreta-123", perfil = "Docente"
        });
        Assert.Equal(HttpStatusCode.OK, creada.StatusCode);

        var entrada = await cliente.PostAsJsonAsync("/api/v1/acceso/clave", new
        {
            email = "http.profe@gmail.com", clave = "secreta-123", perfil = "Docente"
        });
        var token = (await entrada.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var conSesion = api.CreateClient();
        conSesion.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var yo = await conSesion.GetFromJsonAsync<JsonElement>("/api/v1/acceso/yo");
        Assert.Equal("Docente", yo.GetProperty("rol").GetString());

        var mala = await cliente.PostAsJsonAsync("/api/v1/acceso/clave", new { email = "http.profe@gmail.com", clave = "mala-clave" });
        Assert.Equal(HttpStatusCode.Unauthorized, mala.StatusCode);
        Assert.Equal("clave_incorrecta", (await mala.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Solo_el_administrador_genera_el_enlace_para_restablecer()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, _, _) = api.Sembrar();

        var comoAlumno = await api.Como(alumnoId, "Alumno").PostAsync($"/api/v1/admin/usuarios/{alumnoId}/restablecer-clave", null);
        Assert.Equal(HttpStatusCode.Forbidden, comoAlumno.StatusCode);

        var respuesta = await api.Como(Guid.NewGuid(), "Admin").PostAsync($"/api/v1/admin/usuarios/{alumnoId}/restablecer-clave", null);
        var enlace = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        var url = enlace.GetProperty("url").GetString()!;

        var cliente = api.CreateClient();
        var cambio = await cliente.PostAsJsonAsync("/api/v1/acceso/clave/restablecer", new
        {
            token = url[(url.IndexOf("#t=") + 3)..], clave = "nueva-clave-456"
        });
        Assert.Equal(HttpStatusCode.OK, cambio.StatusCode);
        Assert.Equal(alumnoId.ToString(), (await cambio.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("usuario").GetProperty("id").GetString());

        var usuarios = await api.Como(Guid.NewGuid(), "Admin").GetFromJsonAsync<JsonElement>("/api/v1/admin/usuarios");
        Assert.Contains(usuarios.EnumerateArray(), u => u.GetProperty("id").GetString() == alumnoId.ToString() && u.GetProperty("conClave").GetBoolean());
    }

    [Fact]
    public async Task El_administrador_da_de_alta_y_matricula_y_el_alumno_no_puede()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, cursoId, _) = api.Sembrar();
        var admin = api.Como(Guid.NewGuid(), "Admin");

        var alta = await admin.PutAsJsonAsync("/api/v1/admin/usuarios", new
        {
            email = "Nuevo.Alumno@UPC.edu.pe", nombre = "Nuevo Alumno", rol = "Alumno", cursos = new[] { cursoId }
        });
        Assert.Equal(HttpStatusCode.OK, alta.StatusCode);

        using (var db = api.NuevoContexto())
        {
            var nuevo = db.Usuarios.Single(u => u.Email == "nuevo.alumno@upc.edu.pe");
            Assert.Contains(db.Matriculas, m => m.UsuarioId == nuevo.Id && m.CursoId == cursoId);
        }

        var rolInventado = await admin.PutAsJsonAsync("/api/v1/admin/usuarios", new
        {
            email = "x@upc.edu.pe", nombre = "X", rol = "Rector", cursos = Array.Empty<Guid>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, rolInventado.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await api.Como(alumnoId, "Alumno").GetAsync("/api/v1/admin/usuarios")).StatusCode);

        // El profesor administra su contenido, no los usuarios.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await api.Como(Guid.NewGuid(), "Docente").GetAsync("/api/v1/admin/usuarios")).StatusCode);
    }

    [Fact]
    public async Task Cada_profesor_administra_solo_sus_cursos()
    {
        using var api = new ApiDePruebas();
        var (docenteId, _, _, claseId) = api.Sembrar();

        Assert.Equal(HttpStatusCode.OK,
            (await api.Como(docenteId, "Docente").GetAsync($"/api/v1/clases/{claseId}/reporte")).StatusCode);

        var otro = api.Como(Guid.NewGuid(), "Docente");
        Assert.Equal(HttpStatusCode.Forbidden, (await otro.GetAsync($"/api/v1/clases/{claseId}/reporte")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await otro.PutAsJsonAsync($"/api/v1/clases/{claseId}/ampliacion", new { permitida = true })).StatusCode);
    }

    [Fact]
    public async Task El_alumno_cambia_de_profesor_y_deja_de_ver_los_cursos_del_anterior()
    {
        using var api = new ApiDePruebas();
        var (docenteId, alumnoId, cursoId, _) = api.Sembrar();

        Guid otroId;
        using (var db = api.NuevoContexto())
        {
            var otro = new Usuario { Email = "otro@uni.edu", Nombre = "Otro Profe", Rol = RolUsuario.Docente };
            db.Usuarios.Add(otro);
            db.Cursos.Add(new Curso { Codigo = "ECO1", Nombre = "Economía", DocenteId = otro.Id });
            db.SaveChanges();
            otroId = otro.Id;
        }

        var alumno = api.Como(alumnoId, "Alumno");
        var actual = await alumno.GetFromJsonAsync<JsonElement>("/api/v1/alumno/profesor");
        Assert.Equal(docenteId, actual.GetProperty("profesor").GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.OK,
            (await alumno.PutAsJsonAsync("/api/v1/alumno/profesor", new { profesorId = otroId })).StatusCode);

        using (var db = api.NuevoContexto())
        {
            var cursos = db.Matriculas.Where(m => m.UsuarioId == alumnoId).Select(m => m.Curso!.Codigo).ToList();
            Assert.Equal(["ECO1"], cursos);
        }

        Assert.Equal(HttpStatusCode.BadRequest,
            (await alumno.PutAsJsonAsync("/api/v1/alumno/profesor", new { profesorId = alumnoId })).StatusCode);
    }
}

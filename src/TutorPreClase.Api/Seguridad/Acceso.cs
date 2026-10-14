using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Academico;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Api.Seguridad;

/// <summary>Seccion "Acceso": inicio de sesion con la cuenta de la universidad (RF-31).</summary>
public sealed class OpcionesAcceso
{
    public const string Seccion = "Acceso";

    /// <summary>Hoy solo "google"; la web muestra un botón por cada proveedor configurado aquí.</summary>
    public Dictionary<string, OpcionesProveedorIdentidad> Proveedores { get; set; } = [];

    /// <summary>Ruta de la web a la que vuelve cada proveedor: {UrlRetorno}/{proveedor}.</summary>
    public string UrlRetorno { get; set; } = "http://localhost:4200/entrar";

    /// <summary>Correos que entran como administrador aunque nadie los haya dado de alta.</summary>
    public string[] Administradores { get; set; } = [];

    /// <summary>
    /// Con registro abierto, quien entra por primera vez elige si es alumno o profesor, sin
    /// que nadie lo apruebe. Cerrado, solo entran los registrados (RF-33).
    /// </summary>
    public bool RegistroAbierto { get; set; } = true;

    /// <summary>
    /// Además de Google o Microsoft, crear una cuenta con correo y contraseña y entrar con
    /// ella (RF-41). Sigue las mismas reglas de registro abierto o cerrado.
    /// </summary>
    public bool ConClave { get; set; } = true;

    /// <summary>Clave (base64, 32+ bytes) con la que la API firma sus sesiones.</summary>
    public string ClaveSesion { get; set; } = "";

    public int HorasSesion { get; set; } = 12;

    /// <summary>
    /// Carpeta con las credenciales OAuth descargadas (fuera del repo): la API las lee sin que
    /// nadie tenga que copiarlas a la configuración. Ver <see cref="CredencialesEnCarpeta"/>.
    /// </summary>
    public string CarpetaSecretos { get; set; } = "";
}

/// <summary>
/// Completa el ClientId y el secreto de cada proveedor que no los tenga configurados, a
/// partir de los archivos de la carpeta de secretos:
/// <list type="bullet">
/// <item>Google: el JSON que descarga la consola (<c>client_secret_*.json</c>, sección <c>web</c>).</item>
/// <item>Microsoft: <c>microsoft.json</c> con <c>client_id</c> y <c>client_secret</c>.</item>
/// </list>
/// Nunca se registran los valores, solo si se encontraron.
/// </summary>
public static class CredencialesEnCarpeta
{
    public static void Completar(OpcionesAcceso opciones)
    {
        var carpeta = opciones.CarpetaSecretos;
        if (string.IsNullOrWhiteSpace(carpeta) || !Directory.Exists(carpeta)) return;

        if (opciones.Proveedores.TryGetValue("google", out var google) && string.IsNullOrWhiteSpace(google.ClientId))
        {
            var archivo = Directory.GetFiles(carpeta, "client_secret_*.json").OrderBy(f => f).FirstOrDefault();
            if (archivo is not null) Leer(google, archivo, "web");
        }

        if (opciones.Proveedores.TryGetValue("microsoft", out var microsoft) && string.IsNullOrWhiteSpace(microsoft.ClientId))
        {
            var archivo = Path.Combine(carpeta, "microsoft.json");
            if (File.Exists(archivo)) Leer(microsoft, archivo, null);
        }
    }

    private static void Leer(OpcionesProveedorIdentidad proveedor, string archivo, string? seccion)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(archivo));
            var raiz = seccion is not null && doc.RootElement.TryGetProperty(seccion, out var s) ? s : doc.RootElement;

            if (raiz.TryGetProperty("client_id", out var id) && raiz.TryGetProperty("client_secret", out var secreto))
            {
                proveedor.ClientId = id.GetString() ?? "";
                proveedor.ClientSecret = secreto.GetString() ?? "";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Un archivo ilegible deja el proveedor sin configurar: su botón lo dirá.
        }
    }
}

public sealed class OpcionesProveedorIdentidad
{
    public string Nombre { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string UrlAutorizacion { get; set; } = "";
    public string UrlToken { get; set; } = "";

    /// <summary>Emisores validos del id_token (prefijo, para los multi-inquilino).</summary>
    public string[] Emisores { get; set; } = [];
}

public sealed class AccesoException(string codigo, string mensaje, int estado = StatusCodes.Status400BadRequest)
    : Exception(mensaje)
{
    public string Codigo { get; } = codigo;
    public int Estado { get; } = estado;
}

/// <summary>
/// <c>Rol</c> es con el que actúa en esta sesión; <c>Vistas</c>, los que puede usar. Un
/// profesor puede entrar también como alumno para estudiar o ver lo que ven sus alumnos.
/// </summary>
public sealed record UsuarioSesion(Guid Id, string Nombre, string Email, string Rol, IReadOnlyList<string> Vistas);

public sealed record Sesion(string Token, UsuarioSesion Usuario);

/// <summary>Primer acceso: la identidad ya está validada y falta que la persona elija su rol.</summary>
public sealed record RegistroPendiente(string Token, string Email, string Nombre, string? Perfil = null);

/// <summary>Crear una cuenta con correo y contraseña (RF-41).</summary>
public sealed record CuentaNuevaPeticion(string Email, string Nombre, string Clave, string Perfil);

/// <summary>Entrar con correo y contraseña (RF-41).</summary>
public sealed record EntradaConClavePeticion(string Email, string Clave, string? Perfil);

/// <summary>Elegir una contraseña nueva con el enlace que dio el administrador (RF-42).</summary>
public sealed record RestablecerClavePeticion(string Token, string Clave);

/// <summary>El enlace se muestra una sola vez al administrador; solo se guarda su SHA-256.</summary>
public sealed record EnlaceRestablecer(string Url, DateTimeOffset Vence);

/// <summary>El canje da una sesión (usuario conocido) o un registro pendiente (primera vez).</summary>
public sealed record ResultadoAcceso(Sesion? Sesion, RegistroPendiente? Registro);

/// <summary>
/// Firma y valida las sesiones propias de la API. El proveedor (Microsoft o Google) solo
/// dice quien es la persona; el rol lo decide la plataforma (RF-31, RF-33).
/// </summary>
public sealed class ServicioSesion(IOptions<OpcionesAcceso> opciones, IHostEnvironment entorno)
{
    public const string Emisor = "tutorpreclase";
    public const string Esquema = "Sesion";

    private readonly SymmetricSecurityKey _clave = Clave(opciones.Value.ClaveSesion, entorno);

    public SymmetricSecurityKey ClaveFirma => _clave;

    /// <summary>Los roles con los que puede actuar: el profesor, también como alumno.</summary>
    public static IReadOnlyList<string> VistasDe(Usuario usuario) =>
        usuario.Rol == RolUsuario.Docente
            ? [nameof(RolUsuario.Docente), nameof(RolUsuario.Alumno)]
            : [usuario.Rol.ToString()];

    public Sesion Emitir(Usuario usuario, string? comoRol = null)
    {
        var vistas = VistasDe(usuario);
        var rol = comoRol is not null && vistas.Contains(comoRol) ? comoRol : usuario.Rol.ToString();

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Emisor,
            Audience = Emisor,
            Expires = DateTime.UtcNow.AddHours(Math.Max(1, opciones.Value.HorasSesion)),
            SigningCredentials = new SigningCredentials(_clave, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, usuario.Id.ToString()),
                new Claim("role", rol),
                new Claim(JwtRegisteredClaimNames.Name, usuario.Nombre),
                new Claim(JwtRegisteredClaimNames.Email, usuario.Email)
            ])
        });

        return new Sesion(token, new UsuarioSesion(usuario.Id, usuario.Nombre, usuario.Email, rol, vistas));
    }

    private static SymmetricSecurityKey Clave(string configurada, IHostEnvironment entorno)
    {
        if (!string.IsNullOrWhiteSpace(configurada))
            return new SymmetricSecurityKey(Convert.FromBase64String(configurada));

        if (entorno.IsProduction())
            throw new InvalidOperationException("Falta Acceso:ClaveSesion: la API no puede firmar sesiones.");

        // En desarrollo, una clave por proceso: al reiniciar hay que volver a entrar.
        return new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
    }
}

/// <summary>
/// OpenID Connect con codigo de autorizacion + PKCE contra Microsoft o Google (RF-31). El
/// canje lo hace la API directamente con el proveedor por TLS, asi que el id_token que
/// recibe se valida por sus afirmaciones (emisor, audiencia, vigencia, nonce) sin viajar
/// por el navegador.
/// </summary>
public sealed class ConexionIdentidad(
    IMemoryCache cache,
    IHttpClientFactory http,
    IAppDbContext db,
    ServicioSesion sesiones,
    IOptions<OpcionesAcceso> opciones,
    ILogger<ConexionIdentidad> log)
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Los proveedores definidos, con si ya tienen ClientId: la web muestra un botón por cada
    /// uno. Uno retirado de la configuración no reaparece porque quede su ClientId en una
    /// variable de entorno: sin sus direcciones no es un proveedor.
    /// </summary>
    public IEnumerable<(string Id, string Nombre, bool Configurado)> Disponibles() =>
        opciones.Value.Proveedores
            .Where(p => Definido(p.Value))
            .Select(p => (p.Key, p.Value.Nombre, !string.IsNullOrWhiteSpace(p.Value.ClientId)));

    /// <summary>
    /// <paramref name="perfil"/> es lo que la persona eligió antes de ir al proveedor
    /// ("Alumno" o "Docente"); viaja en el servidor ligado al <c>state</c>, no en la URL.
    /// </summary>
    public string Iniciar(string proveedor, string? perfil = null)
    {
        if (perfil is not null and not (nameof(RolUsuario.Alumno) or nameof(RolUsuario.Docente)))
            throw new AccesoException("rol_invalido", "Elige si entras como alumno o como profesor.");

        var config = Proveedor(proveedor);

        var estado = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        var verificador = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var desafio = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verificador)));

        cache.Set(ClaveCache(estado), new Pendiente(proveedor, verificador, nonce, perfil), Vigencia);

        return config.UrlAutorizacion +
               $"?client_id={Uri.EscapeDataString(config.ClientId)}" +
               "&response_type=code" +
               $"&redirect_uri={Uri.EscapeDataString(Retorno(proveedor))}" +
               $"&scope={Uri.EscapeDataString("openid email profile")}" +
               $"&state={estado}&nonce={nonce}" +
               $"&code_challenge={desafio}&code_challenge_method=S256" +
               "&prompt=select_account";
    }

    public async Task<ResultadoAcceso> CanjearAsync(string proveedor, string codigo, string estado, CancellationToken ct)
    {
        var config = Proveedor(proveedor);

        // El estado es de un solo uso y liga el retorno con quien inicio el acceso.
        if (!cache.TryGetValue(ClaveCache(estado), out Pendiente? pendiente) || pendiente is null ||
            pendiente.Proveedor != proveedor)
            throw new AccesoException("acceso_vencido", "El inicio de sesión venció. Vuelve a intentarlo.");

        cache.Remove(ClaveCache(estado));

        var idToken = await PedirTokenAsync(proveedor, config, codigo, pendiente.Verificador, ct);
        var (email, nombre) = Validar(config, idToken, pendiente.Nonce);

        var perfil = pendiente.Perfil;
        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (usuario is not null)
        {
            // El profesor cuya carpeta ya existía quedó registrado con su correo como nombre.
            if (usuario.Nombre == usuario.Email && nombre.Length > 0) usuario.Nombre = nombre;

            // El proveedor prueba que el correo es de quien entra. Una contraseña creada antes
            // sin esa prueba pudo ponerla otra persona: se descarta (RF-41).
            if (!usuario.CorreoVerificado)
            {
                if (usuario.ClaveHash is not null)
                    log.LogWarning("{Email} entró con {Proveedor}: se descarta la contraseña sin verificar", email, proveedor);
                usuario.ClaveHash = null;
                usuario.CorreoVerificado = true;
            }

            // Quien entra como profesor lo es desde ahora (y puede seguir entrando como alumno).
            if (perfil == nameof(RolUsuario.Docente) && usuario.Rol == RolUsuario.Alumno)
                usuario.Rol = RolUsuario.Docente;

            await db.SaveChangesAsync(ct);
            return new ResultadoAcceso(sesiones.Emitir(usuario, perfil), null);
        }

        var o = opciones.Value;
        if (o.Administradores.Any(a => string.Equals(a.Trim(), email, StringComparison.OrdinalIgnoreCase)))
            return new ResultadoAcceso(sesiones.Emitir(await CrearAsync(email, nombre, RolUsuario.Admin, null, null, ct)), null);

        if (!o.RegistroAbierto)
            throw new AccesoException("no_registrado",
                $"La cuenta {email} no está registrada en la plataforma. Pide al administrador que te dé de alta.",
                StatusCodes.Status403Forbidden);

        // Primera vez como profesor: no hay nada más que elegir.
        if (perfil == nameof(RolUsuario.Docente))
            return new ResultadoAcceso(sesiones.Emitir(await CrearAsync(email, nombre, RolUsuario.Docente, null, null, ct)), null);

        // Primera vez como alumno (o sin perfil): falta elegir profesor (o el rol).
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        var registro = new RegistroPendiente(token, email, nombre.Length > 0 ? nombre : email, perfil);
        cache.Set(ClaveRegistro(token), registro, Vigencia);

        return new ResultadoAcceso(null, registro);
    }

    public bool ConClave => opciones.Value.ConClave;

    public bool RegistroVigente(string? token) =>
        !string.IsNullOrEmpty(token) && cache.TryGetValue(ClaveRegistro(token), out RegistroPendiente? _);

    /// <summary>
    /// Completa el primer acceso con el rol que eligió la persona (RF-33). El alumno elige
    /// además a su profesor y queda solo en los cursos de ese profesor.
    /// </summary>
    public async Task<Sesion> RegistrarAsync(string token, string rol, Guid? profesorId, CancellationToken ct)
    {
        if (!cache.TryGetValue(ClaveRegistro(token), out RegistroPendiente? registro) || registro is null)
            throw new AccesoException("acceso_vencido", "El registro venció. Vuelve a entrar.");

        var eleccion = rol switch
        {
            "Alumno" => RolUsuario.Alumno,
            "Docente" => RolUsuario.Docente,
            _ => throw new AccesoException("rol_invalido", "Elige si entras como alumno o como profesor.")
        };

        if (eleccion == RolUsuario.Alumno)
            await ValidarProfesorAsync(profesorId, ct);

        cache.Remove(ClaveRegistro(token));
        var claveHash = cache.Get<string>(ClaveRegistroConClave(token));
        cache.Remove(ClaveRegistroConClave(token));

        // Si entró dos veces a la vez, la segunda encuentra al usuario ya creado. Con
        // contraseña no: nadie entra a una cuenta existente solo por haber elegido una.
        var existente = await db.Usuarios.FirstOrDefaultAsync(u => u.Email == registro.Email, ct);
        if (existente is not null && claveHash is not null) throw CuentaExistente(registro.Email);

        var usuario = existente
                      ?? await CrearAsync(registro.Email, registro.Nombre, eleccion,
                          eleccion == RolUsuario.Alumno ? profesorId : null, claveHash, ct);

        return sesiones.Emitir(usuario);
    }

    public const int LargoMinimoClave = 8;

    /// <summary>
    /// Crea una cuenta con correo y contraseña (RF-41). El profesor entra al momento; el
    /// alumno recibe un registro pendiente para elegir a su profesor, como con Google.
    /// El correo no se verifica: por eso nunca da rol de administrador, y si luego alguien
    /// entra con Google o Microsoft con ese correo, la contraseña se descarta.
    /// </summary>
    public async Task<ResultadoAcceso> CrearCuentaAsync(CuentaNuevaPeticion peticion, CancellationToken ct)
    {
        var o = opciones.Value;
        if (!o.ConClave)
            throw new AccesoException("clave_no_disponible", "La plataforma no admite cuentas con contraseña.");

        var email = NormalizarCorreo(peticion.Email);
        var nombre = (peticion.Nombre ?? "").Trim();
        var clave = peticion.Clave ?? "";

        if (peticion.Perfil is not (nameof(RolUsuario.Alumno) or nameof(RolUsuario.Docente)))
            throw new AccesoException("rol_invalido", "Elige si entras como alumno o como profesor.");
        if (nombre.Length is 0 or > 200)
            throw new AccesoException("nombre_invalido", "Escribe tu nombre.");
        if (clave.Length < LargoMinimoClave)
            throw new AccesoException("clave_corta", $"La contraseña debe tener al menos {LargoMinimoClave} caracteres.");
        if (clave.Length > 128)
            throw new AccesoException("clave_larga", "La contraseña no puede pasar de 128 caracteres.");

        if (o.Administradores.Any(a => string.Equals(a.Trim(), email, StringComparison.OrdinalIgnoreCase)))
            throw new AccesoException("cuenta_institucional",
                "Esta cuenta es de administración: entra con Google o Microsoft.", StatusCodes.Status403Forbidden);

        if (await db.Usuarios.AnyAsync(u => u.Email == email, ct)) throw CuentaExistente(email);

        if (!o.RegistroAbierto)
            throw new AccesoException("no_registrado",
                $"La cuenta {email} no está registrada en la plataforma. Pide al administrador que te dé de alta.",
                StatusCodes.Status403Forbidden);

        var claveHash = Hasher.HashPassword(null!, clave);

        if (peticion.Perfil == nameof(RolUsuario.Docente))
            return new ResultadoAcceso(
                sesiones.Emitir(await CrearAsync(email, nombre, RolUsuario.Docente, null, claveHash, ct)), null);

        // El hash queda en el servidor; al navegador solo va el token del registro.
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        var registro = new RegistroPendiente(token, email, nombre, nameof(RolUsuario.Alumno));
        cache.Set(ClaveRegistro(token), registro, Vigencia);
        cache.Set(ClaveRegistroConClave(token), claveHash, Vigencia);

        return new ResultadoAcceso(null, registro);
    }

    /// <summary>
    /// Entra con correo y contraseña (RF-41). El perfil funciona como con Google: un profesor
    /// puede entrar como alumno y un alumno que entra como profesor pasa a serlo.
    /// </summary>
    public async Task<Sesion> EntrarConClaveAsync(EntradaConClavePeticion peticion, CancellationToken ct)
    {
        if (!opciones.Value.ConClave)
            throw new AccesoException("clave_no_disponible", "La plataforma no admite cuentas con contraseña.");

        var perfil = peticion.Perfil;
        if (perfil is not null and not (nameof(RolUsuario.Alumno) or nameof(RolUsuario.Docente)))
            throw new AccesoException("rol_invalido", "Elige si entras como alumno o como profesor.");

        var email = (peticion.Email ?? "").Trim().ToLowerInvariant();
        var usuario = email.Length == 0 ? null : await db.Usuarios.FirstOrDefaultAsync(u => u.Email == email, ct);

        var resultado = usuario?.ClaveHash is { } hash
            ? Hasher.VerifyHashedPassword(usuario, hash, peticion.Clave ?? "")
            : PasswordVerificationResult.Failed;

        if (usuario is null || resultado == PasswordVerificationResult.Failed)
        {
            // La cuenta existe pero no tiene contraseña: entra por su proveedor.
            if (usuario is { ClaveHash: null })
                throw new AccesoException("cuenta_sin_clave",
                    "Esta cuenta entra con Google o Microsoft. Usa ese botón.", StatusCodes.Status401Unauthorized);

            throw new AccesoException("clave_incorrecta", "Correo o contraseña incorrectos.", StatusCodes.Status401Unauthorized);
        }

        if (resultado == PasswordVerificationResult.SuccessRehashNeeded)
            usuario.ClaveHash = Hasher.HashPassword(usuario, peticion.Clave!);

        if (perfil == nameof(RolUsuario.Docente) && usuario.Rol == RolUsuario.Alumno)
            usuario.Rol = RolUsuario.Docente;

        await db.SaveChangesAsync(ct);
        return sesiones.Emitir(usuario, perfil);
    }

    public static readonly TimeSpan VigenciaRestablecer = TimeSpan.FromHours(24);

    /// <summary>
    /// Olvidé mi contraseña (RF-42). No hay servicio de correo: el administrador genera un
    /// enlace de un solo uso (24 h) y se lo pasa a la persona. Generar otro anula el anterior.
    /// Sirve también para dar contraseña a quien solo entraba con Google.
    /// </summary>
    public async Task<EnlaceRestablecer> GenerarEnlaceRestablecerAsync(Guid usuarioId, CancellationToken ct)
    {
        if (!opciones.Value.ConClave)
            throw new AccesoException("clave_no_disponible", "La plataforma no admite cuentas con contraseña.");

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId, ct)
                      ?? throw new AccesoException("usuario_no_encontrado", "Esa persona no está registrada.", StatusCodes.Status404NotFound);

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var vence = DateTimeOffset.UtcNow.Add(VigenciaRestablecer);
        usuario.RestablecerClaveHash = HashEnlace(token);
        usuario.RestablecerClaveVence = vence;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Se generó un enlace para restablecer la contraseña de {Email}", usuario.Email);

        // En el fragmento (#) el token no llega a ningún servidor ni a sus registros.
        return new EnlaceRestablecer($"{UrlWeb()}/restablecer#t={token}", vence);
    }

    /// <summary>Con el enlace vigente, fija la contraseña nueva, lo anula y abre la sesión.</summary>
    public async Task<Sesion> RestablecerClaveAsync(RestablecerClavePeticion peticion, CancellationToken ct)
    {
        if (!opciones.Value.ConClave)
            throw new AccesoException("clave_no_disponible", "La plataforma no admite cuentas con contraseña.");

        var clave = peticion.Clave ?? "";
        if (clave.Length < LargoMinimoClave)
            throw new AccesoException("clave_corta", $"La contraseña debe tener al menos {LargoMinimoClave} caracteres.");
        if (clave.Length > 128)
            throw new AccesoException("clave_larga", "La contraseña no puede pasar de 128 caracteres.");

        var hash = string.IsNullOrWhiteSpace(peticion.Token) ? null : HashEnlace(peticion.Token.Trim());
        var usuario = hash is null ? null : await db.Usuarios.FirstOrDefaultAsync(u => u.RestablecerClaveHash == hash, ct);

        if (usuario is null || usuario.RestablecerClaveVence is not { } vence || vence < DateTimeOffset.UtcNow)
            throw new AccesoException("enlace_vencido",
                "Este enlace ya se usó o venció. Pide uno nuevo al administrador.", StatusCodes.Status400BadRequest);

        usuario.ClaveHash = Hasher.HashPassword(usuario, clave);
        usuario.RestablecerClaveHash = null;
        usuario.RestablecerClaveVence = null;
        await db.SaveChangesAsync(ct);

        log.LogInformation("{Email} restableció su contraseña", usuario.Email);
        return sesiones.Emitir(usuario);
    }

    private static string HashEnlace(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>La web es la de la URL de retorno sin su ruta /entrar.</summary>
    private string UrlWeb()
    {
        var retorno = new Uri(opciones.Value.UrlRetorno);
        return retorno.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>PBKDF2 con sal por usuario, el mismo de ASP.NET Core Identity.</summary>
    private static readonly PasswordHasher<Usuario> Hasher = new();

    private static string NormalizarCorreo(string? correo)
    {
        var email = (correo ?? "").Trim().ToLowerInvariant();
        var arroba = email.IndexOf('@');
        if (email.Length > 320 || arroba < 1 || arroba != email.LastIndexOf('@') ||
            !email[(arroba + 1)..].Contains('.') || email.Any(char.IsWhiteSpace))
            throw new AccesoException("correo_invalido", "Escribe un correo válido.");
        return email;
    }

    private static AccesoException CuentaExistente(string email) =>
        new("cuenta_existente", $"Ya hay una cuenta con {email}. Entra con tu contraseña o con Google o Microsoft.",
            StatusCodes.Status409Conflict);

    public async Task ValidarProfesorAsync(Guid? profesorId, CancellationToken ct)
    {
        if (profesorId is not Guid id || !await db.Usuarios.AnyAsync(u => u.Id == id && u.Rol == RolUsuario.Docente, ct))
            throw new AccesoException("profesor_invalido", "Elige a tu profesor de la lista.");
    }

    /// <summary>Sin <paramref name="claveHash"/>, la identidad vino de Google o Microsoft: correo verificado.</summary>
    private async Task<Usuario> CrearAsync(
        string email, string nombre, RolUsuario rol, Guid? profesorId, string? claveHash, CancellationToken ct)
    {
        var usuario = new Usuario
        {
            Email = email,
            Nombre = nombre.Length > 0 ? nombre : email,
            Rol = rol,
            ProfesorId = profesorId,
            ClaveHash = claveHash,
            CorreoVerificado = claveHash is null
        };
        db.Usuarios.Add(usuario);

        if (rol == RolUsuario.Alumno) await MatriculasPorProfesor.AlinearAlumnoAsync(db, usuario, ct);

        await db.SaveChangesAsync(ct);
        log.LogInformation("{Email} se registró como {Rol}", email, rol);
        return usuario;
    }

    private static string ClaveRegistro(string token) => $"registro:{token}";

    private static string ClaveRegistroConClave(string token) => $"registro-clave:{token}";

    private async Task<string> PedirTokenAsync(
        string proveedor, OpcionesProveedorIdentidad config, string codigo, string verificador, CancellationToken ct)
    {
        using var respuesta = await http.CreateClient("identidad").PostAsync(config.UrlToken, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = codigo,
                ["redirect_uri"] = Retorno(proveedor),
                ["client_id"] = config.ClientId,
                ["client_secret"] = config.ClientSecret,
                ["code_verifier"] = verificador
            }), ct);

        // El cuerpo no se registra: si el canje sale bien, trae tokens.
        if (!respuesta.IsSuccessStatusCode)
        {
            log.LogWarning("{Proveedor} rechazo el canje del codigo con {Estado}", proveedor, (int)respuesta.StatusCode);
            throw new AccesoException("acceso_rechazado", $"{config.Nombre} no aceptó el inicio de sesión. Vuelve a intentarlo.");
        }

        using var cuerpo = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(ct));
        return cuerpo.RootElement.TryGetProperty("id_token", out var token) && token.GetString() is { } valor
            ? valor
            : throw new AccesoException("acceso_rechazado", $"{config.Nombre} no devolvió tu identidad.");
    }

    public static (string Email, string Nombre) Validar(OpcionesProveedorIdentidad config, string idToken, string nonce)
    {
        JsonWebToken token;
        try
        {
            token = new JsonWebToken(idToken);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenMalformedException)
        {
            throw new AccesoException("acceso_rechazado", "La identidad recibida no es válida.");
        }

        var emisorValido = config.Emisores.Any(e => token.Issuer.StartsWith(e, StringComparison.OrdinalIgnoreCase));
        var audienciaValida = token.Audiences.Contains(config.ClientId);
        var vigente = token.ValidTo > DateTime.UtcNow.AddMinutes(-2);
        var mismoNonce = token.TryGetPayloadValue<string>("nonce", out var n) && n == nonce;

        if (!emisorValido || !audienciaValida || !vigente || !mismoNonce)
            throw new AccesoException("acceso_rechazado", "La identidad recibida no es válida.");

        // Google marca si el correo esta verificado; Microsoft usa el nombre de la cuenta.
        if (token.TryGetPayloadValue<bool>("email_verified", out var verificado) && !verificado)
            throw new AccesoException("acceso_rechazado", "Tu correo no está verificado en tu cuenta.");

        var email = token.TryGetPayloadValue<string>("email", out var e) && !string.IsNullOrWhiteSpace(e) ? e
                  : token.TryGetPayloadValue<string>("preferred_username", out var p) ? p : "";

        if (!email.Contains('@'))
            throw new AccesoException("acceso_rechazado", "Tu cuenta no tiene un correo asociado.");

        var nombre = token.TryGetPayloadValue<string>("name", out var nom) ? nom.Trim() : "";
        return (email.Trim().ToLowerInvariant(), nombre);
    }

    private OpcionesProveedorIdentidad Proveedor(string proveedor) =>
        opciones.Value.Proveedores.TryGetValue(proveedor, out var config) && Definido(config) &&
        !string.IsNullOrWhiteSpace(config.ClientId)
            ? config
            : throw new AccesoException("proveedor_no_disponible", "Ese inicio de sesión no está configurado.");

    private static bool Definido(OpcionesProveedorIdentidad config) =>
        !string.IsNullOrWhiteSpace(config.UrlAutorizacion) && !string.IsNullOrWhiteSpace(config.UrlToken);

    private string Retorno(string proveedor) => $"{opciones.Value.UrlRetorno.TrimEnd('/')}/{proveedor}";

    private static string ClaveCache(string estado) => $"acceso:{estado}";

    private sealed record Pendiente(string Proveedor, string Verificador, string Nonce, string? Perfil);
}

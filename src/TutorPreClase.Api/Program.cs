using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using TutorPreClase.Api.Endpoints;
using TutorPreClase.Api.Mcp;
using TutorPreClase.Api.Seguridad;
using TutorPreClase.Application.Evaluacion;
using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Reportes;
using TutorPreClase.Infrastructure;
using TutorPreClase.Infrastructure.Contenido;
using TutorPreClase.Infrastructure.Persistencia;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AgregarInfraestructura(builder.Configuration);
builder.Services.AddScoped<IServicioReporte, ServicioReporte>();

builder.Services.AddSingleton<IColaExtraccion, ColaExtraccionEnMemoria>();
builder.Services.AddHostedService<ServicioExtraccionEnSegundoPlano>();

// La IA condensa el material una vez, con la cuenta del profesor (SDD §6.2).
builder.Services.AddHostedService<ServicioCondensacionEnSegundoPlano>();

// La carpeta de contenido es el canal del docente: se revisa periodicamente (SDD §6.1).
builder.Services.AddHostedService<ServicioSincronizacionCarpeta>();

builder.Services.AddProblemDetails();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origenes").Get<string[]>() ?? ["http://localhost:4200"])
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Inicio de sesion con Microsoft o Google (RF-31): la API canjea el codigo y emite su
// propia sesion, con el rol que tiene el usuario en la plataforma.
builder.Services.Configure<OpcionesAcceso>(builder.Configuration.GetSection(OpcionesAcceso.Seccion));
builder.Services.PostConfigure<OpcionesAcceso>(CredencialesEnCarpeta.Completar);

// Gemini se conecta con el mismo cliente OAuth de Google del inicio de sesión (RF-37): sus
// credenciales salen de secrets/ o de las variables del servidor, nunca de appsettings.
builder.Services.AddOptions<OpcionesAgentes>()
    .PostConfigure<Microsoft.Extensions.Options.IOptions<OpcionesAcceso>>((agentes, acceso) =>
    {
        if (!acceso.Value.Proveedores.TryGetValue("google", out var google)) return;

        foreach (var agente in agentes.Values.Where(a => a.Conexion == ConexionAgente.OAuthGoogle))
        {
            if (string.IsNullOrWhiteSpace(agente.ClientId)) agente.ClientId = google.ClientId;
            if (string.IsNullOrWhiteSpace(agente.ClientSecret)) agente.ClientSecret = google.ClientSecret;
        }
    });
builder.Services.AddSingleton<ServicioSesion>();
builder.Services.AddScoped<ConexionIdentidad>();
builder.Services.AddHttpClient("identidad", c => c.Timeout = TimeSpan.FromSeconds(20));

const string EsquemaMixto = "Mixto";
var esProduccion = builder.Environment.IsProduction();

var autenticacion = builder.Services.AddAuthentication(EsquemaMixto)
    // Con "Authorization: Bearer" manda la sesion; fuera de produccion, sin ella, las
    // cabeceras de prueba (X-Usuario-Id) para curl y las pruebas automaticas.
    // En /mcp solo vale el token personal del asistente del alumno (RF-39).
    .AddPolicyScheme(EsquemaMixto, EsquemaMixto, o => o.ForwardDefaultSelector = contexto =>
        contexto.Request.Path.StartsWithSegments(EndpointsMcp.Ruta)
            ? ManejadorTokenMcp.Esquema
            : esProduccion || contexto.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)
                ? ServicioSesion.Esquema
                : ManejadorAutenticacionDesarrollo.Esquema)
    .AddJwtBearer(ServicioSesion.Esquema, _ => { })
    .AddScheme<OpcionesAutenticacionTokenMcp, ManejadorTokenMcp>(ManejadorTokenMcp.Esquema, _ => { });

builder.Services.AddOptions<JwtBearerOptions>(ServicioSesion.Esquema)
    .Configure<ServicioSesion>((o, sesiones) => o.TokenValidationParameters = new()
    {
        ValidIssuer = ServicioSesion.Emisor,
        ValidAudience = ServicioSesion.Emisor,
        IssuerSigningKey = sesiones.ClaveFirma,
        RoleClaimType = System.Security.Claims.ClaimTypes.Role
    });

if (!esProduccion)
{
    autenticacion.AddScheme<OpcionesAutenticacionDesarrollo, ManejadorAutenticacionDesarrollo>(
        ManejadorAutenticacionDesarrollo.Esquema, _ => { });
}

builder.Services.AddAuthorization();

// El Tutor como servidor MCP (RF-39): sin estado, cada llamada trae el token del alumno.
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "tutor-preclase", Title = "Tutor Pre-Clase", Version = "1.0.0" };
        o.ServerInstructions = HerramientasMcp.Instrucciones;
    })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<HerramientasMcp>();

var app = builder.Build();

// Fuera de produccion conviene ver el detalle del fallo, no un 500 opaco.
if (app.Environment.IsProduction()) app.UseExceptionHandler();
else app.UseDeveloperExceptionPage();

app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Los errores de negocio del examen viajan como 409 con su codigo, no como 500.
app.Use(async (contexto, siguiente) =>
{
    try
    {
        await siguiente(contexto);
    }
    catch (ExamenException ex)
    {
        contexto.Response.StatusCode = StatusCodes.Status409Conflict;
        await contexto.Response.WriteAsJsonAsync(new { error = ex.Codigo, mensaje = ex.Message });
    }
    catch (CredencialException ex)
    {
        contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
        await contexto.Response.WriteAsJsonAsync(new { error = ex.Codigo, mensaje = ex.Message });
    }
    catch (AccesoException ex)
    {
        contexto.Response.StatusCode = ex.Estado;
        await contexto.Response.WriteAsJsonAsync(new { error = ex.Codigo, mensaje = ex.Message });
    }
    catch (UnauthorizedAccessException)
    {
        contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
});

app.MapGet("/salud", () => Results.Ok(new { estado = "ok" })).AllowAnonymous();

app.MapearAcceso(app.Environment);
app.MapearAlumno();
app.MapearCredenciales();
app.MapearDocente();
app.MapearAdmin();
app.MapearMcp();

// En un despliegue (Railway) el contenedor deja el esquema al día al arrancar y asegura
// el agente de fábrica; Base:MigrarAlArrancar=false lo desactiva.
if (!app.Environment.IsDevelopment() && app.Configuration.GetValue("Base:MigrarAlArrancar", false))
{
    using var ambito = app.Services.CreateScope();
    var db = ambito.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await DatosIniciales.SembrarAsync(db);
}

if (app.Environment.IsDevelopment())
{
    using var ambito = app.Services.CreateScope();
    var db = ambito.ServiceProvider.GetRequiredService<AppDbContext>();

    // Las migraciones son de PostgreSQL; con Sqlite se crea el esquema directo.
    if (db.Database.IsNpgsql()) await db.Database.MigrateAsync();
    else
    {
        await db.Database.EnsureCreatedAsync();

        // EnsureCreated no toca una base que ya existe: las tablas nuevas (p. ej. token_mcp)
        // se agregan aquí para no tener que borrar la base local con sus datos.
        var script = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ")
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ");
        await db.Database.ExecuteSqlRawAsync(script);

        // Tampoco quita columnas: el examen dejó de tener fechas propias (lo habilita la
        // fecha de su clase).
        await EsquemaSqlite.QuitarColumnaAsync(db, "examen", "AbreEn");
        await EsquemaSqlite.QuitarColumnaAsync(db, "examen", "CierraEn");

        // Ni agrega columnas: el reloj del intento ahora se puede pausar.
        await EsquemaSqlite.AgregarColumnaAsync(db, "intento", "PausadoDesde", "INTEGER NULL");
        await EsquemaSqlite.AgregarColumnaAsync(db, "intento", "SegundosEnPausa", "INTEGER NOT NULL DEFAULT 0");
    }

    await DatosIniciales.SembrarAsync(db);

    // Primero la carpeta, para que la demo encuentre los cursos y clases que crea.
    await ambito.ServiceProvider.GetRequiredService<ISincronizadorCarpeta>().SincronizarAsync();

    if (app.Configuration.GetValue("Demo:Sembrar", false))
    {
        var log = ambito.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Demo");
        var ids = await DatosDemo.SembrarAsync(db, log);
        app.MapGet("/demo", () => Results.Ok(ids)).AllowAnonymous();
    }
}

app.Run();

public partial class Program;

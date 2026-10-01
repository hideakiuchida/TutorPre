using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Contenido;
using TutorPreClase.Application.Evaluacion;
using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Nivel;
using TutorPreClase.Application.Tutor;
using TutorPreClase.Infrastructure.Contenido;
using TutorPreClase.Infrastructure.Llm;
using TutorPreClase.Infrastructure.Persistencia;

namespace TutorPreClase.Infrastructure;

public static class RegistroServicios
{
    public static IServiceCollection AgregarInfraestructura(
        this IServiceCollection servicios, IConfiguration config)
    {
        // Postgres es el proveedor del SDD; Sqlite permite levantar en local o en
        // pruebas sin Docker. Solo se registra uno: EF no admite dos a la vez.
        var proveedor = config["Base:Proveedor"] ?? "Postgres";

        servicios.AddDbContext<AppDbContext>(o =>
        {
            if (proveedor.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
                o.UseSqlite(config.GetConnectionString("Sqlite") ?? "DataSource=tutorpreclase.db");
            else
                o.UseNpgsql(config.GetConnectionString("Postgres"));
        });

        servicios.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        servicios.AddMemoryCache();
        servicios.AddSingleton<IRelojSistema, RelojSistema>();

        servicios.Configure<OpcionesContextoClase>(config.GetSection(OpcionesContextoClase.Seccion));
        servicios.Configure<OpcionesAgentes>(config.GetSection(OpcionesAgentes.Seccion));

        servicios.AddScoped<IContextoClaseService, ContextoClaseService>();
        servicios.AddScoped<INivelService, NivelService>();
        servicios.AddScoped<IServicioExamen, ServicioExamen>();
        servicios.AddScoped<IEjecutorHerramientas, EjecutorHerramientas>();
        servicios.AddSingleton<IGeneradorExamen>(new GeneradorExamen());
        servicios.AddScoped<IAgenteTutorService, AgenteTutorService>();
        servicios.AddScoped<TutorPreClase.Application.Mcp.IServicioTokenMcp, TutorPreClase.Application.Mcp.ServicioTokenMcp>();

        // El examen se genera pregunta a pregunta (RF-04).
        servicios.AddSingleton<CandadosIntento>();
        servicios.AddScoped<IPreguntasDelIntento, PreguntasDelIntento>();
        servicios.AddSingleton<IPreparadorPreguntas, TutorPreClase.Infrastructure.Evaluacion.PreparadorPreguntasEnSegundoPlano>();

        servicios.AddSingleton<IAlmacenArchivos>(_ =>
            new AlmacenArchivosLocal(config["Almacen:Raiz"] ?? "almacen"));
        servicios.AddScoped<IServicioContenido, ServicioContenido>();
        servicios.AddScoped<ICondensadorMaterial, CondensadorMaterial>();
        servicios.AddSingleton<IColaCondensacion, ColaCondensacionEnMemoria>();
        servicios.Configure<OpcionesCarpetaContenido>(config.GetSection(OpcionesCarpetaContenido.Seccion));
        servicios.AddScoped<ISincronizadorCarpeta, SincronizadorCarpeta>();
        servicios.AddSingleton<IAvisosContenido, AvisosContenidoEnMemoria>();

        servicios.AddSingleton<IExtractorTexto, ExtractorPdf>();
        servicios.AddSingleton<IExtractorTexto, ExtractorPptx>();
        servicios.AddSingleton<IExtractorTexto, ExtractorDocx>();
        servicios.AddSingleton<IExtractorTexto, ExtractorXlsx>();
        servicios.AddSingleton<IExtractorTexto, ExtractorTextoPlano>();
        servicios.AddSingleton<IExtractorTextoFactory, ExtractorTextoFactory>();

        // Las claves cifran las credenciales de los alumnos: en un contenedor van a un
        // volumen, o cada despliegue las perdería y dejaría ilegibles las guardadas.
        // El nombre fijo solo va ahí: cambiarlo en local dejaría ilegibles las ya guardadas.
        var proteccion = servicios.AddDataProtection();
        if (config["ProteccionDatos:Carpeta"] is { Length: > 0 } carpetaClaves)
            proteccion.SetApplicationName("TutorPreClase")
                .PersistKeysToFileSystem(new DirectoryInfo(carpetaClaves));
        servicios.AddSingleton<IValidadorCredencial, ValidadorCredencial>();
        servicios.AddScoped<IBovedaCredenciales, BovedaCredenciales>();
        // Cada agente se conecta a su manera: OpenRouter (clave) o Google (Gemini, RF-37).
        servicios.AddSingleton<TokensGoogle>();
        servicios.AddHttpClient(TokensGoogle.Cliente, c => c.Timeout = TimeSpan.FromSeconds(20));
        servicios.AddScoped<ConexionOpenRouter>();
        servicios.AddScoped<ConexionGoogleGemini>();
        servicios.AddScoped<IConexionOAuth, ConexionesOAuth>();
        servicios.AddSingleton<IProveedorLlmFactory, ProveedorLlmFactory>();
        AgregarClientesLlm(servicios, config);

        return servicios;
    }

    private static void AgregarClientesLlm(IServiceCollection servicios, IConfiguration config)
    {
        var agentes = config.GetSection(OpcionesAgentes.Seccion).Get<OpcionesAgentes>() ?? [];

        foreach (var (id, opciones) in agentes)
        {
            servicios.AddHttpClient($"llm:{id}", cliente =>
            {
                if (!string.IsNullOrWhiteSpace(opciones.BaseUrl))
                    cliente.BaseAddress = new Uri(opciones.BaseUrl);

                cliente.Timeout = TimeSpan.FromMinutes(2);
            });
        }
    }
}

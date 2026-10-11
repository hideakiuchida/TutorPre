using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TutorPreClase.Application.Llm;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Infrastructure.Contenido;
using TutorPreClase.Infrastructure.Llm;
using TutorPreClase.Infrastructure.Persistencia;

namespace TutorPreClase.Tests.Infraestructura;

/// <summary>
/// Levanta la API real (rutas, autenticacion, SSE) sobre SQLite en memoria y con el
/// proveedor LLM guionado, sin tocar la red ni PostgreSQL.
/// </summary>
public sealed class ApiDePruebas : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _conexion = new("DataSource=:memory:");

    public ProveedorGuionado Proveedor { get; } = new();

    /// <summary>Ajustes de configuración extra para una prueba (clave de configuración → valor).</summary>
    private readonly IReadOnlyDictionary<string, string> _ajustes;

    /// <summary>Servicios que una prueba cambia además de los de siempre (p. ej. el generador del examen).</summary>
    public Action<IServiceCollection>? Servicios { get; init; }

    public ApiDePruebas(IReadOnlyDictionary<string, string>? ajustes = null)
    {
        _ajustes = ajustes ?? new Dictionary<string, string>();
        _conexion.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // La API se configura para Sqlite; la conexion en memoria la comparte el test.
        builder.UseSetting("Base:Proveedor", "Sqlite");
        builder.UseSetting("ConnectionStrings:Sqlite", _conexion.ConnectionString);
        foreach (var (clave, valor) in _ajustes) builder.UseSetting(clave, valor);

        builder.ConfigureServices(servicios =>
        {
            servicios.RemoveAll<DbContextOptions<AppDbContext>>();
            servicios.AddDbContext<AppDbContext>(o => o.UseSqlite(_conexion));

            servicios.RemoveAll<IProveedorLlmFactory>();
            servicios.AddSingleton<IProveedorLlmFactory>(Proveedor);

            // Validar una credencial no debe salir a la red en pruebas.
            servicios.RemoveAll<IValidadorCredencial>();
            servicios.AddSingleton<IValidadorCredencial, ValidadorSiempreOk>();

            // La condensación en segundo plano compartiría la única conexión SQLite en memoria
            // con la petición en curso; aquí solo se prueba que se encola (CondensacionTests
            // prueba el condensador).
            var condensacion = servicios.Where(d => d.ImplementationType == typeof(ServicioCondensacionEnSegundoPlano)).ToList();
            foreach (var d in condensacion) servicios.Remove(d);

            // Igual con la pregunta preparada en segundo plano: se genera cuando se pide.
            servicios.RemoveAll<TutorPreClase.Application.Evaluacion.IPreparadorPreguntas>();
            servicios.AddSingleton<TutorPreClase.Application.Evaluacion.IPreparadorPreguntas, PreparadorAnotado>();

            Servicios?.Invoke(servicios);
        });
    }

    public AppDbContext NuevoContexto()
    {
        var opciones = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conexion).Options;
        return new AppDbContext(opciones);
    }

    /// <summary>Crea el esquema y deja un docente, un alumno matriculado y una clase.</summary>
    public (Guid DocenteId, Guid AlumnoId, Guid CursoId, Guid ClaseId) Sembrar()
    {
        using var db = NuevoContexto();
        db.Database.EnsureCreated();

        var docente = new Usuario { Email = "docente@uni.edu", Nombre = "Docente", Rol = RolUsuario.Docente };
        var alumno = new Usuario { Email = "alumna@uni.edu", Nombre = "Alumna", Rol = RolUsuario.Alumno, ProfesorId = docente.Id };
        var curso = new Curso { Codigo = "IA101", Nombre = "Redes Neuronales", Periodo = "2026-2", DocenteId = docente.Id };

        var clase = new Clase
        {
            CursoId = curso.Id,
            Titulo = "Clase 03 - Redes profundas",
            Inicio = DateTimeOffset.UtcNow.AddDays(-1),
            Orden = 3
        };

        db.Usuarios.AddRange(docente, alumno);
        db.Cursos.Add(curso);
        db.Clases.Add(clase);
        db.Matriculas.Add(new Matricula { UsuarioId = alumno.Id, CursoId = curso.Id, RolEnCurso = RolUsuario.Alumno });
        db.Matriculas.Add(new Matricula { UsuarioId = docente.Id, CursoId = curso.Id, RolEnCurso = RolUsuario.Docente });
        db.Agentes.Add(new AgenteIA
        {
            Id = "prueba",
            NombreVisible = "IA de prueba",
            Proveedor = "Prueba",
            Modelo = "modelo-de-prueba",
            BaseUrl = "https://ia.prueba",
            Habilitado = true
        });

        db.SaveChanges();

        // El alumno conecta su credencial BYOK, como en el flujo real.
        ConectarCredencial(alumno.Id, "prueba");

        return (docente.Id, alumno.Id, curso.Id, clase.Id);
    }

    /// <summary>Conecta una credencial usando la boveda real de la aplicacion.</summary>
    public void ConectarCredencial(Guid usuarioId, string agenteId, string clave = "sk-prueba-de-prueba-0000-1234")
    {
        using var ambito = Services.CreateScope();
        var boveda = ambito.ServiceProvider.GetRequiredService<IBovedaCredenciales>();
        boveda.ConectarAsync(usuarioId, agenteId, clave).GetAwaiter().GetResult();
    }

    public HttpClient Como(Guid usuarioId, string rol)
    {
        var cliente = CreateClient();
        cliente.DefaultRequestHeaders.Add("X-Usuario-Id", usuarioId.ToString());
        cliente.DefaultRequestHeaders.Add("X-Usuario-Rol", rol);
        return cliente;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _conexion.Dispose();
    }
}

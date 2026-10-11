using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Contenido;
using TutorPreClase.Application.Evaluacion;
using TutorPreClase.Application.Llm;
using TutorPreClase.Application.Nivel;
using TutorPreClase.Application.Tutor;
using TutorPreClase.Domain.Entidades;
using Microsoft.AspNetCore.DataProtection;
using TutorPreClase.Infrastructure.Llm;
using TutorPreClase.Infrastructure.Persistencia;

namespace TutorPreClase.Tests.Infraestructura;

public sealed class RelojFijo(DateTimeOffset ahora) : IRelojSistema
{
    public DateTimeOffset Ahora { get; set; } = ahora;
}

/// <summary>Valida cualquier credencial: las pruebas no salen a la red.</summary>
public sealed class ValidadorSiempreOk : IValidadorCredencial
{
    public Task<bool> EsUsableAsync(AgenteIA agente, string clave, CancellationToken ct = default) =>
        Task.FromResult(true);
}

/// <summary>Proveedor de prueba: devuelve un guion de eventos, sin salir a la red.</summary>
public sealed class ProveedorGuionado(string id = "prueba") : ILlmProvider, IProveedorLlmFactory
{
    private readonly Queue<LlmEvento[]> _guion = new();

    public List<LlmSolicitud> Solicitudes { get; } = [];

    public string Id { get; } = id;

    public ProveedorGuionado Responde(params LlmEvento[] eventos)
    {
        _guion.Enqueue(eventos);
        return this;
    }

    public ProveedorGuionado RespondeTexto(string texto) =>
        Responde(new TextoParcial(texto), new Fin(10, 5));

    public ProveedorGuionado LlamaHerramienta(string nombre, object argumentos) =>
        Responde(
            new LlamadaHerramienta(
                $"tool_{Guid.NewGuid():N}",
                nombre,
                JsonDocument.Parse(JsonSerializer.Serialize(argumentos)).RootElement.Clone()),
            new Fin(10, 5));

    public async IAsyncEnumerable<LlmEvento> StreamAsync(
        LlmSolicitud solicitud, [EnumeratorCancellation] CancellationToken ct)
    {
        Solicitudes.Add(solicitud);

        var eventos = _guion.Count > 0 ? _guion.Dequeue() : [new Fin(0, 0)];

        foreach (var evento in eventos)
        {
            await Task.Yield();
            yield return evento;
        }
    }

    public ILlmProvider Obtener(string agenteId, string apiKey) => this;
    public bool Existe(string agenteId) => true;
}

/// <summary>
/// Monta el sistema completo sobre SQLite en memoria: mismo DbContext y mismos
/// servicios que en produccion, con el proveedor LLM guionado.
/// </summary>
public sealed class BancoDePruebas : IDisposable
{
    private readonly SqliteConnection _conexion;

    public AppDbContext Db { get; }
    public RelojFijo Reloj { get; }
    public ProveedorGuionado Proveedor { get; } = new();
    public IAgenteTutorService Tutor { get; private set; }
    public IServicioExamen Examen { get; }
    public INivelService Nivel { get; }
    public IContextoClaseService Contexto { get; }
    public IBovedaCredenciales Boveda { get; }
    public IPreguntasDelIntento Preguntas { get; }

    /// <summary>En pruebas no hay segundo plano: la pregunta se genera cuando se pide.</summary>
    public PreparadorAnotado Preparador { get; } = new();

    public Guid AlumnoId { get; private set; }
    public Guid ClaseId { get; private set; }
    public Guid ExamenId { get; private set; }

    public BancoDePruebas(DateTimeOffset? ahora = null)
    {
        _conexion = new SqliteConnection("DataSource=:memory:");
        _conexion.Open();

        var opciones = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conexion)
            .Options;

        Db = new AppDbContext(opciones);
        Db.Database.EnsureCreated();

        Reloj = new RelojFijo(ahora ?? new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero));

        var cache = new MemoryCache(new MemoryCacheOptions());
        Contexto = new ContextoClaseService(Db, cache, Options.Create(new OpcionesContextoClase()));
        Nivel = new NivelService(Db, Reloj);
        Examen = new ServicioExamen(Db, Reloj, Nivel);

        Boveda = new BovedaCredenciales(
            Db, DataProtectionProvider.Create("TutorPreClase.Tests"), new ValidadorSiempreOk(), Reloj,
            NullLogger<BovedaCredenciales>.Instance);

        Preguntas = new PreguntasDelIntento(
            Db, Boveda, Proveedor, Contexto, Generador, new CandadosIntento(), NullLogger<PreguntasDelIntento>.Instance);

        var ejecutor = new EjecutorHerramientas(Db, Examen, Nivel, Reloj, Preguntas, Preparador);

        Tutor = new AgenteTutorService(
            Db, Contexto, ejecutor, Examen, Proveedor, Boveda, Generador, Preparador, Reloj,
            NullLogger<AgenteTutorService>.Instance);
    }

    /// <summary>Sustituye a la IA que arma el examen: siempre las mismas 2 preguntas.</summary>
    public GeneradorFijo Generador { get; } = new();

    /// <summary>
    /// Curso, clase con contenido, examen publicado y un alumno. Las 2 preguntas de cada
    /// intento las "genera" <see cref="Generador"/> al empezarlo, como haria la IA (RF-04).
    /// </summary>
    public BancoDePruebas Sembrar(bool ampliacionPermitida = true, ModoFeedback feedback = ModoFeedback.AlFinal)
    {
        var alumno = new Usuario { Email = "alumna@uni.edu", Nombre = "Alumna", Rol = RolUsuario.Alumno };
        var curso = new Curso { Codigo = "IA101", Nombre = "Redes Neuronales", Periodo = "2026-2" };

        var clase = new Clase
        {
            CursoId = curso.Id,
            Titulo = "Clase 03 - Redes profundas",
            Inicio = Reloj.Ahora.AddDays(-1),
            Orden = 3,
            AmpliacionPermitida = ampliacionPermitida
        };

        var archivo = new ArchivoContenido
        {
            ClaseId = clase.Id,
            Nombre = "Clase03.pdf",
            Tipo = "pdf",
            BlobUrl = "cursos/x/clases/y/Clase03.pdf",
            HashSha256 = "abc123",
            Estado = EstadoArchivo.Listo
        };

        archivo.Paginas.Add(new PaginaContenido
        {
            ArchivoId = archivo.Id,
            ClaseId = clase.Id,
            Pagina = 11,
            Texto = "La sigmoide satura en los extremos y reduce el gradiente.",
            Tokens = 14
        });

        archivo.Paginas.Add(new PaginaContenido
        {
            ArchivoId = archivo.Id,
            ClaseId = clase.Id,
            Pagina = 12,
            Texto = "ReLU mantiene gradiente 1 para entradas positivas.",
            Tokens = 12
        });

        var examen = new Examen
        {
            ClaseId = clase.Id,
            MaxIntentos = 3,
            ModoFeedback = feedback,
            Publicado = true,
            PreguntasPorIntento = 2
        };

        Db.Usuarios.Add(alumno);
        Db.Cursos.Add(curso);
        Db.Clases.Add(clase);
        Db.Archivos.Add(archivo);
        Db.Examenes.Add(examen);
        Db.Matriculas.Add(new Matricula { UsuarioId = alumno.Id, CursoId = curso.Id, RolEnCurso = RolUsuario.Alumno });
        Db.Agentes.Add(new AgenteIA
        {
            Id = "prueba",
            NombreVisible = "IA de prueba",
            Proveedor = "Prueba",
            Modelo = "modelo-de-prueba",
            BaseUrl = "https://ia.prueba",
            Habilitado = true
        });

        Db.SaveChanges();

        // El alumno llega con su credencial BYOK ya conectada.
        Boveda.ConectarAsync(alumno.Id, "prueba", "sk-prueba-de-prueba-0000-1234").GetAwaiter().GetResult();

        AlumnoId = alumno.Id;
        ClaseId = clase.Id;
        ExamenId = examen.Id;

        return this;
    }

    public static Pregunta Pregunta(string enunciado, int orden, string correcta) => new()
    {
        Enunciado = enunciado,
        Justificacion = "Esta en el material de la clase.",
        Tema = "Funciones de activacion",
        Orden = orden,
        Origen = OrigenPregunta.IA,
        Aprobada = true,
        Alternativas =
        [
            new Alternativa { Letra = "A", Texto = correcta, EsCorrecta = true },
            new Alternativa { Letra = "B", Texto = "Otra", EsCorrecta = false },
            new Alternativa { Letra = "C", Texto = "Otra mas", EsCorrecta = false },
            new Alternativa { Letra = "D", Texto = "Ninguna", EsCorrecta = false }
        ]
    };

    /// <summary>Sustituye el tutor por uno con otra fabrica de proveedor.</summary>
    public BancoDePruebas ConTutor(IAgenteTutorService tutor)
    {
        Tutor = tutor;
        return this;
    }

    public async Task<List<EventoTutor>> EnviarAsync(Guid conversacionId, string texto)
    {
        var eventos = new List<EventoTutor>();
        await foreach (var evento in Tutor.ProcesarMensajeAsync(conversacionId, texto))
            eventos.Add(evento);

        return eventos;
    }

    public void Dispose()
    {
        Db.Dispose();
        _conexion.Dispose();
    }
}

/// <summary>Generador de examen de prueba: dos preguntas conocidas, la correcta en la A.</summary>
public sealed class GeneradorFijo : IGeneradorExamen
{
    public int Llamadas { get; private set; }

    /// <summary>El material con el que se pidió el último examen.</summary>
    public ContextoClase? UltimoMaterial { get; private set; }

    /// <summary>Si se fija, la generacion falla con este error del proveedor.</summary>
    public ErrorProveedor? Falla { get; set; }

    /// <summary>Falla solo con algunos modelos: para probar el respaldo con otra IA.</summary>
    public Func<string, ErrorProveedor?>? FallaCon { get; set; }

    public string? UltimoModelo { get; private set; }

    /// <summary>Lo que ya tenía el intento en la última llamada (generación pregunta a pregunta).</summary>
    public IReadOnlyCollection<string>? UltimasYaPreguntadas { get; private set; }

    public Task<IReadOnlyList<Pregunta>> GenerarAsync(
        ILlmProvider proveedor, string modelo, ContextoClase contexto, int cantidad,
        IReadOnlyCollection<string>? yaPreguntadas = null, CancellationToken ct = default)
    {
        Llamadas++;
        UltimoMaterial = contexto;
        UltimasYaPreguntadas = yaPreguntadas;
        UltimoModelo = modelo;
        if ((Falla ?? FallaCon?.Invoke(modelo)) is ErrorProveedor error)
            throw new GeneracionExamenException("Fallo el proveedor.", error);

        // Pregunta a pregunta: devuelve las siguientes de la lista, según lo ya preguntado.
        var desde = yaPreguntadas?.Count ?? 0;
        IReadOnlyList<Pregunta> preguntas = Lista
            .Skip(desde)
            .Take(cantidad)
            .Select((p, i) => BancoDePruebas.Pregunta(p.Enunciado, desde + i + 1, p.Correcta))
            .ToList();

        return Task.FromResult(preguntas);
    }

    private static readonly (string Enunciado, string Correcta)[] Lista =
    [
        ("Que funcion evita el desvanecimiento del gradiente?", "ReLU"),
        ("Que problema tiene la sigmoide en capas profundas?", "Satura"),
        ("Que capa reduce la dimension espacial?", "Pooling")
    ];
}

/// <summary>Anota los intentos cuya siguiente pregunta se pidió preparar en segundo plano.</summary>
public sealed class PreparadorAnotado : IPreparadorPreguntas
{
    public List<Guid> Pedidos { get; } = [];

    public void Preparar(Guid intentoId) => Pedidos.Add(intentoId);
}

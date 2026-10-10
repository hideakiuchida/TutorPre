using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Academico;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Infrastructure.Contenido;

/// <summary>Seccion "CarpetaContenido" de configuracion (SDD §6.1).</summary>
public sealed class OpcionesCarpetaContenido
{
    public const string Seccion = "CarpetaContenido";

    /// <summary>Carpeta raiz; relativa al directorio de trabajo de la API. Vacia = desactivada.</summary>
    public string Ruta { get; set; } = "";

    /// <summary>Periodo con el que se crea un curso nuevo.</summary>
    public string Periodo { get; set; } = "";

    public int IntervaloSegundos { get; set; } = 30;

    /// <summary>Zona horaria de la universidad: la hora de una clase nueva se fija en ella.</summary>
    public string ZonaHoraria { get; set; } = "America/Lima";
}

public sealed record ResumenSincronizacion(int Cursos, int Clases, int Nuevos, int Eliminados)
{
    public bool HuboCambios => Nuevos > 0 || Eliminados > 0;
}

public interface ISincronizadorCarpeta
{
    Task<ResumenSincronizacion> SincronizarAsync(CancellationToken ct = default);
}

/// <summary>
/// Convencion de nombres de la carpeta de contenido:
/// <c>{raiz}/{correo del profesor}/{CODIGO - Nombre del curso}/{N - Titulo de la clase}/archivos</c>.
/// </summary>
public static partial class NombresCarpeta
{
    /// <summary>La carpeta de primer nivel es el correo del profesor dueño de sus cursos.</summary>
    public static string? Profesor(string carpeta)
    {
        var correo = carpeta.Trim().ToLowerInvariant();
        var arroba = correo.IndexOf('@');
        return arroba > 0 && arroba < correo.Length - 1 && !correo.Contains(' ') ? correo : null;
    }

    public static (string Codigo, string Nombre) Curso(string carpeta)
    {
        var partes = carpeta.Split(" - ", 2, StringSplitOptions.TrimEntries);
        return partes.Length == 2 && partes[0].Length > 0 && partes[1].Length > 0
            ? (partes[0], partes[1])
            : (carpeta.Trim(), carpeta.Trim());
    }

    /// <summary>El titulo es el nombre completo; el orden, el primer numero que aparezca.</summary>
    public static (int Orden, string Titulo) Clase(string carpeta, int posicion)
    {
        var numero = PrimerNumero().Match(carpeta);
        return (numero.Success ? int.Parse(numero.Value) : posicion, carpeta.Trim());
    }

    /// <summary>Temporales de Office ("~$…") y ocultos no son contenido.</summary>
    public static bool EsIgnorable(string archivo) =>
        archivo.StartsWith('.') || archivo.StartsWith("~$", StringComparison.Ordinal);

    [GeneratedRegex(@"\d+")]
    private static partial Regex PrimerNumero();
}

/// <summary>
/// La carpeta de contenido es el canal del docente (SDD §6.1): cada profesor tiene la suya
/// (su correo) y administra ahi sus cursos. Lo que pone llega a la clase y lo que quita
/// desaparece. Un archivo sin cambios no se vuelve a extraer (mismo SHA-256); uno
/// modificado se reemplaza. Las clases no se borran nunca desde aqui, porque cuelgan de
/// ellas examenes e intentos.
/// </summary>
public sealed class SincronizadorCarpeta(
    IAppDbContext db,
    IServicioContenido contenido,
    IColaExtraccion cola,
    IExtractorTextoFactory extractores,
    IRelojSistema reloj,
    IAvisosContenido avisos,
    IOptions<OpcionesCarpetaContenido> opciones,
    ILogger<SincronizadorCarpeta> log) : ISincronizadorCarpeta
{
    public async Task<ResumenSincronizacion> SincronizarAsync(CancellationToken ct = default)
    {
        var raiz = opciones.Value.Ruta;
        if (string.IsNullOrWhiteSpace(raiz)) return new(0, 0, 0, 0);

        raiz = Path.GetFullPath(raiz);
        if (!Directory.Exists(raiz))
        {
            log.LogWarning("La carpeta de contenido {Carpeta} no existe", raiz);
            return new(0, 0, 0, 0);
        }

        int cursos = 0, clases = 0, nuevos = 0, eliminados = 0;

        foreach (var carpetaProfesor in Subcarpetas(raiz))
        {
            var correo = NombresCarpeta.Profesor(Path.GetFileName(carpetaProfesor));
            if (correo is null)
            {
                log.LogWarning("Se omite {Carpeta}: el primer nivel debe ser el correo del profesor " +
                               "(course-content/<correo>/<CÓDIGO - Curso>/<Clase>)", Path.GetFileName(carpetaProfesor));
                continue;
            }

            var profesor = await ProfesorAsync(correo, ct);

            foreach (var carpetaCurso in Subcarpetas(carpetaProfesor))
            {
                cursos++;
                var curso = await CursoAsync(Path.GetFileName(carpetaCurso), profesor, ct);

                // El profesor y sus alumnos, tambien en los cursos que aparecen despues.
                await MatriculasPorProfesor.AlinearCursoAsync(db, curso, ct);
                await db.SaveChangesAsync(ct);

                var cambiadas = new List<Guid>();
                var carpetasClase = Subcarpetas(carpetaCurso);
                for (var i = 0; i < carpetasClase.Count; i++)
                {
                    clases++;
                    var (clase, claseNueva) = await ClaseAsync(curso, Path.GetFileName(carpetasClase[i]), i + 1, ct);
                    var examenNuevo = await AsegurarExamenAsync(clase, ct);
                    var (n, e) = await ArchivosAsync(curso, clase, carpetasClase[i], ct);
                    nuevos += n;
                    eliminados += e;

                    if (claseNueva || examenNuevo || n > 0 || e > 0) cambiadas.Add(clase.Id);
                }

                // Solo si el docente cambio algo: una pasada sin cambios no molesta a nadie.
                if (cambiadas.Count > 0) avisos.Publicar(new CambioContenido(curso.Id, cambiadas));
            }
        }

        var resumen = new ResumenSincronizacion(cursos, clases, nuevos, eliminados);
        if (resumen.HuboCambios)
            log.LogInformation("Carpeta de contenido sincronizada: {Nuevos} archivos nuevos, {Eliminados} retirados",
                nuevos, eliminados);

        return resumen;
    }

    private static List<string> Subcarpetas(string carpeta) =>
        Directory.GetDirectories(carpeta)
            .Where(d => !NombresCarpeta.EsIgnorable(Path.GetFileName(d)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// El profesor puede tener su carpeta antes de haber entrado nunca: se le registra como
    /// docente con su correo y, cuando entre con Google o Microsoft, ya encuentra sus cursos.
    /// </summary>
    private async Task<Usuario> ProfesorAsync(string correo, CancellationToken ct)
    {
        var profesor = await db.Usuarios.FirstOrDefaultAsync(u => u.Email == correo, ct);
        if (profesor is not null) return profesor;

        profesor = new Usuario { Email = correo, Nombre = correo, Rol = RolUsuario.Docente };
        db.Usuarios.Add(profesor);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Profesor {Correo} registrado desde su carpeta de contenido", correo);
        return profesor;
    }

    private async Task<Curso> CursoAsync(string carpeta, Usuario profesor, CancellationToken ct)
    {
        var (codigo, nombre) = NombresCarpeta.Curso(carpeta);

        // Dos profesores pueden usar el mismo codigo. Un curso sin dueño con ese codigo
        // (creado antes de que hubiera carpetas por profesor) se asigna a quien lo tiene ahora.
        var curso = await db.Cursos.FirstOrDefaultAsync(c => c.Codigo == codigo && c.DocenteId == profesor.Id, ct)
                    ?? await db.Cursos.FirstOrDefaultAsync(c => c.Codigo == codigo && c.DocenteId == null, ct);

        if (curso is not null)
        {
            curso.DocenteId ??= profesor.Id;
            return curso;
        }

        curso = new Curso { Codigo = codigo, Nombre = nombre, Periodo = opciones.Value.Periodo, DocenteId = profesor.Id };
        db.Cursos.Add(curso);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Curso {Codigo} de {Profesor} creado desde la carpeta de contenido", codigo, profesor.Email);
        return curso;
    }

    private async Task<(Clase Clase, bool Nueva)> ClaseAsync(Curso curso, string carpeta, int posicion, CancellationToken ct)
    {
        var (orden, titulo) = NombresCarpeta.Clase(carpeta, posicion);

        var clase = await db.Clases.FirstOrDefaultAsync(c => c.CursoId == curso.Id && c.Titulo == titulo, ct);
        if (clase is not null) return (clase, false);

        // Una clase por semana a partir de mañana, a las 19:00 de la universidad (no del
        // servidor, que suele estar en UTC). El docente la reprograma despues (RF-01).
        var zona = Zona(opciones.Value.ZonaHoraria);
        var hoy = TimeZoneInfo.ConvertTime(reloj.Ahora, zona);
        var dia = hoy.Date.AddDays(1 + 7 * Math.Max(0, orden - 1)).AddHours(19);

        clase = new Clase
        {
            CursoId = curso.Id,
            Titulo = titulo,
            Orden = orden,
            Inicio = new DateTimeOffset(dia, zona.GetUtcOffset(dia))
        };

        db.Clases.Add(clase);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Clase {Titulo} creada desde la carpeta de contenido", titulo);
        return (clase, true);
    }

    /// <summary>
    /// Toda clase de la carpeta tiene su examen publicado y sin fechas propias: se puede
    /// rendir desde que la clase se habilita (al llegar su fecha). Las preguntas no se guardan aqui; la IA genera las de cada
    /// alumno al empezar cada intento (RF-04). Un examen existente no se toca: el docente
    /// puede haberlo ajustado.
    /// </summary>
    private async Task<bool> AsegurarExamenAsync(Clase clase, CancellationToken ct)
    {
        if (await db.Examenes.AnyAsync(e => e.ClaseId == clase.Id, ct)) return false;

        db.Examenes.Add(new Examen
        {
            ClaseId = clase.Id,
            MaxIntentos = 3,
            MinutosLimite = 20,
            ModoFeedback = ModoFeedback.AlFinal,
            PreguntasPorIntento = 6,
            Publicado = true
        });

        await db.SaveChangesAsync(ct);
        return true;
    }

    private TimeZoneInfo Zona(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            log.LogWarning("Zona horaria {Zona} desconocida; se usa UTC", id);
            return TimeZoneInfo.Utc;
        }
    }

    private async Task<(int Nuevos, int Eliminados)> ArchivosAsync(
        Curso curso, Clase clase, string carpeta, CancellationToken ct)
    {
        var existentes = await db.Archivos
            .Where(a => a.ClaseId == clase.Id)
            .Select(a => new { a.Id, a.Nombre })
            .ToListAsync(ct);

        var vigentes = new HashSet<Guid>();
        var nuevos = 0;

        foreach (var ruta in Directory.GetFiles(carpeta).Order(StringComparer.OrdinalIgnoreCase))
        {
            var nombre = Path.GetFileName(ruta);
            if (NombresCarpeta.EsIgnorable(nombre)) continue;

            if (extractores.Para(nombre) is null)
            {
                log.LogDebug("Se omite {Archivo}: formato no admitido", nombre);
                continue;
            }

            try
            {
                await using var flujo = File.OpenRead(ruta);
                var subida = await contenido.SubirAsync(curso.Id, clase.Id, nombre, flujo, ct);
                vigentes.Add(subida.ArchivoId);

                if (subida.Duplicado) continue;

                nuevos++;
                await cola.EncolarAsync(subida.ArchivoId, ct);
            }
            catch (IOException ex)
            {
                // Se esta copiando o esta abierto en otro programa: se deja lo que habia
                // y se reintenta en la siguiente pasada.
                log.LogInformation(ex, "No se pudo leer {Archivo}; se reintentara", nombre);
                vigentes.UnionWith(existentes.Where(a => a.Nombre == nombre).Select(a => a.Id));
            }
        }

        var retirados = existentes.Where(a => !vigentes.Contains(a.Id)).ToList();
        foreach (var archivo in retirados)
            await contenido.EliminarAsync(archivo.Id, ct);

        return (nuevos, retirados.Count);
    }
}

/// <summary>Revisa la carpeta cada cierto tiempo: el docente solo copia archivos.</summary>
public sealed class ServicioSincronizacionCarpeta(
    IServiceScopeFactory ambitos,
    IOptions<OpcionesCarpetaContenido> opciones,
    ILogger<ServicioSincronizacionCarpeta> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opciones.Value.Ruta)) return;

        using var temporizador = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, opciones.Value.IntervaloSegundos)));

        do
        {
            try
            {
                using var ambito = ambitos.CreateScope();
                await ambito.ServiceProvider.GetRequiredService<ISincronizadorCarpeta>().SincronizarAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Un fallo de una pasada no debe detener las siguientes.
                log.LogError(ex, "Fallo la sincronizacion de la carpeta de contenido");
            }
        }
        while (await temporizador.WaitForNextTickAsync(ct));
    }
}

using Microsoft.EntityFrameworkCore;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Nivel;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Domain.Reglas;

namespace TutorPreClase.Application.Reportes;

/// <summary>
/// La ficha de cada alumno en la clase (RF-22): la nota con la que se fijó su nivel (su mejor
/// intento) con sus aciertos, cuántas veces rindió, cuándo fue la última y los temas que el
/// tutor le marcó para reforzar.
/// </summary>
public sealed record NivelDeAlumno(
    Guid AlumnoId,
    string Nombre,
    string Nivel,
    IReadOnlyList<string> TemasDebiles,
    string Origen,
    decimal? Nota = null,
    int Intentos = 0,
    int Correctas = 0,
    int Preguntas = 0,
    DateTimeOffset? RendidoEn = null);

/// <summary>
/// Cada alumno rinde preguntas propias generadas por la IA (RF-04), asi que lo comparable
/// entre alumnos es el tema, no la pregunta.
/// </summary>
public sealed record TemaFallado(string Tema, int Fallos, int Respondidas, int PorcentajeFallo);

public sealed record DudaReportada(string Texto, string? Tema, bool ConAmpliacion, DateTimeOffset CreadoEn);

public sealed record UsoAgente(string AgenteId, int Intentos);

public sealed record ReporteClase(
    Guid ClaseId,
    string Clase,
    int Alumnos,
    decimal PromedioNota,
    IReadOnlyDictionary<string, int> DistribucionNotas,
    IReadOnlyDictionary<string, int> DistribucionNiveles,
    IReadOnlyList<NivelDeAlumno> Niveles,
    IReadOnlyList<TemaFallado> TemasMasFallados,
    IReadOnlyList<string> TemasDebilesDelGrupo,
    IReadOnlyList<DudaReportada> DudasFueraDelMaterial,
    IReadOnlyList<UsoAgente> UsoPorAgente);

public interface IServicioReporte
{
    Task<ReporteClase?> DeClaseAsync(Guid claseId, CancellationToken ct = default);
}

/// <summary>Reporte del docente (RF-16, RF-22): notas, niveles, temas debiles y dudas.</summary>
public sealed class ServicioReporte(IAppDbContext db) : IServicioReporte
{
    public async Task<ReporteClase?> DeClaseAsync(Guid claseId, CancellationToken ct = default)
    {
        var clase = await db.Clases.AsNoTracking().FirstOrDefaultAsync(c => c.Id == claseId, ct);
        if (clase is null) return null;

        var examen = await db.Examenes
            .AsNoTracking()
            .Include(e => e.Preguntas).ThenInclude(p => p.Alternativas)
            .FirstOrDefaultAsync(e => e.ClaseId == claseId, ct);

        var intentos = examen is null
            ? []
            : await db.Intentos
                .AsNoTracking()
                .Where(i => i.ExamenId == examen.Id && i.Estado != EstadoIntento.EnCurso)
                .Include(i => i.Respuestas)
                .ToListAsync(ct);

        // Una nota por alumno, la misma con la que se fija su nivel (su mejor intento): si
        // se promediaran los intentos, quien repite el examen contaría como varios alumnos.
        var notas = intentos
            .Where(i => i.Puntaje.HasValue)
            .GroupBy(i => i.AlumnoId)
            .Select(g => ClasificacionNivel.NotaVigente(g))
            .ToList();

        var niveles = await db.Niveles.AsNoTracking().Where(n => n.ClaseId == claseId).ToListAsync(ct);
        var alumnosIds = niveles.Select(n => n.AlumnoId).ToList();

        var nombres = await db.Usuarios
            .AsNoTracking()
            .Where(u => alumnosIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);

        var nivelesDto = niveles
            .Select(n =>
            {
                var suyos = intentos.Where(i => i.AlumnoId == n.AlumnoId).ToList();
                var ultimo = suyos.OrderByDescending(i => i.Envio ?? i.Inicio).FirstOrDefault();

                // Los aciertos acompañan a la nota: son los del intento que la dio.
                var mejor = suyos.OrderByDescending(i => i.Puntaje ?? 0m).ThenByDescending(i => i.Envio ?? i.Inicio).FirstOrDefault();

                return new NivelDeAlumno(
                    n.AlumnoId,
                    nombres.GetValueOrDefault(n.AlumnoId, "(sin nombre)"),
                    n.Nivel.ToString(),
                    NivelService.Deserializar(n.TemasDebiles),
                    n.Origen.ToString(),
                    // La misma nota con la que el servidor fijó el nivel.
                    Nota: suyos.Count == 0 ? null : ClasificacionNivel.NotaVigente(suyos),
                    Intentos: suyos.Count,
                    Correctas: mejor?.Respuestas.Count(r => r.EsCorrecta) ?? 0,
                    Preguntas: mejor?.PreguntasPlaneadas ?? 0,
                    RendidoEn: ultimo?.Envio);
            })
            .OrderBy(n => n.Nombre)
            .ToList();

        var dudas = await db.DudasSinCobertura
            .AsNoTracking()
            .Where(d => d.ClaseId == claseId)
            .OrderByDescending(d => d.CreadoEn)
            .Take(100)
            .ToListAsync(ct);

        return new ReporteClase(
            claseId,
            clase.Titulo,
            Alumnos: intentos.Select(i => i.AlumnoId).Distinct().Count(),
            PromedioNota: notas.Count == 0 ? 0m : Math.Round(notas.Average(), 1),
            DistribucionNotas: Histograma(notas),
            DistribucionNiveles: niveles
                .GroupBy(n => n.Nivel.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            Niveles: nivelesDto,
            TemasMasFallados: TemasFallados(examen, intentos),
            TemasDebilesDelGrupo: nivelesDto
                .SelectMany(n => n.TemasDebiles)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .Take(10)
                .ToList(),
            DudasFueraDelMaterial: dudas
                .Select(d => new DudaReportada(d.Texto, d.Tema, d.RespondidaConAmpliacion, d.CreadoEn))
                .ToList(),
            UsoPorAgente: intentos
                .GroupBy(i => i.AgenteId)
                .Select(g => new UsoAgente(g.Key, g.Count()))
                .OrderByDescending(u => u.Intentos)
                .ToList());
    }

    private static Dictionary<string, int> Histograma(List<decimal> notas)
    {
        var tramos = new Dictionary<string, int>
        {
            ["0-10"] = 0, ["11-14"] = 0, ["15-17"] = 0, ["18-20"] = 0
        };

        foreach (var nota in notas)
        {
            var tramo = nota switch
            {
                < 11m => "0-10",
                < 15m => "11-14",
                < 18m => "15-17",
                _ => "18-20"
            };
            tramos[tramo]++;
        }

        return tramos;
    }

    private static List<TemaFallado> TemasFallados(Examen? examen, List<Intento> intentos)
    {
        if (examen is null) return [];

        var temaDe = examen.Preguntas.ToDictionary(p => p.Id, p => p.Tema);

        return intentos
            .SelectMany(i => i.Respuestas)
            .GroupBy(r => temaDe.GetValueOrDefault(r.PreguntaId, "(sin tema)"), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var fallos = g.Count(r => !r.EsCorrecta);
                return new TemaFallado(g.Key, fallos, g.Count(), (int)Math.Round(100m * fallos / g.Count()));
            })
            .Where(t => t.Fallos > 0)
            .OrderByDescending(t => t.PorcentajeFallo)
            .ThenByDescending(t => t.Fallos)
            .ToList();
    }
}

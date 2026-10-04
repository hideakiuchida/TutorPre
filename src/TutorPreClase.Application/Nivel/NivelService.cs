using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Domain.Reglas;

namespace TutorPreClase.Application.Nivel;

public interface INivelService
{
    /// <summary>
    /// Recalcula el nivel a partir del mejor intento enviado (SDD §5.6). Con
    /// <paramref name="nuevoIntento"/>, además vacía los temas a reforzar: el diagnóstico es el
    /// del examen que acaba de terminar, no la suma de todos los anteriores.
    /// </summary>
    Task<NivelAlumno> RecalcularAsync(Guid alumnoId, Guid claseId, bool nuevoIntento = false, CancellationToken ct = default);

    /// <summary>Guarda los temas debiles que aporta el tutor; no toca el nivel.</summary>
    Task RegistrarTemasDebilesAsync(Guid alumnoId, Guid claseId, IReadOnlyList<string> temas, CancellationToken ct = default);

    /// <summary>El docente deja los temas a reforzar como él decida; no toca el nivel.</summary>
    Task<IReadOnlyList<string>> FijarTemasDebilesAsync(Guid alumnoId, Guid claseId, IReadOnlyList<string> temas, CancellationToken ct = default);

    /// <summary>Correccion manual del docente; queda con origen Docente y no se pisa.</summary>
    Task<NivelAlumno> CorregirAsync(Guid alumnoId, Guid claseId, NivelAlumnoValor nivel, CancellationToken ct = default);
}

public sealed class NivelService(IAppDbContext db, IRelojSistema reloj) : INivelService
{
    public async Task<NivelAlumno> RecalcularAsync(
        Guid alumnoId, Guid claseId, bool nuevoIntento = false, CancellationToken ct = default)
    {
        var registro = await ObtenerOCrearAsync(alumnoId, claseId, ct);

        // Un examen nuevo trae su propio diagnóstico: los temas del anterior ya no cuentan.
        if (nuevoIntento) registro.TemasDebiles = JsonSerializer.Serialize(Array.Empty<string>());

        // El docente manda: una correccion manual no la pisa el calculo automatico.
        if (registro.Origen == OrigenNivel.Docente)
        {
            await db.SaveChangesAsync(ct);
            return registro;
        }

        var intentos = await db.Intentos
            .Where(i => i.AlumnoId == alumnoId && i.Examen!.ClaseId == claseId)
            .ToListAsync(ct);

        registro.Nivel = ClasificacionNivel.Desde(ClasificacionNivel.NotaVigente(intentos));
        registro.Origen = OrigenNivel.Automatico;
        registro.ActualizadoEn = reloj.Ahora;

        await db.SaveChangesAsync(ct);
        return registro;
    }

    public async Task RegistrarTemasDebilesAsync(
        Guid alumnoId, Guid claseId, IReadOnlyList<string> temas, CancellationToken ct = default)
    {
        var registro = await ObtenerOCrearAsync(alumnoId, claseId, ct);

        var previos = Deserializar(registro.TemasDebiles);
        var union = previos
            .Concat(temas.Select(t => t.Trim()).Where(t => t.Length > 0))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        registro.TemasDebiles = JsonSerializer.Serialize(union);
        registro.ActualizadoEn = reloj.Ahora;

        // El tutor solo aporta el diagnostico cualitativo: el nivel no cambia aqui.
        if (registro.Origen == OrigenNivel.Automatico) registro.Origen = OrigenNivel.Tutor;

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> FijarTemasDebilesAsync(
        Guid alumnoId, Guid claseId, IReadOnlyList<string> temas, CancellationToken ct = default)
    {
        var registro = await ObtenerOCrearAsync(alumnoId, claseId, ct);

        var limpios = temas
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        registro.TemasDebiles = JsonSerializer.Serialize(limpios);
        registro.ActualizadoEn = reloj.Ahora;

        await db.SaveChangesAsync(ct);
        return limpios;
    }

    public async Task<NivelAlumno> CorregirAsync(
        Guid alumnoId, Guid claseId, NivelAlumnoValor nivel, CancellationToken ct = default)
    {
        var registro = await ObtenerOCrearAsync(alumnoId, claseId, ct);

        registro.Nivel = nivel;
        registro.Origen = OrigenNivel.Docente;
        registro.ActualizadoEn = reloj.Ahora;

        await db.SaveChangesAsync(ct);
        return registro;
    }

    private async Task<NivelAlumno> ObtenerOCrearAsync(Guid alumnoId, Guid claseId, CancellationToken ct)
    {
        var registro = await db.Niveles
            .FirstOrDefaultAsync(n => n.AlumnoId == alumnoId && n.ClaseId == claseId, ct);

        if (registro is not null) return registro;

        registro = new NivelAlumno
        {
            AlumnoId = alumnoId,
            ClaseId = claseId,
            Nivel = NivelAlumnoValor.Inicial,
            Origen = OrigenNivel.Automatico,
            ActualizadoEn = reloj.Ahora
        };

        db.Niveles.Add(registro);
        return registro;
    }

    public static IReadOnlyList<string> Deserializar(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

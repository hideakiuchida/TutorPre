using Microsoft.EntityFrameworkCore;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Evaluacion;

namespace TutorPreClase.Application.Academico;

/// <summary>
/// Una clase se habilita para el alumno cuando llega su fecha: antes no puede abrir el
/// chat, leer el material ni rendir el examen. El examen no tiene fechas propias.
/// </summary>
public static class ClaseHabilitada
{
    public const string Codigo = "clase_no_habilitada";

    public static bool Es(DateTimeOffset inicio, DateTimeOffset ahora) => ahora >= inicio;

    public static async Task ExigirAsync(IAppDbContext db, Guid claseId, DateTimeOffset ahora, CancellationToken ct = default)
    {
        var inicio = await db.Clases
            .Where(c => c.Id == claseId)
            .Select(c => (DateTimeOffset?)c.Inicio)
            .FirstOrDefaultAsync(ct);

        if (inicio is DateTimeOffset fecha && !Es(fecha, ahora))
            throw new ExamenException(Codigo, "Esta clase todavía no está habilitada: se abre cuando llegue su fecha.");
    }
}

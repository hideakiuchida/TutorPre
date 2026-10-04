using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Contenido;
using TutorPreClase.Application.Llm;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Application.Evaluacion;

/// <summary>Resultado de pedir la siguiente pregunta: `Mensaje` explica por qué no se pudo.</summary>
public sealed record GeneracionPregunta(bool Ok, string? Mensaje = null)
{
    public static GeneracionPregunta Lista { get; } = new(true);
}

public interface IPreguntasDelIntento
{
    /// <summary>
    /// Genera la siguiente pregunta del intento si hace falta: el intento sigue en curso,
    /// faltan preguntas y no hay más de una ya preparada sin responder.
    /// </summary>
    Task<GeneracionPregunta> GenerarSiguienteAsync(Guid intentoId, CancellationToken ct = default);
}

/// <summary>
/// Prepara la siguiente pregunta en segundo plano mientras el alumno responde la actual,
/// para que al pedirla ya esté lista. Si no llegó a tiempo, el tutor la genera en ese momento.
/// </summary>
public interface IPreparadorPreguntas
{
    void Preparar(Guid intentoId);
}

/// <summary>Un candado por intento: la generación en segundo plano y la del momento no se pisan.</summary>
public sealed class CandadosIntento
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _candados = new();

    public SemaphoreSlim Para(Guid intentoId) => _candados.GetOrAdd(intentoId, _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// El examen se genera **pregunta a pregunta** (RF-04): al empezar solo existe la primera,
/// y cada siguiente la pide la IA con la cuenta del alumno mientras él responde, sin
/// repetir lo ya preguntado. La clave de respuestas sigue fuera del chat (§6.4) y la nota
/// la calcula el servidor (RF-09).
/// </summary>
public sealed class PreguntasDelIntento(
    IAppDbContext db,
    IBovedaCredenciales credenciales,
    IProveedorLlmFactory proveedores,
    IContextoClaseService contexto,
    IGeneradorExamen generador,
    CandadosIntento candados,
    ILogger<PreguntasDelIntento> log) : IPreguntasDelIntento
{
    /// <summary>Preguntas listas por delante de lo respondido: más gastaría cupo sin necesidad.</summary>
    private const int Adelantadas = 1;

    public async Task<GeneracionPregunta> GenerarSiguienteAsync(Guid intentoId, CancellationToken ct = default)
    {
        var candado = candados.Para(intentoId);
        await candado.WaitAsync(ct);

        try
        {
            var intento = await db.Intentos.AsNoTracking().FirstOrDefaultAsync(i => i.Id == intentoId, ct);
            if (intento is null || intento.Estado != EstadoIntento.EnCurso) return GeneracionPregunta.Lista;

            // Se vuelve a contar dentro del candado: otra generación pudo terminar mientras tanto.
            var existentes = await db.Preguntas
                .AsNoTracking()
                .Where(p => p.IntentoId == intentoId)
                .Select(p => new { p.Enunciado, p.Tema })
                .ToListAsync(ct);

            var respondidas = await db.Respuestas.CountAsync(r => r.IntentoId == intentoId, ct);

            if (existentes.Count >= intento.PreguntasPlaneadas) return GeneracionPregunta.Lista;
            if (existentes.Count - respondidas > Adelantadas) return GeneracionPregunta.Lista;

            var agente = await db.Agentes.FirstOrDefaultAsync(a => a.Id == intento.AgenteId, ct);
            var clave = agente is null ? null : await credenciales.ClaveParaAsync(intento.AlumnoId, agente.Id, ct);
            if (agente is null || clave is null)
                return new GeneracionPregunta(false, "Conecta tu cuenta de IA para seguir con el examen.");

            var claseId = await db.Examenes.Where(e => e.Id == intento.ExamenId).Select(e => e.ClaseId).FirstAsync(ct);
            var material = await contexto.ObtenerOriginalAsync(claseId, ct);

            var yaPreguntadas = existentes.Select(p => $"{p.Tema}: {p.Enunciado}").ToList();

            IReadOnlyList<Pregunta>? nuevas = null;
            ErrorProveedor? fallo = null;

            // Primero la IA del intento. Si se quedó sin cupo o está saturada, el examen no se
            // detiene con el reloj corriendo: se prueba con las otras IA que el alumno conectó.
            foreach (var (candidato, modelo) in await CandidatosAsync(intento, agente, ct))
            {
                var claveCandidato = candidato.Id == agente.Id
                    ? clave
                    : await credenciales.ClaveParaAsync(intento.AlumnoId, candidato.Id, ct);
                if (claveCandidato is null) continue;

                try
                {
                    nuevas = await generador.GenerarAsync(
                        proveedores.Obtener(candidato.Id, claveCandidato), modelo, material, 1, yaPreguntadas, ct);
                    break;
                }
                catch (GeneracionExamenException ex)
                {
                    if (ex.Error?.CredencialRechazada == true)
                        await credenciales.MarcarInvalidaAsync(intento.AlumnoId, candidato.Id, ct);

                    log.LogWarning("No se pudo generar la pregunta {Numero} del intento {Intento} con {Agente}: {Detalle}",
                        existentes.Count + 1, intentoId, candidato.Id, ex.Error?.Mensaje ?? ex.Message);

                    // El mensaje al alumno habla de la IA de su examen, no de los respaldos.
                    fallo ??= ex.Error ?? new ErrorProveedor(ex.Message, true);

                    // Solo el cupo o la saturación justifican cambiar de IA.
                    if (ex.Error?.LimiteDeUso != true) break;
                }
            }

            if (nuevas is null) return new GeneracionPregunta(false, Mensaje(fallo));

            var pregunta = nuevas[0];
            pregunta.ExamenId = intento.ExamenId;
            pregunta.IntentoId = intentoId;
            pregunta.Orden = existentes.Count + 1;

            db.Preguntas.Add(pregunta);
            await db.SaveChangesAsync(ct);

            return GeneracionPregunta.Lista;
        }
        finally
        {
            candado.Release();
        }
    }

    /// <summary>La IA del intento con su modelo y, detrás, las demás conectadas con el suyo.</summary>
    private async Task<List<(AgenteIA Agente, string Modelo)>> CandidatosAsync(
        Intento intento, AgenteIA delIntento, CancellationToken ct)
    {
        var conectados = await credenciales.AgentesConectadosAsync(intento.AlumnoId, ct);

        var otros = (await db.Agentes
                .AsNoTracking()
                .Where(a => a.Habilitado && a.Id != delIntento.Id)
                .ToListAsync(ct))
            .Where(a => conectados.Contains(a.Id) && proveedores.Existe(a.Id))
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => (a, a.Modelo));

        return [(delIntento, intento.Modelo), .. otros];
    }

    private static string Mensaje(ErrorProveedor? error) => error switch
    {
        { CredencialRechazada: true } => "Tu cuenta de IA fue rechazada. Vuelve a conectarla para seguir con el examen.",
        { SinSaldo: true } => "Tu cuenta de IA no tiene saldo. Cárgale créditos o conecta otra IA en «Mi tutor» para seguir.",
        { Saturado: true } => "El modelo está saturado ahora mismo.",
        { LimiteDeUso: true } => "Se acabó el cupo de tu cuenta de IA; puedes conectar otra en «Mi tutor».",
        _ => "La IA no respondió."
    };
}

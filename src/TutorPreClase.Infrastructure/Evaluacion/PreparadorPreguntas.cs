using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TutorPreClase.Application.Evaluacion;

namespace TutorPreClase.Infrastructure.Evaluacion;

/// <summary>
/// Genera la siguiente pregunta en segundo plano, en su propio ámbito (su propio
/// DbContext), mientras el alumno responde la actual. Un fallo aquí no afecta al examen: la
/// pregunta se vuelve a pedir cuando el tutor la necesite.
/// </summary>
public sealed class PreparadorPreguntasEnSegundoPlano(
    IServiceScopeFactory ambitos,
    ILogger<PreparadorPreguntasEnSegundoPlano> log) : IPreparadorPreguntas
{
    public void Preparar(Guid intentoId) =>
        _ = Task.Run(async () =>
        {
            try
            {
                using var ambito = ambitos.CreateScope();
                await ambito.ServiceProvider.GetRequiredService<IPreguntasDelIntento>().GenerarSiguienteAsync(intentoId);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "No se pudo preparar la siguiente pregunta del intento {Intento}", intentoId);
            }
        });
}

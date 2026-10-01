using Microsoft.EntityFrameworkCore;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Application.Abstracciones;

public interface IAppDbContext
{
    DbSet<Usuario> Usuarios { get; }
    DbSet<Curso> Cursos { get; }
    DbSet<Matricula> Matriculas { get; }
    DbSet<Clase> Clases { get; }
    DbSet<ArchivoContenido> Archivos { get; }
    DbSet<PaginaContenido> Paginas { get; }
    DbSet<Examen> Examenes { get; }
    DbSet<Pregunta> Preguntas { get; }
    DbSet<Alternativa> Alternativas { get; }
    DbSet<PreguntaReferencia> PreguntaReferencias { get; }
    DbSet<AgenteIA> Agentes { get; }
    DbSet<Intento> Intentos { get; }
    DbSet<RespuestaIntento> Respuestas { get; }
    DbSet<Conversacion> Conversaciones { get; }
    DbSet<MensajeChat> Mensajes { get; }
    DbSet<DudaSinCobertura> DudasSinCobertura { get; }
    DbSet<NivelAlumno> Niveles { get; }
    DbSet<CredencialAgente> Credenciales { get; }
    DbSet<TokenMcp> TokensMcp { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

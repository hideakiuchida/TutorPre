using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Infrastructure.Persistencia;

public sealed class AppDbContext(DbContextOptions<AppDbContext> opciones)
    : DbContext(opciones), IAppDbContext
{
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Curso> Cursos => Set<Curso>();
    public DbSet<Matricula> Matriculas => Set<Matricula>();
    public DbSet<Clase> Clases => Set<Clase>();
    public DbSet<ArchivoContenido> Archivos => Set<ArchivoContenido>();
    public DbSet<PaginaContenido> Paginas => Set<PaginaContenido>();
    public DbSet<Examen> Examenes => Set<Examen>();
    public DbSet<Pregunta> Preguntas => Set<Pregunta>();
    public DbSet<Alternativa> Alternativas => Set<Alternativa>();
    public DbSet<PreguntaReferencia> PreguntaReferencias => Set<PreguntaReferencia>();
    public DbSet<AgenteIA> Agentes => Set<AgenteIA>();
    public DbSet<Intento> Intentos => Set<Intento>();
    public DbSet<RespuestaIntento> Respuestas => Set<RespuestaIntento>();
    public DbSet<Conversacion> Conversaciones => Set<Conversacion>();
    public DbSet<MensajeChat> Mensajes => Set<MensajeChat>();
    public DbSet<DudaSinCobertura> DudasSinCobertura => Set<DudaSinCobertura>();
    public DbSet<NivelAlumno> Niveles => Set<NivelAlumno>();
    public DbSet<CredencialAgente> Credenciales => Set<CredencialAgente>();
    public DbSet<TokenMcp> TokensMcp => Set<TokenMcp>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // En PostgreSQL las columnas documentales van como jsonb; en SQLite (tests) como texto.
        var esPostgres = Database.IsNpgsql();
        var tipoJson = esPostgres ? "jsonb" : null;

        // SQLite no sabe comparar DateTimeOffset: en pruebas se guardan como binario.
        if (!esPostgres) ConvertirFechasParaSqlite(b);

        // PostgreSQL solo acepta DateTimeOffset en UTC, y las horas de clase llegan con la
        // zona de Lima (-05:00): se guardan en UTC sin cambiar el instante.
        else ConvertirFechasAUtc(b);

        b.Entity<Usuario>(e =>
        {
            e.ToTable("usuario");
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(320).IsRequired();
            e.Property(u => u.Nombre).HasMaxLength(200).IsRequired();
            e.Property(u => u.Rol).HasConversion<string>().HasMaxLength(20);
            e.Property(u => u.ClaveHash).HasMaxLength(200);
        });

        b.Entity<Curso>(e =>
        {
            e.ToTable("curso");
            e.Property(c => c.Codigo).HasMaxLength(40).IsRequired();
            e.Property(c => c.Nombre).HasMaxLength(200).IsRequired();
            e.Property(c => c.Periodo).HasMaxLength(20);
        });

        b.Entity<Matricula>(e =>
        {
            e.ToTable("matricula");
            e.HasKey(m => new { m.UsuarioId, m.CursoId });
            e.Property(m => m.RolEnCurso).HasConversion<string>().HasMaxLength(20);
            e.HasOne(m => m.Usuario).WithMany(u => u.Matriculas).HasForeignKey(m => m.UsuarioId);
            e.HasOne(m => m.Curso).WithMany(c => c.Matriculas).HasForeignKey(m => m.CursoId);
        });

        b.Entity<Clase>(e =>
        {
            e.ToTable("clase");
            e.Property(c => c.Titulo).HasMaxLength(300).IsRequired();
            e.Property(c => c.AmpliacionPermitida).HasDefaultValue(false);
            e.HasOne(c => c.Curso).WithMany(c => c.Clases)
                .HasForeignKey(c => c.CursoId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ArchivoContenido>(e =>
        {
            e.ToTable("archivo_contenido");
            e.Property(a => a.Nombre).HasMaxLength(400).IsRequired();
            e.Property(a => a.Tipo).HasMaxLength(10).IsRequired();
            e.Property(a => a.HashSha256).HasMaxLength(64).IsRequired();
            e.Property(a => a.Estado).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Condensado).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(a => new { a.ClaseId, a.HashSha256 }).IsUnique();
            e.HasOne(a => a.Clase).WithMany(c => c.Archivos)
                .HasForeignKey(a => a.ClaseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PaginaContenido>(e =>
        {
            e.ToTable("pagina_contenido");
            e.HasIndex(p => p.ClaseId);
            e.HasIndex(p => new { p.ArchivoId, p.Pagina }).IsUnique();
            e.HasOne(p => p.Archivo).WithMany(a => a.Paginas)
                .HasForeignKey(p => p.ArchivoId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Examen>(e =>
        {
            e.ToTable("examen");
            e.HasIndex(x => x.ClaseId).IsUnique();
            e.Property(x => x.ModoFeedback).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PreguntasPorIntento).HasDefaultValue(6);
            e.HasOne(x => x.Clase).WithOne(c => c.Examen)
                .HasForeignKey<Examen>(x => x.ClaseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Pregunta>(e =>
        {
            e.ToTable("pregunta");
            e.Property(p => p.Enunciado).IsRequired();
            e.Property(p => p.Tema).HasMaxLength(200);
            e.Property(p => p.Origen).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Nivel).HasConversion<string>().HasMaxLength(20);
            e.HasOne(p => p.Examen).WithMany(x => x.Preguntas)
                .HasForeignKey(p => p.ExamenId).OnDelete(DeleteBehavior.Cascade);
            // Las preguntas generadas para un intento viven y mueren con el.
            e.HasOne(p => p.Intento).WithMany()
                .HasForeignKey(p => p.IntentoId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Alternativa>(e =>
        {
            e.ToTable("alternativa");
            e.Property(a => a.Letra).HasMaxLength(1).IsRequired();
            e.Property(a => a.Texto).IsRequired();
            e.HasOne(a => a.Pregunta).WithMany(p => p.Alternativas)
                .HasForeignKey(a => a.PreguntaId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PreguntaReferencia>(e =>
        {
            e.ToTable("pregunta_referencia");
            e.HasKey(r => new { r.PreguntaId, r.PaginaId });
            e.HasOne(r => r.Pregunta).WithMany(p => p.Referencias)
                .HasForeignKey(r => r.PreguntaId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Pagina).WithMany()
                .HasForeignKey(r => r.PaginaId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AgenteIA>(e =>
        {
            e.ToTable("agente_ia");
            e.Property(a => a.Id).HasMaxLength(20);
            e.Property(a => a.NombreVisible).HasMaxLength(60).IsRequired();
            e.Property(a => a.Proveedor).HasMaxLength(40).IsRequired();
            e.Property(a => a.Modelo).HasMaxLength(100).IsRequired();
            e.Property(a => a.BaseUrl).HasMaxLength(300).IsRequired();
            e.Property(a => a.Descripcion).HasMaxLength(300);
            e.Property(a => a.UrlConsola).HasMaxLength(300);
        });

        b.Entity<Intento>(e =>
        {
            e.ToTable("intento");
            e.Property(i => i.AgenteId).HasMaxLength(20).IsRequired();
            e.Property(i => i.Modelo).HasMaxLength(100);
            e.Property(i => i.Estado).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.Puntaje).HasPrecision(4, 1);
            e.HasIndex(i => new { i.ExamenId, i.AlumnoId });
            e.HasOne(i => i.Examen).WithMany()
                .HasForeignKey(i => i.ExamenId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RespuestaIntento>(e =>
        {
            e.ToTable("respuesta_intento");
            e.HasIndex(r => new { r.IntentoId, r.PreguntaId }).IsUnique();
            e.HasOne(r => r.Intento).WithMany(i => i.Respuestas)
                .HasForeignKey(r => r.IntentoId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Pregunta).WithMany()
                .HasForeignKey(r => r.PreguntaId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Conversacion>(e =>
        {
            e.ToTable("conversacion");
            e.Property(c => c.AgenteId).HasMaxLength(20).IsRequired();
            e.Property(c => c.Modo).HasConversion<string>().HasMaxLength(20);
            // Un unico hilo por alumno y clase (SDD §4.1).
            e.HasIndex(c => new { c.ClaseId, c.AlumnoId }).IsUnique();
            e.HasIndex(c => c.IntentoId).IsUnique();
            e.HasOne(c => c.Clase).WithMany()
                .HasForeignKey(c => c.ClaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Intento).WithOne()
                .HasForeignKey<Conversacion>(c => c.IntentoId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MensajeChat>(e =>
        {
            e.ToTable("mensaje_chat");
            e.Property(m => m.AgenteId).HasMaxLength(20);
            e.Property(m => m.Rol).HasConversion<string>().HasMaxLength(20);
            e.Property(m => m.Herramienta).HasMaxLength(60);
            if (tipoJson is not null) e.Property(m => m.Fuentes).HasColumnType(tipoJson);
            e.HasIndex(m => new { m.ConversacionId, m.CreadoEn });
            e.HasOne(m => m.Conversacion).WithMany(c => c.Mensajes)
                .HasForeignKey(m => m.ConversacionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DudaSinCobertura>(e =>
        {
            e.ToTable("duda_sin_cobertura");
            e.Property(d => d.Texto).IsRequired();
            e.Property(d => d.Tema).HasMaxLength(200);
            e.HasIndex(d => d.ClaseId);
        });

        b.Entity<TokenMcp>(e =>
        {
            e.ToTable("token_mcp");
            e.Property(t => t.Hash).HasMaxLength(64).IsRequired();
            e.Property(t => t.Ultimos4).HasMaxLength(4).IsRequired();
            // Se busca por hash en cada llamada del asistente (RF-39).
            e.HasIndex(t => t.Hash).IsUnique();
            e.HasIndex(t => t.UsuarioId);
            e.HasOne(t => t.Usuario).WithMany()
                .HasForeignKey(t => t.UsuarioId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CredencialAgente>(e =>
        {
            e.ToTable("credencial_agente");
            e.Property(c => c.AgenteId).HasMaxLength(20).IsRequired();
            e.Property(c => c.ClaveCifrada).IsRequired();
            e.Property(c => c.Ultimos4).HasMaxLength(4).IsRequired();
            e.Property(c => c.Estado).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Origen).HasConversion<string>().HasMaxLength(20)
                .HasDefaultValue(OrigenCredencial.Pegada).HasSentinel(OrigenCredencial.Pegada);
            // Una credencial por alumno y agente; se va con el usuario.
            e.HasIndex(c => new { c.UsuarioId, c.AgenteId }).IsUnique();
            e.HasOne(c => c.Usuario).WithMany()
                .HasForeignKey(c => c.UsuarioId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<NivelAlumno>(e =>
        {
            e.ToTable("nivel_alumno");
            e.Property(n => n.Nivel).HasConversion<string>().HasMaxLength(20);
            e.Property(n => n.Origen).HasConversion<string>().HasMaxLength(20);
            if (tipoJson is not null) e.Property(n => n.TemasDebiles).HasColumnType(tipoJson);
            e.HasIndex(n => new { n.AlumnoId, n.ClaseId }).IsUnique();
        });
    }

    /// <summary>
    /// SQLite no admite ORDER BY sobre DateTimeOffset. Solo en ese proveedor las fechas
    /// se guardan como binario para que las pruebas usen el mismo modelo que produccion.
    /// </summary>
    private static void ConvertirFechasParaSqlite(ModelBuilder b)
    {
        var conversor = new DateTimeOffsetToBinaryConverter();

        foreach (var entidad in b.Model.GetEntityTypes())
        {
            foreach (var propiedad in entidad.GetProperties())
            {
                if (propiedad.ClrType == typeof(DateTimeOffset) || propiedad.ClrType == typeof(DateTimeOffset?))
                    propiedad.SetValueConverter(conversor);
            }
        }
    }

    private static void ConvertirFechasAUtc(ModelBuilder b)
    {
        var conversor = new ValueConverter<DateTimeOffset, DateTimeOffset>(
            fecha => fecha.ToUniversalTime(), fecha => fecha);

        foreach (var entidad in b.Model.GetEntityTypes())
        {
            foreach (var propiedad in entidad.GetProperties())
            {
                if (propiedad.ClrType == typeof(DateTimeOffset) || propiedad.ClrType == typeof(DateTimeOffset?))
                    propiedad.SetValueConverter(conversor);
            }
        }
    }
}

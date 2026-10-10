using Microsoft.EntityFrameworkCore;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Domain.Reglas;

namespace TutorPreClase.Tests;

public class ReglasExamenTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private static Examen ExamenAbierto(int maxIntentos = 3, int? minutos = null) => new()
    {
        Publicado = true,
        MaxIntentos = maxIntentos,
        MinutosLimite = minutos
    };

    [Fact]
    public void Permite_iniciar_un_examen_publicado()
        => Assert.True(ReglasExamen.PuedeIniciar(ExamenAbierto(), [], Ahora).Permitido);

    [Fact]
    public void Rechaza_examen_no_publicado()
    {
        var examen = ExamenAbierto();
        examen.Publicado = false;

        Assert.Equal(MotivoRechazo.NoPublicado, ReglasExamen.PuedeIniciar(examen, [], Ahora).Motivo);
    }

    [Fact]
    public void El_examen_no_tiene_fechas_y_solo_lo_limitan_intentos_y_tiempo()
    {
        var examen = new Examen { Publicado = true, MaxIntentos = 3, MinutosLimite = 20 };
        var intento = new Intento { Estado = EstadoIntento.EnCurso, Inicio = Ahora.AddMinutes(-5) };

        Assert.True(ReglasExamen.PuedeIniciar(examen, [], Ahora.AddYears(1)).Permitido);
        Assert.False(ReglasExamen.TiempoVencido(examen, intento, Ahora));
        Assert.Equal(15 * 60, ReglasExamen.SegundosRestantes(examen, intento, Ahora));

        // El tiempo por intento sigue contando.
        Assert.True(ReglasExamen.TiempoVencido(examen, intento, Ahora.AddMinutes(16)));
    }

    [Fact]
    public void El_tiempo_en_pausa_no_se_descuenta()
    {
        var examen = new Examen { Publicado = true, MaxIntentos = 3, MinutosLimite = 20 };

        // Empezó hace 50 minutos, estuvo 25 en pausa y lleva otros 10 detenido.
        var intento = new Intento
        {
            Estado = EstadoIntento.EnCurso,
            Inicio = Ahora.AddMinutes(-50),
            SegundosEnPausa = 25 * 60,
            PausadoDesde = Ahora.AddMinutes(-10)
        };

        // Corrió 50 - 25 - 10 = 15 minutos: le quedan 5, ahora y dentro de una hora.
        Assert.Equal(5 * 60, ReglasExamen.SegundosRestantes(examen, intento, Ahora));
        Assert.Equal(5 * 60, ReglasExamen.SegundosRestantes(examen, intento, Ahora.AddHours(1)));
        Assert.False(ReglasExamen.TiempoVencido(examen, intento, Ahora.AddHours(1)));

        // Al reanudar, vuelve a correr.
        intento.SegundosEnPausa += 10 * 60;
        intento.PausadoDesde = null;
        Assert.Equal(5 * 60, ReglasExamen.SegundosRestantes(examen, intento, Ahora));
        Assert.True(ReglasExamen.TiempoVencido(examen, intento, Ahora.AddMinutes(6)));
    }

    [Fact]
    public void La_clase_se_habilita_al_llegar_su_fecha()
    {
        Assert.False(TutorPreClase.Application.Academico.ClaseHabilitada.Es(Ahora.AddMinutes(1), Ahora));
        Assert.True(TutorPreClase.Application.Academico.ClaseHabilitada.Es(Ahora, Ahora));
        Assert.True(TutorPreClase.Application.Academico.ClaseHabilitada.Es(Ahora.AddDays(-7), Ahora));
    }

    [Fact]
    public void Rechaza_cuando_se_agotaron_los_intentos()
    {
        Intento[] previos = [
            new() { Estado = EstadoIntento.Enviado },
            new() { Estado = EstadoIntento.Enviado },
            new() { Estado = EstadoIntento.EnRevision }
        ];

        Assert.Equal(MotivoRechazo.SinIntentosDisponibles,
            ReglasExamen.PuedeIniciar(ExamenAbierto(maxIntentos: 3), previos, Ahora).Motivo);
    }

    [Fact]
    public void Rechaza_si_ya_hay_un_intento_en_curso()
        => Assert.Equal(MotivoRechazo.IntentoEnCurso,
            ReglasExamen.PuedeIniciar(ExamenAbierto(), [new Intento { Estado = EstadoIntento.EnCurso }], Ahora).Motivo);

    [Fact]
    public void Registrar_respuesta_valida_pertenencia_duplicado_y_tiempo()
    {
        var intento = new Intento { Estado = EstadoIntento.EnCurso, Inicio = Ahora };
        var pregunta = new Pregunta { Enunciado = "¿?", IntentoId = intento.Id };
        var deOtroAlumno = new Pregunta { Enunciado = "¿?", IntentoId = Guid.NewGuid() };
        var examen = ExamenAbierto(minutos: 10);
        examen.Preguntas.AddRange([pregunta, deOtroAlumno]);

        Assert.True(ReglasExamen.PuedeRegistrarRespuesta(examen, intento, pregunta.Id, Ahora).Permitido);

        Assert.Equal(MotivoRechazo.PreguntaAjenaAlIntento,
            ReglasExamen.PuedeRegistrarRespuesta(examen, intento, Guid.NewGuid(), Ahora).Motivo);

        // Cada alumno rinde sus propias preguntas (RF-04): la de otro intento no vale aqui.
        Assert.Equal(MotivoRechazo.PreguntaAjenaAlIntento,
            ReglasExamen.PuedeRegistrarRespuesta(examen, intento, deOtroAlumno.Id, Ahora).Motivo);

        intento.Respuestas.Add(new RespuestaIntento { PreguntaId = pregunta.Id });
        Assert.Equal(MotivoRechazo.PreguntaYaRespondida,
            ReglasExamen.PuedeRegistrarRespuesta(examen, intento, pregunta.Id, Ahora).Motivo);

        intento.Respuestas.Clear();
        Assert.Equal(MotivoRechazo.TiempoVencido,
            ReglasExamen.PuedeRegistrarRespuesta(examen, intento, pregunta.Id, Ahora.AddMinutes(11)).Motivo);
    }

    [Fact]
    public void Sin_temporizador_no_hay_segundos_restantes()
        => Assert.Null(ReglasExamen.SegundosRestantes(ExamenAbierto(), new Intento { Inicio = Ahora }, Ahora));
}

public class EsquemaLocalTests
{
    [Fact]
    public async Task Una_base_local_antigua_admite_examenes_sin_fecha_limite_sin_perder_datos()
    {
        using var conexion = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        conexion.Open();

        var opciones = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TutorPreClase.Infrastructure.Persistencia.AppDbContext>()
            .UseSqlite(conexion).Options;
        using var db = new TutorPreClase.Infrastructure.Persistencia.AppDbContext(opciones);

        // La tabla como la dejó una versión anterior: el cierre era obligatorio.
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE \"viejo\" (\"Id\" TEXT NOT NULL PRIMARY KEY, \"CierraEn\" INTEGER NOT NULL, \"MaxIntentos\" INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("INSERT INTO viejo VALUES ('a', 5, 3)");

        Assert.True(await TutorPreClase.Infrastructure.Persistencia.EsquemaSqlite.HacerOpcionalAsync(db, "viejo", "CierraEn"));
        Assert.False(await TutorPreClase.Infrastructure.Persistencia.EsquemaSqlite.HacerOpcionalAsync(db, "viejo", "CierraEn"));

        db.Database.ExecuteSqlRaw("INSERT INTO viejo VALUES ('b', NULL, 3)");

        using var contar = conexion.CreateCommand();
        contar.CommandText = "SELECT COUNT(*) || '-' || SUM(\"MaxIntentos\") FROM viejo";
        Assert.Equal("2-6", contar.ExecuteScalar());
    }
}

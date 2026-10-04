using TutorPreClase.Domain.Entidades;
using TutorPreClase.Domain.Reglas;

namespace TutorPreClase.Tests;

public class CalificacionTests
{
    [Theory]
    [InlineData(7, 10, 14.0)]
    [InlineData(10, 10, 20.0)]
    [InlineData(0, 10, 0.0)]
    [InlineData(2, 3, 13.3)]
    [InlineData(1, 3, 6.7)]
    public void Nota_usa_escala_peruana_con_un_decimal(int correctas, int total, decimal esperada)
        => Assert.Equal(esperada, Calificacion.Nota(correctas, total));

    [Fact]
    public void Nota_sin_preguntas_es_cero() => Assert.Equal(0m, Calificacion.Nota(0, 0));

    [Fact]
    public void Nota_rechaza_mas_correctas_que_preguntas()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Calificacion.Nota(11, 10));

    [Theory]
    [InlineData(7, 10, 70)]
    [InlineData(1, 3, 33)]
    public void Porcentaje_redondea_al_entero(int correctas, int total, int esperado)
        => Assert.Equal(esperado, Calificacion.Porcentaje(correctas, total));
}

public class ClasificacionNivelTests
{
    [Theory]
    [InlineData(0.0, NivelAlumnoValor.Inicial)]
    [InlineData(10.9, NivelAlumnoValor.Inicial)]
    [InlineData(11.0, NivelAlumnoValor.Basico)]
    [InlineData(14.9, NivelAlumnoValor.Basico)]
    [InlineData(15.0, NivelAlumnoValor.Intermedio)]
    [InlineData(17.9, NivelAlumnoValor.Intermedio)]
    [InlineData(18.0, NivelAlumnoValor.Avanzado)]
    [InlineData(20.0, NivelAlumnoValor.Avanzado)]
    public void Nivel_sigue_los_rangos_del_sdd(decimal nota, NivelAlumnoValor esperado)
        => Assert.Equal(esperado, ClasificacionNivel.Desde(nota));

    [Fact]
    public void Nota_vigente_es_la_del_mejor_intento_enviado()
    {
        var intentos = new List<Intento>
        {
            new() { Estado = EstadoIntento.Enviado, Puntaje = 12m },
            new() { Estado = EstadoIntento.EnRevision, Puntaje = 16m },
            new() { Estado = EstadoIntento.EnCurso, Puntaje = null }
        };

        Assert.Equal(16m, ClasificacionNivel.NotaVigente(intentos));
    }

    [Fact]
    public void Sin_intentos_enviados_la_nota_vigente_es_cero()
        => Assert.Equal(0m, ClasificacionNivel.NotaVigente([new Intento { Estado = EstadoIntento.EnCurso }]));
}

public class SalvaguardasTests
{
    private static readonly IReadOnlySet<(string, int)> Paginas =
        new HashSet<(string, int)> { ("Clase03.pdf", 11), ("Clase03.pdf", 12) };

    [Fact]
    public void Detecta_el_bloque_de_ampliacion()
    {
        Assert.True(Salvaguardas.ContieneAmpliacion("Texto.\n\nAmpliación fuera del material: ReLU…"));
        Assert.True(Salvaguardas.ContieneAmpliacion("AMPLIACION FUERA DEL MATERIAL: x"));
        Assert.False(Salvaguardas.ContieneAmpliacion("Una explicación normal del material."));
    }

    [Fact]
    public void Corta_la_ampliacion_conservando_lo_anterior()
    {
        var texto = "Según el material [Clase03.pdf, p. 12] la sigmoide satura.\n\n" +
                    "Ampliación fuera del material: además, en la práctica se usa GELU…";

        var cortado = Salvaguardas.CortarAmpliacion(texto);

        Assert.DoesNotContain("GELU", cortado);
        Assert.Contains("la sigmoide satura", cortado);
        Assert.EndsWith(Salvaguardas.SinCobertura, cortado);
    }

    [Fact]
    public void Si_la_respuesta_es_solo_ampliacion_queda_el_aviso()
        => Assert.Equal(Salvaguardas.SinCobertura,
            Salvaguardas.CortarAmpliacion("Ampliación fuera del material: todo esto es mío."));

    [Fact]
    public void Extrae_citas_y_marca_las_que_no_resuelven()
    {
        var texto = "Ver [Clase03.pdf, p. 12] y también [Clase03.pdf, p. 99] y otra vez [Clase03.pdf, p. 12].";

        var citas = Salvaguardas.ExtraerCitas(texto, Paginas);

        Assert.Equal(2, citas.Count);
        Assert.True(citas[0].Valida);
        Assert.Equal(12, citas[0].Pagina);
        Assert.False(citas[1].Valida);
        Assert.Equal(99, citas[1].Pagina);
    }

    [Fact]
    public void Una_respuesta_sin_cita_valida_ni_ampliacion_no_esta_sustentada()
    {
        var texto = "Confía en mí, es ReLU.";
        var citas = Salvaguardas.ExtraerCitas(texto, Paginas);

        Assert.False(Salvaguardas.RespuestaSustentada(texto, citas));
    }

    [Fact]
    public void Una_cita_invalida_sola_no_sustenta_la_respuesta()
    {
        var texto = "Es ReLU [Clase03.pdf, p. 99].";
        var citas = Salvaguardas.ExtraerCitas(texto, Paginas);

        Assert.False(Salvaguardas.RespuestaSustentada(texto, citas));
    }

    [Fact]
    public void Varias_fuentes_en_un_mismo_corchete_se_extraen_todas()
    {
        var paginas = new HashSet<(string, int)> { ("M1 Estados Financieros.pdf", 39), ("M1 Estados Financieros.pdf", 44), ("Infografía.pdf", 1) };

        // Así cita el tutor en producción.
        var citas = Salvaguardas.ExtraerCitas(
            "El costo se activa [M1 Estados Financieros.pdf, p. 44; Infografía.pdf, p. 1]. " +
            "El gasto va a resultados [M1 Estados Financieros.pdf, p. 39, 44; Otro.pdf, p. 7]. Ver [nota al pie].",
            paginas);

        Assert.Equal(
            [("M1 Estados Financieros.pdf", 44, true), ("Infografía.pdf", 1, true), ("M1 Estados Financieros.pdf", 39, true), ("Otro.pdf", 7, false)],
            citas.Select(c => (c.Archivo, c.Pagina, c.Valida)));
    }
}

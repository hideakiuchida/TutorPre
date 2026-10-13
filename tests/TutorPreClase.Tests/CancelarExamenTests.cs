using TutorPreClase.Application.Evaluacion;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

/// <summary>
/// El alumno puede cancelar el examen en curso, que queda como si no lo hubiera empezado, y
/// reiniciar su chat sin perder sus notas.
/// </summary>
public class CancelarExamenTests
{
    [Fact]
    public async Task Cancelar_el_examen_no_deja_intento_ni_nota_y_borra_del_chat_solo_lo_del_examen()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

        banco.Proveedor.RespondeTexto("Las funciones de activación deciden si una neurona se activa.");
        await banco.EnviarAsync(conversacion.Id, "explícame el tema");

        banco.Reloj.Ahora = banco.Reloj.Ahora.AddMinutes(5);
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);
        await FlujoExamenTests.Empezar(banco, conversacion.Id);
        await banco.EnviarAsync(conversacion.Id, "B");

        var chat = await banco.Tutor.CancelarExamenAsync(conversacion.Id);

        Assert.Equal(ModoConversacion.Consulta, chat.Modo);
        Assert.Null(chat.IntentoId);

        banco.Db.ChangeTracker.Clear();
        Assert.Empty(banco.Db.Intentos);
        Assert.Empty(banco.Db.Respuestas);
        Assert.Empty(banco.Db.Preguntas);
        Assert.Empty(banco.Db.Niveles);

        // Queda lo que conversó antes del examen, y nada del examen.
        var textos = banco.Db.Mensajes.Select(m => m.Texto).ToList();
        Assert.Equal(2, textos.Count);
        Assert.Contains("explícame el tema", textos);
        Assert.DoesNotContain(textos, t => t.Contains("Pregunta 1 de 2"));

        // No consumió intento: puede empezar otra vez, con todos los suyos.
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);
        Assert.Single(banco.Db.Intentos);
    }

    [Fact]
    public async Task Cancelar_un_segundo_examen_conserva_la_nota_del_primero_y_vuelve_a_su_revision()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

        await banco.Tutor.IniciarExamenAsync(conversacion.Id);
        await FlujoExamenTests.Empezar(banco, conversacion.Id);
        await banco.EnviarAsync(conversacion.Id, "A");
        banco.Proveedor.RespondeTexto("Evaluación final.");
        await banco.EnviarAsync(conversacion.Id, "B");

        var primero = banco.Db.Intentos.Single();
        var temas = banco.Db.Niveles.Single().TemasDebiles;

        banco.Reloj.Ahora = banco.Reloj.Ahora.AddMinutes(30);
        await banco.Tutor.IniciarExamenAsync(conversacion.Id);
        await FlujoExamenTests.Empezar(banco, conversacion.Id);

        var chat = await banco.Tutor.CancelarExamenAsync(conversacion.Id);

        Assert.Equal(ModoConversacion.Revision, chat.Modo);
        Assert.Equal(primero.Id, chat.IntentoId);

        banco.Db.ChangeTracker.Clear();
        var intento = banco.Db.Intentos.Single();
        Assert.Equal((primero.Id, 10.0m), (intento.Id, intento.Puntaje));
        Assert.Equal(temas, banco.Db.Niveles.Single().TemasDebiles);
        Assert.Contains(banco.Db.Mensajes.ToList(), m => m.Texto.Contains("Evaluación final."));
    }

    [Fact]
    public async Task Sin_examen_en_curso_no_hay_nada_que_cancelar()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

        var error = await Assert.ThrowsAsync<ExamenException>(() => banco.Tutor.CancelarExamenAsync(conversacion.Id));

        Assert.Equal("sin_examen_en_curso", error.Codigo);
    }

    [Fact]
    public async Task Reiniciar_el_chat_borra_los_mensajes_y_conserva_los_intentos()
    {
        using var banco = new BancoDePruebas().Sembrar();
        var conversacion = await banco.Tutor.AbrirConversacionAsync(banco.ClaseId, banco.AlumnoId, "prueba");

        await banco.Tutor.IniciarExamenAsync(conversacion.Id);
        await FlujoExamenTests.Empezar(banco, conversacion.Id);

        // Durante el examen no: antes hay que terminarlo o cancelarlo.
        var error = await Assert.ThrowsAsync<ExamenException>(() => banco.Tutor.ReiniciarConversacionAsync(conversacion.Id));
        Assert.Equal("examen_en_curso", error.Codigo);

        await banco.EnviarAsync(conversacion.Id, "A");
        banco.Proveedor.RespondeTexto("Evaluación final.");
        await banco.EnviarAsync(conversacion.Id, "A");

        var chat = await banco.Tutor.ReiniciarConversacionAsync(conversacion.Id);

        banco.Db.ChangeTracker.Clear();
        Assert.Empty(banco.Db.Mensajes);
        Assert.Equal(20.0m, banco.Db.Intentos.Single().Puntaje);
        Assert.Equal(ModoConversacion.Revision, chat.Modo);
    }
}

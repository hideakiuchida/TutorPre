using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TutorPreClase.Application.Abstracciones;
using TutorPreClase.Application.Academico;
using TutorPreClase.Application.Contenido;
using TutorPreClase.Application.Evaluacion;
using TutorPreClase.Application.Llm;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Domain.Reglas;

namespace TutorPreClase.Application.Tutor;

public interface IAgenteTutorService
{
    IAsyncEnumerable<EventoTutor> ProcesarMensajeAsync(Guid conversacionId, string mensaje, CancellationToken ct = default);
    Task<Conversacion> AbrirConversacionAsync(Guid claseId, Guid alumnoId, string agenteId, CancellationToken ct = default);
    Task<Conversacion> IniciarExamenAsync(Guid conversacionId, CancellationToken ct = default);
    Task<Conversacion> CambiarAgenteAsync(Guid conversacionId, string agenteId, CancellationToken ct = default);

    /// <summary>
    /// Cancela el examen en curso: no queda intento ni nota, y del chat se borra todo lo del
    /// examen. El chat vuelve a como estaba antes de empezarlo.
    /// </summary>
    Task<Conversacion> CancelarExamenAsync(Guid conversacionId, CancellationToken ct = default);

    /// <summary>
    /// Borra los mensajes del chat. Los intentos y sus notas no se tocan; durante el examen no
    /// se puede.
    /// </summary>
    Task<Conversacion> ReiniciarConversacionAsync(Guid conversacionId, CancellationToken ct = default);
}

/// <summary>
/// El proxy del SDD: convierte el agente generico del proveedor en el tutor del curso.
/// Aporta el contenido del docente, las herramientas del servidor y las reglas de cada modo.
/// </summary>
public sealed class AgenteTutorService(
    IAppDbContext db,
    IContextoClaseService contexto,
    IEjecutorHerramientas ejecutor,
    IServicioExamen examen,
    IProveedorLlmFactory proveedores,
    IBovedaCredenciales credenciales,
    IGeneradorExamen generador,
    IPreparadorPreguntas preparador,
    IRelojSistema reloj,
    ILogger<AgenteTutorService> log) : IAgenteTutorService
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private const int MaxVueltasHerramienta = 6;
    private const int MensajesDeHistorial = 20;
    private const int MaxCorreccionesExamen = 2;

    public async Task<Conversacion> AbrirConversacionAsync(
        Guid claseId, Guid alumnoId, string agenteId, CancellationToken ct = default)
    {
        var existente = await db.Conversaciones
            .FirstOrDefaultAsync(c => c.ClaseId == claseId && c.AlumnoId == alumnoId, ct);

        if (existente is not null)
        {
            if (existente.Modo != ModoConversacion.Evaluacion && existente.AgenteId != agenteId)
            {
                existente.AgenteId = agenteId;
                await db.SaveChangesAsync(ct);
            }
            return existente;
        }

        var conversacion = new Conversacion
        {
            ClaseId = claseId,
            AlumnoId = alumnoId,
            AgenteId = agenteId,
            Modo = ModoConversacion.Consulta,
            CreadaEn = reloj.Ahora
        };

        db.Conversaciones.Add(conversacion);
        await db.SaveChangesAsync(ct);
        return conversacion;
    }

    public async Task<Conversacion> IniciarExamenAsync(Guid conversacionId, CancellationToken ct = default)
    {
        var conversacion = await CargarAsync(conversacionId, ct);

        if (conversacion.Modo == ModoConversacion.Evaluacion)
            throw new ExamenException("examen_en_curso", "Ya tienes el examen en curso.");

        var agente = await ExigirAgenteAsync(conversacion.AgenteId, ct);

        var clave = await credenciales.ClaveParaAsync(conversacion.AlumnoId, agente.Id, ct)
            ?? throw new ExamenException("sin_credencial",
                $"Conecta tu credencial de {agente.NombreVisible} antes de empezar el examen.");

        // El examen sale del texto original, no de los apuntes condensados: el detalle manda.
        var material = await contexto.ObtenerOriginalAsync(conversacion.ClaseId, ct);
        if (material.Tokens == 0)
            throw new ExamenException("sin_material",
                "Esta clase todavía no tiene material: el examen se arma a partir de él.");

        // El examen de cada alumno lo genera la IA con su propia cuenta, a partir del
        // material de la clase (RF-04); el servidor lo valida antes de crear el intento.
        var proveedor = proveedores.Obtener(agente.Id, clave);

        var intento = await examen.IniciarIntentoAsync(
            conversacion.ClaseId, conversacion.AlumnoId, agente.Id, agente.Modelo,
            // Pregunta a pregunta: solo la primera antes de empezar; el resto se prepara
            // mientras responde, así el alumno no espera a que la IA arme todo el examen.
            (_, c) => GenerarAsync(proveedor, agente, conversacion.AlumnoId, material, 1, c),
            ct);

        conversacion.IntentoId = intento.Id;
        conversacion.Modo = ModoConversacion.Evaluacion;
        await db.SaveChangesAsync(ct);

        preparador.Preparar(intento.Id);

        return conversacion;
    }

    public async Task<Conversacion> CancelarExamenAsync(Guid conversacionId, CancellationToken ct = default)
    {
        var conversacion = await CargarAsync(conversacionId, ct);

        if (conversacion is not { Modo: ModoConversacion.Evaluacion, IntentoId: Guid intentoId })
            throw new ExamenException("sin_examen_en_curso", "No tienes un examen en curso.");

        var inicio = await db.Intentos.Where(i => i.Id == intentoId).Select(i => i.Inicio).FirstAsync(ct);

        // Si ya había rendido antes, el chat vuelve a la revisión de ese intento.
        var anterior = await db.Intentos
            .Where(i => i.AlumnoId == conversacion.AlumnoId && i.Examen!.ClaseId == conversacion.ClaseId &&
                        i.Estado != EstadoIntento.EnCurso)
            .OrderByDescending(i => i.Envio)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(ct);

        conversacion.IntentoId = anterior;
        conversacion.Modo = anterior is null ? ModoConversacion.Consulta : ModoConversacion.Revision;

        // Todo lo del examen sale del chat: desde «estoy listo» hasta la última pregunta.
        db.Mensajes.RemoveRange(await db.Mensajes
            .Where(m => m.ConversacionId == conversacion.Id && m.CreadoEn >= inicio)
            .ToListAsync(ct));
        await db.SaveChangesAsync(ct);

        await examen.CancelarIntentoAsync(intentoId, ct);

        log.LogInformation("Examen cancelado en conversacion {Conversacion}", conversacion.Id);
        return conversacion;
    }

    public async Task<Conversacion> ReiniciarConversacionAsync(Guid conversacionId, CancellationToken ct = default)
    {
        var conversacion = await CargarAsync(conversacionId, ct);

        if (conversacion.Modo == ModoConversacion.Evaluacion)
            throw new ExamenException("examen_en_curso", "Termina o cancela el examen antes de reiniciar el chat.");

        db.Mensajes.RemoveRange(await db.Mensajes
            .Where(m => m.ConversacionId == conversacion.Id)
            .ToListAsync(ct));
        await db.SaveChangesAsync(ct);

        return conversacion;
    }

    public async Task<Conversacion> CambiarAgenteAsync(
        Guid conversacionId, string agenteId, CancellationToken ct = default)
    {
        var conversacion = await CargarAsync(conversacionId, ct);
        var agente = await ExigirAgenteAsync(agenteId, ct);

        // Durante el examen la eleccion queda fija para que el intento sea auditable (SDD §5.1).
        if (conversacion.Modo == ModoConversacion.Evaluacion)
            throw new ExamenException("agente_fijo", "No puedes cambiar de agente durante el examen.");

        conversacion.AgenteId = agente.Id;
        await db.SaveChangesAsync(ct);
        return conversacion;
    }

    public async IAsyncEnumerable<EventoTutor> ProcesarMensajeAsync(
        Guid conversacionId, string mensaje, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var conversacion = await CargarAsync(conversacionId, ct);
        var clase = await db.Clases.FirstAsync(c => c.Id == conversacion.ClaseId, ct);
        var curso = await db.Cursos.FirstAsync(c => c.Id == clase.CursoId, ct);
        var agente = await ExigirAgenteAsync(conversacion.AgenteId, ct);

        var ampliacionPermitida = AmpliacionPermitida(conversacion.Modo, clase);
        yield return new EventoModo(conversacion.Modo, ampliacionPermitida);

        // La hoja de respuestas no espera al modelo (RNF-01): también al volver a entrar.
        if (conversacion is { Modo: ModoConversacion.Evaluacion, IntentoId: Guid enCurso })
            yield return await ProgresoAsync(enCurso, ct);

        // Un reintento (RF-17): si el tutor no llegó a contestar el mismo mensaje, no se guarda
        // otra vez; el historial queda con una sola pregunta y su respuesta.
        var ultimo = await db.Mensajes
            .AsNoTracking()
            .Where(m => m.ConversacionId == conversacion.Id && m.Rol != RolMensaje.Herramienta)
            .OrderByDescending(m => m.CreadoEn)
            .Select(m => new { m.Rol, m.Texto })
            .FirstOrDefaultAsync(ct);

        if (ultimo is not { Rol: RolMensaje.Alumno } || ultimo.Texto != mensaje)
        {
            db.Mensajes.Add(new MensajeChat
            {
                ConversacionId = conversacion.Id,
                AgenteId = agente.Id,
                Rol = RolMensaje.Alumno,
                Texto = mensaje,
                CreadoEn = reloj.Ahora
            });
            await db.SaveChangesAsync(ct);
        }

        // La credencial es del alumno dueño del chat (BYOK, SDD §5.1).
        var clave = await credenciales.ClaveParaAsync(conversacion.AlumnoId, agente.Id, ct);

        if (clave is null)
        {
            yield return new EventoAviso("sin_credencial",
                $"Conecta tu credencial de {agente.NombreVisible} para conversar con este agente.");
            yield return new EventoFin(agente.Id, false, 0, 0);
            yield break;
        }

        var historial = await HistorialAsync(conversacion.Id, ct);
        var feedback = await ModoFeedbackAsync(conversacion.ClaseId, ct);

        // Al conversar, si la clase es grande, solo las páginas relacionadas con lo que pregunta
        // el alumno (SDD §6.2). En el examen el material no viaja: las preguntas las entrega el
        // servidor y así el tutor no tiene de dónde sacar una pista (RF-15). Solo con feedback
        // inmediato lo necesita, para explicar cada corrección.
        var contextoClase = conversacion.Modo switch
        {
            ModoConversacion.Evaluacion when feedback == ModoFeedback.Inmediato =>
                await contexto.ObtenerAsync(conversacion.ClaseId, ct),
            ModoConversacion.Evaluacion => ContextoClase.Vacio,
            _ => await contexto.ObtenerParaPreguntaAsync(conversacion.ClaseId, Consulta(historial), ct)
        };
        var proveedor = proveedores.Obtener(agente.Id, clave);

        var intento = conversacion.IntentoId is Guid id
            ? await db.Intentos.FirstOrDefaultAsync(i => i.Id == id, ct)
            : null;

        string Prompt() => PromptTutor.Construir(new DatosPrompt(
            curso.Nombre,
            clase.Titulo,
            conversacion.Modo,
            intento?.Estado,
            feedback,
            ampliacionPermitida,
            contextoClase.Texto));

        var prompt = Prompt();

        var textoFinal = new StringBuilder();
        var tokensEntrada = 0;
        var tokensSalida = 0;
        var huboAmpliacion = false;
        var cortadaPorTope = false;

        // Se emite en vivo (RNF-02). Con la ampliacion desactivada el emisor retiene solo
        // la cola donde podria empezar la marca: lo que viene detras nunca llega (SDD §6.4).
        var emisor = new EmisorConCorte(retenerAmpliacion: !ampliacionPermitida);

        // Lo que pasó con el examen en este turno: lo decide el servidor, no el texto del modelo.
        var intentoRegistrar = false;
        var registroOk = false;
        var preguntaMostrada = false;
        var cerroExamen = false;
        var dudaRegistrada = false;
        var correcciones = 0;

        // Al cerrar el examen siempre hay retroalimentación: si el tutor no llega a escribir su
        // evaluación (sin cupo, respuesta vacía), la arma el servidor con el resultado del intento.
        string? resultadoCierre = null;
        var evaluacionEscrita = false;

        // Lo que el servidor resuelve en el examen sin esperar al modelo (RNF-01), aunque esté
        // saturado: presentar la pregunta que toca si el alumno no la tiene delante (al empezar,
        // o si la anterior no se pudo generar) y registrar una respuesta inequívoca, sea la letra
        // sola o el texto de una única alternativa. Lo demás lo interpreta el tutor.
        LlamadaHerramienta? delServidor = null;
        var soloPresentar = false;

        if (conversacion is { Modo: ModoConversacion.Evaluacion, IntentoId: Guid conPregunta })
        {
            var aLaVista = await PreguntaALaVistaAsync(conPregunta, historial, ct);
            soloPresentar = aLaVista is null && !GuionExamen.PideTerminar(mensaje);

            var letra = aLaVista is null ? null
                : GuionExamen.LetraDirecta(mensaje)
                  ?? GuionExamen.LetraPorContenido(mensaje, aLaVista.Alternativas.Select(a => (a.Letra, a.Texto)));

            if (letra is not null)
            {
                delServidor = new LlamadaHerramienta(
                    $"servidor_{Guid.NewGuid():N}",
                    Herramientas.RegistrarRespuesta,
                    JsonSerializer.SerializeToElement(new { alternativa = letra, texto_original = mensaje }));
            }
        }

        for (var vuelta = 0; vuelta < MaxVueltasHerramienta && !soloPresentar; vuelta++)
        {
            var enExamen = conversacion.Modo == ModoConversacion.Evaluacion;
            var textoVuelta = new StringBuilder();
            var llamada = delServidor;
            var esDelServidor = delServidor is not null;
            delServidor = null;

            if (!esDelServidor)
            {
                var solicitud = new LlmSolicitud(
                    agente.Modelo,
                    prompt,
                    historial,
                    Herramientas.Para(conversacion.Modo));

                ErrorProveedor? error = null;

                await foreach (var evento in proveedor.StreamAsync(solicitud, ct))
                {
                    switch (evento)
                    {
                        case TextoParcial t:
                            textoVuelta.Append(t.Texto);

                            // En el examen el texto se retiene hasta comprobarlo.
                            if (enExamen) break;

                            var visible = emisor.Agregar(t.Texto);
                            if (visible.Length > 0) yield return new EventoToken(visible);
                            break;

                        case LlamadaHerramienta l:
                            llamada = l;
                            break;

                        case Fin f:
                            tokensEntrada += f.TokensEntrada;
                            tokensSalida += f.TokensSalida;
                            cortadaPorTope |= f.CortadaPorTope;
                            break;

                        case ErrorProveedor e:
                            error = e;
                            break;
                    }
                }

                if (error is not null)
                {
                    log.LogWarning("Proveedor {Agente} fallo: {Mensaje}", agente.Id, error.Mensaje);

                    IReadOnlyList<Cita> citasDelCierre = [];

                    if (resultadoCierre is not null && !evaluacionEscrita)
                    {
                        var retro = RetroalimentacionDelServidor(textoFinal, resultadoCierre);
                        yield return new EventoToken(retro);

                        citasDelCierre = Salvaguardas.ExtraerCitas(textoFinal.ToString(), contextoClase.PaginasDeLaClase);
                        if (citasDelCierre.Count > 0) yield return new EventoFuentes(citasDelCierre);
                    }

                    if (error.CredencialRechazada)
                    {
                        await credenciales.MarcarInvalidaAsync(conversacion.AlumnoId, agente.Id, ct);
                        yield return new EventoAviso("credencial_rechazada",
                            $"{agente.NombreVisible} rechazó tu credencial. Vuelve a conectarla para seguir.");
                    }
                    else if (cerroExamen)
                    {
                        // El examen ya cerró y la nota ya salió: lo único que faltó es la evaluación
                        // del tutor. No se ofrece reenviar la última respuesta, que ya está registrada.
                        yield return new EventoAviso("evaluacion_pendiente",
                            "Tu nota está guardada. El tutor no pudo completar ahora su evaluación; " +
                            "cuando quieras, escribe «revisemos mis errores».");
                    }
                    else if (error.LimiteDeUso)
                    {
                        // La credencial sigue siendo buena: solo se acabo el cupo (RF-30).
                        yield return new EventoAviso("limite_de_uso", MensajeLimite(agente.NombreVisible, error, "Tu avance está guardado."));
                    }
                    else
                    {
                        yield return new EventoError(error.Mensaje, error.Reintentable);
                    }

                    // Lo que el servidor ya le dijo al alumno en este turno (que registró su
                    // respuesta, su nota) no se pierde porque el modelo falle después.
                    if (textoFinal.Length > 0)
                    {
                        db.Mensajes.Add(new MensajeChat
                        {
                            ConversacionId = conversacion.Id,
                            AgenteId = agente.Id,
                            Rol = RolMensaje.Agente,
                            Texto = textoFinal.ToString().TrimEnd(),
                            Fuentes = citasDelCierre.Count > 0 ? JsonSerializer.Serialize(citasDelCierre, JsonWeb) : null,
                            CreadoEn = reloj.Ahora
                        });
                        await db.SaveChangesAsync(ct);
                    }

                    yield break;
                }

                if (enExamen)
                {
                    var textoExamen = textoVuelta.ToString().Trim();
                    var esRegistro = llamada?.Nombre == Herramientas.RegistrarRespuesta;
                    var falta = GuionExamen.Falta(textoExamen, intentoRegistrar || esRegistro);

                    if (llamada is null && falta is not null)
                    {
                        log.LogWarning("Examen {Conversacion}: se descarta la respuesta del modelo ({Motivo})",
                            conversacion.Id, falta);

                        if (correcciones++ < MaxCorreccionesExamen)
                        {
                            if (textoExamen.Length > 0) historial = [.. historial, new LlmMensaje(RolLlm.Asistente, textoExamen)];
                            historial = [.. historial, new LlmMensaje(RolLlm.Usuario, GuionExamen.RecordatorioHerramientas)];
                            continue;
                        }

                        // El modelo no usa las herramientas: el servidor responde por él.
                        textoExamen = registroOk ? GuionExamen.Registrada : GuionExamen.SoloLaLetra;
                        falta = null;
                    }

                    // Junto a registrar_respuesta el texto sobra: lo que pasó lo dice el servidor.
                    if (falta is null && !esRegistro && textoExamen.Length > 0)
                    {
                        var bloque = Parrafo(textoFinal, Salvaguardas.CortarAmpliacion(textoExamen));
                        textoFinal.Append(bloque);
                        yield return new EventoToken(bloque);
                    }
                }
                else
                {
                    textoFinal.Append(textoVuelta);
                    evaluacionEscrita |= cerroExamen && textoVuelta.ToString().Trim().Length > 0;
                }

                if (llamada is null) break;

                historial = [.. historial, new LlmMensaje(
                    RolLlm.Asistente,
                    textoVuelta.ToString(),
                    llamada.Id,
                    llamada.Nombre,
                    HerramientaArgumentos: llamada.Argumentos,
                    HerramientaFirma: llamada.Firma)];
            }

            // Cerrar con preguntas pendientes solo si lo pide el alumno o se acabó el tiempo:
            // que el modelo lo decida por su cuenta le costaría la nota.
            var resultado =
                llamada!.Nombre == Herramientas.FinalizarExamen && enExamen && intento is not null &&
                !GuionExamen.PideTerminar(mensaje) && await QuedanPreguntasConTiempoAsync(intento, ct)
                    ? await GuardarAsync(conversacion, agente, new ResultadoHerramienta(
                        llamada.Nombre,
                        JsonSerializer.Serialize(new
                        {
                            error = "quedan_preguntas",
                            mensaje = "Aún quedan preguntas y el alumno no pidió terminar. Sigue con obtener_siguiente_pregunta."
                        }),
                        Ok: false), ct)
                    : await EjecutarYGuardarAsync(conversacion, agente, llamada.Nombre, llamada.Argumentos, ct);

            yield return new EventoHerramienta(resultado.Nombre, resultado.Ok ? "ok" : "error");

            var json = resultado.Json;
            var jsonCierre = resultado.CierraExamen ? resultado.Json : null;
            var registradaAhora = false;

            if (llamada.Nombre == Herramientas.RegistrarRespuesta)
            {
                intentoRegistrar = true;
                registradaAhora = resultado.Ok && GuionExamen.Verdadero(json, "registrada");
                registroOk |= registradaAhora;

                // Sin nada más que responder, el examen lo cierra el servidor (RF-09).
                if (registradaAhora && !GuionExamen.Verdadero(json, "quedan_preguntas"))
                {
                    var cierre = await EjecutarYGuardarAsync(
                        conversacion, agente, Herramientas.FinalizarExamen, JsonSerializer.SerializeToElement(new { }), ct);
                    yield return new EventoHerramienta(cierre.Nombre, cierre.Ok ? "ok" : "error");

                    if (cierre.Ok)
                    {
                        var conCierre = JsonNode.Parse(json)!.AsObject();
                        conCierre["examen_finalizado"] = true;
                        conCierre["resultado"] = JsonNode.Parse(cierre.Json);
                        json = conCierre.ToJsonString();
                        jsonCierre = cierre.Json;
                        resultado = resultado with { CierraExamen = cierre.CierraExamen };
                    }
                }
            }

            dudaRegistrada |= llamada.Nombre == Herramientas.RegistrarDudaSinCobertura && resultado.Ok;

            historial = esDelServidor
                ? [.. historial, new LlmMensaje(RolLlm.Usuario,
                    $"(Nota del servidor: la respuesta del alumno ya se registró con registrar_respuesta. Resultado: {json})")]
                : [.. historial, new LlmMensaje(RolLlm.Herramienta, null, llamada.Id, llamada.Nombre, json)];

            if (conversacion.IntentoId is Guid intentoId && conversacion.Modo == ModoConversacion.Evaluacion)
                yield return await ProgresoAsync(intentoId, ct);

            if (resultado.CierraExamen)
            {
                // El chat pasa a Revisión: otras reglas, el material vuelve y ya se puede citar.
                cerroExamen = true;
                resultadoCierre = jsonCierre;
                ampliacionPermitida = AmpliacionPermitida(conversacion.Modo, clase);
                yield return new EventoModo(conversacion.Modo, ampliacionPermitida);

                contextoClase = await contexto.ObtenerAsync(conversacion.ClaseId, ct);
                prompt = Prompt();
                emisor = new EmisorConCorte(retenerAmpliacion: !ampliacionPermitida);

                // La nota la comunica el servidor: el alumno la ve aunque el tutor se quede
                // sin cupo justo ahora. El tutor añade después su evaluación.
                var nota = Parrafo(textoFinal, GuionExamen.Nota(jsonCierre!)) + "\n\n";
                textoFinal.Append(nota);
                yield return new EventoToken(nota);
                continue;
            }

            if (conversacion.Modo != ModoConversacion.Evaluacion) continue;

            // La pregunta la escribe el servidor tal como está guardada: el modelo no la repite.
            if (llamada.Nombre == Herramientas.ObtenerSiguientePregunta && resultado.Ok &&
                GuionExamen.Pregunta(json) is string pregunta)
            {
                var bloque = Parrafo(textoFinal, pregunta);
                textoFinal.Append(bloque);
                yield return new EventoToken(bloque);
                preguntaMostrada = true;
                break;
            }

            // Con el feedback al final no hay nada que comentar: se pasa a la siguiente.
            if (registradaAhora && feedback == ModoFeedback.AlFinal)
            {
                var bloque = Parrafo(textoFinal, GuionExamen.Registrada);
                textoFinal.Append(bloque);
                yield return new EventoToken(bloque);
                break;
            }
        }

        // Sigue el examen y el alumno no tiene delante la pregunta que toca: se la da el servidor.
        if (conversacion is { Modo: ModoConversacion.Evaluacion, IntentoId: Guid pendienteDe } && !preguntaMostrada &&
            (registroOk || await PreguntaALaVistaAsync(pendienteDe, historial, ct) is null))
        {
            var siguiente = await EjecutarYGuardarAsync(
                conversacion, agente, Herramientas.ObtenerSiguientePregunta, JsonSerializer.SerializeToElement(new { }), ct);

            var contenido = siguiente.Ok
                ? GuionExamen.Pregunta(siguiente.Json)
                : $"No pude preparar la siguiente pregunta: {GuionExamen.MensajeDeError(siguiente.Json)} " +
                  "Detuve el reloj del examen: escribe «siguiente» para volver a intentarlo y el tiempo seguirá " +
                  "desde donde quedó.";

            if (contenido is not null)
            {
                var bloque = Parrafo(textoFinal, contenido);
                textoFinal.Append(bloque);
                yield return new EventoToken(bloque);
            }

            yield return await ProgresoAsync(pendienteDe, ct);
        }

        // El tutor cerró el turno sin escribir su evaluación: la retroalimentación la da el servidor.
        if (resultadoCierre is not null && !evaluacionEscrita)
        {
            yield return new EventoToken(RetroalimentacionDelServidor(textoFinal, resultadoCierre));
        }

        var texto = textoFinal.ToString();

        var resto = emisor.Cerrar();
        if (resto.Length > 0) yield return new EventoToken(resto);

        // Lo guardado coincide con lo que vio el alumno: si el emisor corto, se corta aqui.
        if (emisor.Cortado || (!ampliacionPermitida && Salvaguardas.ContieneAmpliacion(texto)))
        {
            log.LogInformation("Ampliacion cortada en conversacion {Conversacion} (modo {Modo})",
                conversacion.Id, conversacion.Modo);
            texto = Salvaguardas.CortarAmpliacion(texto);
        }
        else
        {
            huboAmpliacion = Salvaguardas.ContieneAmpliacion(texto);
        }

        if (cortadaPorTope)
        {
            yield return new EventoAviso("respuesta_incompleta",
                "La respuesta quedó incompleta porque superó el largo máximo. Pide al tutor que continúe.");
        }

        // Una cita es valida si la pagina es de la clase, aunque esta vez no se enviara.
        var citas = Salvaguardas.ExtraerCitas(texto, contextoClase.PaginasDeLaClase);
        if (citas.Count > 0) yield return new EventoFuentes(citas);

        // La evaluación final y el "no está en el material" no tienen nada que citar.
        if (conversacion.Modo == ModoConversacion.Revision &&
            !cerroExamen && !dudaRegistrada &&
            texto.Length > 0 &&
            !Salvaguardas.RespuestaSustentada(texto, citas))
        {
            yield return new EventoAviso("sin_sustento",
                "La respuesta no cita el material de la clase; revisala con tu docente.");
        }

        db.Mensajes.Add(new MensajeChat
        {
            ConversacionId = conversacion.Id,
            AgenteId = agente.Id,
            Rol = RolMensaje.Agente,
            Texto = texto,
            // Con los mismos nombres que el evento `fuentes`: la web las lee igual al recargar.
            Fuentes = citas.Count > 0 ? JsonSerializer.Serialize(citas, JsonWeb) : null,
            UsaAmpliacion = huboAmpliacion,
            TokensEntrada = tokensEntrada,
            TokensSalida = tokensSalida,
            CreadoEn = reloj.Ahora
        });
        await db.SaveChangesAsync(ct);

        yield return new EventoFin(agente.Id, huboAmpliacion, tokensEntrada, tokensSalida);
    }

    /// <summary>Separa lo que escribe el servidor de lo que ya había en la respuesta.</summary>
    private static string Parrafo(StringBuilder previo, string texto) =>
        previo.Length == 0 ? texto : $"\n\n{texto}";

    /// <summary>
    /// Añade a la respuesta la retroalimentación armada con el resultado del intento. Los temas
    /// a reforzar ya los dejó registrados el servidor al cerrar el examen (RF-21, RF-22).
    /// </summary>
    private static string RetroalimentacionDelServidor(StringBuilder textoFinal, string resultadoJson)
    {
        // La nota termina en párrafo aparte: la retroalimentación va a continuación.
        var bloque = textoFinal.ToString().EndsWith("\n\n", StringComparison.Ordinal)
            ? GuionExamen.Retroalimentacion(resultadoJson)
            : Parrafo(textoFinal, GuionExamen.Retroalimentacion(resultadoJson));

        textoFinal.Append(bloque);
        return bloque;
    }

    private async Task<EventoProgreso> ProgresoAsync(Guid intentoId, CancellationToken ct)
    {
        var progreso = await examen.ProgresoAsync(intentoId, ct);
        return new EventoProgreso(progreso.Respondidas, progreso.Total, progreso.SegundosRestantes, progreso.EnPausa);
    }

    private async Task<ResultadoHerramienta> EjecutarYGuardarAsync(
        Conversacion conversacion, AgenteIA agente, string nombre, JsonElement argumentos, CancellationToken ct) =>
        await GuardarAsync(conversacion, agente, await ejecutor.EjecutarAsync(conversacion, nombre, argumentos, ct), ct);

    private async Task<ResultadoHerramienta> GuardarAsync(
        Conversacion conversacion, AgenteIA agente, ResultadoHerramienta resultado, CancellationToken ct)
    {
        db.Mensajes.Add(new MensajeChat
        {
            ConversacionId = conversacion.Id,
            AgenteId = agente.Id,
            Rol = RolMensaje.Herramienta,
            Texto = resultado.Json,
            Herramienta = resultado.Nombre,
            CreadoEn = reloj.Ahora
        });
        await db.SaveChangesAsync(ct);

        return resultado;
    }

    /// <summary>
    /// La pregunta que toca responder, si ya está escrita en el chat. Si no (aún no se generó,
    /// o el alumno acaba de entrar), una respuesta suelta no se puede atribuir a ninguna.
    /// </summary>
    private async Task<PreguntaDto?> PreguntaALaVistaAsync(Guid intentoId, IReadOnlyList<LlmMensaje> historial, CancellationToken ct)
    {
        var pendiente = await examen.SiguientePreguntaAsync(intentoId, ct);

        return pendiente is not null && historial.Any(m =>
            m.Rol == RolLlm.Asistente && m.Texto?.Contains(pendiente.Enunciado, StringComparison.Ordinal) == true)
            ? pendiente
            : null;
    }

    private async Task<bool> QuedanPreguntasConTiempoAsync(Intento intento, CancellationToken ct)
    {
        var progreso = await examen.ProgresoAsync(intento.Id, ct);
        if (progreso.Respondidas >= progreso.Total) return false;

        var examenClase = await db.Examenes.AsNoTracking().FirstAsync(e => e.Id == intento.ExamenId, ct);
        return !ReglasExamen.TiempoVencido(examenClase, intento, reloj.Ahora);
    }

    /// <summary>
    /// Durante el examen nunca se amplia, y el docente puede apagarla por clase (RF-20).
    /// </summary>
    private static bool AmpliacionPermitida(ModoConversacion modo, Clase clase) =>
        modo != ModoConversacion.Evaluacion && clase.AmpliacionPermitida;

    private async Task<ModoFeedback> ModoFeedbackAsync(Guid claseId, CancellationToken ct)
    {
        var examenClase = await db.Examenes.AsNoTracking().FirstOrDefaultAsync(e => e.ClaseId == claseId, ct);
        return examenClase?.ModoFeedback ?? ModoFeedback.AlFinal;
    }

    private async Task<List<LlmMensaje>> HistorialAsync(Guid conversacionId, CancellationToken ct)
    {
        var mensajes = await db.Mensajes
            .AsNoTracking()
            .Where(m => m.ConversacionId == conversacionId && m.Rol != RolMensaje.Herramienta)
            .OrderByDescending(m => m.CreadoEn)
            .Take(MensajesDeHistorial)
            .ToListAsync(ct);

        mensajes.Reverse();

        return mensajes
            .Select(m => new LlmMensaje(
                m.Rol == RolMensaje.Alumno ? RolLlm.Usuario : RolLlm.Asistente,
                m.Texto))
            .ToList();
    }

    /// <summary>
    /// La pregunta y la anterior del alumno: "¿y eso cómo se calcula?" solo se entiende con
    /// el turno previo.
    /// </summary>
    private static string Consulta(IReadOnlyList<LlmMensaje> historial) =>
        string.Join('\n', historial
            .Where(m => m.Rol == RolLlm.Usuario && !string.IsNullOrWhiteSpace(m.Texto))
            .TakeLast(2)
            .Select(m => m.Texto));

    private async Task<Conversacion> CargarAsync(Guid conversacionId, CancellationToken ct) =>
        await db.Conversaciones.FirstOrDefaultAsync(c => c.Id == conversacionId, ct)
        ?? throw new ExamenException("conversacion_no_encontrada", "No existe la conversacion.");

    private async Task<AgenteIA> ExigirAgenteAsync(string agenteId, CancellationToken ct) =>
        await db.Agentes.FirstOrDefaultAsync(a => a.Id == agenteId && a.Habilitado, ct)
        ?? throw new ExamenException("agente_no_disponible", "Ese agente no esta habilitado.");


    /// <summary>
    /// Traduce un fallo del generador a lo que ve el alumno, con las mismas reglas que el
    /// chat: clave rechazada se marca invalida (RF-28), cupo agotado no (RF-30).
    /// </summary>
    private async Task<IReadOnlyList<Pregunta>> GenerarAsync(
        ILlmProvider proveedor, AgenteIA agente, Guid alumnoId, ContextoClase material, int cantidad, CancellationToken ct)
    {
        try
        {
            return await generador.GenerarAsync(proveedor, agente.Modelo, material, cantidad, ct: ct);
        }
        catch (GeneracionExamenException ex) when (ex.Error?.LimiteDeUso == true)
        {
            // La IA elegida está saturada o sin cupo: antes de pedirle al alumno que espere, se
            // prueba con las otras que tiene conectadas, como con las preguntas siguientes.
            if (await GenerarConOtraAsync(agente, alumnoId, material, cantidad, ct) is { } preguntas)
                return preguntas;

            throw new ExamenException("limite_de_uso", MensajeLimite(agente.NombreVisible, ex.Error, "No se gastó ningún intento."));
        }
        catch (GeneracionExamenException ex) when (ex.Error?.CredencialRechazada == true)
        {
            await credenciales.MarcarInvalidaAsync(alumnoId, agente.Id, ct);
            throw new ExamenException("credencial_rechazada",
                $"{agente.NombreVisible} rechazó tu credencial. Vuelve a conectarla para seguir.");
        }
        catch (GeneracionExamenException ex)
        {
            log.LogWarning(ex, "No se pudo generar el examen con {Agente}: {Detalle}", agente.Id, ex.Error?.Mensaje);
            throw new ExamenException("examen_no_generado",
                "No se pudo preparar tu examen. No se gastó ningún intento: vuelve a intentarlo.");
        }
    }

    /// <summary>La primera pregunta con otra IA conectada del alumno, o null si ninguna pudo.</summary>
    private async Task<IReadOnlyList<Pregunta>?> GenerarConOtraAsync(
        AgenteIA elegido, Guid alumnoId, ContextoClase material, int cantidad, CancellationToken ct)
    {
        var conectados = await credenciales.AgentesConectadosAsync(alumnoId, ct);

        var otros = (await db.Agentes.AsNoTracking().Where(a => a.Habilitado && a.Id != elegido.Id).ToListAsync(ct))
            .Where(a => conectados.Contains(a.Id) && proveedores.Existe(a.Id))
            .OrderBy(a => a.Id, StringComparer.Ordinal);

        foreach (var otro in otros)
        {
            if (await credenciales.ClaveParaAsync(alumnoId, otro.Id, ct) is not string clave) continue;

            try
            {
                return await generador.GenerarAsync(proveedores.Obtener(otro.Id, clave), otro.Modelo, material, cantidad, ct: ct);
            }
            catch (GeneracionExamenException ex)
            {
                log.LogWarning("Tampoco se pudo generar el examen con {Agente}: {Detalle}", otro.Id, ex.Error?.Mensaje ?? ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// El avance no se pierde: lo guarda el servidor, no el proveedor. Un modelo gratuito
    /// saturado no es culpa del cupo del alumno, y se dice así.
    /// </summary>
    private string MensajeLimite(string agente, ErrorProveedor error, string avance)
    {
        if (error.SinSaldo)
            return $"Tu cuenta de {agente} no tiene saldo. Cárgale créditos en la consola del proveedor " +
                   $"o elige otro tutor en «Mi tutor». {avance}";

        if (error.Saturado)
            return "El modelo está saturado en este momento (le pasa a todos los que lo usan, " +
                   $"no es tu cupo). Vuelve a intentarlo en unos segundos. {avance}";

        var cuando = error.ReintentarEn is DateTimeOffset momento && momento > reloj.Ahora
            ? $"Podrás seguir en unos {Math.Max(1, (int)Math.Ceiling((momento - reloj.Ahora).TotalMinutes))} min"
            : "Podrás seguir más tarde";

        return $"Se acabó tu cupo de uso en {agente}. {cuando}. {avance}";
    }
}

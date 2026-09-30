using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TutorPreClase.Application.Abstracciones;
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
    IRelojSistema reloj,
    ILogger<AgenteTutorService> log) : IAgenteTutorService
{
    private const int MaxVueltasHerramienta = 6;
    private const int MensajesDeHistorial = 20;

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

        var material = await contexto.ObtenerAsync(conversacion.ClaseId, ct);
        if (material.Tokens == 0)
            throw new ExamenException("sin_material",
                "Esta clase todavía no tiene material: el examen se arma a partir de él.");

        // El examen de cada alumno lo genera la IA con su propia cuenta, a partir del
        // material de la clase (RF-04); el servidor lo valida antes de crear el intento.
        var proveedor = proveedores.Obtener(agente.Id, clave);

        var intento = await examen.IniciarIntentoAsync(
            conversacion.ClaseId, conversacion.AlumnoId, agente.Id, agente.Modelo,
            (config, c) => GenerarAsync(proveedor, agente, conversacion.AlumnoId, material, config.PreguntasPorIntento, c),
            ct);

        conversacion.IntentoId = intento.Id;
        conversacion.Modo = ModoConversacion.Evaluacion;
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

        db.Mensajes.Add(new MensajeChat
        {
            ConversacionId = conversacion.Id,
            AgenteId = agente.Id,
            Rol = RolMensaje.Alumno,
            Texto = mensaje,
            CreadoEn = reloj.Ahora
        });
        await db.SaveChangesAsync(ct);

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

        // En el examen va el material completo; al conversar, si la clase es grande, solo
        // las páginas relacionadas con lo que pregunta el alumno (SDD §6.2).
        var contextoClase = conversacion.Modo == ModoConversacion.Evaluacion
            ? await contexto.ObtenerAsync(conversacion.ClaseId, ct)
            : await contexto.ObtenerParaPreguntaAsync(conversacion.ClaseId, Consulta(historial), ct);
        var proveedor = proveedores.Obtener(agente.Id, clave);

        var intento = conversacion.IntentoId is Guid id
            ? await db.Intentos.FirstOrDefaultAsync(i => i.Id == id, ct)
            : null;

        var prompt = PromptTutor.Construir(new DatosPrompt(
            curso.Nombre,
            clase.Titulo,
            conversacion.Modo,
            intento?.Estado,
            await ModoFeedbackAsync(conversacion.ClaseId, ct),
            ampliacionPermitida,
            contextoClase.Texto));

        var textoFinal = new StringBuilder();
        var tokensEntrada = 0;
        var tokensSalida = 0;
        var huboAmpliacion = false;
        var cortadaPorTope = false;

        // Se emite en vivo (RNF-02). Con la ampliacion desactivada el emisor retiene solo
        // la cola donde podria empezar la marca: lo que viene detras nunca llega (SDD §6.4).
        var emisor = new EmisorConCorte(retenerAmpliacion: !ampliacionPermitida);

        for (var vuelta = 0; vuelta < MaxVueltasHerramienta; vuelta++)
        {
            var solicitud = new LlmSolicitud(
                agente.Modelo,
                prompt,
                historial,
                Herramientas.Para(conversacion.Modo));

            var textoVuelta = new StringBuilder();
            LlamadaHerramienta? llamada = null;
            ErrorProveedor? error = null;

            await foreach (var evento in proveedor.StreamAsync(solicitud, ct))
            {
                switch (evento)
                {
                    case TextoParcial t:
                        textoVuelta.Append(t.Texto);
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

                if (error.CredencialRechazada)
                {
                    await credenciales.MarcarInvalidaAsync(conversacion.AlumnoId, agente.Id, ct);
                    yield return new EventoAviso("credencial_rechazada",
                        $"{agente.NombreVisible} rechazó tu credencial. Vuelve a conectarla para seguir.");
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

                yield break;
            }

            textoFinal.Append(textoVuelta);

            if (llamada is null) break;

            historial = [.. historial, new LlmMensaje(
                RolLlm.Asistente,
                textoVuelta.ToString(),
                llamada.Id,
                llamada.Nombre,
                HerramientaArgumentos: llamada.Argumentos)];

            var resultado = await ejecutor.EjecutarAsync(conversacion, llamada.Nombre, llamada.Argumentos, ct);

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

            yield return new EventoHerramienta(resultado.Nombre, resultado.Ok ? "ok" : "error");

            historial = [.. historial, new LlmMensaje(
                RolLlm.Herramienta,
                null,
                llamada.Id,
                llamada.Nombre,
                resultado.Json)];

            if (conversacion.IntentoId is Guid intentoId && conversacion.Modo == ModoConversacion.Evaluacion)
            {
                var progreso = await examen.ProgresoAsync(intentoId, ct);
                yield return new EventoProgreso(progreso.Respondidas, progreso.Total, progreso.SegundosRestantes);
            }

            if (resultado.CierraExamen)
            {
                ampliacionPermitida = AmpliacionPermitida(conversacion.Modo, clase);
                yield return new EventoModo(conversacion.Modo, ampliacionPermitida);
            }
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

        if (conversacion.Modo == ModoConversacion.Revision &&
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
            Fuentes = citas.Count > 0 ? JsonSerializer.Serialize(citas) : null,
            UsaAmpliacion = huboAmpliacion,
            TokensEntrada = tokensEntrada,
            TokensSalida = tokensSalida,
            CreadoEn = reloj.Ahora
        });
        await db.SaveChangesAsync(ct);

        yield return new EventoFin(agente.Id, huboAmpliacion, tokensEntrada, tokensSalida);
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
            return await generador.GenerarAsync(proveedor, agente.Modelo, material, cantidad, ct);
        }
        catch (GeneracionExamenException ex) when (ex.Error?.CredencialRechazada == true)
        {
            await credenciales.MarcarInvalidaAsync(alumnoId, agente.Id, ct);
            throw new ExamenException("credencial_rechazada",
                $"{agente.NombreVisible} rechazó tu credencial. Vuelve a conectarla para seguir.");
        }
        catch (GeneracionExamenException ex) when (ex.Error?.LimiteDeUso == true)
        {
            throw new ExamenException("limite_de_uso", MensajeLimite(agente.NombreVisible, ex.Error, "No se gastó ningún intento."));
        }
        catch (GeneracionExamenException ex)
        {
            log.LogWarning(ex, "No se pudo generar el examen con {Agente}: {Detalle}", agente.Id, ex.Error?.Mensaje);
            throw new ExamenException("examen_no_generado",
                "No se pudo preparar tu examen. No se gastó ningún intento: vuelve a intentarlo.");
        }
    }

    /// <summary>
    /// El avance no se pierde: lo guarda el servidor, no el proveedor. Un modelo gratuito
    /// saturado no es culpa del cupo del alumno, y se dice así.
    /// </summary>
    private string MensajeLimite(string agente, ErrorProveedor error, string avance)
    {
        if (error.Saturado)
            return "El modelo gratuito está saturado en este momento (le pasa a todos los que lo usan, " +
                   $"no es tu cupo). Vuelve a intentarlo en unos segundos. {avance}";

        var cuando = error.ReintentarEn is DateTimeOffset momento && momento > reloj.Ahora
            ? $"Podrás seguir en unos {Math.Max(1, (int)Math.Ceiling((momento - reloj.Ahora).TotalMinutes))} min"
            : "Podrás seguir más tarde";

        return $"Se acabó tu cupo de uso en {agente}. {cuando}. {avance}";
    }
}

import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ConfiguracionTutor } from './configuracion-tutor';
import { EventoTutor, TutorStreamService } from '../datos/tutor-stream.service';
import { SesionService } from '../datos/sesion.service';
import { AccesoService } from '../datos/acceso.service';
import {
  Agente,
  ClaseResumen,
  Credencial,
  Mensaje,
  ModoChat,
  Progreso,
  separarAmpliacion,
  tramos,
} from '../datos/tutor.modelos';

/** Marca de que ya se intentó conectar la IA automáticamente en esta sesión. */
const AUTOMATICO = 'tutor.openrouter.automatico';

/** El tutor que eligió en "Mi tutor": al volver a entrar sigue con el mismo. */
const TUTOR_ELEGIDO = 'tutor.agente';

function leerTutorElegido(): string | null {
  try {
    return localStorage.getItem(TUTOR_ELEGIDO);
  } catch {
    return null;
  }
}

function guardarTutorElegido(agenteId: string): void {
  try {
    localStorage.setItem(TUTOR_ELEGIDO, agenteId);
  } catch {
    // Sin almacenamiento solo se pierde la preferencia al recargar.
  }
}

/**
 * Única pantalla del alumno (SDD §8): lista de clases y el chat. No hay formulario de
 * examen ni botones de alternativas; todo entra como texto de la conversación.
 */
@Component({
  selector: 'app-chat-tutor',
  imports: [FormsModule, DatePipe, ConfiguracionTutor],
  templateUrl: './chat-tutor.page.html',
  styleUrl: './chat-tutor.page.css',
})
export class ChatTutorPage {
  private readonly api = inject(TutorStreamService);
  private readonly sesion = inject(SesionService);
  private readonly acceso = inject(AccesoService);
  private readonly destruccion = inject(DestroyRef);

  /** Nombre del profesor que eligió: solo ve sus cursos (RF-33). */
  readonly profesor = signal('');
  readonly clases = signal<ClaseResumen[]>([]);
  readonly agentes = signal<Agente[]>([]);
  readonly claseActiva = signal<ClaseResumen | null>(null);
  readonly agenteActivo = signal<string>('');
  readonly conversacionId = signal<string>('');
  readonly modo = signal<ModoChat>('Consulta');
  readonly ampliacionPermitida = signal(true);
  readonly progreso = signal<Progreso | null>(null);
  readonly mensajes = signal<Mensaje[]>([]);
  readonly enviando = signal(false);
  readonly preparandoExamen = signal(false);
  readonly novedad = signal('');
  readonly error = signal<string>('');
  readonly borrador = signal('');

  readonly credenciales = signal<Credencial[]>([]);
  readonly conectando = signal(false);
  readonly errorCredencial = signal('');
  /** "Mi tutor" abierto encima del chat. */
  readonly configuracionAbierta = signal(false);

  readonly usuarioId = this.sesion.usuarioId;
  readonly enExamen = computed(() => this.modo() === 'Evaluacion');

  /** Sin ningún agente conectado no hay tutor con quien hablar (RF-27). */
  readonly agentesConectados = computed(() => this.agentes().filter((a) => a.conectado));
  readonly sinAgente = computed(() => this.agentesConectados().length === 0);

  /** Agentes que se conectan iniciando sesión en el proveedor (RF-29, RF-37). */
  readonly agentesOAuth = computed(() => this.agentes().filter((a) => a.conexion === 'OAuth'));

  /** El tutor con el que conversa, para la cabecera. */
  readonly tutorActivo = computed(() => this.agentesConectados().find((a) => a.id === this.agenteActivo()));

  readonly tramos = tramos;

  /** Una casilla por pregunta, marcada si ya está respondida: el examen es una secuencia. */
  readonly casillas = computed(() => {
    const p = this.progreso();
    return p ? Array.from({ length: p.total }, (_, i) => i < p.respondidas) : [];
  });

  readonly tiempoRestante = computed(() => {
    const segundos = this.progreso()?.segundosRestantes;
    if (segundos === null || segundos === undefined) return null;

    const s = Math.max(0, segundos);
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  });

  readonly etiquetaModo = computed(() => {
    switch (this.modo()) {
      case 'Evaluacion':
        return 'Examen en curso';
      case 'Revision':
        return 'Revisión de errores';
      default:
        return 'Consulta libre';
    }
  });

  async ngOnInit(): Promise<void> {
    this.escucharNovedades();
    if (!this.usuarioId()) return;
    await this.completarOAuth();
    if (!(await this.cargarProfesor())) return;
    await this.cargar();
  }

  salir(): void {
    this.sesion.salir();
  }

  cambiarProfesor(): void {
    this.sesion.eligiendoProfesor.set(true);
  }

  /** Sin profesor elegido no hay cursos que mostrar: primero lo elige. */
  private async cargarProfesor(): Promise<boolean> {
    try {
      const { profesor } = await this.acceso.miProfesor();
      if (!profesor) {
        this.sesion.eligiendoProfesor.set(true);
        return false;
      }
      this.profesor.set(profesor.nombre);
    } catch {
      // Sin el nombre del profesor el chat sigue funcionando.
    }
    return true;
  }

  /**
   * La página no se refresca por su cuenta: solo cuando el servidor avisa que el docente
   * subió o quitó material de un curso del alumno (SDD §6.1). Si la conexión se corta, se
   * reconecta con espera creciente.
   */
  private escucharNovedades(): void {
    const control = new AbortController();
    this.destruccion.onDestroy(() => control.abort());

    const esperar = (ms: number) => new Promise((listo) => setTimeout(listo, ms));

    void (async () => {
      let espera = 1_000;

      while (!control.signal.aborted) {
        if (this.usuarioId()) {
          try {
            for await (const cambio of this.api.novedades(this.sesion.cabeceras(), control.signal)) {
              espera = 1_000;
              await this.aplicarNovedad(cambio.clases);
            }
          } catch {
            if (control.signal.aborted) return;
          }
        }

        await esperar(espera);
        espera = Math.min(espera * 2, 30_000);
      }
    })();
  }

  private async aplicarNovedad(clasesCambiadas: string[]): Promise<void> {
    try {
      const clases = await this.api.clases();
      this.clases.set(clases);

      // La clase abierta se reemplaza por su versión nueva (p. ej. si se publicó su examen).
      const activa = this.claseActiva();
      if (!activa) return;

      this.claseActiva.set(clases.find((c) => c.claseId === activa.claseId) ?? activa);

      if (clasesCambiadas.includes(activa.claseId))
        this.novedad.set('Tu docente actualizó el material de esta clase. El tutor lo usará desde tu próxima pregunta.');
    } catch {
      // Si falla la lectura, el siguiente aviso la vuelve a intentar.
    }
  }

  private async cargar(): Promise<void> {
    try {
      const [clases, agentes, credenciales] = await Promise.all([
        this.api.clases(),
        this.api.agentes(),
        this.api.credenciales(),
      ]);

      this.clases.set(clases);
      this.agentes.set(agentes);
      this.credenciales.set(credenciales);
      const elegido = leerTutorElegido();
      this.agenteActivo.set(
        this.agentesConectados().find((a) => a.id === elegido)?.id ??
          this.agentesConectados()[0]?.id ??
          agentes[0]?.id ??
          '',
      );

      // Sin credencial, lo primero es conectarla: el chat no sirve de nada aún.
      this.configuracionAbierta.set(this.sinAgente());

      // RF-32: al entrar sin IA conectada se le lleva directo a autorizarla, una sola vez por
      // sesión: si cancela o falla, se queda el botón y no se le reenvía en bucle. Con más de
      // una opción (Gemini y OpenRouter, RF-37) elige él en el panel.
      const oauth = this.agentesOAuth().length === 1 ? this.agentesOAuth()[0] : undefined;
      if (this.sinAgente() && oauth && !this.errorCredencial() && !sessionStorage.getItem(AUTOMATICO)) {
        sessionStorage.setItem(AUTOMATICO, '1');
        await this.entrarCon(oauth.id);
        return;
      }

      if (clases.length > 0 && !this.sinAgente()) await this.abrir(clases[0]);
    } catch {
      this.error.set('No se pudo cargar tus clases. ¿Está levantada la API?');
    }
  }

  alternarConfiguracion(): void {
    this.configuracionAbierta.update((abierto) => !abierto || this.sinAgente());
  }

  /** Con la primera IA conectada por clave, se abre la clase y se puede conversar. */
  async alConectarClave(): Promise<void> {
    const antes = this.sinAgente();
    await this.refrescarCredenciales();
    if (antes && !this.sinAgente()) {
      this.configuracionAbierta.set(false);
      const clase = this.claseActiva() ?? this.clases()[0];
      if (clase) await this.abrir(clase);
    }
  }

  /** Lleva al alumno a iniciar sesión en el proveedor (RF-29). */
  async entrarCon(agenteId: string): Promise<void> {
    if (this.conectando()) return;

    this.conectando.set(true);
    this.errorCredencial.set('');

    try {
      const { url } = await this.api.iniciarOAuth(agenteId);
      window.location.assign(url);
    } catch (e: any) {
      this.errorCredencial.set(e?.error?.mensaje ?? 'No se pudo iniciar sesión.');
      this.conectando.set(false);
    }
  }

  /**
   * El proveedor vuelve a /conectar/{agente}?code=…: se entrega el código a la API y se
   * limpia la URL para que no quede en el historial (SDD §8.1).
   */
  private async completarOAuth(): Promise<void> {
    const ruta = /^\/conectar\/([\w-]+)\/?$/.exec(location.pathname);
    if (!ruta) return;

    const codigo = new URLSearchParams(location.search).get('code');
    history.replaceState(null, '', '/');

    if (!codigo) {
      this.errorCredencial.set('No se completó el inicio de sesión. Vuelve a intentarlo.');
      return;
    }

    this.conectando.set(true);
    try {
      await this.api.canjearOAuth(ruta[1], codigo);
    } catch (e: any) {
      this.errorCredencial.set(e?.error?.mensaje ?? 'No se pudo conectar tu cuenta.');
    } finally {
      this.conectando.set(false);
    }
  }

  async desconectar(agenteId: string): Promise<void> {
    await this.api.desconectarCredencial(agenteId);
    await this.refrescarCredenciales();
    if (this.sinAgente()) this.configuracionAbierta.set(true);
  }

  private async refrescarCredenciales(): Promise<void> {
    const [agentes, credenciales] = await Promise.all([this.api.agentes(), this.api.credenciales()]);
    this.agentes.set(agentes);
    this.credenciales.set(credenciales);

    if (!this.agentesConectados().some((a) => a.id === this.agenteActivo()))
      this.agenteActivo.set(this.agentesConectados()[0]?.id ?? '');
  }

  async abrir(clase: ClaseResumen): Promise<void> {
    this.claseActiva.set(clase);
    this.error.set('');
    this.novedad.set('');
    this.progreso.set(null);

    const conversacion = await this.api.abrirConversacion(clase.claseId, this.agenteActivo());
    this.conversacionId.set(conversacion.conversacionId);
    this.modo.set(conversacion.modo);
    this.agenteActivo.set(conversacion.agenteId);

    const historial = await this.api.historial(conversacion.conversacionId);

    this.mensajes.set(
      historial.map((m) => {
        const { cuerpo, ampliacion } = separarAmpliacion(m.texto);
        return {
          rol: m.rol === 'Alumno' ? 'Alumno' : 'Agente',
          texto: cuerpo,
          ampliacion,
          fuentes: m.fuentes ? JSON.parse(m.fuentes) : undefined,
        } satisfies Mensaje;
      }),
    );
  }

  async cambiarAgente(agenteId: string): Promise<void> {
    if (this.enExamen()) {
      this.error.set('No puedes cambiar de agente durante el examen.');
      return;
    }

    // Sin clase abierta todavía, la próxima conversación ya empieza con este tutor.
    if (this.conversacionId()) await this.api.cambiarAgente(this.conversacionId(), agenteId);
    this.agenteActivo.set(agenteId);
    guardarTutorElegido(agenteId);
  }

  /**
   * La IA arma un examen propio para el alumno a partir del material (RF-04): puede tardar
   * unos segundos, así que el chat lo dice mientras tanto.
   */
  async comenzarExamen(): Promise<void> {
    if (this.preparandoExamen()) return;

    this.preparandoExamen.set(true);
    this.error.set('');
    this.mensajes.update((ms) => [
      ...ms,
      { rol: 'Agente', texto: 'Preparando tu examen con el material de la clase…', enCurso: true },
    ]);

    try {
      const inicio = await this.api.iniciarExamen(this.conversacionId());
      this.mensajes.update((ms) => ms.slice(0, -1));
      this.modo.set(inicio.modo);
      await this.enviar('Estoy listo para empezar el examen.');
    } catch (e: any) {
      this.mensajes.update((ms) => ms.slice(0, -1));
      this.error.set(e?.error?.mensaje ?? 'No se pudo preparar tu examen. Vuelve a intentarlo.');

      // Si la cuenta fue rechazada, hay que reconectarla antes de nada.
      if (e?.error?.error === 'credencial_rechazada') {
        this.configuracionAbierta.set(true);
        void this.refrescarCredenciales();
      }
    } finally {
      this.preparandoExamen.set(false);
    }
  }

  async enviarBorrador(): Promise<void> {
    const texto = this.borrador().trim();
    if (!texto) return;

    this.borrador.set('');
    await this.enviar(texto);
  }

  private async enviar(texto: string): Promise<void> {
    if (this.enviando() || !this.conversacionId()) return;

    this.enviando.set(true);
    this.error.set('');
    this.novedad.set('');

    this.mensajes.update((ms) => [
      ...ms,
      { rol: 'Alumno', texto },
      { rol: 'Agente', texto: '', enCurso: true },
    ]);

    try {
      for await (const evento of this.api.enviar(
        this.conversacionId(),
        texto,
        this.sesion.cabeceras(),
      )) {
        this.aplicar(evento);
      }
    } catch {
      this.error.set('Se perdió la conexión con el tutor.');
    } finally {
      this.enviando.set(false);
      this.actualizarUltimo((m) => ({ ...m, enCurso: false }));
      this.repartirAmpliacion();
    }
  }

  private aplicar(evento: EventoTutor): void {
    switch (evento.tipo) {
      case 'modo':
        this.modo.set(evento.modo);
        this.ampliacionPermitida.set(evento.ampliacionPermitida);
        break;

      case 'token':
        this.actualizarUltimo((m) => ({ ...m, texto: m.texto + evento.texto }));
        break;

      case 'progreso':
        this.progreso.set(evento.progreso);
        break;

      case 'fuentes':
        this.actualizarUltimo((m) => ({ ...m, fuentes: evento.fuentes }));
        break;

      case 'aviso':
        this.actualizarUltimo((m) => ({ ...m, aviso: evento.mensaje }));
        // Credencial ausente o rechazada: hay que reconectarla antes de seguir. Un
        // límite de uso no la invalida (RF-30): solo se avisa.
        if (evento.codigo === 'sin_credencial' || evento.codigo === 'credencial_rechazada') {
          this.configuracionAbierta.set(true);
          void this.refrescarCredenciales();
        }
        break;

      case 'error':
        this.error.set(evento.mensaje);
        break;
    }
  }

  /** La ampliación se separa para pintarla en su propio recuadro, sin chip de fuente. */
  private repartirAmpliacion(): void {
    this.actualizarUltimo((m) => {
      const { cuerpo, ampliacion } = separarAmpliacion(m.texto);
      return { ...m, texto: cuerpo, ampliacion };
    });
  }

  private actualizarUltimo(cambio: (m: Mensaje) => Mensaje): void {
    this.mensajes.update((ms) => {
      if (ms.length === 0) return ms;

      const copia = [...ms];
      copia[copia.length - 1] = cambio(copia[copia.length - 1]);
      return copia;
    });
  }
}

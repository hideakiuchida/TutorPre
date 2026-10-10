import { Component, DestroyRef, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe, DecimalPipe } from '@angular/common';
import { ConfiguracionTutor } from './configuracion-tutor';
import { EventoTutor, TutorStreamService } from '../datos/tutor-stream.service';
import { SesionService } from '../datos/sesion.service';
import { AccesoService } from '../datos/acceso.service';
import {
  Agente,
  ClaseResumen,
  Credencial,
  Fuente,
  Mensaje,
  ModoChat,
  Progreso,
  separarAmpliacion,
  tramos,
  claseHabilitada,
  ventanaExamen,
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

/** La última clase que abrió: al volver a entrar sigue donde estaba. */
const CLASE_ABIERTA = 'tutor.clase';

function leerClaseAbierta(): string | null {
  try {
    return localStorage.getItem(CLASE_ABIERTA);
  } catch {
    return null;
  }
}

function guardarClaseAbierta(claseId: string): void {
  try {
    localStorage.setItem(CLASE_ABIERTA, claseId);
  } catch {
    // Sin almacenamiento solo se pierde la preferencia al recargar.
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
 * Las fuentes guardadas con cada mensaje. Los mensajes anteriores las guardaron con
 * mayúscula inicial (Archivo, Pagina, Valida): se leen igual, para no perder sus citas.
 */
function leerFuentes(json: string): Fuente[] {
  try {
    return (JSON.parse(json) as any[]).map((f) => ({
      archivo: f.archivo ?? f.Archivo ?? '',
      pagina: f.pagina ?? f.Pagina ?? 0,
      valida: f.valida ?? f.Valida ?? false,
    }));
  } catch {
    return [];
  }
}

/**
 * Única pantalla del alumno (SDD §8): lista de clases y el chat. No hay formulario de
 * examen ni botones de alternativas; todo entra como texto de la conversación.
 */
@Component({
  selector: 'app-chat-tutor',
  imports: [FormsModule, DatePipe, DecimalPipe, ConfiguracionTutor],
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

  /** El mensaje que el tutor no llegó a contestar: se puede volver a enviar con un clic (RF-17). */
  readonly porReintentar = signal('');

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

  /** El reloj de la página: mueve el tiempo del examen y la ventana de cada clase. */
  private readonly ahora = signal(Date.now());

  /** Cuándo se acaba el tiempo, según el último progreso que mandó el servidor. */
  private readonly finDelTiempo = signal<number | null>(null);

  /** Con el reloj en pausa el tiempo no baja: se muestra lo que quedaba al detenerlo. */
  readonly relojEnPausa = computed(() => this.progreso()?.enPausa === true);

  readonly tiempoRestante = computed(() => {
    const fin = this.finDelTiempo();
    if (fin === null) return null;

    const s = this.relojEnPausa()
      ? Math.max(0, this.progreso()?.segundosRestantes ?? 0)
      : Math.max(0, Math.round((fin - this.ahora()) / 1000));
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  });

  /** El examen de la clase abierta se puede empezar ahora. */
  readonly examenDisponible = computed(() => this.ventana(this.claseActiva()) === 'disponible');

  ventana(clase: ClaseResumen | null): ReturnType<typeof ventanaExamen> {
    return ventanaExamen(clase, this.ahora());
  }

  /** Una clase se abre cuando llega su fecha; antes solo se ve en la lista. */
  habilitada(clase: ClaseResumen | null): boolean {
    return claseHabilitada(clase, this.ahora());
  }

  /** Archivos de la clase abierta, por nombre: de ahí sale la descarga de cada referencia. */
  private readonly archivos = signal<Record<string, string>>({});
  readonly descargando = signal('');

  descargable(archivo: string): boolean {
    return !!this.archivos()[archivo];
  }

  async descargar(archivo: string): Promise<void> {
    const clase = this.claseActiva();
    const archivoId = this.archivos()[archivo];
    if (!clase || !archivoId || this.descargando()) return;

    this.descargando.set(archivo);
    try {
      const contenido = await this.api.descargarArchivo(clase.claseId, archivoId);
      const url = URL.createObjectURL(contenido);
      const enlace = document.createElement('a');
      enlace.href = url;
      enlace.download = archivo;
      enlace.click();
      URL.revokeObjectURL(url);
    } catch {
      this.error.set(`No se pudo descargar «${archivo}». Vuelve a intentarlo.`);
    } finally {
      this.descargando.set('');
    }
  }

  private readonly lista = viewChild<ElementRef<HTMLElement>>('lista');

  /** El alumno está leyendo lo último: si sube a releer, el chat no lo arrastra de vuelta. */
  private alFinal = true;

  constructor() {
    // Cada mensaje o trozo nuevo deja a la vista lo último, como en cualquier chat.
    effect(() => {
      this.mensajes();
      const lista = this.lista()?.nativeElement;
      if (lista && this.alFinal) requestAnimationFrame(() => (lista.scrollTop = lista.scrollHeight));
    });
  }

  alDesplazar(): void {
    const lista = this.lista()?.nativeElement;
    if (lista) this.alFinal = lista.scrollHeight - lista.scrollTop - lista.clientHeight < 80;
  }

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
    const reloj = setInterval(() => this.ahora.set(Date.now()), 1_000);
    this.destruccion.onDestroy(() => clearInterval(reloj));

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

      // Sin credencial, lo primero es conectarla: el chat no sirve de nada aún. Y si la
      // conexión que acaba de intentar falló, el aviso está en ese panel: se abre para que lo vea.
      this.configuracionAbierta.set(this.sinAgente() || !!this.errorCredencial());

      // RF-32: al entrar sin IA conectada se le lleva directo a autorizarla, una sola vez por
      // sesión: si cancela o falla, se queda el botón y no se le reenvía en bucle. Con más de
      // una opción por cuenta elige él en el panel.
      const oauth = this.agentesOAuth().length === 1 ? this.agentesOAuth()[0] : undefined;
      if (this.sinAgente() && oauth && !this.errorCredencial() && !sessionStorage.getItem(AUTOMATICO)) {
        sessionStorage.setItem(AUTOMATICO, '1');
        await this.entrarCon(oauth.id);
        return;
      }

      // Se abre la última que tenía abierta; si aún no llegó su fecha, la primera habilitada.
      const abiertas = clases.filter((c) => this.habilitada(c));
      if (abiertas.length > 0 && !this.sinAgente()) {
        const ultima = leerClaseAbierta();
        await this.abrir(abiertas.find((c) => c.claseId === ultima) ?? abiertas[0]);
      }
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
      const clase = this.claseActiva() ?? this.clases().find((c) => this.habilitada(c));
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
    if (!this.habilitada(clase)) return;

    this.claseActiva.set(clase);
    this.archivos.set({});
    this.error.set('');
    this.novedad.set('');
    this.porReintentar.set('');
    this.fijarProgreso(null);
    guardarClaseAbierta(clase.claseId);

    const conversacion = await this.api.abrirConversacion(clase.claseId, this.agenteActivo());
    this.conversacionId.set(conversacion.conversacionId);
    this.modo.set(conversacion.modo);
    this.agenteActivo.set(conversacion.agenteId);
    this.fijarProgreso(conversacion.progreso ?? null);

    const historial = await this.api.historial(conversacion.conversacionId);
    this.alFinal = true;

    this.mensajes.set(
      historial.map((m) => {
        const { cuerpo, ampliacion } = separarAmpliacion(m.texto);
        return {
          rol: m.rol === 'Alumno' ? 'Alumno' : 'Agente',
          texto: cuerpo,
          ampliacion,
          fuentes: m.fuentes ? leerFuentes(m.fuentes) : undefined,
        } satisfies Mensaje;
      }),
    );

    // Si lo último guardado es un mensaje suyo, el tutor no llegó a contestarlo (RF-17).
    const ultimo = historial[historial.length - 1];
    if (ultimo?.rol === 'Alumno') this.porReintentar.set(ultimo.texto);

    // Sin la lista de archivos el chat funciona igual: las referencias solo no se descargan.
    try {
      const material = await this.api.material(clase.claseId);
      if (this.claseActiva()?.claseId === clase.claseId)
        this.archivos.set(Object.fromEntries(material.map((a) => [a.nombre, a.archivoId])));
    } catch {
      this.archivos.set({});
    }
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
      void this.refrescarClases();
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

  /** Vuelve a enviar el mensaje que quedó sin respuesta; no se repite en el chat. */
  async reintentar(): Promise<void> {
    const texto = this.porReintentar();
    if (texto) await this.enviar(texto, true);
  }

  private async enviar(texto: string, reintento = false): Promise<void> {
    if (this.enviando() || !this.conversacionId()) return;

    this.enviando.set(true);
    this.error.set('');
    this.novedad.set('');
    this.porReintentar.set('');
    this.alFinal = true;
    this.ultimoEnviado = texto;

    // En un reintento el mensaje del alumno ya está en pantalla: solo se quita el aviso viejo.
    this.mensajes.update((ms) => {
      const previos = reintento && ms[ms.length - 1]?.rol === 'Agente' ? ms.slice(0, -1) : ms;
      return [
        ...previos,
        ...(reintento ? [] : [{ rol: 'Alumno' as const, texto }]),
        { rol: 'Agente' as const, texto: '', enCurso: true },
      ];
    });

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
      this.porReintentar.set(texto);
    } finally {
      this.enviando.set(false);
      this.actualizarUltimo((m) => ({ ...m, enCurso: false }));
      this.repartirAmpliacion();
      this.quitarRespuestaVacia();
    }
  }

  /** Si el tutor no llegó a escribir nada (falló el proveedor), no queda un globo en blanco. */
  private quitarRespuestaVacia(): void {
    this.mensajes.update((ms) => {
      const ultimo = ms[ms.length - 1];
      const vacio = ultimo?.rol === 'Agente' && !ultimo.texto && !ultimo.ampliacion && !ultimo.aviso;
      return vacio ? ms.slice(0, -1) : ms;
    });
  }

  private fijarProgreso(progreso: Progreso | null): void {
    this.progreso.set(progreso);

    // El reloj se pone en hora en el mismo momento: en una pestaña en segundo plano el
    // navegador lo atrasa, y el tiempo restante saldría de más.
    this.ahora.set(Date.now());

    const segundos = progreso?.segundosRestantes;
    this.finDelTiempo.set(segundos === null || segundos === undefined ? null : Date.now() + segundos * 1000);
  }

  private aplicar(evento: EventoTutor): void {
    switch (evento.tipo) {
      case 'modo':
        // Al cerrarse el examen cambian su nota y sus intentos: la lista de clases se pone al día.
        if (this.modo() === 'Evaluacion' && evento.modo !== 'Evaluacion') void this.refrescarClases();
        this.modo.set(evento.modo);
        this.ampliacionPermitida.set(evento.ampliacionPermitida);
        break;

      case 'token':
        this.actualizarUltimo((m) => ({ ...m, texto: m.texto + evento.texto }));
        break;

      case 'progreso':
        this.fijarProgreso(evento.progreso);
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
        // Saturado o sin cupo: el mensaje no se contestó y se puede volver a enviar.
        if (evento.codigo === 'limite_de_uso') this.porReintentar.set(this.ultimoEnviado);
        break;

      case 'error':
        this.error.set(evento.mensaje);
        if (evento.reintentable) this.porReintentar.set(this.ultimoEnviado);
        break;
    }
  }

  private ultimoEnviado = '';

  private async refrescarClases(): Promise<void> {
    try {
      const clases = await this.api.clases();
      this.clases.set(clases);

      const activa = this.claseActiva();
      if (activa) this.claseActiva.set(clases.find((c) => c.claseId === activa.claseId) ?? activa);
    } catch {
      // La lista se pondrá al día la próxima vez que se cargue.
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

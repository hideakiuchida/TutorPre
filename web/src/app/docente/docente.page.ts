import { Component, OnDestroy, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { TutorStreamService } from '../datos/tutor-stream.service';
import { SesionService } from '../datos/sesion.service';
import { Agente, ClaseResumen, Credencial } from '../datos/tutor.modelos';
import {
  AjustesExamen,
  ArchivoClase,
  ContenidoClase,
  DocenteService,
  PaginaComparada,
  ReporteClase,
} from '../datos/docente.service';

/** Mientras la IA condensa, el panel se refresca cada tanto para mostrar el avance. */
const REFRESCO_MS = 5000;

/**
 * Pantalla del docente (RF-34). El material no se sube aquí: se copia a la carpeta del
 * curso y aquí se ve cómo quedó, junto con los apuntes que hace la IA para el tutor
 * (SDD §6.2), el examen, la ampliación y el reporte.
 */
@Component({
  selector: 'app-docente',
  imports: [DatePipe, DecimalPipe],
  templateUrl: './docente.page.html',
  styleUrl: './docente.page.css',
})
export class DocentePage implements OnDestroy {
  private readonly api = inject(TutorStreamService);
  private readonly docente = inject(DocenteService);
  readonly sesion = inject(SesionService);

  readonly clases = signal<ClaseResumen[]>([]);
  readonly activa = signal<ClaseResumen | null>(null);
  readonly contenido = signal<ContenidoClase | null>(null);
  readonly reporte = signal<ReporteClase | null>(null);
  readonly error = signal('');

  /** Cuenta de IA del profesor (Gemini): con ella la IA condensa su material. */
  readonly cuenta = signal<Credencial | null>(null);
  readonly agentes = signal<Agente[]>([]);
  readonly conectando = signal(false);

  readonly agentesOAuth = computed(() => this.agentes().filter((a) => a.conexion === 'OAuth'));
  readonly nombreCuenta = computed(
    () => this.agentes().find((a) => a.id === this.cuenta()?.agenteId)?.nombre ?? 'tu cuenta de IA',
  );

  /** Archivo cuyo original y apuntes se comparan página por página. */
  readonly comparando = signal<ArchivoClase | null>(null);
  readonly paginas = signal<PaginaComparada[]>([]);

  /** Hay apuntes en camino (no cuenta un archivo que espera a que el modelo se desature). */
  readonly condensando = computed(() =>
    (this.contenido()?.archivos ?? []).some(
      (a) => a.condensado === 'Procesando' || (a.condensado === 'Pendiente' && !a.errorCondensado),
    ),
  );

  /** Los tramos de nota del reporte, con el nivel que le corresponde a cada uno (SDD §5.6). */
  readonly tramosNota = [
    { rango: '0-10', nivel: 'Inicial', clave: 'Inicial' },
    { rango: '11-14', nivel: 'Básico', clave: 'Basico' },
    { rango: '15-17', nivel: 'Intermedio', clave: 'Intermedio' },
    { rango: '18-20', nivel: 'Avanzado', clave: 'Avanzado' },
  ];

  /** La nota más alta y la más baja del grupo; con un solo alumno no dicen nada nuevo. */
  readonly extremos = computed(() => {
    const notas = (this.reporte()?.niveles ?? []).map((n) => n.nota).filter((n): n is number => n !== null);
    return notas.length > 1 ? { alta: Math.max(...notas), baja: Math.min(...notas) } : null;
  });

  private refresco?: ReturnType<typeof setTimeout>;

  async ngOnInit(): Promise<void> {
    await this.completarOAuth();
    await this.cargarCuenta();

    try {
      const clases = await this.api.clases();
      this.clases.set(clases);
      if (clases.length) await this.abrir(clases[0]);
    } catch {
      this.error.set('No se pudo cargar tus clases.');
    }
  }

  ngOnDestroy(): void {
    clearTimeout(this.refresco);
  }

  async abrir(clase: ClaseResumen): Promise<void> {
    this.activa.set(clase);
    this.contenido.set(null);
    this.reporte.set(null);
    this.comparando.set(null);
    this.error.set('');

    try {
      const [contenido, reporte] = await Promise.all([
        this.docente.contenido(clase.claseId),
        this.docente.reporte(clase.claseId),
      ]);
      this.contenido.set(contenido);
      this.reporte.set(reporte);
      this.ajustes.set(contenido.examen);
      this.fecha.set(clase.inicio);
      this.avisoExamen.set('');
      this.programarRefresco();
    } catch {
      this.error.set('No se pudo cargar esta clase.');
    }
  }

  /** Lo que el profesor está editando del examen; se copia de lo guardado al abrir la clase. */
  readonly ajustes = signal<AjustesExamen | null>(null);
  readonly guardandoExamen = signal(false);
  readonly avisoExamen = signal('');

  /** La fecha de la clase que se está editando: es la que la habilita para los alumnos. */
  readonly fecha = signal('');
  readonly fechaCambiada = computed(() => !!this.fecha() && this.fecha() !== this.activa()?.inicio);

  /** Hay cambios sin guardar respecto de lo que tiene el servidor. */
  readonly examenCambiado = computed(
    () => this.fechaCambiada() || JSON.stringify(this.ajustes()) !== JSON.stringify(this.contenido()?.examen ?? null),
  );

  cambiarFecha(local: string): void {
    const iso = this.fechaIso(local);
    if (iso) this.fecha.set(iso);
    this.avisoExamen.set('');
  }

  cambiarAjuste(cambio: Partial<AjustesExamen>): void {
    const actual = this.ajustes();
    if (!actual) return;

    this.ajustes.set({ ...actual, ...cambio });
    this.avisoExamen.set('');
  }

  /** Un campo numérico vacío o fuera de rango se queda en el límite más cercano. */
  entero(valor: string, minimo: number, maximo: number): number {
    const n = Math.round(Number(valor));
    return Number.isFinite(n) ? Math.min(maximo, Math.max(minimo, n)) : minimo;
  }

  /** Fecha en el formato que usa `datetime-local`, en la hora del navegador. */
  fechaLocal(iso: string | null): string {
    if (!iso) return '';
    const d = new Date(iso);
    const dos = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${dos(d.getMonth() + 1)}-${dos(d.getDate())}T${dos(d.getHours())}:${dos(d.getMinutes())}`;
  }

  fechaIso(local: string): string | null {
    return local ? new Date(local).toISOString() : null;
  }

  async guardarExamen(): Promise<void> {
    const clase = this.activa();
    const ajustes = this.ajustes();
    const contenido = this.contenido();
    if (!clase || !ajustes || !contenido) return;

    this.guardandoExamen.set(true);
    this.avisoExamen.set('');

    try {
      if (this.fechaCambiada()) {
        const { inicio } = await this.docente.guardarFecha(clase.claseId, this.fecha());
        const actualizada = { ...clase, inicio };
        this.clases.update((lista) => lista.map((c) => (c.claseId === clase.claseId ? actualizada : c)));
        this.activa.set(actualizada);
        this.fecha.set(inicio);
      }

      await this.docente.guardarExamen(clase.claseId, ajustes);
      this.contenido.set({ ...contenido, examen: ajustes });
      this.avisoExamen.set('Guardado. Vale para los intentos que empiecen desde ahora.');
    } catch (e: any) {
      this.avisoExamen.set(e?.error?.mensaje ?? 'No se pudo guardar. Revisa los valores y vuelve a intentarlo.');
    } finally {
      this.guardandoExamen.set(false);
    }
  }

  /** Quita un tema a reforzar que el docente no considera válido para ese alumno. */
  async quitarTema(alumnoId: string, tema: string): Promise<void> {
    const clase = this.activa();
    const reporte = this.reporte();
    const alumno = reporte?.niveles.find((n) => n.alumnoId === alumnoId);
    if (!clase || !reporte || !alumno) return;

    const { temasDebiles } = await this.docente.fijarTemas(
      clase.claseId,
      alumnoId,
      alumno.temasDebiles.filter((t) => t !== tema),
    );

    this.reporte.set({
      ...reporte,
      niveles: reporte.niveles.map((n) => (n.alumnoId === alumnoId ? { ...n, temasDebiles } : n)),
    });
  }

  async alternarAmpliacion(permitida: boolean): Promise<void> {
    const clase = this.activa();
    const contenido = this.contenido();
    if (!clase || !contenido) return;

    const { ampliacionPermitida } = await this.docente.ampliacion(clase.claseId, permitida);
    this.contenido.set({ ...contenido, ampliacionPermitida });
  }

  /** La cuenta se conecta igual que la del alumno: OAuth con Google, sin claves. */
  async conectar(agenteId: string): Promise<void> {
    this.conectando.set(true);
    try {
      const { url } = await this.api.iniciarOAuth(agenteId);
      location.href = url;
    } catch (e: any) {
      this.conectando.set(false);
      this.error.set(e?.error?.mensaje ?? 'No se pudo iniciar la conexión.');
    }
  }

  async condensar(forzar: boolean): Promise<void> {
    const clase = this.activa();
    if (!clase) return;

    await this.docente.condensar(clase.claseId, forzar);
    await this.refrescarContenido();
  }

  async alternarOriginal(archivo: ArchivoClase): Promise<void> {
    await this.docente.usarOriginal(archivo.archivoId, !archivo.usarOriginal);
    await this.refrescarContenido();
  }

  async comparar(archivo: ArchivoClase): Promise<void> {
    if (this.comparando()?.archivoId === archivo.archivoId) {
      this.comparando.set(null);
      return;
    }

    this.comparando.set(archivo);
    this.paginas.set(await this.docente.paginas(archivo.archivoId));
  }

  /** Lo que ve el profesor en la columna "Para el tutor". */
  estadoTutor(a: ArchivoClase): string {
    if (a.estado !== 'Listo') return '—';
    if (a.usarOriginal) return 'Original (elegido por ti)';

    switch (a.condensado) {
      case 'Listo':
        return a.tokensCondensado < a.tokensOriginal ? 'Apuntes de la IA' : 'Original (ya era breve)';
      case 'SinCuenta':
        return 'Original: conecta Gemini para condensar';
      case 'Error':
        return 'Original: no se pudo condensar';
      case 'Pendiente':
        return a.errorCondensado ? 'Original: el modelo gratuito está saturado, se reintentará solo' : 'Condensando…';
      default:
        return 'Condensando…';
    }
  }

  /** "Basico" es el nombre del valor en la API; al profesor se le muestra con tilde. */
  nombreNivel(nivel: string): string {
    return nivel === 'Basico' ? 'Básico' : nivel;
  }

  maximo(distribucion: Partial<Record<string, number>>): number {
    return Math.max(1, ...Object.values(distribucion).map((v) => v ?? 0));
  }

  private async refrescarContenido(): Promise<void> {
    const clase = this.activa();
    if (!clase) return;

    this.contenido.set(await this.docente.contenido(clase.claseId));
    this.programarRefresco();
  }

  private programarRefresco(): void {
    clearTimeout(this.refresco);
    if (this.condensando()) this.refresco = setTimeout(() => this.refrescarContenido(), REFRESCO_MS);
  }

  private async cargarCuenta(): Promise<void> {
    try {
      const [credenciales, agentes] = await Promise.all([this.api.credenciales(), this.api.agentes()]);
      this.agentes.set(agentes);
      this.cuenta.set(credenciales.find((c) => c.estado === 'Valida') ?? null);
    } catch {
      this.cuenta.set(null);
    }
  }

  /**
   * El proveedor vuelve a /conectar/{agente}?code=…: se canjea el código y se piden
   * los apuntes de todas sus clases, que hasta ahora el tutor leía en original.
   */
  private async completarOAuth(): Promise<void> {
    const ruta = /^\/conectar\/([\w-]+)\/?$/.exec(location.pathname);
    if (!ruta) return;

    const codigo = new URLSearchParams(location.search).get('code');
    history.replaceState(null, '', '/');
    if (!codigo) return;

    try {
      await this.api.canjearOAuth(ruta[1], codigo);
      const clases = await this.api.clases();
      await Promise.all(clases.map((c) => this.docente.condensar(c.claseId)));
    } catch (e: any) {
      this.error.set(e?.error?.mensaje ?? 'No se pudo conectar tu cuenta.');
    }
  }
}

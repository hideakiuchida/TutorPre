import { Component, computed, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { TutorStreamService } from '../datos/tutor-stream.service';
import { Agente, Credencial, TokenMcp } from '../datos/tutor.modelos';

type Pestana = 'cuenta' | 'clave' | 'asistente';

/**
 * "Mi tutor" (SDD §8): elige con qué IA conversa y la conecta de una de tres formas, cada
 * una en su pestaña: iniciando sesión en el proveedor (RF-29, RF-37), pegando su propia
 * clave de API (RF-38) o usando el Tutor desde su asistente como servidor MCP (RF-39).
 */
@Component({
  selector: 'app-configuracion-tutor',
  imports: [DatePipe],
  templateUrl: './configuracion-tutor.html',
  styleUrl: './configuracion-tutor.css',
})
export class ConfiguracionTutor {
  private readonly api = inject(TutorStreamService);

  readonly agentes = input.required<Agente[]>();
  readonly credenciales = input.required<Credencial[]>();
  readonly agenteActivo = input.required<string>();
  readonly enExamen = input(false);
  readonly conectando = input(false);
  readonly errorCredencial = input('');

  readonly elegir = output<string>();
  readonly entrar = output<string>();
  readonly desconectar = output<string>();
  /** Se conectó una clave: el padre recarga agentes y credenciales. */
  readonly cambio = output<void>();
  readonly cerrar = output<void>();

  readonly pestana = signal<Pestana>('cuenta');
  readonly pestanas: { id: Pestana; titulo: string }[] = [
    { id: 'cuenta', titulo: 'Con tu cuenta' },
    { id: 'clave', titulo: 'Con tu clave de API' },
    { id: 'asistente', titulo: 'Desde tu asistente' },
  ];

  readonly conectados = computed(() => this.agentes().filter((a) => a.conectado));
  readonly porCuenta = computed(() => this.agentes().filter((a) => a.conexion === 'OAuth'));
  readonly porClave = computed(() => this.agentes().filter((a) => a.conexion === 'Clave'));

  /** Lo escrito en cada campo de clave; se borra en cuanto se envía. */
  readonly claves = signal<Partial<Record<string, string>>>({});
  readonly guardandoClave = signal('');
  readonly errorClave = signal<Record<string, string>>({});

  readonly mcp = signal<{ token: TokenMcp | null; url: string } | null>(null);
  /** El token en claro, solo justo después de generarlo. */
  readonly tokenNuevo = signal('');
  readonly errorMcp = signal('');
  readonly copiado = signal('');

  credencialDe(agenteId: string): Credencial | undefined {
    return this.credenciales().find((c) => c.agenteId === agenteId);
  }

  abrirPestana(id: Pestana): void {
    this.pestana.set(id);
    if (id === 'asistente' && !this.mcp()) void this.cargarMcp();
  }

  /** Flechas izquierda/derecha entre pestañas, como pide el patrón de pestañas accesibles. */
  moverPestana(evento: KeyboardEvent): void {
    const paso = evento.key === 'ArrowRight' ? 1 : evento.key === 'ArrowLeft' ? -1 : 0;
    if (!paso) return;

    evento.preventDefault();
    const i = this.pestanas.findIndex((p) => p.id === this.pestana());
    const siguiente = this.pestanas[(i + paso + this.pestanas.length) % this.pestanas.length];
    this.abrirPestana(siguiente.id);
    document.getElementById(`pestana-${siguiente.id}`)?.focus();
  }

  escribirClave(agenteId: string, valor: string): void {
    this.claves.update((c) => ({ ...c, [agenteId]: valor }));
  }

  async conectarClave(agenteId: string): Promise<void> {
    const clave = (this.claves()[agenteId] ?? '').trim();
    if (!clave || this.guardandoClave()) return;

    // La clave no se queda en la pantalla ni en memoria del componente.
    this.claves.update((c) => ({ ...c, [agenteId]: '' }));
    this.errorClave.update((e) => ({ ...e, [agenteId]: '' }));
    this.guardandoClave.set(agenteId);

    try {
      await this.api.conectarClave(agenteId, clave);
      this.cambio.emit();
    } catch (e: any) {
      this.errorClave.update((err) => ({
        ...err,
        [agenteId]: e?.error?.mensaje ?? 'No se pudo comprobar la clave. Vuelve a intentarlo.',
      }));
    } finally {
      this.guardandoClave.set('');
    }
  }

  private async cargarMcp(): Promise<void> {
    try {
      this.mcp.set(await this.api.tokenMcp());
    } catch {
      this.errorMcp.set('No se pudo consultar tu token.');
    }
  }

  async generarToken(): Promise<void> {
    this.errorMcp.set('');
    try {
      const nuevo = await this.api.generarTokenMcp();
      this.tokenNuevo.set(nuevo.valor);
      this.mcp.set({ token: nuevo.token, url: nuevo.url });
    } catch {
      this.errorMcp.set('No se pudo generar el token.');
    }
  }

  async revocarToken(): Promise<void> {
    await this.api.revocarTokenMcp();
    this.tokenNuevo.set('');
    this.mcp.update((m) => (m ? { ...m, token: null } : m));
  }

  /** El comando para Claude Code, con el token si se acaba de generar. */
  readonly comandoClaude = computed(() => {
    const url = this.mcp()?.url ?? '';
    const token = this.tokenNuevo() || '<tu-token>';
    return `claude mcp add --transport http tutor-preclase ${url} --header "Authorization: Bearer ${token}"`;
  });

  async copiar(texto: string, que: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(texto);
      this.copiado.set(que);
      setTimeout(() => this.copiado.set(''), 2000);
    } catch {
      this.copiado.set('');
    }
  }
}

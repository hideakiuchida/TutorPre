import { Component, computed, inject, signal } from '@angular/core';
import { AccesoService, CursoAdmin, UsuarioAdmin } from '../datos/acceso.service';
import { Rol, SesionService } from '../datos/sesion.service';

const ROLES: Rol[] = ['Alumno', 'Docente', 'Admin'];

/**
 * Gestión de usuarios (RF-33). Cada persona elige su rol al entrar; aquí el administrador
 * lo corrige o da de alta a alguien de antemano.
 */
@Component({
  selector: 'app-admin',
  templateUrl: './admin.page.html',
  styleUrl: './admin.page.css',
})
export class AdminPage {
  private readonly acceso = inject(AccesoService);
  readonly sesion = inject(SesionService);

  readonly roles = ROLES;
  readonly usuarios = signal<UsuarioAdmin[]>([]);
  readonly cursos = signal<CursoAdmin[]>([]);
  readonly error = signal('');
  readonly aviso = signal('');

  // Formulario de una persona
  readonly email = signal('');
  readonly nombre = signal('');
  readonly rol = signal<Rol>('Alumno');
  readonly cursosElegidos = signal<string[]>([]);

  // Alta por lista
  readonly lista = signal('');
  readonly cursoLista = signal('');

  /** Olvidé mi contraseña (RF-42): el enlace recién generado, para copiarlo y pasarlo. */
  readonly enlace = signal<{ email: string; url: string; vence: string } | null>(null);
  readonly copiado = signal(false);

  readonly codigoDe = computed(() => new Map(this.cursos().map((c) => [c.id, c.codigo])));

  async ngOnInit(): Promise<void> {
    await this.cargar();
  }

  private async cargar(): Promise<void> {
    try {
      const [usuarios, cursos] = await Promise.all([this.acceso.usuarios(), this.acceso.cursos()]);
      this.usuarios.set(usuarios);
      this.cursos.set(cursos);
      if (!this.cursoLista() && cursos.length) this.cursoLista.set(cursos[0].id);
    } catch {
      this.error.set('No se pudo cargar la lista de usuarios.');
    }
  }

  nombreRol(rol: Rol): string {
    return rol === 'Admin' ? 'Administrador' : rol;
  }

  editar(u: UsuarioAdmin): void {
    this.email.set(u.email);
    this.nombre.set(u.nombre);
    this.rol.set(u.rol);
    this.cursosElegidos.set([...u.cursos]);
    this.aviso.set('');
  }

  async generarEnlace(u: UsuarioAdmin): Promise<void> {
    this.error.set('');
    this.copiado.set(false);
    try {
      const { url, vence } = await this.acceso.enlaceRestablecer(u.id);
      this.enlace.set({ email: u.email, url, vence: new Date(vence).toLocaleString('es-PE', { dateStyle: 'medium', timeStyle: 'short' }) });
    } catch (e: any) {
      this.error.set(e?.error?.mensaje ?? 'No se pudo generar el enlace.');
    }
  }

  async copiarEnlace(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.enlace()!.url);
      this.copiado.set(true);
    } catch {
      this.error.set('No se pudo copiar: selecciona el enlace y cópialo a mano.');
    }
  }

  alternarCurso(cursoId: string, marcado: boolean): void {
    this.cursosElegidos.update((c) => (marcado ? [...c, cursoId] : c.filter((id) => id !== cursoId)));
  }

  async guardar(): Promise<void> {
    this.error.set('');
    try {
      await this.acceso.guardarUsuario({
        email: this.email(),
        nombre: this.nombre(),
        rol: this.rol(),
        cursos: this.cursosElegidos(),
      });
      this.aviso.set(`Guardado: ${this.email().trim().toLowerCase()} ya puede entrar.`);
      this.email.set('');
      this.nombre.set('');
      this.cursosElegidos.set([]);
      await this.cargar();
    } catch (e: any) {
      this.error.set(e?.error?.mensaje ?? 'No se pudo guardar.');
    }
  }

  /** Una persona por línea: correo, nombre, rol (el rol es opcional y por defecto Alumno). */
  async guardarLista(): Promise<void> {
    this.error.set('');

    const filas = this.lista()
      .split('\n')
      .map((l) => l.split(/[,;\t]/).map((c) => c.trim()))
      .filter((c) => c[0]);

    if (!filas.length) return;

    try {
      const { guardados } = await this.acceso.guardarLote(
        filas.map(([email, nombre, rol]) => ({
          email,
          nombre: nombre ?? '',
          rol: (ROLES.find((r) => r.toLowerCase() === (rol ?? '').toLowerCase()) ?? 'Alumno') as Rol,
          cursos: this.cursoLista() ? [this.cursoLista()] : [],
        })),
      );
      this.aviso.set(`${guardados} personas guardadas y matriculadas.`);
      this.lista.set('');
      await this.cargar();
    } catch (e: any) {
      this.error.set(e?.error?.mensaje ?? 'No se pudo guardar la lista.');
    }
  }
}

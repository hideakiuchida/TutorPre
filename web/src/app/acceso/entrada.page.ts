import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AccesoService, Profesor, RegistroPendiente, ResultadoAcceso } from '../datos/acceso.service';
import { SesionService } from '../datos/sesion.service';
import { ElegirProfesor } from './elegir-profesor';

/**
 * Página de entrada de todos los roles (RF-31). La persona entra con su cuenta de Google o
 * Microsoft, o con un correo y contraseña creados aquí (RF-41);
 * al volver del proveedor (/entrar/{proveedor}?code=…) se canjea el código. La
 * primera vez elige si es alumno (y de qué profesor) o profesor (RF-33).
 */
@Component({
  selector: 'app-entrada',
  imports: [FormsModule, ElegirProfesor],
  templateUrl: './entrada.page.html',
  styleUrl: './entrada.page.css',
})
export class EntradaPage {
  private readonly acceso = inject(AccesoService);
  private readonly sesion = inject(SesionService);

  readonly proveedores = signal<{ id: string; nombre: string; configurado: boolean }[]>([]);
  readonly ocupado = signal(false);
  readonly error = signal('');

  /** Lo primero que se elige: con qué perfil se entra. Luego, el proveedor. */
  readonly perfil = signal<'Alumno' | 'Docente' | null>(null);

  // Correo y contraseña (RF-41)
  readonly conClave = signal(false);
  readonly modoClave = signal<'entrar' | 'crear'>('entrar');
  nombre = '';
  correo = '';
  clave = '';

  // Primer acceso
  readonly registro = signal<RegistroPendiente | null>(null);
  readonly eligeAlumno = signal(false);
  readonly profesores = signal<Profesor[]>([]);
  readonly profesorElegido = signal('');

  /** Usuario conocido: entra. Alumno nuevo: elige a su profesor. */
  private recibir({ sesion, registro }: ResultadoAcceso): void {
    if (sesion) {
      this.sesion.establecer(sesion);
      return;
    }

    this.registro.set(registro);
    this.ocupado.set(false);
    if (registro?.perfil === 'Alumno') void this.soyAlumno();
  }

  async soyProfesor(): Promise<void> {
    await this.registrar('Docente', null);
  }

  async soyAlumno(): Promise<void> {
    this.error.set('');
    try {
      this.profesores.set(await this.acceso.profesores(this.registro()!.token));
      this.eligeAlumno.set(true);
    } catch (e: any) {
      this.falloRegistro(e);
    }
  }

  async entrarComoAlumno(): Promise<void> {
    await this.registrar('Alumno', this.profesorElegido());
  }

  private async registrar(rol: 'Alumno' | 'Docente', profesorId: string | null): Promise<void> {
    this.ocupado.set(true);
    this.error.set('');
    try {
      this.sesion.establecer(await this.acceso.registrar(this.registro()!.token, rol, profesorId));
    } catch (e: any) {
      this.falloRegistro(e);
      this.ocupado.set(false);
    }
  }

  /** Si el registro venció, se vuelve a empezar desde los botones de entrada. */
  private falloRegistro(e: any): void {
    this.error.set(e?.error?.mensaje ?? 'No se pudo completar el registro.');
    if (e?.error?.codigo === 'acceso_vencido' || e?.status === 401) {
      this.registro.set(null);
      this.eligeAlumno.set(false);
      void this.ngOnInit();
    }
  }

  async ngOnInit(): Promise<void> {
    if (await this.completarRetorno()) return;

    try {
      const disponibles = await this.acceso.proveedores();
      this.proveedores.set(disponibles.proveedores);
      this.conClave.set(disponibles.conClave);
    } catch {
      this.error.set('No se pudo conectar con la plataforma. Inténtalo en un momento.');
    }
  }

  async entrarCon(proveedor: { id: string; nombre: string; configurado: boolean }): Promise<void> {
    this.error.set('');
    if (!proveedor.configurado) {
      this.error.set(`El inicio de sesión con ${proveedor.nombre} aún no está configurado en la plataforma.`);
      return;
    }

    this.ocupado.set(true);
    try {
      const { url } = await this.acceso.iniciar(proveedor.id, this.perfil() ?? 'Alumno');
      window.location.assign(url);
    } catch (e: any) {
      this.error.set(e?.error?.mensaje ?? 'No se pudo iniciar sesión.');
      this.ocupado.set(false);
    }
  }

  /** Entra con su contraseña, o crea la cuenta: el alumno nuevo pasa a elegir profesor. */
  async enviarClave(): Promise<void> {
    const perfil = this.perfil() ?? 'Alumno';
    this.error.set('');
    this.ocupado.set(true);
    try {
      if (this.modoClave() === 'entrar') {
        this.sesion.establecer(await this.acceso.entrarConClave(this.correo, this.clave, perfil));
      } else {
        this.recibir(await this.acceso.crearCuenta({ email: this.correo, nombre: this.nombre, clave: this.clave, perfil }));
      }
      this.clave = '';
    } catch (e: any) {
      this.error.set(e?.error?.mensaje ?? (this.modoClave() === 'entrar' ? 'No se pudo iniciar sesión.' : 'No se pudo crear la cuenta.'));
      this.ocupado.set(false);
    }
  }

  cambiarModoClave(modo: 'entrar' | 'crear'): void {
    this.modoClave.set(modo);
    this.error.set('');
  }

  /** El proveedor vuelve con ?code=…&state=…: se canjea y se limpia la URL. */
  private async completarRetorno(): Promise<boolean> {
    const ruta = /^\/entrar\/([\w-]+)\/?$/.exec(location.pathname);
    if (!ruta) return false;

    const parametros = new URLSearchParams(location.search);
    const code = parametros.get('code');
    const state = parametros.get('state');
    history.replaceState(null, '', '/');

    if (!code || !state) {
      this.error.set('No se completó el inicio de sesión. Vuelve a intentarlo.');
      void this.ngOnInit();
      return true;
    }

    this.ocupado.set(true);
    try {
      this.recibir(await this.acceso.canjear(ruta[1], code, state));
    } catch (e: any) {
      this.error.set(e?.error?.mensaje ?? 'No se pudo iniciar sesión.');
      this.ocupado.set(false);
      void this.ngOnInit();
    }

    return true;
  }
}

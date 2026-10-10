import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

export type Rol = 'Alumno' | 'Docente' | 'Admin';

export interface UsuarioSesion {
  id: string;
  nombre: string;
  email: string;
  /** Con el que actúa en esta sesión. */
  rol: Rol;
  /** Con los que puede entrar: un profesor, también como alumno. */
  vistas?: Rol[];
}

export interface Sesion {
  token: string;
  usuario: UsuarioSesion;
}

const CLAVE = 'tutor.sesion';

/** La sesión dura lo que la pestaña: sirve para ir al proveedor de IA y volver sin perderla. */
function leer(): Sesion | null {
  try {
    const guardada = sessionStorage.getItem(CLAVE);
    return guardada ? (JSON.parse(guardada) as Sesion) : null;
  } catch {
    return null;
  }
}

/**
 * Identidad del usuario (RF-31): la da el inicio de sesión con Microsoft o Google, y el rol
 * lo decide la plataforma. La API la reconoce por el token de sesión que ella misma firmó.
 */
@Injectable({ providedIn: 'root' })
export class SesionService {
  readonly sesion = signal<Sesion | null>(leer());
  readonly usuario = computed(() => this.sesion()?.usuario ?? null);
  readonly usuarioId = computed(() => this.usuario()?.id ?? '');

  /** El alumno está eligiendo (o cambiando) a su profesor: ve solo los cursos de ese profesor. */
  readonly eligiendoProfesor = signal(false);

  establecer(sesion: Sesion): void {
    this.sesion.set(sesion);
    sessionStorage.setItem(CLAVE, JSON.stringify(sesion));
  }

  salir(): void {
    this.sesion.set(null);
    this.eligiendoProfesor.set(false);
    sessionStorage.removeItem(CLAVE);
    sessionStorage.removeItem('tutor.openrouter.automatico');
    history.replaceState(null, '', '/');
  }

  /** Para las peticiones que no pasan por HttpClient (los flujos SSE con fetch). */
  cabeceras(): Record<string, string> {
    const token = this.sesion()?.token;
    return token ? { Authorization: `Bearer ${token}` } : {};
  }
}

/** Añade la sesión a cada petición; si la API ya no la reconoce (venció), se vuelve a entrar. */
export const interceptorSesion: HttpInterceptorFn = (peticion, siguiente) => {
  const sesion = inject(SesionService);
  const token = sesion.sesion()?.token;

  return siguiente(token ? peticion.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : peticion).pipe(
    catchError((error: unknown) => {
      if (token && error instanceof HttpErrorResponse && error.status === 401) sesion.salir();
      return throwError(() => error);
    }),
  );
};

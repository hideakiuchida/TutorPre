import { environment } from '../../environments/environment';
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { Rol, Sesion, UsuarioSesion } from './sesion.service';

const API = `${environment.apiUrl}/v1`;

export interface Proveedores {
  proveedores: { id: string; nombre: string; configurado: boolean }[];
  /** Se puede crear una cuenta con correo y contraseña y entrar con ella (RF-41). */
  conClave: boolean;
  desarrollo: boolean;
}

export interface UsuarioAdmin {
  id: string;
  email: string;
  nombre: string;
  rol: Rol;
  cursos: string[];
}

export interface CursoAdmin {
  id: string;
  codigo: string;
  nombre: string;
}

/** Primer acceso: la identidad está validada y falta elegir el rol (RF-33). */
export interface RegistroPendiente {
  token: string;
  email: string;
  nombre: string;
  perfil: 'Alumno' | 'Docente' | null;
}

export interface ResultadoAcceso {
  sesion: Sesion | null;
  registro: RegistroPendiente | null;
}

export interface Profesor {
  id: string;
  nombre: string;
  cursos: string[];
}

/** Inicio de sesión (RF-31) y administración de usuarios (RF-33). */
@Injectable({ providedIn: 'root' })
export class AccesoService {
  private readonly http = inject(HttpClient);

  proveedores(): Promise<Proveedores> {
    return firstValueFrom(this.http.get<Proveedores>(`${API}/acceso/proveedores`));
  }

  /** El perfil elegido antes de ir al proveedor queda en el servidor, ligado a este inicio. */
  iniciar(proveedor: string, perfil: 'Alumno' | 'Docente'): Promise<{ url: string }> {
    return firstValueFrom(this.http.post<{ url: string }>(`${API}/acceso/${proveedor}/inicio`, { perfil }));
  }

  canjear(proveedor: string, code: string, state: string): Promise<ResultadoAcceso> {
    return firstValueFrom(this.http.post<ResultadoAcceso>(`${API}/acceso/${proveedor}/canje`, { code, state }));
  }

  /** El profesor entra al momento; el alumno recibe un registro para elegir a su profesor. */
  crearCuenta(c: { email: string; nombre: string; clave: string; perfil: 'Alumno' | 'Docente' }): Promise<ResultadoAcceso> {
    return firstValueFrom(this.http.post<ResultadoAcceso>(`${API}/acceso/cuenta`, c));
  }

  entrarConClave(email: string, clave: string, perfil: 'Alumno' | 'Docente'): Promise<Sesion> {
    return firstValueFrom(this.http.post<Sesion>(`${API}/acceso/clave`, { email, clave, perfil }));
  }

  registrar(token: string, rol: 'Alumno' | 'Docente', profesorId: string | null): Promise<Sesion> {
    return firstValueFrom(this.http.post<Sesion>(`${API}/acceso/registro`, { token, rol, profesorId }));
  }

  /** Durante el registro se pasa su token; con sesión, no hace falta. */
  profesores(registro?: string): Promise<Profesor[]> {
    const params: Record<string, string> = registro ? { registro } : {};
    return firstValueFrom(this.http.get<Profesor[]>(`${API}/acceso/profesores`, { params }));
  }

  miProfesor(): Promise<{ profesor: { id: string; nombre: string } | null }> {
    return firstValueFrom(this.http.get<{ profesor: { id: string; nombre: string } | null }>(`${API}/alumno/profesor`));
  }

  elegirProfesor(profesorId: string) {
    return firstValueFrom(this.http.put(`${API}/alumno/profesor`, { profesorId }));
  }

  yo(): Promise<UsuarioSesion> {
    return firstValueFrom(this.http.get<UsuarioSesion>(`${API}/acceso/yo`));
  }

  usuariosDePrueba(): Promise<UsuarioAdmin[]> {
    return firstValueFrom(this.http.get<UsuarioAdmin[]>(`${API}/acceso/desarrollo/usuarios`));
  }

  entrarDePrueba(usuarioId: string): Promise<Sesion> {
    return firstValueFrom(this.http.post<Sesion>(`${API}/acceso/desarrollo`, { usuarioId }));
  }

  usuarios(): Promise<UsuarioAdmin[]> {
    return firstValueFrom(this.http.get<UsuarioAdmin[]>(`${API}/admin/usuarios`));
  }

  cursos(): Promise<CursoAdmin[]> {
    return firstValueFrom(this.http.get<CursoAdmin[]>(`${API}/admin/cursos`));
  }

  guardarUsuario(u: { email: string; nombre: string; rol: Rol; cursos: string[] }) {
    return firstValueFrom(this.http.put(`${API}/admin/usuarios`, u));
  }

  guardarLote(usuarios: { email: string; nombre: string; rol: Rol; cursos: string[] }[]) {
    return firstValueFrom(this.http.post<{ guardados: number }>(`${API}/admin/usuarios/lote`, { usuarios }));
  }
}

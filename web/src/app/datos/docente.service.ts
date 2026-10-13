import { environment } from '../../environments/environment';
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

const API = `${environment.apiUrl}/v1`;

/** Estado de los apuntes que hace la IA de cada archivo (SDD §6.2). */
export type EstadoCondensado = 'Pendiente' | 'Procesando' | 'Listo' | 'SinCuenta' | 'Error';

export interface ArchivoClase {
  archivoId: string;
  nombre: string;
  estado: string;
  error: string | null;
  paginas: number;
  condensado: EstadoCondensado;
  errorCondensado: string | null;
  usarOriginal: boolean;
  tokensOriginal: number;
  tokensCondensado: number;
}

export interface ContenidoClase {
  archivos: ArchivoClase[];
  /** `tokens`: lo que lee el tutor; `tokensOriginal`: el texto sin condensar. */
  contextoClase: { tokens: number; tokensOriginal: number; truncado: boolean };
  ampliacionPermitida: boolean;
  examen: AjustesExamen | null;
}

/** Lo que el profesor decide del examen de una clase (RF-34). */
export interface AjustesExamen {
  maxIntentos: number;
  /** null: sin límite de tiempo. */
  minutosLimite: number | null;
  modoFeedback: 'AlFinal' | 'Inmediato';
  preguntasPorIntento: number;
}

export interface PaginaComparada {
  pagina: number;
  texto: string;
  tokens: number;
  condensado: string | null;
  tokensCondensado: number;
}

/** La ficha de un alumno en la clase: nota, nivel y los temas que le toca reforzar. */
export interface AlumnoEnClase {
  alumnoId: string;
  nombre: string;
  nivel: 'Inicial' | 'Basico' | 'Intermedio' | 'Avanzado';
  /** 'Docente' si el profesor corrigió el nivel a mano. */
  origen: string;
  temasDebiles: string[];
  nota: number | null;
  intentos: number;
  correctas: number;
  preguntas: number;
  rendidoEn: string | null;
}

export interface ReporteClase {
  clase: string;
  alumnos: number;
  promedioNota: number;
  distribucionNotas: Partial<Record<string, number>>;
  distribucionNiveles: Record<string, number>;
  niveles: AlumnoEnClase[];
  temasMasFallados: { tema: string; fallos: number; respondidas: number; porcentajeFallo: number }[];
  dudasFueraDelMaterial: { texto: string; tema: string | null; conAmpliacion: boolean; creadoEn: string }[];
}

/** Lo que ve y ajusta el docente de cada clase (RF-34, RF-13, RF-16, RF-20, RF-22). */
@Injectable({ providedIn: 'root' })
export class DocenteService {
  private readonly http = inject(HttpClient);

  contenido(claseId: string): Promise<ContenidoClase> {
    return firstValueFrom(this.http.get<ContenidoClase>(`${API}/clases/${claseId}/contenido`));
  }

  reporte(claseId: string): Promise<ReporteClase> {
    return firstValueFrom(this.http.get<ReporteClase>(`${API}/clases/${claseId}/reporte`));
  }

  guardarExamen(claseId: string, ajustes: AjustesExamen) {
    return firstValueFrom(this.http.put<{ examenId: string }>(`${API}/clases/${claseId}/examen`, ajustes));
  }

  /** Deja los temas a reforzar de un alumno como el docente decida. */
  fijarTemas(claseId: string, alumnoId: string, temas: string[]) {
    return firstValueFrom(
      this.http.put<{ temasDebiles: string[] }>(`${API}/clases/${claseId}/niveles/${alumnoId}/temas`, { temas }),
    );
  }

  /** Pide a la IA los apuntes que faltan, o todos de nuevo con `forzar`. */
  condensar(claseId: string, forzar = false) {
    return firstValueFrom(this.http.post<{ encolados: number }>(`${API}/clases/${claseId}/condensar`, { forzar }));
  }

  usarOriginal(archivoId: string, usarOriginal: boolean) {
    return firstValueFrom(
      this.http.put<{ usarOriginal: boolean }>(`${API}/contenido/${archivoId}/condensado`, { usarOriginal }),
    );
  }

  paginas(archivoId: string): Promise<PaginaComparada[]> {
    return firstValueFrom(this.http.get<PaginaComparada[]>(`${API}/contenido/${archivoId}/paginas`));
  }

  ampliacion(claseId: string, permitida: boolean) {
    return firstValueFrom(
      this.http.put<{ ampliacionPermitida: boolean }>(`${API}/clases/${claseId}/ampliacion`, { permitida }),
    );
  }
}

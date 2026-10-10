import { environment } from '../../environments/environment';
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { Agente, ClaseResumen, Credencial, Fuente, ModoChat, Progreso, TokenMcp } from './tutor.modelos';

export type EventoTutor =
  | { tipo: 'modo'; modo: ModoChat; ampliacionPermitida: boolean }
  | { tipo: 'token'; texto: string }
  | { tipo: 'herramienta'; nombre: string; estado: string }
  | { tipo: 'progreso'; progreso: Progreso }
  | { tipo: 'fuentes'; fuentes: Fuente[] }
  | { tipo: 'aviso'; codigo: string; mensaje: string }
  | { tipo: 'fin'; usaAmpliacion: boolean }
  | { tipo: 'error'; mensaje: string; reintentable: boolean };

const API = `${environment.apiUrl}/v1`;

/** El docente subió o quitó material en estas clases del curso. */
export interface CambioContenido {
  cursoId: string;
  clases: string[];
}

/**
 * Consume los eventos SSE del tutor con `fetch` streaming (SDD §8.1). La web no
 * interpreta el texto para construir controles: solo pinta lo que llega.
 */
@Injectable({ providedIn: 'root' })
export class TutorStreamService {
  private readonly http = inject(HttpClient);

  agentes(): Promise<Agente[]> {
    return firstValueFrom(this.http.get<Agente[]>(`${API}/agentes`));
  }

  clases(): Promise<ClaseResumen[]> {
    return firstValueFrom(this.http.get<ClaseResumen[]>(`${API}/alumno/clases`));
  }

  /** Los archivos de la clase: con su id, cada referencia del tutor se puede descargar. */
  material(claseId: string) {
    return firstValueFrom(this.http.get<{ archivoId: string; nombre: string }[]>(`${API}/clases/${claseId}/material`));
  }

  descargarArchivo(claseId: string, archivoId: string) {
    return firstValueFrom(this.http.get(`${API}/clases/${claseId}/material/${archivoId}`, { responseType: 'blob' }));
  }

  credenciales(): Promise<Credencial[]> {
    return firstValueFrom(this.http.get<Credencial[]>(`${API}/alumno/credenciales`));
  }

  /** Pide a la API la URL de inicio de sesión; el verificador PKCE se queda en el servidor. */
  iniciarOAuth(agenteId: string) {
    return firstValueFrom(
      this.http.post<{ url: string }>(`${API}/alumno/credenciales/${agenteId}/oauth/inicio`, {}),
    );
  }

  /** Entrega el código de un solo uso; la clave la canjea y la guarda la API. */
  canjearOAuth(agenteId: string, code: string) {
    return firstValueFrom(
      this.http.post<Credencial>(`${API}/alumno/credenciales/${agenteId}/oauth/canje`, { code }),
    );
  }

  desconectarCredencial(agenteId: string) {
    return firstValueFrom(this.http.delete<void>(`${API}/alumno/credenciales/${agenteId}`));
  }

  /** Clave de API propia (RF-38): la valida el proveedor y la API solo devuelve los últimos 4. */
  conectarClave(agenteId: string, clave: string) {
    return firstValueFrom(this.http.put<Credencial>(`${API}/alumno/credenciales/${agenteId}`, { clave }));
  }

  /** Token para usar el Tutor desde el asistente del alumno como servidor MCP (RF-39). */
  tokenMcp() {
    return firstValueFrom(this.http.get<{ token: TokenMcp | null; url: string }>(`${API}/alumno/token-mcp`));
  }

  /** El valor del token solo viene en esta respuesta: no se puede volver a consultar. */
  generarTokenMcp() {
    return firstValueFrom(
      this.http.post<{ valor: string; token: TokenMcp; url: string }>(`${API}/alumno/token-mcp`, {}),
    );
  }

  revocarTokenMcp() {
    return firstValueFrom(this.http.delete<void>(`${API}/alumno/token-mcp`));
  }

  abrirConversacion(claseId: string, agenteId: string) {
    return firstValueFrom(
      this.http.post<{
        conversacionId: string;
        modo: ModoChat;
        agenteId: string;
        /** Solo con el examen en curso: para pintar la hoja de respuestas al volver a entrar. */
        progreso: Progreso | null;
      }>(
        `${API}/clases/${claseId}/conversacion`,
        { agenteId },
      ),
    );
  }

  historial(conversacionId: string) {
    return firstValueFrom(
      this.http.get<
        { rol: string; texto: string; fuentes: string | null; usaAmpliacion: boolean }[]
      >(`${API}/conversaciones/${conversacionId}/mensajes`),
    );
  }

  iniciarExamen(conversacionId: string) {
    return firstValueFrom(
      this.http.post<{ intentoId: string; modo: ModoChat }>(
        `${API}/conversaciones/${conversacionId}/examen`,
        {},
      ),
    );
  }

  cambiarAgente(conversacionId: string, agenteId: string) {
    return firstValueFrom(
      this.http.put<{ agenteId: string }>(`${API}/conversaciones/${conversacionId}/agente`, {
        agenteId,
      }),
    );
  }

  /** Envía un mensaje y va entregando los eventos a medida que llegan. */
  async *enviar(
    conversacionId: string,
    texto: string,
    cabeceras: Record<string, string>,
  ): AsyncGenerator<EventoTutor> {
    const respuesta = await fetch(`${API}/conversaciones/${conversacionId}/mensajes`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...cabeceras },
      body: JSON.stringify({ texto }),
    });

    if (!respuesta.ok || !respuesta.body) {
      yield { tipo: 'error', mensaje: `La API respondió ${respuesta.status}.`, reintentable: true };
      return;
    }

    for await (const bloque of this.bloques(respuesta.body)) {
      const evento = this.interpretar(bloque);
      if (evento) yield evento;
    }
  }

  /**
   * Avisos de que el docente cambió el material de un curso del alumno (SDD §6.1). La
   * conexión queda abierta; termina si el servidor la corta o se aborta `señal`.
   */
  async *novedades(cabeceras: Record<string, string>, señal: AbortSignal): AsyncGenerator<CambioContenido> {
    const respuesta = await fetch(`${API}/alumno/novedades`, { headers: cabeceras, signal: señal });
    if (!respuesta.ok || !respuesta.body) throw new Error(`La API respondió ${respuesta.status}.`);

    for await (const bloque of this.bloques(respuesta.body)) {
      if (!bloque.startsWith('event: contenido')) continue; // latidos y comentarios

      const datos = bloque.split('\n').find((l) => l.startsWith('data:'));
      if (datos) yield JSON.parse(datos.slice(5)) as CambioContenido;
    }
  }

  /** Parte un flujo SSE en eventos: se separan por una línea en blanco. */
  private async *bloques(cuerpo: ReadableStream<Uint8Array>): AsyncGenerator<string> {
    const lector = cuerpo.getReader();
    const decodificador = new TextDecoder();
    let pendiente = '';

    while (true) {
      const { done, value } = await lector.read();
      if (done) break;

      pendiente += decodificador.decode(value, { stream: true });

      const bloques = pendiente.split('\n\n');
      pendiente = bloques.pop() ?? '';
      yield* bloques;
    }
  }

  private interpretar(bloque: string): EventoTutor | null {
    let nombre = '';
    let datos = '';

    for (const linea of bloque.split('\n')) {
      if (linea.startsWith('event:')) nombre = linea.slice(6).trim();
      else if (linea.startsWith('data:')) datos += linea.slice(5).trim();
    }

    if (!nombre || !datos) return null;

    let cuerpo: any;
    try {
      cuerpo = JSON.parse(datos);
    } catch {
      return null;
    }

    switch (nombre) {
      case 'modo':
        return { tipo: 'modo', modo: cuerpo.modo, ampliacionPermitida: cuerpo.ampliacionPermitida };
      case 'token':
        return { tipo: 'token', texto: cuerpo.texto };
      case 'herramienta':
        return { tipo: 'herramienta', nombre: cuerpo.nombre, estado: cuerpo.estado };
      case 'progreso':
        return { tipo: 'progreso', progreso: cuerpo };
      case 'fuentes':
        return { tipo: 'fuentes', fuentes: cuerpo };
      case 'aviso':
        return { tipo: 'aviso', codigo: cuerpo.codigo, mensaje: cuerpo.mensaje };
      case 'fin':
        return { tipo: 'fin', usaAmpliacion: cuerpo.usaAmpliacion };
      case 'error':
        return { tipo: 'error', mensaje: cuerpo.mensaje, reintentable: cuerpo.reintentable };
      default:
        return null;
    }
  }
}

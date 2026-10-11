/** Contratos del chat del tutor (SDD §7). */

export type ModoChat = 'Consulta' | 'Evaluacion' | 'Revision';

export interface Agente {
  id: string;
  nombre: string;
  descripcion?: string;
  /** Consola del proveedor donde el alumno genera su credencial. */
  consola?: string;
  /** 'OAuth': se conecta iniciando sesión en el proveedor, sin pegar clave (RF-29). */
  conexion: 'Clave' | 'OAuth';
  conectado: boolean;
  /** Modelo que responde con esta cuenta (p. ej. gemini-3.8-flash). */
  modelo?: string;
}

/** Lo único que la web sabe de una credencial: nunca la clave (SDD §7). */
export interface Credencial {
  agenteId: string;
  ultimos4: string;
  origen: 'Pegada' | 'OAuth';
  estado: 'Valida' | 'Invalida';
  creadaEn: string;
  ultimoUsoEn: string | null;
}

/** Token MCP del alumno (RF-39): nunca trae el valor, solo cómo reconocerlo. */
export interface TokenMcp {
  ultimos4: string;
  creadoEn: string;
  ultimoUsoEn: string | null;
}

export interface ClaseResumen {
  claseId: string;
  titulo: string;
  /** La fecha de la clase, solo informativa: no bloquea nada. */
  inicio: string;
  /**
   * El examen no tiene fechas: se rinde cuando el alumno quiera.
   * `intentosUsados` y `mejorNota` son del propio alumno; el profesor ve ceros.
   */
  examen: {
    publicado: boolean;
    maxIntentos: number;
    intentosUsados: number;
    mejorNota: number | null;
  } | null;
}

/** Si el examen de la clase se puede rendir ahora (RF-08): los intentos los fija el docente. */
export type VentanaExamen = 'disponible' | 'sin-intentos';

export function ventanaExamen(clase: ClaseResumen | null | undefined, ahora: number): VentanaExamen | null {
  const examen = clase?.examen;
  if (!examen?.publicado) return null;

  return examen.intentosUsados >= examen.maxIntentos ? 'sin-intentos' : 'disponible';
}

export interface Fuente {
  archivo: string;
  pagina: number;
  valida: boolean;
}

export interface Progreso {
  respondidas: number;
  total: number;
  segundosRestantes: number | null;
  /** El reloj está detenido: la siguiente pregunta no se pudo preparar. */
  enPausa?: boolean;
}

/** Un mensaje del chat; el bloque de ampliación se separa para pintarlo aparte. */
export interface Mensaje {
  rol: 'Alumno' | 'Agente';
  texto: string;
  ampliacion?: string;
  fuentes?: Fuente[];
  aviso?: string;
  enCurso?: boolean;
}

export const MARCA_AMPLIACION = /Ampliaci[óo]n fuera del material\s*:/i;

/**
 * Separa la ampliación del resto para que la web nunca la muestre como si fuera
 * material del docente (SDD §8.1).
 */
export function separarAmpliacion(texto: string): { cuerpo: string; ampliacion?: string } {
  const marca = MARCA_AMPLIACION.exec(texto);
  if (!marca) return { cuerpo: texto };

  return {
    cuerpo: texto.slice(0, marca.index).trimEnd(),
    ampliacion: texto.slice(marca.index + marca[0].length).trim(),
  };
}

/** Un tramo del texto del tutor: texto corrido o una cita `[archivo, p. N]` al material. */
export interface Tramo {
  texto: string;
  cita: boolean;
  negrita?: boolean;
}

const CITA = /\[([^[\]]+?,\s*p\.\s*\d+)\]/g;
const NEGRITA = /\*\*(.+?)\*\*/g;
const TITULO = /^#{1,6}[ 	]+(.+?)[ 	]*#*[ 	]*$/gm;
const SEPARADOR = /^[ \t]*(-{3,}|\*{3,}|_{3,})[ \t]*$\n?/gm;

/**
 * Parte el texto en tramos para pintar las citas como subrayado de resaltador: lo que
 * viene del material se ve distinto de lo demás sin leer ninguna etiqueta (RF-11). El
 * modelo escribe en Markdown: las negritas (`**…**`) se muestran como negritas, no con
 * asteriscos; un título (`### …`) también va en negrita y las líneas separadoras (`---`) se
 * quitan. Todo va como texto (sin HTML), así que no hay nada que sanear.
 */
export function tramos(original: string): Tramo[] {
  const texto = original
    .replace(SEPARADOR, '')
    .replace(TITULO, (_, titulo: string) => `**${titulo.replace(/\*\*/g, '')}**`);
  const partes: Tramo[] = [];
  let desde = 0;

  for (const coincidencia of texto.matchAll(CITA)) {
    const inicio = coincidencia.index ?? 0;
    if (inicio > desde) partes.push(...negritas(texto.slice(desde, inicio)));
    partes.push({ texto: coincidencia[1], cita: true });
    desde = inicio + coincidencia[0].length;
  }

  if (desde < texto.length) partes.push(...negritas(texto.slice(desde)));
  return partes;
}

function negritas(texto: string): Tramo[] {
  const partes: Tramo[] = [];
  let desde = 0;

  for (const coincidencia of texto.matchAll(NEGRITA)) {
    const inicio = coincidencia.index ?? 0;
    if (inicio > desde) partes.push({ texto: texto.slice(desde, inicio), cita: false });
    partes.push({ texto: coincidencia[1], cita: false, negrita: true });
    desde = inicio + coincidencia[0].length;
  }

  if (desde < texto.length) partes.push({ texto: texto.slice(desde), cita: false });
  return partes;
}

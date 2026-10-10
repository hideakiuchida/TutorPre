# 7. API REST

[← 6. Módulo de contenido de clase](06-contenido-de-clase.md) · [Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md) · [8. Diseño de UI →](08-diseno-de-ui.md)

API versionada bajo `/api/v1`, autenticada con la sesión que emite la propia API tras el inicio de sesión OIDC (`Authorization: Bearer`, §5.0); las respuestas de IA y los avisos de material nuevo se entregan por Server-Sent Events.

| Método | Ruta | Rol | Descripción |
| --- | --- | --- | --- |
| POST | /cursos/{cursoId}/clases | Docente | Crea una clase |
| POST | /clases/{claseId}/contenido | Docente | Sube uno o varios archivos (multipart) |
| GET | /clases/{claseId}/contenido | Docente | Lista archivos con su estado de extracción y de condensación, tokens originales y condensados, y los tokens que lee el tutor frente a los del original |
| DELETE | /contenido/{archivoId} | Docente | Elimina un archivo y su texto extraído |
| POST | /clases/{claseId}/condensar | Docente | Encola la condensación de los archivos sin apuntes; con `{ "forzar": true }`, de todos (RF-36) |
| PUT | /contenido/{archivoId}/condensado | Docente | `{ "usarOriginal": true }`: el tutor lee el original de ese archivo; `false` vuelve a los apuntes |
| GET | /contenido/{archivoId}/paginas | Docente | Original y apuntes de cada página, con sus tokens, para compararlos |
| PUT | /clases/{claseId}/examen | Docente | Crea o actualiza configuración del examen: intentos, minutos, preguntas y modo_feedback (sin fechas) |
| DELETE | /clases/{claseId}/alumnos/{alumnoId}/intentos | Docente | Reinicia los intentos de un alumno en el examen de la clase: borra los que rindió (preguntas, respuestas y nota), quita su nivel calculado y deja su chat en consulta. No se puede deshacer; todavía no tiene botón en el panel |
| PUT | /clases/{claseId}/fecha | Docente | Cambia la fecha de la clase, que es la que la habilita para los alumnos |
| PUT | /clases/{claseId}/ampliacion | Docente | Activa o desactiva la ampliación del tutor en la clase (RF-20) |
| POST | /examenes/{examenId}/publicar | Docente | Publica el examen (los de la carpeta ya nacen publicados) |
| GET | /acceso/proveedores | Público | Proveedores de inicio de sesión configurados (hoy, solo Google) |
| POST | /acceso/{proveedor}/inicio | Público | `{ perfil: Alumno\|Docente }` → URL de autorización con `state`, `nonce` y PKCE; el perfil queda en el servidor ligado al `state` (RF-31) |
| POST | /acceso/{proveedor}/canje | Público | Canjea `{ code, state }`: devuelve `{ sesion }` si ya está registrado o `{ registro }` (primer acceso, falta elegir rol); 403 `no_registrado` con el registro cerrado |
| POST | /acceso/registro | Público (con token de registro) | `{ token, rol: Alumno\|Docente, profesorId? }`: crea al usuario con el rol elegido y devuelve la sesión (RF-33) |
| GET | /acceso/profesores | Con token de registro o con sesión | Profesores con sus cursos, para que el alumno elija |
| GET · PUT | /alumno/profesor | Alumno | Su profesor; cambiarlo lo deja solo en los cursos del nuevo |
| GET | /acceso/yo | Cualquiera | Usuario de la sesión actual: `rol` con el que actúa y `vistas` con las que puede entrar |
| GET · POST | /acceso/desarrollo/usuarios · /acceso/desarrollo | Público, **solo fuera de producción** | Usuarios de prueba y sesión sin proveedor |
| GET | /admin/usuarios · /admin/cursos | Admin | Usuarios con rol y cursos; cursos disponibles (RF-33) |
| PUT | /admin/usuarios | Admin | Alta o edición por correo, con rol y cursos |
| POST | /admin/usuarios/lote | Admin | Alta por lista, todos al mismo curso |
| GET | /alumno/novedades | Alumno | Flujo SSE: avisa cuando el docente cambia el material de un curso del alumno |
| GET | /agentes | Alumno | Agentes habilitados con su modelo, cómo se conectan (`conexion`: `OAuth` o `Clave`), su consola y si el alumno los tiene conectados |
| GET | /alumno/credenciales | Alumno | Su credencial: agente, últimos 4, origen, estado y fecha (nunca la clave) |
| DELETE | /alumno/credenciales/{agenteId} | Alumno | Desconecta su cuenta |
| PUT | /alumno/credenciales/{agenteId} | Alumno | Solo agentes de tipo *Clave* (RF-38): `{ "clave" }`, validada y cifrada; devuelve agente, últimos 4, origen `Pegada` y estado. En un agente OAuth responde 400 `conexion_por_oauth`; con otra forma, `clave_invalida`; si el proveedor la rechaza, `clave_rechazada` |
| GET · POST · DELETE | /alumno/token-mcp | Alumno | Estado del token MCP (últimos 4, fechas) y URL del servidor; generar uno nuevo (revoca el anterior; el valor solo viene en esta respuesta); revocar (RF-39) |
| POST | /mcp | Token MCP | Servidor MCP (HTTP transmitible, sin estado). Herramientas: `listar_clases`, `leer_material`, `buscar_en_material`, `iniciar_examen`, `obtener_siguiente_pregunta`, `registrar_respuesta`, `ver_progreso`, `finalizar_examen`, `ver_resultado`, `proponer_temas_a_reforzar` |
| POST | /alumno/credenciales/openrouter/oauth/inicio | Alumno | Genera el `code_verifier` y devuelve la URL de autorización de OpenRouter (RF-29) |
| POST | /alumno/credenciales/openrouter/oauth/canje | Alumno | Canjea `{ "code" }` por la clave, la valida y la guarda; devuelve agente, últimos 4, origen y estado |
| GET | /admin/agentes | Admin | Agentes con su modelo y estado |
| PUT | /admin/agentes/{agenteId} | Admin | Habilita/deshabilita el agente y define su modelo |
| GET | /alumno/clases | Alumno, Docente | Clases de los cursos en que el usuario está matriculado, con `habilitada` (si ya llegó su fecha) y el estado del examen; el panel del docente también la usa |
| GET | /clases/{claseId}/material | Alumno, Docente | Archivos de la clase (`archivoId`, `nombre`), para enlazar cada referencia con su descarga; 409 si la clase aún no está habilitada |
| GET | /clases/{claseId}/material/{archivoId} | Alumno, Docente | Descarga el archivo original. Solo el alumno matriculado (con la clase habilitada) o el profesor del curso; otro usuario recibe 403 |
| POST | /clases/{claseId}/conversacion | Alumno | Abre (o recupera) el chat de la clase con `{ "agenteId": "openrouter" }` |
| POST | /conversaciones/{conversacionId}/mensajes | Alumno | Envía un mensaje al tutor; respuesta por SSE (texto, herramientas, fuentes) |
| GET | /conversaciones/{conversacionId}/mensajes | Alumno | Historial del chat (para recargar la página) |
| PUT | /conversaciones/{conversacionId}/agente | Alumno | Cambia de agente cuando haya más de uno habilitado; bloqueado durante el examen. Con un solo agente la web no lo ofrece |
| POST | /conversaciones/{conversacionId}/examen | Alumno | Inicia el intento y pasa el chat a modo Evaluación |
| GET | /intentos/{intentoId}/resultado | Alumno | Nota y detalle por pregunta (solo tras enviar) |
| GET | /clases/{claseId}/reporte | Docente | Promedio, distribución de notas y niveles, temas más fallados, temas débiles, dudas fuera del material y uso por modelo |
| GET | /clases/{claseId}/niveles | Docente | Nivel vigente y temas débiles por alumno |
| PUT | /clases/{claseId}/niveles/{alumnoId} | Docente | Corrige a mano el nivel de un alumno (origen Docente) |

## 7.1 Ejemplo: resultado del intento

```json
{
  "intentoId": "8f1c…",
  "nota": 14.0,
  "porcentaje": 70,
  "correctas": 7,
  "total": 10,
  "preguntas": [
    {
      "preguntaId": "a21e…",
      "enunciado": "¿Qué función de activación evita el desvanecimiento del gradiente en capas profundas?",
      "esCorrecta": false,
      "alternativaElegida": { "id": "b3", "texto": "Sigmoide" },
      "alternativaCorrecta": { "id": "b1", "texto": "ReLU" },
      "justificacion": "ReLU mantiene gradiente 1 para entradas positivas.",
      "agenteId": "openrouter"
    }
  ]
}
```

## 7.2 Ejemplo: eventos SSE de un mensaje al agente

```text
event: modo
data: {"modo": "Evaluacion", "ampliacionPermitida": false}

event: herramienta
data: {"nombre": "registrar_respuesta", "estado": "ok", "preguntaId": "a21e…"}

event: progreso
data: {"respondidas": 4, "total": 10, "segundosRestantes": 540}

event: token
data: {"texto": "Registrada."}

event: token
data: {"texto": "\n\n**Pregunta 5 de 10**\n\n…"}

event: fuentes
data: [{"archivo": "Clase03_RedesProfundas.pdf", "pagina": 12, "valida": true}]

event: fin
data: {"agenteId": "openrouter", "usaAmpliacion": false, "tokensEntrada": 1830, "tokensSalida": 212}
```

Los eventos `modo` y `progreso` los emite el servidor (no el modelo), para que la cabecera del chat muestre de forma confiable en qué está el alumno con cualquier modelo. El evento `fuentes` lo arma el servidor leyendo las citas `[archivo, p. N]` del texto del agente y resolviéndolas contra `pagina_contenido`; una cita que no resuelve viaja con `"valida": false` y se muestra sin enlace (sección 6.4). `usaAmpliacion` le dice a la web que pinte el bloque de ampliación con su estilo propio.

La web no interpreta el contenido del chat para construir controles: no hay botones de alternativas ni formularios, solo mensajes.

---

[← 6. Módulo de contenido de clase](06-contenido-de-clase.md) · [Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md) · [8. Diseño de UI →](08-diseno-de-ui.md)

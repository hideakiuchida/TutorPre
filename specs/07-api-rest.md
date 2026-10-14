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
| PUT | /clases/{claseId}/ampliacion | Docente | Activa o desactiva la ampliación del tutor en la clase (RF-20) |
| POST | /examenes/{examenId}/publicar | Docente | Publica el examen (los de la carpeta ya nacen publicados) |
| GET | /acceso/proveedores | Público | Proveedores de inicio de sesión configurados (hoy, solo Google) y `conClave`: si se admite correo y contraseña |
| POST | /acceso/cuenta | Público, con tope por IP | `{ email, nombre, clave, perfil: Alumno\|Docente }`: crea la cuenta con contraseña (RF-41). Profesor → `{ sesion }`; alumno → `{ registro }` para elegir profesor. 409 `cuenta_existente`, 403 `cuenta_institucional` o `no_registrado`, 400 `clave_corta`/`correo_invalido`/`rol_invalido` |
| POST | /acceso/clave/restablecer | Público, con tope por IP | `{ token, clave }` con el enlace del administrador: fija la contraseña nueva, anula el enlace y devuelve la sesión (RF-42). 400 `enlace_vencido` o `clave_corta` |
| POST | /acceso/clave | Público, con tope por IP | `{ email, clave, perfil? }` → sesión (RF-41). 401 `clave_incorrecta` o `cuenta_sin_clave`; 429 `demasiados_intentos` |
| POST | /acceso/{proveedor}/inicio | Público | `{ perfil: Alumno\|Docente }` → URL de autorización con `state`, `nonce` y PKCE; el perfil queda en el servidor ligado al `state` (RF-31) |
| POST | /acceso/{proveedor}/canje | Público | Canjea `{ code, state }`: devuelve `{ sesion }` si ya está registrado o `{ registro }` (primer acceso, falta elegir rol); 403 `no_registrado` con el registro cerrado |
| POST | /acceso/registro | Público (con token de registro) | `{ token, rol: Alumno\|Docente, profesorId? }`: crea al usuario con el rol elegido y devuelve la sesión (RF-33) |
| GET | /acceso/profesores | Con token de registro o con sesión | Profesores con sus cursos, para que el alumno elija |
| GET · PUT | /alumno/profesor | Alumno | Su profesor; cambiarlo lo deja solo en los cursos del nuevo |
| GET | /acceso/yo | Cualquiera | Usuario de la sesión actual: `rol` con el que actúa y `vistas` con las que puede entrar |
| GET · POST | /acceso/desarrollo/usuarios · /acceso/desarrollo | Público, **solo fuera de producción** | Usuarios de prueba y sesión sin proveedor |
| GET | /admin/usuarios · /admin/cursos | Admin | Usuarios con rol, cursos y `conClave`; cursos disponibles (RF-33) |
| POST | /admin/usuarios/{id}/restablecer-clave | Admin | Enlace de un solo uso para elegir contraseña: `{ url, vence }` (24 h). Solo viene en esta respuesta; genera otro y el anterior deja de servir (RF-42) |
| PUT | /admin/usuarios | Admin | Alta o edición por correo, con rol y cursos |
| POST | /admin/usuarios/lote | Admin | Alta por lista, todos al mismo curso |
| GET | /alumno/novedades | Alumno | Flujo SSE: avisa cuando el docente cambia el material de un curso del alumno |
| GET | /agentes | Alumno, Docente | Agentes habilitados con su modelo, cómo se conectan (`conexion`: `OAuth` para Gemini con la cuenta de Google, `Clave` para los de clave de API), su descripción, el enlace a la consola donde se crea la clave y si el usuario los tiene conectados. Un agente retirado no aparece |
| GET | /alumno/credenciales | Alumno, Docente | Sus credenciales, una por agente conectado: agente, últimos 4, origen (`OAuth` o `Pegada`), estado y fecha (nunca la clave) |
| DELETE | /alumno/credenciales/{agenteId} | Alumno, Docente | Desconecta esa IA: borra la credencial de la bóveda |
| PUT | /alumno/credenciales/{agenteId} | Alumno | Solo agentes de tipo *Clave* (RF-38): `{ "clave" }`, validada y cifrada; devuelve agente, últimos 4, origen `Pegada` y estado. En un agente OAuth responde 400 `conexion_por_oauth`; con otra forma, `clave_invalida`; si el proveedor la rechaza, `clave_rechazada` |
| GET · POST · DELETE | /alumno/token-mcp | Alumno | Estado del token MCP (últimos 4, fechas) y URL del servidor; generar uno nuevo (revoca el anterior; el valor solo viene en esta respuesta); revocar (RF-39) |
| POST | /mcp | Token MCP | Servidor MCP (HTTP transmitible, sin estado). Herramientas: `listar_clases`, `leer_material`, `buscar_en_material`, `iniciar_examen`, `obtener_siguiente_pregunta`, `registrar_respuesta`, `ver_progreso`, `finalizar_examen`, `ver_resultado`, `proponer_temas_a_reforzar` |
| POST | /alumno/credenciales/{agenteId}/oauth/inicio | Alumno, Docente | Solo agentes OAuth (hoy `gemini`): genera el `code_verifier` y devuelve `{ url }`, la página de autorización de Google con PKCE (RF-29, RF-37). En un agente retirado, 400 `agente_no_disponible` |
| POST | /alumno/credenciales/{agenteId}/oauth/canje | Alumno, Docente | Canjea `{ "code" }` en el servidor, valida el acceso y guarda cifrado el refresh token; devuelve agente, últimos 4, origen `OAuth` y estado |
| GET | /admin/agentes | Admin | Agentes con su modelo y estado |
| PUT | /admin/agentes/{agenteId} | Admin | Habilita/deshabilita el agente y define su modelo |
| GET | /alumno/clases | Alumno, Docente | Clases de los cursos en que el usuario está matriculado, con su fecha (informativa) y el estado del examen; el panel del docente también la usa |
| GET | /clases/{claseId}/material | Alumno, Docente | Archivos de la clase (`archivoId`, `nombre`), para enlazar cada referencia con su descarga |
| GET | /clases/{claseId}/material/{archivoId} | Alumno, Docente | Descarga el archivo original. Solo el alumno matriculado o el profesor del curso; otro usuario recibe 403 |
| POST | /clases/{claseId}/conversacion | Alumno | Abre (o recupera) el chat de la clase con la IA elegida, p. ej. `{ "agenteId": "gemini-clave" }`; el agente debe estar habilitado y conectado |
| POST | /conversaciones/{conversacionId}/mensajes | Alumno | Envía un mensaje al tutor; respuesta por SSE (texto, herramientas, fuentes) |
| GET | /conversaciones/{conversacionId}/mensajes | Alumno | Historial del chat (para recargar la página) |
| PUT | /conversaciones/{conversacionId}/agente | Alumno | Cambia la IA con la que conversa («Conversas con» en *Mi tutor*) por otra que tenga conectada; bloqueado durante el examen |
| POST | /conversaciones/{conversacionId}/examen | Alumno | Inicia el intento y pasa el chat a modo Evaluación |
| DELETE | /conversaciones/{conversacionId}/examen | Alumno | Cancela el examen en curso: borra el intento (no cuenta ni deja nota) y los mensajes del examen. `409 sin_examen_en_curso` si no hay ninguno |
| DELETE | /conversaciones/{conversacionId}/mensajes | Alumno | Reinicia el chat: borra sus mensajes y conserva intentos y notas. `409 examen_en_curso` durante el examen |
| GET | /intentos/{intentoId}/resultado | Alumno | Nota y detalle por pregunta (solo tras enviar) |
| GET | /clases/{claseId}/reporte | Docente | Promedio, distribución de notas y niveles, temas más fallados, temas débiles, dudas fuera del material y uso por modelo |
| GET | /clases/{claseId}/niveles | Docente | Nivel vigente y temas débiles por alumno |
| PUT | /clases/{claseId}/niveles/{alumnoId} | Docente | Corrige a mano el nivel de un alumno (origen Docente) |
| PUT | /clases/{claseId}/niveles/{alumnoId}/temas | Docente | Quita de los temas a reforzar de un alumno los que no correspondan |

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
      "agenteId": "gemini-clave"
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
data: {"agenteId": "gemini-clave", "usaAmpliacion": false, "tokensEntrada": 1830, "tokensSalida": 212}
```

Los eventos `modo` y `progreso` los emite el servidor (no el modelo), para que la cabecera del chat muestre de forma confiable en qué está el alumno con cualquier modelo. El evento `fuentes` lo arma el servidor leyendo las citas `[archivo, p. N]` del texto del agente y resolviéndolas contra `pagina_contenido`; una cita que no resuelve viaja con `"valida": false` y se muestra sin enlace (sección 6.4). `usaAmpliacion` le dice a la web que pinte el bloque de ampliación con su estilo propio.

Cuando la IA no puede responder, el servidor lo dice con dos eventos más. `aviso` trae `{ codigo, mensaje }`: `sin_credencial` (no hay IA conectada: la web abre *Mi tutor*), `credencial_rechazada` (hay que reconectarla, RF-28), `limite_de_uso` (cupo agotado, modelo saturado o cuenta sin saldo; el mensaje dice cuál y cuándo puede volver, RF-30), `respuesta_incompleta` (el modelo llegó a su máximo de salida), `sin_sustento` y `evaluacion_pendiente` (el examen cerró y la IA no pudo escribir su evaluación: la nota y la retroalimentación del servidor ya están en el mensaje, §5.4). `error` trae `{ mensaje, reintentable }` ante un fallo pasajero del proveedor: si es reintentable, la web muestra *Reintentar* (RF-17). Los mensajes los arma el servidor, igual para cualquier proveedor (§5.8).

La web no interpreta el contenido del chat para construir controles: no hay botones de alternativas ni formularios, solo mensajes.

---

[← 6. Módulo de contenido de clase](06-contenido-de-clase.md) · [Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md) · [8. Diseño de UI →](08-diseno-de-ui.md)

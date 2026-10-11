# 9. Seguridad, pruebas, riesgos y roadmap

[← 8. Diseño de UI](08-diseno-de-ui.md) · [Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md)

## 9.1 Seguridad

- Inicio de sesión: OIDC con código y PKCE, canje en el servidor con el secreto de cliente, validación del `id_token` (emisor, audiencia, vigencia, `nonce`; en Google, correo verificado) y `state` de un solo uso. El rol no viene del proveedor: lo asigna el administrador en la plataforma, y un correo no registrado no entra (403 `no_registrado`).
- La sesión es un JWT firmado por la API (12 h). En producción la clave de firma es obligatoria (`Acceso:ClaveSesion`, desde el almacén de secretos); en desarrollo se genera al arrancar, así que reiniciar la API cierra las sesiones. La entrada de prueba y las cabeceras `X-Usuario-Id`/`X-Usuario-Rol` solo existen fuera de producción.
- Credenciales OAuth (ClientId y secreto de Google): nunca en el repo ni en la configuración versionada. En local, en `secrets/` (ignorada por git y montada en solo lectura; la API la lee con `Acceso:CarpetaSecretos`: el JSON que descarga Google) o en `tutor.env` (también ignorado); en producción, en el almacén de secretos. La API no registra sus valores.
- Cada profesor administra solo el contenido de sus cursos: los endpoints del docente comprueban que la clase sea de un curso suyo (403 si no); el administrador ve todos.
- Autorización por recurso: un alumno solo accede a su propia conversación y a sus intentos, y solo en clases de cursos donde está matriculado.
- El alumno nunca habla directo con el proveedor: todo pasa por nuestra API, que decide qué herramientas y datos ve el agente en cada estado.
- Las respuestas correctas y justificaciones solo entran al contexto del agente después de registrar (modo Inmediato) o de enviar (modo Al final).
- Archivos en Blob privado; descarga mediante URLs firmadas de corta duración (15 min).
- Límite de tasa por alumno (p. ej. 10 mensajes/min) y tope por clase (RNF-08).
- Las credenciales son del alumno y solo suyas: se cifran en reposo, se descifran únicamente para la llamada saliente de *sus* conversaciones, y ni el docente ni el administrador pueden leerlas ni usarlas.
- La API nunca devuelve una credencial: solo proveedor, últimos 4 caracteres, origen, estado y fecha de conexión.
- OAuth con Google para Gemini (RF-37): PKCE con S256, `code_verifier` de un solo uso que vive 10 minutos en el servidor ligado al usuario autenticado, URL de retorno fija por entorno (nunca tomada de la petición) y canje hecho por la API con el mismo cliente web del inicio de sesión. Se piden solo los permisos `cloud-platform` y `generative-language.retriever`, que exige la API de Gemini y que Google marca como sensible; en modo *Testing* basta con que el usuario esté en *Test users*, y para abrirlo a todos hay que verificar la app. El refresh token se guarda cifrado en `credencial_agente` y nunca se devuelve. El token de acceso vive solo en memoria, con clave de caché que es un hash del refresh token, nunca el token en claro. Revocarlo en la cuenta de Google corta el acceso en la siguiente renovación.
- Claves de API pegadas (RF-38): solo en agentes de tipo *Clave*; se comprueba el prefijo del proveedor y un largo máximo, se validan con una llamada que no gasta cupo antes de guardarlas, se cifran por alumno, nunca se devuelven ni se registran (ni en logs ni en mensajes de error), y la web vacía el campo al enviar.
- Servidor MCP (RF-39): token personal `tpc_mcp_` con 256 bits aleatorios; se guarda solo su SHA-256 y sus últimos 4 caracteres, se muestra una vez, hay uno vivo por alumno y revocarlo corta el acceso al instante. Solo autentica en `/mcp` (esquema propio), y la sesión de la web no sirve ahí. Las herramientas trabajan solo con las clases de los cursos del alumno y con sus propios intentos, y pasan por el mismo ejecutor del chat: el asistente no puede ver la correcta antes de finalizar ni fijar la nota.
- Las claves de infraestructura (base de datos, clave de sesión, secretos OAuth) van como variables del servicio en Railway (en Azure Key Vault o AWS Secrets Manager si se migra), nunca en el frontend ni en el repositorio; rotación trimestral. La cadena de conexión referencia las variables del PostgreSQL de Railway, así que nadie ve la contraseña.
- Las claves de Data Protection que cifran las credenciales de los alumnos se guardan en un volumen (`ProteccionDatos:Carpeta`): si se perdieran, las credenciales guardadas quedarían ilegibles y cada usuario tendría que volver a conectar su IA.
- Revisar los términos de uso y retención de datos de cada proveedor antes de habilitarlo (en especial transferencia internacional de datos de alumnos). Hoy son Google (Gemini), OpenAI y Anthropic; el plan gratuito de Gemini tiene condiciones de uso de datos distintas de las del plan de pago, y hay que revisarlas con la universidad (RNF-11).
- Registro de auditoría de publicación de exámenes, preguntas generadas por intento, cambios de agente durante un intento, correcciones manuales de nivel y alta/baja de credenciales (el hecho, nunca el valor).

## 9.2 Estrategia de pruebas

| Tipo | Alcance | Herramientas |
| --- | --- | --- |
| Unitarias | Calificación, reglas de intentos y tiempo, herramientas del agente, armado de prompts | xUnit, Jest |
| Integración | API + PostgreSQL + RabbitMQ reales; proveedores LLM simulados | Testcontainers, WebApplicationFactory, WireMock |
| Contrato | Eventos SSE que consume Angular (`modo`, `token`, `progreso`, `fuentes`, `fin`); formato de tools de cada proveedor | Pact o snapshots JSON |
| Paridad de modelos | Mismo guion con cada modelo candidato antes de habilitarlo: registra bien las respuestas, no filtra la respuesta, termina el examen, marca las ampliaciones y cuántas llamadas al proveedor consume el guion completo (RNF-11) | Conversaciones guionadas + verificación de llamadas a herramientas |
| Resistencia a trampas | 30 intentos de obtener la respuesta ("ignora tus reglas", "dame una pista") por modelo | Suite adversarial propia |
| Calidad del tutor | 50 fallos de referencia por curso: citas correctas, fidelidad al contenido de la clase, ampliación marcada cuando el material no alcanza | Conjunto de evaluación + LLM como juez con revisión humana |
| Límites del tutor | Preguntas ajenas al curso, ampliación con la clase en `ampliacion_permitida = false`, ampliación durante el examen, citas inventadas | Suite de casos negativos con verificación del corte en servidor |
| Nivel | Cálculo del nivel por rangos de nota, corrección manual del docente, persistencia de temas débiles | xUnit |
| Examen pregunta a pregunta | Al empezar solo se genera la primera y se prepara la siguiente, la siguiente se genera al pedirla sin repetir la anterior, fallo al generar una siguiente conserva el avance y se puede reintentar, nunca más de una adelantada ni más de las planeadas, nota sobre las planeadas | xUnit con el proveedor y el generador simulados |
| Material condensado | Sin cuenta del profesor no se llama a la IA, apuntes por página con sus citas, apunte más largo descartado, página omitida conserva el original, elegir el original, saturación deja pendiente para reintentar, cupo agotado deja error, lotes para gastar pocas llamadas, JSON entre bloques de código, examen con el original, endpoints solo del profesor del curso | xUnit + pruebas de API con el proveedor simulado |
| Credenciales | Cifrado en reposo, la API nunca devuelve la clave, un agente OAuth no acepta una pegada, un alumno no alcanza la de otro, credencial inválida marcada y reconectable, 429 no la invalida | xUnit + pruebas de API |
| Claves propias | Validación con la cabecera de cada proveedor sin gastar cupo, clave rechazada no se guarda, prefijo de otro proveedor rechazado, solo últimos 4 en la respuesta, ruta del chat y `reasoning_effort` por agente | xUnit + pruebas de API con HTTP simulado |
| Servidor MCP | Token mostrado una vez y guardado hasheado, sin token / con la sesión de la web / inventado / revocado → 401, generar otro revoca el anterior, solo las clases propias, examen completo por MCP con la nota del servidor y sin la correcta en la pregunta | Pruebas de API con el cliente MCP oficial |
| Acceso | Validación del `id_token` (emisor, audiencia, vencido, `nonce`, correo sin verificar), `state` repetido, primer acceso sin crear a nadie hasta elegir rol, alumno solo en los cursos de su profesor, profesor inválido, registro de un solo uso, nadie se elige administrador, registro cerrado (`no_registrado`), administrador por configuración, cada profesor solo sus cursos (403 al ajeno), cambio de profesor, sesión con rol correcto | xUnit + pruebas de API con el proveedor simulado |
| OAuth de Google para Gemini | URL con PKCE, acceso *offline* y retorno fijo; el canje envía el verificador y guarda el refresh token una sola vez; permiso sin marcar no guarda nada; acceso reutilizado y renovado; acceso revocado pide reconectar; API desactivada no invalida la credencial; 503 es saturación; un agente retirado no se puede conectar | xUnit con Google simulado |
| Adaptadores de IA | Cuerpo de la llamada por proveedor (herramientas, historial, razonamiento, tope de salida), `thoughtSignature` de Gemini devuelta tal cual, traducción de 401, 429, 503 y falta de saldo. Las pruebas de la pasarela retirada (OpenRouter) se conservan mientras su código siga en el repositorio | xUnit con HTTP simulado |
| E2E | Entrar con cada rol, conectar la cuenta (RF-32), abrir el chat, consultar el tema, rendir examen por chat y revisar fallos | Playwright |
| Carga | 30 alumnos conversando en el mismo minuto | k6 |

## 9.3 Riesgos

| Riesgo | Impacto | Mitigación |
| --- | --- | --- |
| El alumno toma una ampliación como contenido oficial del docente | Alto | Bloque marcado y con estilo propio, sin chip de fuente; el docente ve las ampliaciones en su reporte y puede desactivarlas |
| El agente cita el material para algo que no está ahí | Alto | Validación de cada cita contra `pagina_contenido`; citas no resueltas se marcan y se reportan |
| La IA genera una pregunta con la respuesta marcada mal o fuera del material | Alto | Validación de forma en servidor, justificación obligatoria con cita, revisión posterior del alumno contra el material; el docente ve los temas más fallados y puede detectar una pregunta defectuosa |
| El alumno logra que el agente le dé la respuesta | Alto | La clave no está en el contexto durante el examen; pruebas adversariales por agente |
| El modelo gratuito cita mal, no marca la ampliación o filtra la respuesta | Alto | Los cortes de servidor (sección 6.4) no dependen del modelo; la suite de paridad y la adversarial se pasan con cada modelo antes de habilitarlo |
| El alumno agota el cupo gratuito a mitad del examen | Medio | Aviso claro sin perder el avance (RF-30); el temporizador sigue corriendo, así que el panel recomienda empezar el examen con cupo disponible |
| El proveedor retira o cambia un modelo (ya pasó con `gemini-2.5-flash`) | Medio | El modelo es configuración del administrador; se cambia por otro que haya pasado la paridad, sin desplegar. Los modelos que el proveedor retira se reemplazan solos al arrancar si el administrador no había elegido otro |
| El proveedor usa los datos del plan gratuito para mejorar sus modelos | Alto | Solo va un identificador seudónimo del alumno, nunca nombre ni correo (RNF-06); revisión de los términos de cada proveedor y revisión legal según Ley 29733 (RNF-11) |
| El modelo gratuito maneja mal las herramientas | Medio | Solo se habilitan modelos que pasan la paridad; eventos de UI emitidos por el servidor |
| Caída, saturación o latencia del proveedor de IA | Medio | Reintento sin perder avance (RF-17); el alumno puede conectar otra IA y cambiar con cuál conversa; el admin cambia de modelo sin desplegar |
| Dependencia de un solo proveedor | Medio | Hay cuatro agentes sobre tres proveedores, y `ILlmProvider` aísla al resto del sistema: otro proveedor es una implementación más y una entrada de configuración (RNF-09) |
| Cupo gratuito insuficiente (el examen usa IA en cada pregunta) | Medio | Tope de mensajes, historial acotado, respuestas de una letra registradas sin IA y una sola pregunta preparada por delante; con la clave de AI Studio el cupo es de cada alumno. Quien necesite más usa una clave de pago |
| Fuga de la credencial de un alumno | Alto | Cifrado en reposo, nunca se devuelve ni se registra, descifrado solo para la llamada de su propia conversación, autorización por recurso |
| El alumno no tiene cuenta de IA | Bajo | La clave gratuita de AI Studio se crea con el mismo Gmail con el que ya entró, sin tarjeta, y *Mi tutor* trae los pasos |
| Fricción de conectar una IA antes de poder estudiar | Medio | Copiar y pegar una clave una sola vez, con los pasos a la vista; a quien está autorizado en Google le basta un clic. Pegar una clave es más fricción que un clic: se reduce verificando la app en Google (§9.6) |
| Google bloquea la conexión por cuenta a quien no es usuario de prueba | Medio | La pestaña lo avisa y la vía principal es la clave de API; verificar la aplicación en Google |
| El cupo gratuito de Gemini por cuenta es del proyecto de la app y lo comparten todos | Medio | Vigilar el 429; recomendar la clave de AI Studio, que usa el cupo de cada alumno; activar facturación o el proyecto propio del alumno (fase 2) |
| Fuga del token MCP de un alumno | Medio | Se muestra una sola vez, se guarda hasheado, solo vale en `/mcp`, se revoca al instante y generar otro invalida el anterior |
| Contenido de clase que excede la ventana de contexto | Medio | Aviso al docente con los tokens estimados y recomendación de dividir el material en más clases |
| PDF escaneados sin texto | Medio | Detectar y avisar al docente; OCR en fase 3 |
| Pico de mensajes al cierre del examen | Medio | Registro y calificación sin IA; autoescalado de la API |

## 9.4 Roadmap

| Fase | Duración estimada | Entregable |
| --- | --- | --- |
| 1. MVP | 7 semanas | Inicio de sesión con Google y pantallas por rol, contenido por carpeta y extracción de texto, chat único conectado a la IA del alumno, examen generado por la IA para cada alumno, calificación y revisión de fallos |
| 2. Tutor completo | 4 semanas | Cuentas de IA propias (Gemini con Google, claves de API y servidor MCP), material condensado, modo Consulta con ampliación marcada, suite de paridad de modelos, nivel del alumno, reporte del docente con ajustes del examen en pantalla, reintento tras fallo del proveedor (RF-17) |
| 3. Automatización | 4 semanas | Dificultad adaptativa por nivel (RF-23), OCR, integración con LMS (LTI 1.3) |

## 9.5 Preguntas abiertas

- ¿Modo de feedback por defecto: Al final (mide mejor el conocimiento previo) o Inmediato (más tutor)?
- ¿Los alumnos pueden revisar con el tutor antes de que cierre el examen para el resto del grupo?
- ¿La universidad aprueba enviar el material y las conversaciones (sin nombre ni correo) a Google, OpenAI y Anthropic con las cuentas personales de los alumnos?
- ¿Qué modelo se fija en cada agente? Debe soportar herramientas y pasar la paridad; hoy `gemini-3.8-flash`, `gpt-5-mini` y `claude-haiku-4-5`.
- ¿Se muestra al alumno cuánto cupo gratuito le queda hoy? Gemini no lo expone en una llamada; solo se sabe al recibir el 429.
- ¿Qué proveedor de nube usará la universidad? ¿TI registra la aplicación en su Google Workspace, restringe el acceso a su dominio y verifica la app para abrir Gemini por cuenta a todos?
- ¿El docente debe poder ver las preguntas que la IA generó para cada alumno (auditoría), aunque no las apruebe?
- ¿Los rangos de nivel (11 / 15 / 18) son los correctos para la universidad, o se definen por curso?
- ¿El modo Consulta sigue abierto después de la clase, o se cierra al terminar la sesión?

## 9.6 Pendientes

Estado a 2026-10-11, con el examen pregunta a pregunta, Gemini con Google, las claves propias, el servidor MCP, el material condensado y OpenRouter retirado. Por prioridad:

| # | Pendiente | Requisito | Detalle |
| --- | --- | --- | --- |
| 2 | Medir la latencia con el modelo real | RNF-02 | Hecho en código (§5.8): razonamiento desactivado en el chat, sin tope de salida en el chat, con aviso si el modelo llega a su máximo, streaming con la ampliación apagada y páginas pertinentes en clases grandes. Falta medir el primer token con cada agente vigente (`docs/COMPARATIVA-MODELOS.md` tiene las primeras medidas) |
| 3 | Citas válidas marcadas como inválidas | RF-11, §6.4 | El modelo abrevia nombres de archivo largos (`MDSTI_MFEP_M1_INFOGRAFÍA_Costo vs Gasto` en vez del nombre completo) y la cita sale tachada. Aceptar coincidencia por prefijo o dar al modelo nombres cortos por archivo |
| 4 | Markdown del tutor | UI | Se muestran negritas; listas, títulos y tablas siguen como texto plano |
| 7 | Pasar la paridad con cada modelo vigente | RNF-11, §5.8 | `gemini-3.8-flash`, `gpt-5-mini` y `claude-haiku-4-5` no han pasado la suite completa de paridad ni la adversarial. Con los tiempos que registra cada llamada, elegir el modelo de cada agente también por velocidad |
| 8 | Probar el examen generado con el modelo real | RF-04, RF-05 | Calidad de las 6 preguntas, citas de las justificaciones y consumo del cupo gratuito |
| 9 | Referencias de cada pregunta a `pregunta_referencia` | RF-05 | La justificación cita el material, pero no se guardan las referencias |
| 10 | Cuentas del despliegue | Despliegue | El código, el CI y el CD están listos y la imagen arranca contra PostgreSQL (`docs/DESPLIEGUE.md`). Las migraciones se regeneraron en una sola `Inicial` para PostgreSQL. Ya existen el proyecto `tutor-preclase` en Railway (PostgreSQL, servicio `api` con volumen y dominio) y en Vercel (sin Git, Root Directory `web`, `API_URL`), solo en producción, y el repositorio pasó a `hideakiuchida/TutorPre`. Faltan los tokens en GitHub, la clave de sesión y el cliente de Google en Railway, y añadir la URL de la web al cliente OAuth de Google |
| 11 | Pantalla para elegir el modelo del tutor | RF-35 | Hoy solo por `PUT /admin/agentes/{id}` |
| 12 | Tope de mensajes por alumno y clase | RNF-08 | Sin implementar |
| 13 | Dificultad adaptativa por nivel | RF-23 | Sin implementar |
| 14 | Carga, volumen y disponibilidad | RNF-03 a RNF-05 | Medir con k6 en un despliegue |
| 15 | Medir la condensación con el modelo real | RF-36 | Primera prueba local: la infografía "Costo vs Gasto" pasó de 409 a 267 tokens (−35 %). Falta completar un curso con Gemini y revisar la fidelidad de los apuntes con el profesor |
| 16 | Profesor sin IA conectada en producción | RF-36 | El profesor tiene que conectar una IA: Gemini con su cuenta de Google desde su panel (solo si es usuario de prueba) o una clave de API entrando como alumno, en *Mi tutor*. Hasta entonces el tutor lee el original. Falta que el panel del docente ofrezca también pegar la clave |
| 17 | Quitar del código la pasarela retirada | RF-37 | `OpenRouterProvider`, `ConexionOpenRouter`, su entrada en `appsettings.json` y sus pruebas siguen en el repositorio, apagados por configuración. Las cuentas conectadas de antes siguen guardadas pero no se usan; el chat pasa a la IA que el alumno elija. Un examen que estuviera en curso con OpenRouter no puede seguir: el profesor reinicia los intentos de ese alumno |
| 17 | Cuentas de IA propias, fase 2 | RF-37, RF-39 | OAuth 2.1 con registro dinámico de clientes en `/mcp` para conectar el Tutor desde los conectores web de Claude.ai y ChatGPT (hoy solo con token en clientes que aceptan cabeceras); *sampling* de MCP para que el asistente del alumno genere las preguntas; proyecto de Google propio (`x-goog-user-project`) para cargar el uso de Gemini a la cuenta del alumno |
| 18 | Verificar la aplicación en Google | RF-37 | El permiso de Gemini por cuenta es sensible y la app no está verificada: Google bloquea a quien no sea usuario de prueba, y a estos el permiso les caduca a los 7 días. Mientras tanto la vía general es la clave de API (RF-38). La verificación pide dominio verificado, política de privacidad y un video de uso |
| 20 | Probar las claves propias con cuentas reales | RF-38 | Verificado en local que OpenAI rechaza una clave inventada; falta una conversación y un examen con una clave real de cada proveedor (la pega la persona) |
| 19 | Medir el examen pregunta a pregunta con el modelo real | RF-04 | Tiempo hasta la primera pregunta y cuántas veces el alumno espera porque la siguiente no llegó a prepararse; calidad y variedad de las preguntas generadas de a una |
| 18 | Cuota de Gemini compartida | RF-37 | El plan gratuito del proyecto `tutor-pre-clase` lo comparten todos los alumnos conectados con Gemini; vigilar el 429 y, si crece, activar facturación o la fase 2. Para abrirlo más allá de 100 usuarios de prueba hay que verificar la app en Google |

---

[← 8. Diseño de UI](08-diseno-de-ui.md) · [Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md)

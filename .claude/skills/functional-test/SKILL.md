---
name: functional-test
description: Prueba funcional del Tutor Pre-Clase trazada al SDD — recorre los requisitos RF-01 a RF-39 (inicio de sesión y pantallas por rol, Mi tutor con cuentas de IA por OAuth, claves de API propias y el servidor MCP, material condensado, examen pregunta a pregunta) y los RNF verificables a mano, en la web y por API, en local o en producción (Vercel + Railway), y reporta cuáles pasan, cuáles fallan y cuáles no están implementados. Úsalo cuando pidan probar la app entera, validarla antes de una demo, probar en producción, o comprobar que un cambio no rompió el flujo completo.
---

# Prueba funcional según el SDD

Cada bloque verifica requisitos concretos de
[`specs/SDD-Plataforma-Evaluacion-PreClase.md`](../../../specs/SDD-Plataforma-Evaluacion-PreClase.md).
Si el spec cambió, **léelo antes**: manda el spec, no esta lista.

El arranque no está aquí: en local, **invoca primero el skill `start-local`**. En producción no
se arranca nada: se prueba lo desplegado.

## Entorno: local o producción

Pregunta o deduce contra cuál se prueba, y dilo en el reporte.

| | Local | Producción |
| --- | --- | --- |
| Web | http://localhost:4200 | https://tutor-preclase.vercel.app |
| API | http://localhost:5080 | https://api-production-9a65.up.railway.app |
| Qué hay desplegado | Lo del árbol de trabajo | El último commit de `main` con CI y CD en verde: compruébalo en https://github.com/hideakiuchida/TutorPre/actions antes de empezar |
| Cómo se actúa por API | `curl` con `X-Usuario-Id` / `X-Usuario-Rol`, y `/demo` para los ids | **No hay cabeceras de desarrollo ni `/demo`** (401). Solo la sesión real: la persona entra con Google en Chrome y las llamadas se hacen desde la página con `fetch` y el token de `sessionStorage['tutor.sesion']`. **Nunca muestres ni copies el token** |
| Datos | De prueba (volumen `tutor-datos`) | **Reales**: no crees cursos ni alumnos de prueba, no borres nada y no cambies ajustes de clases sin decirlo; lo que cambies (ampliación, nivel), déjalo como estaba |
| Material | Copiar a `course-content/` | Viene en la imagen: cambiarlo es commit y CD. No lo pruebes en producción |
| Logs | `docker logs tutor-api` | Railway → proyecto `tutor-preclase` → servicio `api` → *Deployments* → *View logs* (en Chrome) |
| Cuentas de IA | Las conecta la persona | Igual: Gemini (o una clave de API) lo conecta **la persona** en la web de producción |
| Claves de API y token MCP | Prueba el rechazo con claves **inventadas**; una real la pega **la persona** | Igual. El token MCP de producción lo genera y lo copia **la persona**; tú no lo leas ni lo pegues en ningún sitio y, si generaste uno para probar, revócalo al terminar |
| Entrar en local sin Google | `POST /api/v1/acceso/desarrollo {usuarioId}` desde la página y guarda la respuesta en `sessionStorage['tutor.sesion']` | No existe |

En producción, las comprobaciones mínimas antes de los bloques:

```bash
curl -s https://api-production-9a65.up.railway.app/salud                     # {"estado":"ok"}
curl -s https://api-production-9a65.up.railway.app/api/v1/acceso/proveedores # google: configurado true
curl -s -o /dev/null -w "%{http_code}\n" https://api-production-9a65.up.railway.app/api/v1/alumno/clases  # 401
curl -s -o /dev/null -w "%{http_code}\n" https://tutor-preclase.vercel.app/entrar  # 200 (ruta de la SPA)
```

Llamada a la API con la sesión de la página (en Chrome, con `javascript_tool`, en la pestaña de
la web de producción ya con sesión). Devuelve datos, nunca el token:

```js
const { token } = JSON.parse(sessionStorage.getItem('tutor.sesion'));
await fetch('https://api-production-9a65.up.railway.app/api/v1/agentes', { headers: { Authorization: `Bearer ${token}` } }).then(r => r.json())
```

## 0. Preparación

Corre las pruebas .NET en Docker con el paso 2b de `start-local` (en Windows, Smart App
Control bloquea las DLL recién compiladas; no se toca esa configuración).

**No llaman a ningún modelo ni gastan tokens**: usan un proveedor guionado y HTTP simulado, no
hay ninguna clave real en el repositorio ni en el CI, y la API de pruebas corta cualquier
salida a la red (`SinRed` en `ApiDePruebas`). Pasan igual con el contenedor sin red
(`docker run --network none … dotnet test --no-build`). Si una prueba nueva necesita un
proveedor, se le da uno simulado: nunca una clave real.

Gratis y en segundos: cubre RF-02 a RF-05, RF-08, RF-09, RF-12, RF-15, RF-21, RF-24 a RF-31, RF-33 y
RNF-10 a nivel de unidad e integración (el OIDC, con el proveedor simulado). Si algo falla ahí, arréglalo antes de abrir el navegador.

Toma los identificadores: `curl -s http://localhost:5080/demo` → `$DOC`, `$ALU`, `$CLA`.
Por `curl` se actúa con las cabeceras de desarrollo `X-Usuario-Id` / `X-Usuario-Rol`; la web,
en cambio, usa la sesión (`Authorization: Bearer`) que emite la API al entrar.

**La IA del alumno (BYOK).** El tutor conversa con la cuenta o la clave que conecte cada
persona en *Mi tutor*: Gemini con su cuenta de Google, por OAuth (RF-24, RF-29, RF-37), o una clave de
API de Claude, ChatGPT o Gemini (RF-38). La conecta **la persona** en el bloque B — tú no
escribes contraseñas ni creas cuentas. **Nunca pidas la clave por el chat, ni la escribas en
un archivo, un log o el resumen.** Sin ninguna IA conectada, salta los bloques C, D y E y
dilo al reportar.

## Control de consumo: qué gasta tokens y qué no

Los tokens son **de la cuenta de la persona** (BYOK). Solo se llama a un modelo real donde hace
falta ver al modelo real; todo lo demás se prueba sin él.

| Gasta tokens (modelo real) | No gasta |
| --- | --- |
| Bloque C (consulta), D (examen) y E (revisión) | Pruebas .NET, bloques A (salvo RF-36), B, F y G |
| RF-36 condensar el material (solo si se pide) | Rechazos: clave inventada, código OAuth inventado, sin credencial (el proveedor responde 401 sin generar nada) |
| RF-39 «tómame el examen» desde el asistente | `listar_clases`, `leer_material`, reportes, niveles, permisos |

Reglas, antes de enviar el primer mensaje al tutor:

1. **Solo el modelo en uso.** Se prueba con la IA marcada *En uso* en *Mi tutor* (`GET /agentes`
   dice su modelo). No se recorren los demás proveedores «por si acaso»: otro proveedor se
   prueba solo si la persona lo pide o si el cambio toca su adaptador, y entonces con el
   escenario mínimo (un mensaje de consulta y el inicio del examen).
2. **Escenarios reales, una sola vez.** Los mensajes de los bloques C, D y E son los que haría
   un alumno; no se inventan variantes, no se repite un mensaje que ya pasó y no se reintenta
   en bucle: ante un error se leen los logs antes de volver a enviar.
3. **Un solo examen, en la clase más chica.** El material viaja en cada llamada: usa una clase
   de una infografía (M2) y no M1, salvo que el cambio sea de M1. En local, baja las preguntas
   del examen al mínimo del formulario antes de empezar y déjalo como estaba. El segundo
   intento (RF-04) y el reinicio de intentos solo si se pidió probar eso.
4. **Avisa antes de gastar.** Di qué modelo se va a usar y cuántos mensajes (~12 el recorrido
   completo; 3 o 4 si solo se valida un cambio) y espera el visto bueno si la cuenta es de
   pago o la persona pidió cuidar el saldo.
5. **Mide y repórtalo.** Cada llamada deja en el log `LLM <agente> <modelo>: … entrada N,
   salida N, razonamiento N`. Suma entrada y salida de la corrida y ponlo en el reporte.
6. **Si el cambio no toca el tutor, el examen ni los adaptadores, no se llama al modelo**: se
   marcan C, D y E como *no ejecutados para no gastar* y se dice.

## Bloque A — El docente prepara la clase

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-01 | Cada subcarpeta de `course-content/alonso.uchida@gmail.com/MFEP - Finanzas empresariales/` es una clase del curso de ese profesor (`GET /alumno/clases`: M1…M7, 19:00 de Lima, una por semana). **La fecha es solo informativa**: todas las clases se abren, también las de fecha futura — ninguna sale atenuada ni dice *Se habilita el …*, y `POST /clases/<futura>/conversacion` y `GET /clases/<futura>/material` responden 200. La respuesta no trae `habilitada` |
| RF-02 | Copia un `.md` a la carpeta de M2 → en ≤ 30 s aparece en `GET /clases/<M2>/contenido`; bórralo → desaparece. El `.xlsx` de M4 figura con 3 páginas (una por hoja) |
| RF-03 | Todos los archivos en `"estado":"Listo"` con `paginas` ≥ 1; M1 con `contextoClase.tokens` ≈ 9 500 |
| RF-04 | Cada clase de la carpeta tiene su examen publicado sin preguntas (las genera la IA); `POST /examenes/$EX/preguntas` ya no existe |
| RF-14 | `PUT .../examen` con `"modoFeedback":"Inmediato"` y con `"AlFinal"`: ambos aceptados |
| RF-18 | `PUT .../examen` con `"minutosLimite":10`; luego el chat emite `segundosRestantes` en `progreso` |
| RF-36 | Con Gemini conectado por el profesor, cada archivo pasa a `"condensado":"Listo"` y `tokensCondensado` < `tokensOriginal`; `contextoClase.tokens` (lo que lee el tutor) < `contextoClase.tokensOriginal`. En el panel, *Comparar* muestra original y apuntes por página, y *Usar original* devuelve el original al tutor. Sin cuenta, el archivo queda `SinCuenta` y el chat funciona igual con el original. Si el modelo gratuito está saturado queda `Pendiente` con "se reintentará solo" (log: `se condensará en N min`); el examen se sigue generando con el original |

```bash
M2="course-content/alonso.uchida@gmail.com/MFEP - Finanzas empresariales/M2 - Indicadores de gestión"
printf 'Prueba de sincronizacion.\n' > "$M2/prueba-sincronizacion.md"   # bórralo al terminar

curl -s -H "X-Usuario-Id: $DOC" -H "X-Usuario-Rol: Docente" \
  "http://localhost:5080/api/v1/clases/$CLA/contenido"
```

El material es el del curso real: **no** borres ni edites los archivos del docente; usa
solo un archivo de prueba propio y quítalo al terminar.

La condensación (RF-36) gasta llamadas del cupo gratuito **de la cuenta del profesor**:
unas 4 para M1 y 1 por cada infografía. `POST /clases/$CLA/condensar {"forzar":true}` la
repite; no lo hagas en bucle, y no la fuerces si el cambio que se prueba no es de condensación.

```bash
curl -s -H "X-Usuario-Id: $DOC" -H "X-Usuario-Rol: Docente" -H "Content-Type: application/json"   -X POST -d '{"forzar":true}' "http://localhost:5080/api/v1/clases/$CLA/condensar"
docker logs --since 10m tutor-api 2>&1 | grep -E "Material condensado|se condensará"
```

## Bloque B — Entrada, roles y conexión de la IA

Los bloques B a G se prueban **en la pantalla, con Chrome**; el `curl` solo complementa.
Invoca el skill `claude-in-chrome` y carga las herramientas en **una sola** llamada a ToolSearch:

```
select:mcp__claude-in-chrome__list_connected_browsers,mcp__claude-in-chrome__tabs_context_mcp,mcp__claude-in-chrome__tabs_create_mcp,mcp__claude-in-chrome__navigate,mcp__claude-in-chrome__computer,mcp__claude-in-chrome__read_page,mcp__claude-in-chrome__find,mcp__claude-in-chrome__form_input,mcp__claude-in-chrome__resize_window,mcp__claude-in-chrome__tabs_close_mcp
```

**Comprueba la conexión antes de seguir:** `list_connected_browsers` debe devolver al menos
un navegador. Si viene vacío o `tabs_context_mcp` responde *"Browser extension is not
connected"*, **detente y avisa**: la persona debe abrir Chrome con la extensión Claude
(https://claude.ai/chrome) iniciada con la misma cuenta de Claude Code. No sustituyas la
prueba de pantalla por `curl` en silencio; si decide seguir sin navegador, marca como
**no verificados** los requisitos de UI.

Con la conexión lista: `tabs_create_mcp` y navega a `http://localhost:4200`.

**Entrada y roles.** La web solo ofrece OAuth con Google. Tú no inicias sesión en Google:
lo hace la persona (elige su cuenta y vuelve). Solo si ella te lo autoriza
en el chat puedes pulsar *Continuar con Google* y elegir **su** cuenta en el selector; si
Google pide contraseña o verificación, para y avisa. Para las pantallas por rol sin OAuth, en desarrollo pide una sesión
con `POST /api/v1/acceso/desarrollo {"usuarioId":…}` y guárdala en la pestaña con
`sessionStorage.setItem('tutor.sesion', JSON.stringify(sesion))` antes de recargar.

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-08 | El examen **no tiene fechas** (`examen` no trae `abreEn` ni `cierraEn`): toda clase con examen publicado muestra *Examen disponible*, sea su fecha pasada o futura, y nunca dice *Examen cerrado*. En el panel del profesor no hay campo de fecha |
| RF-31 | La entrada (franja roja UPC) pide primero *Soy alumno* / *Soy profesor*; luego *Entras como … Cambiar* y un solo botón, *Continuar con Google*, igual para alumno y profesor (no hay *Continuar con Microsoft*). `GET /acceso/proveedores` devuelve solo `google`, con `configurado: true` |
| RF-31 | *Continuar con Google* lleva a `accounts.google.com` con `code_challenge_method=S256` y `redirect_uri=http://localhost:4200/entrar/google`. La persona elige su cuenta y vuelve: entra con el perfil elegido |
| RF-31 | `POST /acceso/google/inicio {"perfil":"Admin"}` → `rol_invalido`; `POST /acceso/google/canje` con `code`/`state` inventados → error y ninguna sesión; `POST /acceso/microsoft/inicio` → error (proveedor retirado) |
| RF-33 | Un profesor que entra como **alumno** llega al chat (elige profesor, incluido él mismo) y sigue siendo profesor; entrando como **profesor** llega a su panel |
| RF-33 | Como alumna: la barra lateral dice *Con <profesor>*; **Cambiar** abre *Tu profesor* con la lista de profesores y sus cursos; *Volver sin cambiar* regresa al chat |
| RF-33 | Primer acceso como alumno (pruebas .NET y, con una cuenta nueva, en pantalla): tras Google sale directo *Elige a tu profesor*; como profesor entra directo a su panel |
| RF-33 | Un Alumno o un Docente contra `GET /admin/usuarios` → **403** |
| RF-34 | Un docente que no es dueño del curso contra `GET /clases/$CLA/contenido` → **403**; el dueño → 200 |
| RF-34 | Como docente: cada clase muestra su material (estado *Listo*, páginas, tokens), el interruptor de ampliación (apagado) y el reporte. En *Examen y tutor* hay un formulario con preguntas por examen, intentos, minutos por intento (o sin límite), cuándo se muestra la corrección (sin campos de fecha): cambia los intentos, pulsa *Guardar cambios* → sale *Guardado* y `GET /clases/$CLA/contenido` trae el valor nuevo en `examen`; un valor fuera de rango por API (`maxIntentos: 0`) responde **400**. Déjalo como estaba |
| RF-32 | Al entrar como alumna sin ninguna IA conectada **no** hay redirección automática a Google: se abre *Mi tutor* en la pestaña *Con tu clave de API*, con Gemini (AI Studio) primero y los pasos para crear la clave gratis |
| RF-37 | *Entrar con Gemini (tu cuenta de Google)* lleva a `accounts.google.com` con `scope=…/cloud-platform …/generative-language.retriever`, `access_type=offline`, `code_challenge_method=S256` y `redirect_uri` a `/conectar/gemini`. La persona elige su Gmail y acepta el permiso (no tú) → vuelve y Gemini queda conectado. En *Mi tutor* → «Conversas con» se elige entre las IA conectadas; en el log sale `LLM gemini gemini-3.1-flash-lite: primer token … ms`. Revocar el acceso en myaccount.google.com hace que el chat pida reconectar |

**Conexión de la cuenta.**

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-27 | Al entrar sin cuenta conectada, se abre **Mi tutor** encima del chat y el campo de mensaje está deshabilitado |
| §8 | La cabecera dice **«Mi tutor: <agente>»** con *Cambiar* (sin combo); *Cambiar* abre *Mi tutor* como una hoja encima del chat (se cierra con *Volver al chat*, Esc o pulsando fuera; sin ninguna IA conectada no se cierra) con «Conversas con» (cada IA con su modelo, la elegida marcada *En uso*) y tres pestañas: *Con tu clave de API* (la que se abre), *Con tu cuenta*, *Desde tu asistente*. *Con tu cuenta* avisa que solo sirve para cuentas autorizadas en Google y remite a la clave de API. Las flechas del teclado cambian de pestaña. A ~400 px la cabecera cabe en una línea y las pestañas también. En examen, «Conversas con» queda deshabilitado |
| RF-29 | `PUT /alumno/credenciales/gemini` con una clave → 400 `conexion_por_oauth` y no guarda nada |
| RF-38 | Pestaña *Con tu clave de API*: en ChatGPT pega una clave **inventada** con la forma correcta (`sk-proj-…-0000`) → *Conectar* → aviso *rechazó esa credencial* (lo rechaza OpenAI real), el campo queda vacío y `GET /alumno/credenciales` no la tiene. En Claude pega una `sk-proj-…` → aviso *debería empezar por "sk-ant-"* sin llamar al proveedor. Enter en el campo también conecta |
| RF-38 | Con una clave real que **pega la persona**: queda «Conectada: la clave termina en XXXX», aparece en «Conversas con» con su modelo y el chat responde con ese proveedor |
| RF-39 | Pestaña *Desde tu asistente*: *Generar token* muestra `tpc_mcp_…` una sola vez con *Copiar*, la URL `…/mcp` y el comando de Claude Code; al recargar solo se ve «Termina en XXXX». *Revocar* lo invalida |
| RF-39 | Con el token (en local, el de prueba): `initialize` y `tools/call listar_clases` a `/mcp` responden 200 con las clases del alumno; `leer_material` funciona también en una clase de fecha futura; sin token, con la sesión de la web o con el token revocado → 401. En producción, la persona conecta Claude Code con el comando y le pide «¿qué clases tengo?» y «tómame el examen de M1»: cita el material, una pregunta a la vez, y la nota coincide con `GET /intentos/{id}/resultado` |
| RF-29 | `/conectar/gemini?code=inventado&state=inventado` sin haber iniciado → se abre *Mi tutor* con el aviso *"venció o ya se usó"* (también con otra cuenta ya conectada), la URL queda en `/` y nada guardado |
| RF-37 | **Un agente retirado no se ofrece**: con `Agentes:gemini:Habilitado = false` (o sin su entrada en la configuración), `GET /agentes` no lo trae, *Mi tutor* no lo muestra, «Conversas con» no lo lista aunque el usuario tuviera esa cuenta conectada y `POST /alumno/credenciales/gemini/oauth/inicio` responde 400 `agente_no_disponible`. Un chat que quedó con un agente retirado se abre con la IA elegida ahora |
| RF-25 | Un código inventado canjeado por la API (`POST /alumno/credenciales/gemini/oauth/canje`) responde error (lo rechaza Google real) y no se guarda |
| RF-26 / RNF-10 | `GET /api/v1/alumno/credenciales` devuelve solo `agenteId`, `ultimos4`, `origen` (`OAuth`), `estado` y fechas. **Si aparece la clave completa, párate y repórtalo: es un fallo grave** |
| RF-26 | Desde otro `X-Usuario-Id` el mismo endpoint devuelve `[]` |
| RF-30 | Si el modelo gratuito devuelve 429, sale el aviso de cupo (`limite_de_uso`) y la credencial sigue `Valida` |

```bash
curl -s -X POST -H "X-Usuario-Id: $ALU" -H "X-Usuario-Rol: Alumno" \
  "http://localhost:5080/api/v1/alumno/credenciales/gemini/oauth/inicio"   # { url }

curl -s -X POST -H "X-Usuario-Id: $ALU" -H "X-Usuario-Rol: Alumno" \
  -H "Content-Type: application/json" -d '{"code":"codigo-inventado"}' \
  "http://localhost:5080/api/v1/alumno/credenciales/gemini/oauth/canje"    # error: código rechazado

curl -s -H "X-Usuario-Id: $ALU" -H "X-Usuario-Rol: Alumno" \
  "http://localhost:5080/api/v1/alumno/credenciales"

curl -s -H "X-Usuario-Id: $(python -c 'import uuid;print(uuid.uuid4())')" \
  -H "X-Usuario-Rol: Alumno" "http://localhost:5080/api/v1/alumno/credenciales"   # []
```

## Bloque C — Modo Consulta

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-19 | En M1: `¿qué diferencia hay entre costo y gasto?` obtiene respuesta del tema |
| RF-11 | Esa respuesta cita la infografía *Costo vs Gasto* (resaltada en el texto y en las fuentes debajo del globo del tutor). La fuente es un botón con una flecha ↓: al pulsarlo se descarga ese archivo. Por API, `GET /clases/$CLA/material` lista los archivos y `GET /clases/$CLA/material/<archivoId>` responde 200 con el archivo; otro alumno que no lleva el curso recibe **403** |
| RF-37 | **Solo si Gemini es la IA en uso:** la pregunta del WACC de abajo usa una herramienta, así que comprueba que la firma de razonamiento vuelve bien (antes daba *400*). Si Google está saturado (503), el aviso dice *saturado* y no muestra el código |
| RF-12 / RF-20 | `¿qué es el WACC?` (no está en el material de M1). Con la ampliación **apagada, que es lo que viene por defecto**: responde *"Esto no está en el material de la clase; pregúntalo en la sesión."* y no sale nada a lápiz |
| RF-19 | `dame una receta de ceviche` → declina y reconduce al curso |
| RNF-02 | El primer token aparece en menos de ~3 s, también con la ampliación apagada (el texto se ve mientras se escribe). Si tarda más de 2 s aparece *Pensando…*. En el log de la API, cada llamada deja `LLM <agente> <modelo>: primer token … ms, total … ms, entrada …, razonamiento …`; con el razonamiento desactivado, `razonamiento` debe ser 0 |

## Bloque D — Examen por chat

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-04 | **Comenzar examen**: sale *"Preparando tu examen con el material de la clase…"* y en pocos segundos arranca con la **primera** pregunta **sobre el material de M1** (no inventada ni de otro tema). La cabecera dice *Pregunta 1 de 6* aunque las demás aún no existan: se generan **pregunta a pregunta** mientras responde, sin repetir tema (SDD §5.2) |
| RF-04 | Tras cada respuesta, la siguiente llega casi al instante (se preparó en segundo plano) y la escribe **el servidor**, con el formato fijo `Pregunta N de 6` + alternativas A–D. Si no se pudo generar, sale *"No pude preparar la siguiente pregunta… Detuve el reloj del examen: escribe «siguiente»"*, la cabecera muestra el tiempo con la marca *en pausa* y no baja; al llegar la pregunta el reloj sigue desde donde quedó. Las respuestas ya registradas se conservan |
| RF-05 | Cada pregunta trae 4 alternativas; la correcta no cae siempre en la misma letra. Tras el examen, `GET /intentos/$INT/resultado` muestra justificaciones que citan `[archivo, p. N]` |
| RF-04 | Un segundo intento trae preguntas distintas a las del primero |
| RF-06 | La cabecera pasa a *Examen en curso* y el intento guarda el modelo (el agente usado, p. ej. `gemini`, en `usoPorAgente` del reporte) |
| RF-15 | Pide una pista o la respuesta: se niega en una línea, **sin citar ni recordar el material**, y no filtra la correcta |
| RF-07 | Responde con una letra (`creo que es la A`): la registra el servidor al instante, sin llamar al modelo. Responde con el contenido (`el indicador financiero`): el tutor la interpreta y la registra. En ambos casos sale *Registrada.* y **la barra avanza una casilla por respuesta**: si el tutor dice que registró y la barra no se mueve, es un fallo grave |
| RF-18 | Con `minutosLimite`, la cabecera muestra el tiempo restante y **el reloj corre solo**, segundo a segundo. Al recargar la página en pleno examen, la barra y el reloj reaparecen sin escribir nada |
| RF-09 | Al responder la última, **el servidor cierra el examen** y el tutor comunica la **nota sobre 20**, calculada sobre las 6 planeadas; la nota coincide con las respuestas dadas y con `GET /api/v1/intentos/$INT/resultado` |
| §6.4 | Di `termina el examen` con preguntas pendientes: se cierra. Sin pedirlo, el examen no se cierra antes de la sexta respuesta |
| §5.4 | Al terminar, el tutor abre con una **evaluación final** breve: la nota, lo que dominó, los temas a reforzar y una recomendación; luego ofrece repasar los fallos. En el reporte del docente, esos temas aparecen en `temasDebiles` del alumno. **Sin la IA también hay retroalimentación**: si al cerrar el examen el proveedor falla o responde vacío, el mismo mensaje trae la nota y un bloque **Retroalimentación** del servidor (temas a reforzar y, por cada fallo, tu respuesta, la correcta y el porqué con su cita), los temas fallados quedan en `temasDebiles` y, si fue un fallo del proveedor, llega el aviso `evaluacion_pendiente` |
| RNF-01 | El progreso se actualiza al instante, sin esperar al modelo |
| RF-08 | En la lista de clases, bajo la etiqueta del examen, sale siempre el avance del alumno: `0 de 3 intentos` antes de rendir y `Nota 16.7 · 2 de 3 intentos` después (el tope son los intentos que fijó el profesor, 3 por defecto, y es por cada clase). Tras agotar los intentos la etiqueta dice *Sin intentos disponibles*, no hay botón *Comenzar examen* e iniciar otro por API responde `SinIntentosDisponibles` |
| RF-17 | Si el tutor no responde (saturado, sin cupo, error o conexión perdida) aparece *"El tutor no llegó a responder tu último mensaje. Reintentar"*: al pulsarlo se reenvía sin duplicar el mensaje en el chat, y en `GET /conversaciones/$CONV/mensajes` queda una sola pregunta con su respuesta |

## Bloque E — Revisión

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-10 | `revisemos mis errores` → explica por qué falló, el concepto correcto y una pregunta de comprobación |
| RF-11 | Esa explicación cita el material con **fuente válida** (resaltada, con la flecha ↓ de descarga); una cita que no está en el material sale tachada y sin descarga |
| RF-12 | Una repregunta fuera del material recibe el mismo aviso: el tutor no sale del material |
| RF-13 | `GET /api/v1/clases/$CLA/reporte` → `dudasFueraDelMaterial` incluye la consulta sobre el WACC con `conAmpliacion: false` |

## Bloque F — Nivel, reporte y control del docente

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-21 | El reporte trae el nivel del alumno acorde a su nota (0–10,9 Inicial · 11–14,9 Básico · 15–17,9 Intermedio · 18–20 Avanzado) |
| RF-22 | El reporte trae `distribucionNiveles`, nivel por alumno y `temasDebilesDelGrupo`. En el panel del profesor, *Resultados por alumno* muestra de cada uno la nota, el nivel, los aciertos, los intentos y los temas a reforzar; la nota y el nivel aparecen solos al terminar el examen, sin que nadie los cargue |
| RF-16 | El reporte trae `promedioNota`, `distribucionNotas`, `temasMasFallados` y `usoPorAgente` |
| RF-08 | Reiniciar intentos (solo en local, o en producción si la persona lo pide para un alumno concreto: **borra sus notas y no se deshace**): `DELETE /clases/$CLA/alumnos/$ALU/intentos` como profesor responde `intentosBorrados`; después `GET /alumno/clases` da `intentosUsados: 0` y `mejorNota: null`, el alumno deja de salir en el reporte y vuelve a ver *Examen disponible*. Como alumno, **403** |
| RF-21 | `PUT /clases/$CLA/niveles/$ALU` con `{"nivel":"Avanzado"}` responde `"origen":"Docente"` y el cálculo no lo pisa |
| RF-20 | `PUT /clases/$CLA/ampliacion` con `{"permitida":true}`; recarga el chat y repite `¿qué es el WACC?`: ahora lo dice y amplía **a lápiz debajo de la respuesta** ("Fuera del material"). Vuelve a `{"permitida":false}` al terminar: apagada, el corte lo hace el servidor, no el prompt |
| RF-02 / §6.1 | Con el chat abierto en M2, copia el archivo de prueba del bloque A a esa carpeta: en ≤ 30 s sale *"Tu docente actualizó el material de esta clase"* sin recargar. Sin cambios en la carpeta, `read_network_requests` no muestra pedidos periódicos a `/alumno/clases` (solo la conexión abierta a `/alumno/novedades`) |

```bash
curl -s -H "X-Usuario-Id: $DOC" -H "X-Usuario-Rol: Docente" \
  "http://localhost:5080/api/v1/clases/$CLA/reporte" | python -m json.tool
```

## Bloque G — Seguridad y resiliencia

| Requisito | Cómo verificarlo |
| --- | --- |
| SDD §9.1 | Alumno contra `GET /clases/$CLA/reporte` → **403**; sin sesión ni cabeceras → **401** |
| SDD §9.1 | Con una sesión alterada (cambia un carácter del `Bearer` en `sessionStorage` `tutor.sesion`) la API responde 401 y la web vuelve a la entrada |
| SDD §9.1 | Otro alumno contra `GET /conversaciones/$CONV/mensajes` → **403** |
| RF-28 | Desconecta la credencial y escribe en el chat: avisa `sin_credencial` y no llama al proveedor. Con una credencial que el proveedor rechace, se marca *Invalida* y pide reconectarla |
| RNF-06 | El prompt no lleva nombre ni correo del alumno (revisa `docker logs tutor-api`) |
| RNF-07 | Estrecha el navegador a ~400 px: la lista de clases pasa arriba y el chat sigue usable |

## Requisitos que hoy no se pueden dar por buenos

Dilo explícitamente al reportar; no los marques como aprobados:

| Requisito | Estado |
| --- | --- |
| RF-35 | El modelo de cada IA se cambia solo por `PUT /admin/agentes/{id}`; no hay pantalla |
| RF-08 | Reiniciar los intentos de un alumno existe solo por API; el panel del profesor no tiene botón |
| RF-05 | La justificación cita el material, pero las **referencias** no se guardan aún en `pregunta_referencia` |
| RNF-11 | Qué hace cada proveedor con los datos del plan gratuito (retención y entrenamiento) solo se comprueba en sus términos y con la cuenta real conectada |
| RF-23 | La IA todavía no ajusta la dificultad al nivel del alumno |
| RNF-08 | No hay tope de mensajes por alumno y clase |
| RNF-03 / RNF-04 / RNF-05 | Carga, volumen y disponibilidad: no se verifican a mano, hacen falta k6 y medición en despliegue |

## Cerrar

Para parar los procesos, usa el paso 5 del skill `start-local`. Y si quieres dejarlo todo
limpio para la próxima — esto borra también la credencial conectada y el contenido subido:

```bash
docker rm -f tutor-api
docker volume rm tutor-datos tutor-claves
```

## Al terminar

Reporta por bloques, **diciendo si fue en local o en producción** y qué commit estaba
desplegado: qué requisito pasó, cuál falló y con qué evidencia, y cuáles se saltaron (por
falta de credencial, porque no se prueban en producción o porque no están implementados). Quita el archivo de
prueba de `course-content/`. No des por bueno lo que
no comprobaste.

Un modelo real no repite frases exactas: verifica el **comportamiento** (que cite, que marque
la ampliación, que registre la respuesta, que no filtre la correcta), nunca el texto literal.
El recorrido completo son ~12 mensajes cortos, pero cada vuelta de herramientas es una
llamada: gasta buena parte del cupo gratuito diario de la cuenta conectada, o saldo si es de
pago. Sigue el *Control de consumo* de arriba, avisa si se va a repetir en el mismo día y
cierra el reporte con los tokens gastados.

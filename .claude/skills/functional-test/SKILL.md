---
name: functional-test
description: Prueba funcional del Tutor Pre-Clase trazada al SDD — recorre los requisitos RF-01 a RF-35 (incluidos el inicio de sesión y las pantallas por rol) y los RNF verificables a mano, en la web y por API, y reporta cuáles pasan, cuáles fallan y cuáles no están implementados. Úsalo cuando pidan probar la app entera, validarla antes de una demo, o comprobar que un cambio no rompió el flujo completo.
---

# Prueba funcional según el SDD

Cada bloque verifica requisitos concretos de
[`specs/SDD-Plataforma-Evaluacion-PreClase.md`](../../../specs/SDD-Plataforma-Evaluacion-PreClase.md).
Si el spec cambió, **léelo antes**: manda el spec, no esta lista.

El arranque no está aquí: **invoca primero el skill `start-local`**.

## 0. Preparación

Corre las pruebas .NET en Docker con el paso 2b de `start-local` (en Windows, Smart App
Control bloquea las DLL recién compiladas; no se toca esa configuración).

Gratis y en segundos: cubre RF-02 a RF-05, RF-08, RF-09, RF-12, RF-15, RF-21, RF-24 a RF-31, RF-33 y
RNF-10 a nivel de unidad e integración (el OIDC de Microsoft y Google, con el proveedor simulado). Si algo falla ahí, arréglalo antes de abrir el navegador.

Toma los identificadores: `curl -s http://localhost:5080/demo` → `$DOC`, `$ALU`, `$CLA`.
Por `curl` se actúa con las cabeceras de desarrollo `X-Usuario-Id` / `X-Usuario-Rol`; la web,
en cambio, usa la sesión (`Authorization: Bearer`) que emite la API al entrar.

**La cuenta del alumno (BYOK).** La única vía para que el tutor converse es la cuenta de
OpenRouter del alumno, conectada con *Conectar con OpenRouter (gratis)* (RF-24, RF-29); no
hay campo para pegar claves. La conecta la persona en el bloque B iniciando sesión ella
misma en OpenRouter — tú no escribes contraseñas ni creas cuentas. **Nunca pidas la clave
por el chat, ni la escribas en un archivo, un log o el resumen.** Sin cuenta conectada, salta
los bloques C, D y E y dilo al reportar.

## Bloque A — El docente prepara la clase

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-01 | Cada subcarpeta de `course-content/alonso.uchida@gmail.com/MFEP - Finanzas empresariales/` es una clase del curso de ese profesor (`GET /alumno/clases`: M1…M7, 19:00 de Lima, una por semana); `PUT /api/v1/clases/$CLA/examen` fija ventana e intentos |
| RF-02 | Copia un `.md` a la carpeta de M2 → en ≤ 30 s aparece en `GET /clases/<M2>/contenido`; bórralo → desaparece. El `.xlsx` de M4 figura con 3 páginas (una por hoja) |
| RF-03 | Todos los archivos en `"estado":"Listo"` con `paginas` ≥ 1; M1 con `contextoClase.tokens` ≈ 9 500 |
| RF-04 | Cada clase de la carpeta tiene su examen publicado sin preguntas (las genera la IA); `POST /examenes/$EX/preguntas` ya no existe |
| RF-14 | `PUT .../examen` con `"modoFeedback":"Inmediato"` y con `"AlFinal"`: ambos aceptados |
| RF-18 | `PUT .../examen` con `"minutosLimite":10`; luego el chat emite `segundosRestantes` en `progreso` |
| RF-36 | Con OpenRouter conectado por el profesor, cada archivo pasa a `"condensado":"Listo"` y `tokensCondensado` < `tokensOriginal`; `contextoClase.tokens` (lo que lee el tutor) < `contextoClase.tokensOriginal`. En el panel, *Comparar* muestra original y apuntes por página, y *Usar original* devuelve el original al tutor. Sin cuenta, el archivo queda `SinCuenta` y el chat funciona igual con el original. Si el modelo gratuito está saturado queda `Pendiente` con "se reintentará solo" (log: `se condensará en N min`); el examen se sigue generando con el original |

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
repite; no lo hagas en bucle.

```bash
curl -s -H "X-Usuario-Id: $DOC" -H "X-Usuario-Rol: Docente" -H "Content-Type: application/json"   -X POST -d '{"forzar":true}' "http://localhost:5080/api/v1/clases/$CLA/condensar"
docker logs --since 10m tutor-api 2>&1 | grep -E "Material condensado|se condensará"
```

## Bloque B — Entrada, roles y conexión de la cuenta (OpenRouter)

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

**Entrada y roles.** La web solo ofrece OAuth. Tú no inicias sesión en Google, Microsoft ni
OpenRouter: la extensión no puede actuar en `accounts.google.com` y además lo hace la persona
(elige su cuenta y vuelve). Para las pantallas por rol sin OAuth, en desarrollo pide una sesión
con `POST /api/v1/acceso/desarrollo {"usuarioId":…}` y guárdala en la pestaña con
`sessionStorage.setItem('tutor.sesion', JSON.stringify(sesion))` antes de recargar.

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-31 | La entrada (franja roja UPC) pide primero *Soy alumno* / *Soy profesor*; luego *Entras como … Cambiar* y los botones *Continuar con Google* / *Continuar con Microsoft (Outlook)*. `GET /acceso/proveedores` dice cuál está `configurado`; el que no, avisa al pulsarlo |
| RF-31 | *Continuar con Google* lleva a `accounts.google.com` con `code_challenge_method=S256` y `redirect_uri=http://localhost:4200/entrar/google`. La persona elige su cuenta y vuelve: entra con el perfil elegido |
| RF-31 | `POST /acceso/google/inicio {"perfil":"Admin"}` → `rol_invalido`; `POST /acceso/microsoft/canje` con `code`/`state` inventados → error y ninguna sesión |
| RF-33 | Un profesor que entra como **alumno** llega al chat (elige profesor, incluido él mismo) y sigue siendo profesor; entrando como **profesor** llega a su panel |
| RF-33 | Como alumna: la barra lateral dice *Con <profesor>*; **Cambiar** abre *Tu profesor* con la lista de profesores y sus cursos; *Volver sin cambiar* regresa al chat |
| RF-33 | Primer acceso como alumno (pruebas .NET y, con una cuenta nueva, en pantalla): tras Google sale directo *Elige a tu profesor*; como profesor entra directo a su panel |
| RF-33 | Un Alumno o un Docente contra `GET /admin/usuarios` → **403** |
| RF-34 | Un docente que no es dueño del curso contra `GET /clases/$CLA/contenido` → **403**; el dueño → 200 |
| RF-34 | Como docente: cada clase muestra su material (estado *Listo*, páginas, tokens), la ventana del examen, el interruptor de ampliación (apagado) y el reporte |
| RF-32 | Al entrar como alumna sin OpenRouter, la web va sola a `openrouter.ai/auth` (la persona inicia sesión y autoriza, no tú). Si vuelve cancelando, queda en el chat con el panel de conexión y **no** la redirige otra vez |

**Conexión de la cuenta.**

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-27 | Al entrar sin cuenta conectada, se abre el panel **"Conecta tu tutor"** con un único botón y el campo de mensaje está deshabilitado |
| RF-29 | El panel no tiene ningún campo para pegar claves, y `PUT /alumno/credenciales/openrouter` con una clave no guarda nada |
| RF-29 | `/conectar/openrouter?code=inventado` sin haber iniciado → aviso *"venció o ya se usó"*, la URL queda en `/` y nada guardado |
| RF-24 | *Conectar con OpenRouter (gratis)* lleva a `openrouter.ai` con `code_challenge_method=S256` y `callback_url` a `/conectar/openrouter`. La persona inicia sesión y autoriza → vuelve, el panel se cierra y el chat queda listo; **Desconectar** lo revierte |
| RF-25 | Un código inventado canjeado por la API responde `oauth_rechazado` (lo rechaza OpenRouter real) y no se guarda |
| RF-26 / RNF-10 | `GET /api/v1/alumno/credenciales` devuelve solo `agenteId`, `ultimos4`, `origen` (`OAuth`), `estado` y fechas. **Si aparece la clave completa, párate y repórtalo: es un fallo grave** |
| RF-26 | Desde otro `X-Usuario-Id` el mismo endpoint devuelve `[]` |
| RF-06 | Con un solo agente, la cabecera no muestra selector |
| RF-30 | Si el modelo gratuito devuelve 429, sale el aviso de cupo (`limite_de_uso`) y la credencial sigue `Valida` |

```bash
curl -s -X POST -H "X-Usuario-Id: $ALU" -H "X-Usuario-Rol: Alumno" \
  "http://localhost:5080/api/v1/alumno/credenciales/openrouter/oauth/inicio"   # { url }

curl -s -X POST -H "X-Usuario-Id: $ALU" -H "X-Usuario-Rol: Alumno" \
  -H "Content-Type: application/json" -d '{"code":"codigo-inventado"}' \
  "http://localhost:5080/api/v1/alumno/credenciales/openrouter/oauth/canje"    # oauth_rechazado

curl -s -H "X-Usuario-Id: $ALU" -H "X-Usuario-Rol: Alumno" \
  "http://localhost:5080/api/v1/alumno/credenciales"

curl -s -H "X-Usuario-Id: $(python -c 'import uuid;print(uuid.uuid4())')" \
  -H "X-Usuario-Rol: Alumno" "http://localhost:5080/api/v1/alumno/credenciales"   # []
```

## Bloque C — Modo Consulta

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-19 | En M1: `¿qué diferencia hay entre costo y gasto?` obtiene respuesta del tema |
| RF-11 | Esa respuesta cita la infografía *Costo vs Gasto* (resaltada en el texto y en las fuentes debajo del globo del tutor) |
| RF-12 / RF-20 | `¿qué es el WACC?` (no está en el material de M1). Con la ampliación **apagada, que es lo que viene por defecto**: responde *"Esto no está en el material de la clase; pregúntalo en la sesión."* y no sale nada a lápiz |
| RF-19 | `dame una receta de ceviche` → declina y reconduce al curso |
| RNF-02 | El primer token aparece en menos de ~3 s, también con la ampliación apagada (el texto se ve mientras se escribe). Si tarda más de 2 s aparece *Pensando…*. En el log de la API, cada llamada deja `LLM openrouter <modelo>: primer token … ms, total … ms, entrada …, razonamiento …`; con el razonamiento desactivado, `razonamiento` debe ser 0 |

## Bloque D — Examen por chat

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-04 | **Comenzar examen**: sale *"Preparando tu examen con el material de la clase…"* y en unos segundos arranca con 6 preguntas **sobre el material de M1** (no inventadas ni de otro tema) |
| RF-05 | Cada pregunta trae 4 alternativas; la correcta no cae siempre en la misma letra. Tras el examen, `GET /intentos/$INT/resultado` muestra justificaciones que citan `[archivo, p. N]` |
| RF-04 | Un segundo intento trae preguntas distintas a las del primero |
| RF-06 | La cabecera pasa a *Examen en curso* y el intento guarda el modelo (`openrouter` en `usoPorAgente` del reporte) |
| RF-15 | Pide una pista o la respuesta: se niega y no filtra la correcta |
| RF-07 | Responde en lenguaje natural (`creo que es la A`): el progreso avanza y presenta la siguiente |
| RF-18 | Con `minutosLimite`, la cabecera muestra el tiempo restante |
| RF-09 | Al responder la última, comunica la **nota sobre 20**; `GET /api/v1/intentos/$INT/resultado` da la misma nota |
| RNF-01 | El progreso se actualiza al instante, sin esperar al modelo |
| RF-08 | Tras agotar los 3 intentos, iniciar otro responde `SinIntentosDisponibles` |

## Bloque E — Revisión

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-10 | `revisemos mis errores` → explica por qué falló, el concepto correcto y una pregunta de comprobación |
| RF-11 | Esa explicación cita el material con **chip de fuente válido** |
| RF-12 | Una repregunta fuera del material recibe el mismo aviso: el tutor no sale del material |
| RF-13 | `GET /api/v1/clases/$CLA/reporte` → `dudasFueraDelMaterial` incluye la consulta sobre el WACC con `conAmpliacion: false` |

## Bloque F — Nivel, reporte y control del docente

| Requisito | Cómo verificarlo |
| --- | --- |
| RF-21 | El reporte trae el nivel del alumno acorde a su nota (0–10,9 Inicial · 11–14,9 Básico · 15–17,9 Intermedio · 18–20 Avanzado) |
| RF-22 | El reporte trae `distribucionNiveles`, nivel por alumno y `temasDebilesDelGrupo` |
| RF-16 | El reporte trae `promedioNota`, `distribucionNotas`, `temasMasFallados` y `usoPorAgente` |
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
| RF-31 | Google está registrado (proyecto `tutor-pre-clase`, modo prueba: solo entran los *Test users*). **Microsoft no**: la cuenta UPC de alumno no puede registrar apps (403); ver skill `setup-oauth`. Sin su `ClientId`, *Continuar con Microsoft* solo avisa que no está configurado |
| RF-34 | El panel del docente todavía no ajusta ventana, intentos, tiempo ni modo de feedback: solo por `PUT /clases/$CLA/examen` |
| RF-35 | El modelo del tutor se cambia solo por `PUT /admin/agentes/openrouter`; no hay pantalla |
| RF-05 | La justificación cita el material, pero las **referencias** no se guardan aún en `pregunta_referencia` |
| RF-17 | Reintentar tras un fallo no pierde el avance (el historial vive en la base), pero la web no ofrece todavía un botón de reintento: el alumno reenvía el mensaje |
| RNF-11 | `data_collection: deny` se envía en cada llamada, pero que el modelo `:free` elegido tenga proveedores que lo cumplan solo se ve con la cuenta real conectada |
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

Reporta por bloques: qué requisito pasó, cuál falló y con qué evidencia, y cuáles se
saltaron (por falta de credencial o porque no están implementados). Quita el archivo de
prueba de `course-content/`. No des por bueno lo que
no comprobaste.

Un modelo real no repite frases exactas: verifica el **comportamiento** (que cite, que marque
la ampliación, que registre la respuesta, que no filtre la correcta), nunca el texto literal.
El recorrido completo son ~12 mensajes cortos, pero cada vuelta de herramientas es una
llamada: gasta buena parte del cupo gratuito diario de la cuenta conectada (50 llamadas sin
créditos). Avisa si se va a repetir en el mismo día.

# Tutor Pre-Clase

Chatbot que actúa de intermediario entre el alumno y la IA que él conecte (Gemini,
Claude o ChatGPT). La plataforma convierte ese modelo genérico en el **tutor del
curso**: le pone delante el material que subió el profesor, las herramientas del examen y
las reglas de qué puede decir en cada momento.

Cada persona conecta **su propia cuenta o clave de IA** (BYOK): el tutor conversa con ella
y el consumo corre por ella. La plataforma no guarda una clave compartida.

El diseño completo está en [`specs/`](specs/SDD-Plataforma-Evaluacion-PreClase.md): un documento principal que enlaza una parte por sección (requisitos, arquitectura, datos, flujos, contenido, API, UI y seguridad).

## Qué hace

- **Entrada con Google y dos perfiles**: la persona elige *Soy alumno* o *Soy profesor* y
  entra con su cuenta de Google. El alumno elige a su profesor y ve solo sus cursos.
- **Un solo chat por clase** para el alumno: consulta libre, examen y revisión de errores.
- **Todo abierto para el alumno**: las clases de su curso, su material y sus exámenes están
  disponibles siempre. La fecha de cada clase es solo informativa: no habilita ni cierra nada.
- **Examen generado por la IA para cada alumno**, pregunta a pregunta, a partir del
  material. No tiene fechas: se rinde cuando el alumno quiera, con los intentos y el tiempo
  que fije el profesor. El servidor registra las respuestas y califica (0–20), nunca el modelo.
- **Respuestas ancladas al material**: el contenido de la clase viaja en el contexto con
  marcas `[archivo, p. N]`; las citas se validan contra la base y **cada referencia
  descarga el archivo citado**.
- **Material condensado por IA**: al subir un archivo, la IA reescribe cada página como
  apuntes compactos con la cuenta de IA del profesor, una sola vez. El tutor lee menos y
  responde antes. El examen sale del original, y el profesor compara y elige en su panel.
- **Ampliación marcada**: si el material no cubre la duda, el tutor puede profundizar con
  su propio conocimiento en un bloque `Ampliación fuera del material:`. Viene apagada; el
  profesor la enciende por clase, y durante el examen está siempre bloqueada **en el servidor**.
- **Panel del profesor**: estado del material, reglas del examen (preguntas, intentos,
  minutos, cuándo se muestra la corrección) y los resultados: nota y
  nivel por alumno (Inicial/Básico/Intermedio/Avanzado), temas a reforzar, preguntas más
  falladas y dudas que el material no cubrió.
- **Credencial propia y cifrada**: se valida contra el proveedor antes de guardarla y nunca
  se devuelve — la web solo ve los últimos 4 caracteres.
- **Servidor MCP**: con un token personal, el alumno estudia y rinde el examen desde su
  propio asistente (Claude Code, Claude Desktop, Cursor).

## Estructura

| Proyecto | Rol |
| --- | --- |
| `src/TutorPreClase.Domain` | Entidades y reglas puras: calificación, nivel, intentos y tiempo del examen, salvaguardas |
| `src/TutorPreClase.Application` | Contexto de clase, prompt, herramientas, orquestación del tutor, nivel, reportes |
| `src/TutorPreClase.Infrastructure` | EF Core, proveedores LLM (OpenRouter, Gemini, OpenAI, Anthropic), extracción de texto, cola |
| `src/TutorPreClase.Api` | REST + SSE, inicio de sesión (OIDC) y servidor MCP |
| `src/TutorPreClase.Worker` | Extracción de texto fuera del proceso de la API |
| `web` | Angular: entrada, chat del alumno, panel del profesor y administración |
| `tests/TutorPreClase.Tests` | Reglas, orquestación del tutor y API sobre el pipeline HTTP real |

## Levantar en local

El skill [`start-local`](.claude/skills/start-local/SKILL.md) tiene los comandos completos
(API en Docker en `:5080`, web en `:4200`). En resumen:

### 1. API

En desarrollo corre sobre SQLite (`appsettings.Development.json`, `Base:Proveedor = "Sqlite"`),
crea el esquema sola y sincroniza las clases desde `course-content/`:

```bash
dotnet run --project src/TutorPreClase.Api --urls http://localhost:5080
```

Con PostgreSQL, que es lo que usa producción: `docker compose up -d` y
`dotnet dotnet-ef database update --project src/TutorPreClase.Infrastructure --startup-project src/TutorPreClase.Api`.

### 2. Frontend

```bash
cd web
npm start        # http://localhost:4200, con proxy a la API en :5080
```

### 3. Entrar

Con el cliente OAuth de Google en `secrets/` (skill `setup-oauth`) se entra con *Continuar
con Google*. Sin él, en desarrollo `GET /demo` devuelve los identificadores sembrados y
`POST /api/v1/acceso/desarrollo {"usuarioId":…}` da una sesión para ese usuario.

## Credenciales de IA (BYOK)

No hay claves en el repositorio ni en la configuración: **cada alumno conecta la suya** en
el chat, en **Mi tutor** (*Cambiar* en la cabecera), de una de tres formas:

| Pestaña | Cómo se conecta | Opciones |
| --- | --- | --- |
| Con tu clave de API (la recomendada) | Pegando una clave propia, que se comprueba con el proveedor antes de guardarla. La de Gemini es gratis y se crea en un minuto en Google AI Studio | Gemini (AI Studio), Claude (Anthropic) y ChatGPT (OpenAI) |
| Con tu cuenta | Iniciando sesión en el proveedor (OAuth con PKCE), sin copiar claves. Mientras Google no verifique la app, solo para las cuentas dadas de alta como usuarios de prueba | Gemini con la cuenta de Google |
| Desde tu asistente | Con un token personal, el Tutor se usa como servidor MCP desde Claude Code, Claude Desktop o Cursor | `/mcp` |

La credencial se cifra con ASP.NET Data Protection con un propósito distinto por usuario y
solo se descifra para las llamadas de *esa* persona. La API nunca la devuelve: la web solo
ve sus últimos 4 caracteres.

Las suscripciones de claude.ai Pro y de ChatGPT Plus no habilitan la API: para esos
proveedores hace falta una clave de API con saldo.

- Anthropic → https://console.anthropic.com/settings/keys
- OpenAI → https://platform.openai.com/api-keys
- Google AI Studio → https://aistudio.google.com/apikey

Conviene conectar más de una: si la IA del examen se queda sin cupo a mitad de un intento,
la siguiente pregunta se genera con otra de las conectadas.

`appsettings.json` solo describe qué proveedores se pueden conectar, con qué modelo y
contra qué `BaseUrl`. Uno con `"Habilitado": false` no se ofrece ni se usa: así está
**OpenRouter**, retirado porque su modelo gratuito compartido se saturaba; su pasarela sigue
en el código y se reactiva con ese ajuste.

Qué opción conviene —tiempos de respuesta, coherencia en el examen y en las dudas, límites
gratuitos y costo por uso, medidos en producción— está en
[`docs/COMPARATIVA-MODELOS.md`](docs/COMPARATIVA-MODELOS.md).

## Autenticación

La entrada pide primero el perfil (*Soy alumno* / *Soy profesor*) y después la cuenta:
*Continuar con Google*, por OIDC con PKCE. La API
canjea el código, crea o reconoce al usuario y emite **su propia sesión**, que la web envía
como `Authorization: Bearer`. Cómo registrar la aplicación en Google está en el skill `setup-oauth`. La entrada con
Microsoft se retiró: la API sigue siendo OIDC genérico, pero solo tiene configurado Google.

Fuera de producción se acepta además un esquema de desarrollo para probar sin proveedor de
identidad: las cabeceras `X-Usuario-Id` / `X-Usuario-Rol` por `curl`, y
`POST /api/v1/acceso/desarrollo` para obtener una sesión en la web. En producción ninguno
de los dos existe.

## Pruebas

```bash
dotnet test
```

Cubren la calificación y el nivel, las reglas de intentos y tiempo, el corte de la
ampliación, el rechazo de herramientas fuera de modo, el examen completo por chat, el
manejo de credenciales (cifrado, aislamiento entre alumnos, invalidación y reconexión) y
la API real (roles, autorización por recurso y streaming SSE).

## Despliegue

La API va en Docker a **Railway** (con PostgreSQL) y la web a **Vercel**. Despliega GitHub
Actions: el CI prueba la API, compila la web como en Vercel y construye la imagen; el CD
arranca solo si el CI terminó en verde sobre un push a `main` y sube primero la API y
luego la web. La guía para montar las cuentas está en
[`docs/DESPLIEGUE.md`](docs/DESPLIEGUE.md).

## Diferencias con el SDD

- La cola de extracción es en proceso (`Channel`) en lugar de RabbitMQ + MassTransit.
  El contrato `IColaExtraccion` está aislado: cambiar de transporte no toca la API ni el
  servicio de contenido.
- El frontend es una app Angular estándar, no un monorepo Nx; la separación por carpetas
  (`datos`, `chat`) sigue la de las librerías del SDD §8.1.
- No hay pantalla de administrador para elegir el modelo de cada IA (RF-35): se cambia por
  `PUT /api/v1/admin/agentes/{id}`.
- La IA todavía no ajusta la dificultad de las preguntas al nivel del alumno (RF-23).
# Tutor Pre-Clase

Chatbot que actúa de intermediario entre el alumno y el agente de IA que él elija
(Claude, ChatGPT o Kimi). La plataforma convierte ese modelo genérico en el **tutor del
curso**: le pone delante el material que subió el docente, las herramientas del examen y
las reglas de qué puede decir en cada momento.

Cada alumno conecta **su propia credencial de IA** (BYOK): el tutor conversa con su cuenta
y el consumo corre por ella. La plataforma no guarda una clave compartida.

El diseño completo está en [`specs/SDD-Plataforma-Evaluacion-PreClase.md`](specs/SDD-Plataforma-Evaluacion-PreClase.md).

## Qué hace

- **Un solo chat por clase** para el alumno: consulta libre, examen y revisión de errores.
- **Evaluación pre-clase**: el agente conduce el examen por chat; el servidor registra y
  califica (0–20), nunca el modelo.
- **Respuestas ancladas al material**: el contenido de la clase viaja en el contexto con
  marcas `[archivo, p. N]`; las citas se validan contra la base.
- **Material condensado por IA**: al subir un archivo, la IA reescribe cada página como
  apuntes compactos con la cuenta de OpenRouter del profesor, una sola vez. El tutor lee
  menos y responde antes. El examen sale del original, y el profesor compara y elige en
  su panel.
- **Ampliación marcada**: si el material no cubre la duda, el tutor puede profundizar con
  su propio conocimiento en un bloque `Ampliación fuera del material:`. El docente puede
  apagarla por clase, y durante el examen está siempre bloqueada **en el servidor**.
- **Reporte para el docente**: notas, nivel por alumno (Inicial/Básico/Intermedio/Avanzado),
  temas débiles, preguntas más falladas y dudas que el material no cubrió.
- **Credencial propia y cifrada**: el alumno la conecta desde el chat, se valida contra el
  proveedor antes de guardarla y nunca se devuelve — la web solo ve los últimos 4 caracteres.

## Estructura

| Proyecto | Rol |
| --- | --- |
| `src/TutorPreClase.Domain` | Entidades y reglas puras: calificación, nivel, intentos y tiempo del examen, salvaguardas |
| `src/TutorPreClase.Application` | Contexto de clase, prompt, herramientas, orquestación del tutor, nivel, reportes |
| `src/TutorPreClase.Infrastructure` | EF Core, proveedores LLM (Claude / OpenAI / Kimi), extracción de texto, cola |
| `src/TutorPreClase.Api` | REST + SSE |
| `src/TutorPreClase.Worker` | Extracción de texto fuera del proceso de la API |
| `web` | Angular: la pantalla única de chat del alumno |
| `tests/TutorPreClase.Tests` | Reglas, orquestación del tutor y API sobre el pipeline HTTP real |

## Levantar en local

### 1. Base de datos

Con Docker (PostgreSQL, lo que usa producción):

```bash
docker compose up -d
dotnet dotnet-ef database update --project src/TutorPreClase.Infrastructure --startup-project src/TutorPreClase.Api
```

Sin Docker, la API puede correr sobre SQLite: ya viene configurado así en
`appsettings.Development.json` (`Base:Proveedor = "Sqlite"`), que además siembra datos de
demo.

### 2. API

```bash
dotnet run --project src/TutorPreClase.Api --urls http://localhost:5080
```

En desarrollo, `GET /demo` devuelve los identificadores sembrados (docente, alumna, curso,
clase) para probar sin montar un login.

### 3. Frontend

```bash
cd web
npm start        # http://localhost:4200, con proxy a la API en :5080
```

Pega el `alumnoId` de `/demo` en la pantalla de entrada.

## Credenciales de IA (BYOK)

No hay claves en el repositorio ni en la configuración: **cada alumno conecta la suya** en
el chat, en **Mi tutor** (*Cambiar* en la cabecera), de una de tres formas:

| Pestaña | Cómo se conecta | Opciones |
| --- | --- | --- |
| Con tu cuenta | Iniciando sesión en el proveedor (OAuth con PKCE), sin copiar claves | OpenRouter (modelo gratuito) y Gemini con la cuenta de Google |
| Con tu clave de API | Pegando una clave propia, que se comprueba con el proveedor antes de guardarla | Claude (Anthropic), ChatGPT (OpenAI) y Gemini (AI Studio) |
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
contra qué `BaseUrl`.

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

Cubren la calificación y el nivel, las reglas de intentos y tiempo, la habilitación de la clase por fecha, el corte de la
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
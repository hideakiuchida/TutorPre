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
| `src/TutorPreClase.Domain` | Entidades y reglas puras: calificación, nivel, ventana de examen, salvaguardas |
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

No hay claves en el repositorio ni en la configuración: **cada alumno conecta la suya** desde
el chat (panel "Conecta tu agente"), o por API:

```bash
curl -X PUT \
  -H "X-Usuario-Id: <alumnoId>" -H "X-Usuario-Rol: Alumno" \
  -H "Content-Type: application/json" \
  -d '{"clave":"sk-ant-…"}' \
  http://localhost:5080/api/v1/alumno/credenciales/claude
```

Se valida contra el proveedor antes de guardarla, se cifra con ASP.NET Data Protection con
un propósito distinto por usuario, y solo se descifra para las llamadas de *esa* persona.
La API nunca la devuelve.

Hace falta una clave de API: las suscripciones de claude.ai Pro y de ChatGPT Plus no
habilitan la API y no existe un OAuth que permita consumirlas desde una web de terceros.

- Anthropic → https://console.anthropic.com/settings/keys
- OpenAI → https://platform.openai.com/api-keys

`appsettings.json` solo describe qué proveedores se pueden conectar, con qué modelo y
contra qué `BaseUrl`.

## Autenticación

En producción, OIDC: se configura `Oidc:Authority` y `Oidc:Audience` y la API valida el
JWT. Fuera de producción se acepta además un esquema de desarrollo por cabeceras
(`X-Usuario-Id`, `X-Usuario-Rol`) para poder probar sin un proveedor de identidad.

## Pruebas

```bash
dotnet test
```

Cubren la calificación y el nivel, las reglas de ventana e intentos, el corte de la
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
- La generación de preguntas con IA (RF-04) aún no está implementada: el docente las crea
  y aprueba por API.
- El docente no tiene pantalla propia todavía: su parte (contenido, preguntas, reporte,
  niveles) se opera por API.

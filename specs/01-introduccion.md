# 1. Introducción

[Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md) · [2. Requisitos →](02-requisitos.md)

Para el alumno la aplicación es **un solo chatbot**. Detrás de ese chat, nuestra plataforma actúa como intermediario frente al modelo de IA que el alumno usa con su propia cuenta o clave (Gemini, Claude o ChatGPT) y convierte ese modelo genérico en un **agente tutor** del curso: uno que conoce el material que el docente subió para la clase, evalúa al alumno antes de la sesión, resuelve sus dudas sobre el tema y deja al docente un reporte de cómo va el grupo y en qué nivel está cada alumno.

El agente tutor no es un modelo nuevo: es un proxy sobre los agentes de los proveedores. La plataforma aporta las tres cosas que el proveedor no tiene — el contenido del docente en el contexto, las herramientas del examen y las reglas de qué puede decir en cada momento.

**Cada alumno usa su propia cuenta de IA** (*BYOK, bring your own key*). La plataforma no guarda ninguna clave compartida ni paga por los alumnos. En *Mi tutor* hay tres formas de conectarla, y se pueden tener varias a la vez y elegir con cuál conversar:

| Forma | Qué hace el alumno | Con qué IA | Quién paga |
| --- | --- | --- | --- |
| **Con tu clave de API** (la recomendada) | Pega una vez su clave; se comprueba con el proveedor y se guarda cifrada | Gemini (Google AI Studio, gratis), ChatGPT (OpenAI) o Claude (Anthropic) | El cupo gratuito de su cuenta de AI Studio, o su cuenta de desarrollador |
| **Con tu cuenta** | Inicia sesión en Google y acepta un permiso (OAuth con PKCE), sin copiar claves | Gemini | El plan gratuito del proyecto de Google de la plataforma |
| **Desde tu asistente** | Genera un token personal y conecta su propio asistente al servidor MCP del Tutor | Claude Code, Claude Desktop u otro cliente MCP | Su suscripción del asistente |

La clave de API gratuita de Gemini es la vía principal porque funciona con cualquier cuenta de Google. La conexión *con tu cuenta* solo sirve, mientras Google no verifique la aplicación, para las cuentas dadas de alta como usuarios de prueba (§9.6).

> Nota sobre "entrar con mi cuenta de Google, Claude o ChatGPT": iniciar sesión con Google (OpenID Connect) identifica a la persona, pero no le da acceso a ningún modelo; para eso hace falta un permiso aparte, que solo Google ofrece para Gemini. Las suscripciones de claude.ai o ChatGPT no habilitan la API ni ofrecen un OAuth para que una web de terceros use ese plan: con Claude y ChatGPT la vía es la clave de API de la cuenta de desarrollador, o usar el propio asistente a través del servidor MCP.

## 1.1 Propósito

Medir los conocimientos previos del alumno, cerrar sus vacíos con un tutor que responde sobre el tema de la clase apoyado en el material oficial, y darle al docente una lectura del nivel del grupo antes de dictar la sesión.

## 1.2 Alcance

- Incluye: inicio de sesión con Google y pantallas por rol (alumno, docente, administrador), un chatbot único para el alumno (examen, revisión de errores y consultas sobre el tema), agente tutor proxy anclado al contenido del docente, conexión de la IA de cada alumno (clave de API propia, cuenta de Google para Gemini o su propio asistente por MCP), contenido por clase copiado a una carpeta, examen generado por la IA para cada alumno, calificación automática, clasificación de nivel del alumno y reportes para el docente.
- Excluye (v1): cualquier interfaz de examen fuera del chat (formularios, botones de alternativas), consumo de suscripciones de chat (claude.ai Pro, ChatGPT Plus) — no existe API para ello —, videoconferencia, pagos, integración con LMS externos (Moodle/Blackboard) y preguntas de respuesta abierta calificadas por IA.

## 1.3 Actores

| Actor | Responsabilidad principal |
| --- | --- |
| Docente | Sube el material de cada clase a la carpeta de contenido, conecta su IA para que condense ese material y revisa el reporte de avance y nivel del grupo; no escribe ni aprueba preguntas |
| Alumno | Conecta su IA (clave de API, cuenta de Google para Gemini o su asistente por MCP) y conversa con el chatbot: rinde el examen, revisa sus errores y pregunta sobre el tema de la clase |
| Administrador | Gestiona usuarios, cursos y qué modelo usa el tutor; nunca ve ni usa las credenciales de los alumnos |
| Agente tutor (proxy sobre el modelo de la IA que conectó el alumno) | Conduce el examen por chat, comunica la nota que calcula el servidor, explica los errores y responde consultas del tema priorizando el material del docente; propone el nivel del alumno y sus temas débiles a partir de los intentos |

## 1.4 Glosario

- **Examen pre-clase**: evaluación de opción múltiple asociada a una clase, disponible antes de su fecha de inicio y rendida conversando con el agente.
- **Agente tutor**: proxy de la plataforma sobre el modelo de IA; conduce el examen, revisa los errores y responde consultas, siempre con el material del docente en contexto.
- **Modo del chat**: estado de la conversación — *Evaluación* (examen en curso), *Revisión* (repaso de errores) o *Consulta* (preguntas libres sobre el tema).
- **Ampliación**: parte de una respuesta que va más allá del material del docente y se apoya en el conocimiento propio del modelo; siempre se muestra marcada como tal.
- **Nivel del alumno**: clasificación por clase o curso — Inicial, Básico, Intermedio o Avanzado.
- **Credencial del alumno (BYOK)**: el permiso de su cuenta de Google para Gemini (OAuth) o su clave de API de Claude, ChatGPT o Gemini. Se guarda cifrada y nunca se devuelve a la web.
- **Proveedor**: servicio de IA detrás del agente: Google (Gemini), Anthropic (Claude) u OpenAI (ChatGPT).
- **Modelo gratuito**: Gemini no cobra con la clave de AI Studio ni con la cuenta de Google, pero limita las llamadas por minuto y por día.
- **Servidor MCP**: punto de entrada `/mcp` por el que el asistente del propio alumno usa el Tutor como herramienta, con un token personal.
- **Herramienta (tool)**: función del servidor que el agente puede invocar, por ejemplo registrar una respuesta.
- **Carpeta de contenido**: carpeta compartida donde el docente deja el material, una subcarpeta por curso y otra por clase (PDF, PPTX, DOCX, XLSX, MD, TXT). La plataforma la sincroniza sola.
- **Contexto de clase**: texto completo del contenido de la clase, con marcas de archivo y página, que se envía al agente en cada conversación.

---

[Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md) · [2. Requisitos →](02-requisitos.md)

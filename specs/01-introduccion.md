# 1. Introducción

[Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md) · [2. Requisitos →](02-requisitos.md)

Para el alumno la aplicación es **un solo chatbot**. Detrás de ese chat, nuestra plataforma actúa como intermediario frente al modelo de IA que el alumno usa con su propia cuenta o clave (Gemini, Claude o ChatGPT) y convierte ese modelo genérico en un **agente tutor** del curso: uno que conoce el material que el docente subió para la clase, evalúa al alumno antes de la sesión, resuelve sus dudas sobre el tema y deja al docente un reporte de cómo va el grupo y en qué nivel está cada alumno.

El agente tutor no es un modelo nuevo: es un proxy sobre los agentes de los proveedores. La plataforma aporta las tres cosas que el proveedor no tiene — el contenido del docente en el contexto, las herramientas del examen y las reglas de qué puede decir en cada momento.

**Cada alumno usa su propia cuenta de IA** (*BYOK, bring your own key*). Tiene dos formas de conectarla en *Mi tutor*: **con su cuenta de Google**, para usar Gemini sin copiar claves ni poner tarjeta (OAuth con PKCE; la plataforma guarda cifrado el permiso), o **con su propia clave de API** de Claude, ChatGPT o Gemini, que pega una vez, se comprueba con el proveedor y se guarda cifrada. La plataforma no guarda una clave compartida.

> **OpenRouter está retirado (octubre de 2026).** Ya no se ofrece como opción de conexión: en producción su modelo gratuito compartido estaba saturado con frecuencia. La pasarela sigue en el código, deshabilitada por configuración (`Agentes:openrouter:Habilitado = false`; al arrancar, la API deja ese agente deshabilitado en la base). Lo que este documento describe de OpenRouter queda como referencia de esa pasarela; lo vigente es **Gemini con la cuenta de Google** (OAuth) y las **claves de API propias** de Claude, ChatGPT o Gemini.

> Nota sobre "entrar con mi cuenta de Google, Microsoft, Claude o ChatGPT": iniciar sesión con Google, Microsoft o Facebook (OpenID Connect) identifica al alumno, pero no le da acceso a ningún modelo; y las suscripciones de claude.ai o ChatGPT no habilitan la API ni ofrecen un OAuth para que una web de terceros use ese plan. OpenRouter es el único proveedor cuyo inicio de sesión entrega una credencial de IA a nombre del usuario, por eso es la vía elegida. El inicio de sesión institucional (OIDC) queda solo para identidad.

## 1.1 Propósito

Medir los conocimientos previos del alumno, cerrar sus vacíos con un tutor que responde sobre el tema de la clase apoyado en el material oficial, y darle al docente una lectura del nivel del grupo antes de dictar la sesión.

## 1.2 Alcance

- Incluye: inicio de sesión con Microsoft o Google y pantallas por rol (alumno, docente, administrador), un chatbot único para el alumno (examen, revisión de errores y consultas sobre el tema), agente tutor proxy anclado al contenido del docente, conexión de la cuenta de OpenRouter de cada alumno en el mismo inicio de sesión, contenido por clase copiado a una carpeta, examen generado por la IA para cada alumno, calificación automática, clasificación de nivel del alumno y reportes para el docente.
- Excluye (v1): cualquier interfaz de examen fuera del chat (formularios, botones de alternativas), consumo de suscripciones de chat (claude.ai Pro, ChatGPT Plus) — no existe API para ello —, videoconferencia, pagos, integración con LMS externos (Moodle/Blackboard) y preguntas de respuesta abierta calificadas por IA.

## 1.3 Actores

| Actor | Responsabilidad principal |
| --- | --- |
| Docente | Sube el material de cada clase a la carpeta de contenido y revisa el reporte de avance y nivel del grupo; no escribe ni aprueba preguntas |
| Alumno | Conecta su cuenta de Google (Gemini) o su clave de API, y conversa con el chatbot: rinde el examen, revisa sus errores y pregunta sobre el tema de la clase |
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
- **Modelo gratuito**: Gemini con la cuenta de Google no cobra, pero limita las llamadas por minuto y por día.
- **Herramienta (tool)**: función del servidor que el agente puede invocar, por ejemplo registrar una respuesta.
- **Carpeta de contenido**: carpeta compartida donde el docente deja el material, una subcarpeta por curso y otra por clase (PDF, PPTX, DOCX, XLSX, MD, TXT). La plataforma la sincroniza sola.
- **Contexto de clase**: texto completo del contenido de la clase, con marcas de archivo y página, que se envía al agente en cada conversación.

---

[Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md) · [2. Requisitos →](02-requisitos.md)

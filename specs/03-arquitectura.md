# 3. Arquitectura del sistema

[← 2. Requisitos](02-requisitos.md) · [Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md) · [4. Modelo de datos →](04-modelo-de-datos.md)

Para la v1 se propone un monolito modular en .NET con un worker separado para la extracción de texto, porque el volumen (decenas de alumnos por clase) no justifica microservicios y simplifica el despliegue. El frontend es deliberadamente delgado: dibuja el chat y no toma ninguna decisión del examen.

```mermaid
flowchart LR
    U[Alumno / Docente] -- HTTPS --> F["Frontend Angular Nx<br/>chat · gestión · reporte"]
    F -- REST + SSE --> API["API .NET 10<br/>tutor proxy · exámenes · contenido · nivel"]
    API -- "prompts + tools + contexto de clase" --> LLM["OpenRouter<br/>modelo elegido por el admin"]
    API -- evento --> Q[Cola RabbitMQ]
    API -- guarda archivos --> B[Blob Storage]
    API -- datos + texto del contenido --> DB[(PostgreSQL 16)]
    Q -- ExtraerTexto --> W[Worker de extracción]
    W -- lee archivos --> B
    W -- texto por página --> DB
```

La API nunca espera a la extracción: al subir un archivo publica un evento y el worker extrae el texto página por página y lo guarda en PostgreSQL.

En la v1 desplegada, la cola y el worker corren dentro del mismo proceso de la API (una cola en memoria y un servicio en segundo plano, con el mismo contrato), y los archivos van a un volumen de Railway. RabbitMQ y Blob Storage llegan cuando la carga lo pida. La §5.9 dibuja la arquitectura real con OpenRouter y sus flujos.

## 3.1 Módulos del backend

- **Clases y contenido**: CRUD de cursos/clases, subida de archivos, estado de extracción y armado del contexto de clase.
- **Generador de examen**: `GeneradorExamen` pide a la IA, en una llamada aparte del chat, las preguntas de cada intento a partir del contexto de clase; valida su forma y baraja las alternativas en el servidor.
- **Evaluación**: intentos, registro de respuestas, calificación determinista (sin IA) y reglas de intentos y tiempo.
- **Agente tutor (proxy)**: `AgenteTutorService` orquesta la única conversación del alumno en sus tres modos (Evaluación, Revisión, Consulta), expone las herramientas del servidor y decide qué puede ver y decir el agente en cada modo.
- **Gateway LLM**: `OpenRouterProvider`, implementación de `ILlmProvider` sobre la API de OpenRouter (formato de chat de OpenAI), con límites y registro de tokens. La credencial se le inyecta por llamada: el gateway no guarda ninguna. La interfaz deja abierta la puerta a otro gateway sin tocar el resto.
- **Credenciales**: `BovedaCredenciales` cifra, valida y entrega la credencial del alumno dueño de la conversación, y solo a él. `ConexionOpenRouter` conduce el OAuth con PKCE y deja la clave obtenida en la misma bóveda.
- **Nivel del alumno**: `NivelService` combina las notas de los intentos con el diagnóstico del tutor y mantiene el nivel vigente por clase.
- **Reportes**: agregados por clase, pregunta, nivel y agente para el docente.
- **Identidad**: autenticación OIDC y roles (Alumno, Docente, Admin).

## 3.2 Stack tecnológico

| Capa | Tecnología | Motivo |
| --- | --- | --- |
| Frontend | Angular + Nx, Angular Material | SPA responsive; monorepo para librerías compartidas |
| Backend | ASP.NET Core (.NET 10), EF Core | LTS, tipado fuerte, streaming SSE nativo |
| Worker | .NET Worker Service + MassTransit | Procesa la cola de extracción de texto con reintentos |
| Extracción de texto | PdfPig (PDF), Open XML SDK (DOCX/PPTX/XLSX) | Librerías .NET sin dependencias externas |
| Base de datos | PostgreSQL 16 | Datos relacionales y texto del contenido en una sola base |
| Archivos | Volumen de Railway en la v1; Azure Blob Storage o Amazon S3 más adelante | Almacenamiento barato del contenido original |
| Cola | RabbitMQ | Desacopla subida y extracción de texto |
| LLM | Gemini con la cuenta de Google del usuario (`GeminiProvider`) y OpenRouter (modelo `:free` elegido por el administrador, `OpenRouterProvider`), ambos vía ILlmProvider | Cada usuario conecta sus cuentas con un clic (OAuth) y elige con cuál conversar (RF-37); el modelo de cada agente se cambia por configuración |
| Autenticación | OpenID Connect con Google; sesión propia firmada por la API | Sin contraseñas propias; el rol lo decide la plataforma |
| Cifrado de credenciales | ASP.NET Data Protection, con clave por usuario | Cifrado en reposo sin montar un KMS para la v1 |
| Despliegue | API en Docker sobre Railway (con PostgreSQL) y web estática en Vercel, desplegadas por GitHub Actions solo con el CI en verde (`docs/DESPLIEGUE.md`); Azure Container Apps o AWS ECS si la universidad lo exige | Barato para la v1, un despliegue por push a `main` y escalado horizontal más adelante |

---

[← 2. Requisitos](02-requisitos.md) · [Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md) · [4. Modelo de datos →](04-modelo-de-datos.md)

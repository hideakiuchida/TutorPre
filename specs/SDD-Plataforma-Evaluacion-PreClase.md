# SDD — Chatbot Tutor Pre-Clase con Evaluación y Reportes IA

26 de septiembre de 2026 · Alonso

El diseño está repartido en un documento por sección. Este es el documento principal: enlaza cada parte y dice qué contiene. Las referencias del código y de los skills con la forma `SDD §5.2` apuntan a la sección 5, apartado 5.2.

> **Cómo llega la plataforma al modelo de IA (octubre de 2026).** Cada usuario trae su propia IA y la conecta en *Mi tutor* de tres formas: **con su clave de API** (Gemini de Google AI Studio, gratis; ChatGPT o Claude, de pago), **con su cuenta de Google** (Gemini por OAuth) o **desde su propio asistente** (el Tutor como servidor MCP). El resumen está en §1, la conexión en §5.1, los adaptadores en §5.8 y todas las llamadas al modelo, con sus diagramas, en §5.9.

| § | Documento | Contenido |
| --- | --- | --- |
| 1 | [Introducción](01-introduccion.md) | Qué es el producto, alcance, las tres formas de conectar la IA de cada alumno y glosario. |
| 2 | [Requisitos](02-requisitos.md) | Requisitos funcionales (RF) y no funcionales (RNF), quién usa cada uno y el estado de implementación. |
| 3 | [Arquitectura del sistema](03-arquitectura.md) | Componentes, tecnologías y decisiones de arquitectura. |
| 4 | [Modelo de datos](04-modelo-de-datos.md) | Entidades, relaciones y reglas de los datos. |
| 5 | [Flujos principales con el agente tutor](05-flujos-del-agente-tutor.md) | Inicio de sesión, conexión de la IA, examen, consulta, revisión, nivel, capa de proveedores, todas las formas de interacción con el modelo y servidor MCP, paso a paso y con diagramas. |
| 6 | [Módulo de contenido de clase](06-contenido-de-clase.md) | Carpeta del profesor, extracción de texto, contexto de clase, condensación y salvaguardas. |
| 7 | [API REST](07-api-rest.md) | Endpoints por rol, eventos SSE y códigos de error. |
| 8 | [Diseño de UI](08-diseno-de-ui.md) | Pantallas, componentes y principios de la interfaz. |
| 9 | [Seguridad, pruebas, riesgos y roadmap](09-seguridad-pruebas-riesgos-roadmap.md) | Seguridad y privacidad, estrategia de pruebas, riesgos, roadmap y pendientes. |

## Apartados de cada sección

**[1. Introducción](01-introduccion.md)**

- 1.1 Propósito
- 1.2 Alcance
- 1.3 Actores
- 1.4 Glosario

**[2. Requisitos](02-requisitos.md)**

- 2.1 Funcionales
- 2.2 Requisitos por rol, dependencias y orden de implementación
- 2.3 No funcionales

**[3. Arquitectura del sistema](03-arquitectura.md)**

- 3.1 Módulos del backend
- 3.2 Stack tecnológico

**[4. Modelo de datos](04-modelo-de-datos.md)**

- 4.1 DDL de las tablas clave (PostgreSQL)

**[5. Flujos principales con el agente tutor](05-flujos-del-agente-tutor.md)**

- 5.0 Inicio de sesión y rol
- 5.1 Conexión del agente
- 5.2 Examen conversacional
- 5.3 Calificación
- 5.4 Revisión como tutor
- 5.5 Consultas sobre el tema y ampliación
- 5.6 Clasificación de nivel
- 5.7 Herramientas del agente
- 5.8 Capa multi-proveedor
- 5.9 Arquitectura de la IA y flujos de extremo a extremo

**[6. Módulo de contenido de clase](06-contenido-de-clase.md)**

- 6.1 Ingesta de la carpeta
- 6.2 Contexto de clase
- 6.3 Prompt de sistema del agente tutor
- 6.4 Salvaguardas

**[7. API REST](07-api-rest.md)**

- 7.1 Ejemplo: resultado del intento
- 7.2 Ejemplo: eventos SSE de un mensaje al agente

**[8. Diseño de UI](08-diseno-de-ui.md)**

- 8.1 Componentes Angular (librerías Nx)

**[9. Seguridad, pruebas, riesgos y roadmap](09-seguridad-pruebas-riesgos-roadmap.md)**

- 9.1 Seguridad
- 9.2 Estrategia de pruebas
- 9.3 Riesgos
- 9.4 Roadmap
- 9.5 Preguntas abiertas
- 9.6 Pendientes

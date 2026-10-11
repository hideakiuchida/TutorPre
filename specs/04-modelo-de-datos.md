# 4. Modelo de datos

[← 3. Arquitectura del sistema](03-arquitectura.md) · [Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md) · [5. Flujos principales con el agente tutor →](05-flujos-del-agente-tutor.md)

El modelo gira en torno a la **Clase**: de ella cuelgan el contenido y un único examen pre-clase. Como toda la experiencia del alumno ocurre en un chat, la pieza central del lado del alumno es la **Conversación**: en modo Evaluación lleva asociado un **Intento**, y en modo Consulta existe por sí sola.

| Entidad | Campos clave | Relaciones |
| --- | --- | --- |
| Usuario | id, email, nombre, rol (Alumno, Docente, Admin), agente_preferido | N:M con Curso vía Matricula, 1:N CredencialAgente |
| CredencialAgente | id, usuario_id, agente_id, clave_cifrada, ultimos4, origen (OAuth: refresh token de Google; Pegada: clave de API), estado (Valida, Invalida), creada_en, ultimo_uso_en | la credencial BYOK del usuario; única por usuario y agente |
| TokenMcp | id, usuario_id, hash (SHA-256), ultimos4, creado_en, ultimo_uso_en, revocado_en | el token personal con que su asistente entra a `/mcp` (RF-39); uno vivo por usuario, nunca se guarda en claro |
| Curso | id, codigo, nombre, periodo | 1:N Clase |
| Matricula | usuario_id, curso_id, rol_en_curso | puente Usuario–Curso |
| Clase | id, curso_id, titulo, inicio, orden, ampliacion_permitida | 1:N ArchivoContenido, 1:1 Examen, 1:N Conversacion |
| ArchivoContenido | id, clase_id, nombre, tipo, blob_url, estado (Pendiente, Procesando, Listo, Error), hash, condensado (Pendiente, Procesando, Listo, SinCuenta, Error), error_condensado, usar_original | 1:N PaginaContenido |
| PaginaContenido | id, archivo_id, clase_id, pagina, texto, tokens, texto_condensado, tokens_condensado | texto extraído que forma el contexto de clase; los apuntes de la IA van al lado, nunca en su lugar |
| AgenteIA | id (`gemini`, `gemini-clave`, `openai`, `anthropic`), nombre_visible, proveedor, modelo, base_url, url_consola, descripcion, habilitado | 1:N Intento, 1:N CredencialAgente |
| Examen | id, clase_id, abre_en, cierra_en, max_intentos, minutos_limite, modo_feedback (Inmediato, AlFinal), publicado | 1:N Pregunta |
| Pregunta | id, examen_id, enunciado, justificacion, tema, nivel (Inicial, Básico, Intermedio, Avanzado), orden, origen (IA, Docente), aprobada | 1:N Alternativa, N:M PaginaContenido (referencias) |
| Alternativa | id, pregunta_id, letra, texto, es_correcta | — |
| Intento | id, examen_id, alumno_id, agente_id, modelo, estado (EnCurso, Enviado, EnRevision), inicio, envio, puntaje, porcentaje, preguntas_planeadas | 1:1 Conversacion, 1:N RespuestaIntento. `preguntas_planeadas` es el total del intento aunque aún no estén generadas (0 en intentos anteriores al examen pregunta a pregunta) |
| RespuestaIntento | id, intento_id, pregunta_id, alternativa_id, es_correcta, texto_original_alumno | — |
| Conversacion | id, clase_id, alumno_id, agente_id, modo (Evaluacion, Revision, Consulta), intento_id (opcional), creada_en | 1:N MensajeChat; único hilo del alumno por clase |
| MensajeChat | id, conversacion_id, pregunta_id (opcional), agente_id, rol (alumno, agente, herramienta), texto, herramienta, fuentes (jsonb), usa_ampliacion, tokens_entrada, tokens_salida, creado_en | historial del examen, la revisión y las consultas |
| DudaSinCobertura | id, clase_id, conversacion_id, texto, tema, respondida_con_ampliacion, creado_en | alimenta el reporte del docente (RF-13) |
| NivelAlumno | id, alumno_id, clase_id, nivel (Inicial, Básico, Intermedio, Avanzado), temas_debiles (jsonb), origen (Automatico, Tutor, Docente), actualizado_en | nivel vigente que ve el docente y usa el examen |

## 4.1 DDL de las tablas clave (PostgreSQL)

```sql
CREATE TABLE archivo_contenido (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  clase_id    uuid NOT NULL REFERENCES clase(id) ON DELETE CASCADE,
  nombre      text NOT NULL,
  tipo        text NOT NULL,          -- pdf | pptx | docx | md | txt
  blob_url    text NOT NULL,
  hash_sha256 text NOT NULL,
  estado      text NOT NULL DEFAULT 'Pendiente',
  error       text,
  creado_en   timestamptz NOT NULL DEFAULT now(),
  condensado        text NOT NULL DEFAULT 'Pendiente',  -- apuntes de la IA (RF-36)
  error_condensado  text,
  usar_original     boolean NOT NULL DEFAULT false,     -- el profesor prefiere el original
  UNIQUE (clase_id, hash_sha256)
);

CREATE TABLE pagina_contenido (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  archivo_id uuid NOT NULL REFERENCES archivo_contenido(id) ON DELETE CASCADE,
  clase_id   uuid NOT NULL REFERENCES clase(id) ON DELETE CASCADE,
  pagina     int NOT NULL,            -- página o número de diapositiva
  texto      text NOT NULL,
  tokens     int NOT NULL,
  texto_condensado  text,              -- null: el tutor lee el original de esta página
  tokens_condensado int NOT NULL DEFAULT 0,
  UNIQUE (archivo_id, pagina)
);

CREATE INDEX ix_pagina_clase ON pagina_contenido (clase_id);

CREATE TABLE pregunta_referencia (
  pregunta_id uuid REFERENCES pregunta(id) ON DELETE CASCADE,
  pagina_id   uuid REFERENCES pagina_contenido(id) ON DELETE CASCADE,
  PRIMARY KEY (pregunta_id, pagina_id)
);

CREATE TABLE conversacion (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  clase_id   uuid NOT NULL REFERENCES clase(id) ON DELETE CASCADE,
  alumno_id  uuid NOT NULL REFERENCES usuario(id) ON DELETE CASCADE,
  agente_id  text NOT NULL REFERENCES agente_ia(id),
  modo       text NOT NULL,           -- Evaluacion | Revision | Consulta
  intento_id uuid UNIQUE REFERENCES intento(id) ON DELETE CASCADE,
  creada_en  timestamptz NOT NULL DEFAULT now(),
  UNIQUE (clase_id, alumno_id)
);

CREATE TABLE credencial_agente (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  usuario_id    uuid NOT NULL REFERENCES usuario(id) ON DELETE CASCADE,
  agente_id     text NOT NULL REFERENCES agente_ia(id),
  clave_cifrada text NOT NULL,        -- clave de API o refresh token; nunca en claro, nunca se devuelve
  ultimos4      text NOT NULL,        -- lo unico que ve la web
  origen        text NOT NULL DEFAULT 'Pegada',  -- Pegada | OAuth
  estado        text NOT NULL DEFAULT 'Valida',
  creada_en     timestamptz NOT NULL DEFAULT now(),
  ultimo_uso_en timestamptz,
  UNIQUE (usuario_id, agente_id)
);

CREATE TABLE nivel_alumno (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  alumno_id     uuid NOT NULL REFERENCES usuario(id) ON DELETE CASCADE,
  clase_id      uuid NOT NULL REFERENCES clase(id) ON DELETE CASCADE,
  nivel         text NOT NULL,        -- Inicial | Basico | Intermedio | Avanzado
  temas_debiles jsonb NOT NULL DEFAULT '[]',
  origen        text NOT NULL,        -- Automatico | Tutor | Docente
  actualizado_en timestamptz NOT NULL DEFAULT now(),
  UNIQUE (alumno_id, clase_id)
);
```

Un alumno tiene un único hilo por clase (`UNIQUE (clase_id, alumno_id)`): el mismo chat pasa de Consulta a Evaluación cuando inicia el examen y a Revisión cuando termina, de modo que el historial de la clase queda en un solo lugar.

El orden de lectura del contenido de una clase queda determinado por `archivo_contenido.nombre` y `pagina_contenido.pagina`; con eso se arma el contexto de clase de la sección 6.2.

---

[← 3. Arquitectura del sistema](03-arquitectura.md) · [Índice del SDD](SDD-Plataforma-Evaluacion-PreClase.md) · [5. Flujos principales con el agente tutor →](05-flujos-del-agente-tutor.md)

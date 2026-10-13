# Comparativa de las opciones de modelo

Con qué IA conviene que estudie un alumno, según lo medido en producción. Cada alumno
conecta la suya en **Mi tutor** (cuenta, clave de API o su propio asistente por MCP); este
documento compara esas opciones en tiempo de respuesta, coherencia y costo.

**Qué tan firme es cada dato.** Las mediciones salen de llamadas reales registradas en
los logs de Railway entre el 30 de septiembre y el 3 de octubre de 2026 (la línea
`LLM <proveedor> <modelo>: primer token … ms, total … ms, entrada …, salida …`), más lo
observado en pantalla durante la prueba funcional del 3 de octubre. **Solo hay mediciones largas
de una opción**: Gemini con la cuenta de Google. De ChatGPT (`gpt-6-luna`) hay una sola
corrida, la prueba funcional del 12 de octubre en producción, vista en pantalla y sin leer
los logs. Las demás figuran con su precio de lista y sin datos de velocidad ni de calidad;
no se rellenaron con suposiciones.

## Resumen

- **El cupo gratuito alcanza para poco.** Gemini admite unas 20 solicitudes gratuitas al
  día por cuenta y un examen usa 7 u 8.
- **El examen ya no depende de que el modelo sea bueno.** El servidor presenta cada
  pregunta, registra la respuesta, califica y cierra (SDD §5.2, §6.4). El modelo solo
  redacta las preguntas, interpreta las respuestas ambiguas y escribe la evaluación final.
- **Cada proveedor se usa con su modelo más económico** (desde el 12 de octubre de 2026):
  `gpt-6-luna`, `claude-haiku-5-5` y `gemini-3.1-flash-lite`. Con los dos primeros, un examen
  de una clase grande cuesta alrededor de 1 centavo de dólar. No se pudo medir aún su
  velocidad ni su calidad en el tutor.

## Las opciones, lado a lado

| Opción en Mi tutor | Modelo | Primer token | Generar una pregunta | Coherencia en el examen | Coherencia en las dudas | Fallos observados | Costo |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **Gemini** con la cuenta de Google | `gemini-3.1-flash-lite` (las mediciones son de `gemini-3.8-flash`, el modelo anterior) | 2,3–8,4 s (mediana ~5,6 s); la respuesta llega toda junta | 5,1–7,3 s | Preguntas de aplicación, bien redactadas. Negó la pista sin filtrar nada | Concisa y con citas | 503 por alta demanda (4 veces); cupo de 20 solicitudes agotado a mitad de un examen | Gratis, con ese tope |
| **ChatGPT** con clave de API | `gpt-6-luna` | 2,5–3,9 s en pantalla (una sola corrida, 12 de octubre) | Sin datos de log; en pantalla, la primera pregunta llegó en menos de 10 s y las siguientes ya estaban preparadas | Preguntas de aplicación coherentes sobre la infografía de M5; negó la pista sin citar el material | Concisa y con cita válida; declinó lo ajeno al curso | Ninguno en esa corrida. Con el modelo anterior (`gpt-5-mini`) todas las llamadas fallaron: primero la cuenta sin créditos (429) y luego parámetros que el modelo rechaza (400), ya corregidos | 0,10 USD de entrada y 0,50 USD de salida por millón de tokens |
| **Claude** con clave de API | `claude-haiku-5-5` | Sin datos | Sin datos | Sin datos | Sin datos | Con el modelo anterior (`claude-haiku-4-5`) el chat respondió, pero generar el examen falló por una lista de herramientas vacía (400), ya corregido | 0,10 USD de entrada y 0,50 USD de salida por millón; 5 veces más en llamadas de más de 100 000 tokens |
| **Gemini** con clave de AI Studio | `gemini-3.1-flash-lite` | Sin datos | Sin datos | Sin datos | Sin datos | Con el modelo anterior: 503 por alta demanda y cupo gratuito agotado | Gratis con el cupo diario de Google; de pago, 0,25 USD y 1,50 USD por millón. Google lo retira el 7 de mayo de 2027 (reemplazo: `gemini-3.5-flash-lite`, 0,30 y 2,50 USD) |
| **Su asistente por MCP** (Claude Code, Claude Desktop, Cursor) | El de su suscripción | Sin datos | La genera la IA conectada en la web | Sin datos | Sin datos | Solo se probó el protocolo: conexión, lista de herramientas y lista de clases | El chat lo paga su suscripción; las preguntas, la IA conectada |

Cómo leer las columnas:

- **Primer token** es lo que tarda en aparecer la primera palabra. «En el servidor» es el
  tiempo del proveedor; «en pantalla» incluye las vueltas de herramientas.
- **Generar una pregunta** es una llamada aparte del chat, con algo de razonamiento. El
  alumno casi no la nota: la siguiente pregunta se prepara mientras responde la actual.
- **Coherencia** describe el comportamiento visto en la prueba, no una nota. Con una
  muestra tan corta sirve para detectar fallos, no para ordenar modelos por calidad.

## Cuánto consume cada uso

Tokens medidos en producción sobre el material real del curso (M1 es la clase más grande;
M2, una infografía), con un modelo gratuito que ya no se ofrece.

| Uso | Entrada | Salida | `gpt-6-luna` | `claude-haiku-5-5` | `gemini-3.1-flash-lite` de pago |
| --- | --- | --- | --- | --- | --- |
| Una duda sobre el material | ~4 300 | ~200 | 0,0005 USD | 0,0005 USD | 0,0014 USD |
| Una duda fuera del material (dos llamadas) | ~22 500 | ~110 | 0,002 USD | 0,002 USD | 0,006 USD |
| Examen completo de M1 (6 preguntas y evaluación final) | ~87 500 | ~8 700 | 0,013 USD | 0,013 USD | 0,035 USD |
| Examen completo de M2 (material pequeño) | ~11 000 | ~4 000 | 0,003 USD | 0,003 USD | 0,009 USD |

Los costos son **estimaciones**: los tokens que gastó aquel modelo multiplicados por
el precio de lista de cada modelo. Cada modelo consume distinto, sobre todo en salida
(el razonamiento cuenta como salida), así que el costo real puede variar.

Dos cosas abaratan el examen desde el 3 de octubre:

- Una respuesta inequívoca (la letra, o el texto de una única alternativa) la registra el
  servidor sin llamar al modelo.
- Con feedback al final, el material no viaja en el prompt durante el examen.

## Límites que conviene conocer

| Opción | Límite | Qué ve el alumno |
| --- | --- | --- |
| Gemini con cuenta de Google | Unas 20 solicitudes al día por cuenta; a ratos, alta demanda (503) | «Se acabó tu cupo… podrás seguir en unos N min», o «El modelo está saturado… no es tu cupo» y puede reintentar en segundos |
| Clave de API de pago | El saldo de la cuenta | «Tu cuenta no tiene saldo»: cargar créditos o elegir otro tutor |

Si la IA del examen se queda sin cupo a mitad de un intento, la siguiente pregunta se
genera con otra IA que el alumno tenga conectada, para que el reloj no corra en vano. Por
eso conviene conectar más de una.

## Cómo repetir la medición

1. Conecta la opción a medir en **Mi tutor** y elígela en «Conversas con».
2. En una misma clase, haz una duda del material, una duda fuera del material y un examen
   completo.
3. En Railway → servicio `api` → *Logs*, filtra `"primer token"`: cada llamada deja el
   proveedor, el modelo, el primer token, el total y los tokens de entrada, salida y
   razonamiento.
4. Multiplica los tokens por el precio vigente del modelo y actualiza las tablas.

Los precios de lista se consultaron el 12 de octubre de 2026 en las páginas oficiales de cada
proveedor ([GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna),
[Claude Haiku 5.5](https://platform.claude.com/docs/en/about-claude/pricing),
[Gemini](https://ai.google.dev/gemini-api/docs/pricing)). Google tiene un modelo aún más barato,
`gemini-2.5-flash-lite` (0,10 y 0,40 USD), pero solo lo ofrece a cuentas que ya lo usaban.

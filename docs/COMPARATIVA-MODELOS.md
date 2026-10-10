# Comparativa de las opciones de modelo

> **Octubre de 2026:** OpenRouter se retiró de las opciones de conexión (su modelo gratuito compartido se saturaba con frecuencia). Sus filas quedan como referencia de lo medido; hoy se elige entre Gemini con la cuenta de Google y una clave de API propia.

Con qué IA conviene que estudie un alumno, según lo medido en producción. Cada alumno
conecta la suya en **Mi tutor** (cuenta, clave de API o su propio asistente por MCP); este
documento compara esas opciones en tiempo de respuesta, coherencia y costo.

**Qué tan firme es cada dato.** Las mediciones salen de 57 llamadas reales registradas en
los logs de Railway entre el 30 de septiembre y el 3 de octubre de 2026 (la línea
`LLM <proveedor> <modelo>: primer token … ms, total … ms, entrada …, salida …`), más lo
observado en pantalla durante la prueba funcional del 3 de octubre. **Solo hay mediciones
de dos opciones**: OpenRouter y Gemini con la cuenta de Google. Las demás figuran con su
precio de lista y sin datos de velocidad ni de calidad; no se rellenaron con suposiciones.

## Resumen

- **Las dos opciones gratuitas no aguantan un grupo.** Gemini admite unas 20 solicitudes
  gratuitas al día por cuenta y un examen usa 7 u 8; el modelo gratuito de OpenRouter se
  satura con frecuencia.
- **El examen ya no depende de que el modelo sea bueno.** El servidor presenta cada
  pregunta, registra la respuesta, califica y cierra (SDD §5.2, §6.4). El modelo solo
  redacta las preguntas, interpreta las respuestas ambiguas y escribe la evaluación final.
- **La opción de pago más barata sería `gpt-5-mini`**, a unos 4 centavos de dólar por un
  examen de una clase grande. No se pudo medir su velocidad ni su calidad.

## Las opciones, lado a lado

| Opción en Mi tutor | Modelo | Primer token | Generar una pregunta | Coherencia en el examen | Coherencia en las dudas | Fallos observados | Costo |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **OpenRouter** con la cuenta del alumno | `qwen/qwen3.8-27b:free` | ~0,2 s en el servidor; 0,8–3,5 s en pantalla | 4,5–14,5 s | Preguntas del material, con 4 alternativas plausibles. No usa bien las herramientas: dijo «Registrada» sin registrar e inventó preguntas (hoy el servidor lo bloquea) | Responde con el material y declina lo ajeno al curso. Abrevia los nombres de archivo al citar (`[M1, p. 44]`) | Saturación (429) varias veces en una tarde; llamadas de 52 a 93 s; una pregunta cortada por el tope de salida | Gratis: 50 llamadas al día sin créditos |
| **Gemini** con la cuenta de Google | `gemini-3.8-flash` | 2,3–8,4 s (mediana ~5,6 s); la respuesta llega toda junta | 5,1–7,3 s | Preguntas de aplicación, bien redactadas. Negó la pista sin filtrar nada | Concisa y con citas | 503 por alta demanda (4 veces); cupo de 20 solicitudes agotado a mitad de un examen | Gratis, con ese tope |
| **ChatGPT** con clave de API | `gpt-5-mini` | Sin datos | Sin datos | Sin datos | Sin datos | La cuenta usada en la prueba no tenía créditos | 0,25 USD de entrada y 2,00 USD de salida por millón de tokens |
| **Claude** con clave de API | `claude-haiku-4-5` | Sin datos | Sin datos | Sin datos | Sin datos | No se conectó ninguna clave | 1 USD de entrada y 5 USD de salida por millón |
| **Gemini** con clave de AI Studio | `gemini-3.8-flash` | Sin datos | Sin datos | Sin datos | Sin datos | No se conectó ninguna clave | Gratis con unas 20 solicitudes al día; de pago, 0,75 USD y 3,75 USD por millón (se duplica el 1 de enero de 2027) |
| **Su asistente por MCP** (Claude Code, Claude Desktop, Cursor) | El de su suscripción | Sin datos | La genera la IA conectada en la web | Sin datos | Sin datos | Solo se probó el protocolo: conexión, lista de herramientas y lista de clases | El chat lo paga su suscripción; las preguntas, la IA conectada |

Cómo leer las columnas:

- **Primer token** es lo que tarda en aparecer la primera palabra. «En el servidor» es el
  tiempo del proveedor; «en pantalla» incluye las vueltas de herramientas.
- **Generar una pregunta** es una llamada aparte del chat, con algo de razonamiento. El
  alumno casi no la nota: la siguiente pregunta se prepara mientras responde la actual.
- **Coherencia** describe el comportamiento visto en la prueba, no una nota. Con una
  muestra tan corta sirve para detectar fallos, no para ordenar modelos por calidad.

## Cuánto consume cada uso

Tokens medidos con el modelo gratuito de OpenRouter sobre el material real del curso
(M1 es la clase más grande; M2, una infografía).

| Uso | Entrada | Salida | `gpt-5-mini` | `claude-haiku-4-5` | `gemini-3.8-flash` de pago |
| --- | --- | --- | --- | --- | --- |
| Una duda sobre el material | ~4 300 | ~200 | 0,0015 USD | 0,005 USD | 0,004 USD |
| Una duda fuera del material (dos llamadas) | ~22 500 | ~110 | 0,006 USD | 0,023 USD | 0,017 USD |
| Examen completo de M1 (6 preguntas y evaluación final) | ~87 500 | ~8 700 | 0,04 USD | 0,13 USD | 0,10 USD |
| Examen completo de M2 (material pequeño) | ~11 000 | ~4 000 | 0,011 USD | 0,031 USD | 0,023 USD |

Los costos son **estimaciones**: los tokens que gastó el modelo gratuito multiplicados por
el precio de lista de cada modelo. Cada modelo consume distinto, sobre todo en salida
(el razonamiento cuenta como salida), así que el costo real puede variar.

Dos cosas abaratan el examen desde el 3 de octubre:

- Una respuesta inequívoca (la letra, o el texto de una única alternativa) la registra el
  servidor sin llamar al modelo.
- Con feedback al final, el material no viaja en el prompt durante el examen.

## Límites que conviene conocer

| Opción | Límite | Qué ve el alumno |
| --- | --- | --- |
| OpenRouter gratuito | 50 llamadas al día sin créditos; el modelo se comparte entre todos sus usuarios | «El modelo está saturado… no es tu cupo», y puede reintentar en segundos |
| Gemini con cuenta de Google | Unas 20 solicitudes al día por cuenta | «Se acabó tu cupo… podrás seguir en unos N min» |
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

Los precios de lista se consultaron el 3 de octubre de 2026 en páginas de terceros
([gpt-5-mini](https://pricepertoken.com/pricing-page/model/openai-gpt-5-mini),
[Claude Haiku 4.5](https://benchlm.ai/anthropic/api-pricing),
[Gemini 3.8 Flash](https://www.eesel.ai/blog/gemini-3-8-flash-pricing)), no en las páginas
oficiales de cada proveedor: confírmalos allí antes de presupuestar.

---
name: setup-oauth
description: Registra el Tutor Pre-Clase en Google Cloud y en Microsoft Entra para que funcionen los botones "Continuar con Google" y "Continuar con Microsoft (Outlook)", y la conexión "Entrar con Gemini (tu cuenta de Google)" — crea proyecto, pantalla de consentimiento, cliente OAuth, activa la API de Gemini y su permiso, y deja que la API lea las credenciales desde secrets/. Úsalo cuando pidan configurar, registrar o arreglar el inicio de sesión con Google, Microsoft u Outlook, conectar Gemini, o cuando esos botones digan "aún no está configurado".
---

# Configurar el inicio de sesión (Google y Microsoft)

Para mostrar "Elige una cuenta", Google y Microsoft exigen que **la app** esté registrada con
un ID de cliente. Se hace una vez por entorno, con la cuenta de quien administra la app.
Alumnos y profesores no registran nada: solo pulsan el botón.

## Reglas (no negociables)

- **Tú no inicias sesión ni escribes contraseñas o códigos**: si Google o Microsoft los piden,
  para y que lo haga la persona.
- **Tú no aceptas términos ni políticas** (p. ej. *Google API Services: User Data Policy*):
  para, explícale qué acepta y que marque la casilla ella.
- **Nunca copies, leas ni muestres el ID secreto.** La persona descarga o copia las
  credenciales a `secrets/` en la raíz del repo. Tú solo listas nombres de archivo.
- **No crees cuentas** (Azure, Microsoft 365 Developer, etc.): si hacen falta, dilo y que decida.
- Antes de empezar, pide confirmación explícita: crea configuración permanente en sus cuentas.
- Comprueba que `secrets/` esté en `.gitignore` (`git check-ignore -q secrets/x`); si no, añádela
  **antes** de que la persona guarde nada ahí.

## 0. Preparar

Invoca `claude-in-chrome` y carga las herramientas en una sola llamada:

```
select:mcp__claude-in-chrome__tabs_context_mcp,mcp__claude-in-chrome__tabs_create_mcp,mcp__claude-in-chrome__navigate,mcp__claude-in-chrome__computer,mcp__claude-in-chrome__get_page_text,mcp__claude-in-chrome__browser_batch,mcp__claude-in-chrome__tabs_close_mcp
```

`tabs_context_mcp` → `tabs_create_mcp` (pestaña propia; ciérrala al terminar). Si la extensión
no está conectada, detente y avisa.

Redirecciones (deben coincidir exacto con `Acceso:UrlRetorno` + `/{proveedor}`):

| Entorno | Google | Microsoft |
| --- | --- | --- |
| Local | `http://localhost:4200/entrar/google` | `http://localhost:4200/entrar/microsoft` |
| Producción | `https://<dominio>/entrar/google` | `https://<dominio>/entrar/microsoft` |

## 1. Google Cloud (probado)

1. `https://console.cloud.google.com/projectcreate` → nombre **Tutor Pre-Clase** → *Create*.
   En la notificación, *Select Project* (ID `tutor-pre-clase` o el que asigne Google).
2. `https://console.cloud.google.com/auth/overview?project=<id>` → *Get started*:
   - App name: **Tutor Pre-Clase**; User support email: su Gmail.
   - Audience: **External** (queda en *Testing*: solo entran los usuarios de prueba).
   - Contact information: su Gmail.
   - Finish: **la casilla de la User Data Policy la marca la persona** → *Continue* → *Create*.
3. `…/auth/audience?project=<id>` → *Test users* → *Add users* → su Gmail (y los de quienes
   vayan a probar; máximo 100 en modo prueba) → *Save*.
4. `…/auth/clients/create?project=<id>`:
   - Application type: **Web application**; Name: **Tutor Pre-Clase web (local)**.
   - *Authorized redirect URIs* → *Add URI* → la de la tabla → *Create*.
5. Sale **OAuth client created**. Para ahí y pide a la persona **Download JSON** y guardarlo en
   `secrets/` sin renombrarlo (`client_secret_<id>.apps.googleusercontent.com.json`). El secreto
   no se puede volver a descargar después de cerrar ese cuadro (se puede crear otro).

Google avisa que los cambios pueden tardar de 5 minutos a unas horas en aplicarse.

### 1b. Gemini con la cuenta de Google (RF-37)

Usa el mismo cliente OAuth web. Tres cambios en el proyecto, **cada uno con el OK de la
persona** (cambian la configuración de su cuenta):

1. `https://console.cloud.google.com/apis/library/generativelanguage.googleapis.com?project=<id>`
   → *Enable* (la **Gemini API**, servicio `generativelanguage.googleapis.com`).
2. `…/auth/scopes?project=<id>` → *Add or remove scopes* → *Manually add scopes*:
   `https://www.googleapis.com/auth/cloud-platform` y
   `https://www.googleapis.com/auth/generative-language.retriever` (una por línea) → *Add to
   table* → *Update* → **Save**. Con solo el primero, la API de Gemini responde 403
   "insufficient authentication scopes".
   Queda en *Your sensitive scopes*: en modo *Testing* basta con ser usuario de prueba.
3. El cliente web → *Authorized redirect URIs* → `http://localhost:4200/conectar/gemini` y
   `https://<web>/conectar/gemini` → **Save**. Comprueba que se guardaron releyendo la página.

La API toma el ClientId y el secreto del cliente de Google de `Acceso:Proveedores:google`
(secrets/ o variables) para el agente `gemini`. En producción, `Agentes__gemini__UrlRetorno`
apunta a la web publicada.

Google aplica los límites de la API de Gemini **por proyecto**: todos los usuarios conectados
comparten el plan gratuito de este proyecto.

## 2. Microsoft Entra

1. `https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/CreateApplicationBlade/quickStartType~/null/isMSAApp~/false`
   - **Si responde "No tiene acceso" (403)**: la cuenta es de una organización que no deja a sus
     usuarios registrar apps (pasa con cuentas de alumno de la UPC). Para ahí y ofrece:
     a) que TI de la universidad registre la app con estos datos, o
     b) que la persona entre con una cuenta personal de Microsoft y cree su propio directorio de
        Entra (gratis; crear esa cuenta o directorio lo hace ella, no tú).
2. Registro:
   - Nombre: **Tutor Pre-Clase**.
   - Tipos de cuenta: **"Cuentas de cualquier directorio organizativo y cuentas personales de
     Microsoft"** (la API usa el punto `common`: Outlook.com, Hotmail y cuentas de organización).
   - URI de redirección: plataforma **Web**, la de la tabla → *Registrar*.
3. *Certificados y secretos* → *Nuevo secreto de cliente* → descripción "Tutor Pre-Clase",
   vencimiento 6 meses → *Agregar*.
4. Para ahí y pide a la persona crear `secrets/microsoft.json` con el **Id. de aplicación
   (cliente)** de *Información general* y el **Valor** del secreto (se ve una sola vez):

   ```json
   { "client_id": "…", "client_secret": "…" }
   ```

## 3. Conectar con la API

La API lee `secrets/` al arrancar (`Acceso:CarpetaSecretos`, `CredencialesEnCarpeta`): el JSON
de Google (sección `web`) y `microsoft.json`. Lo configurado en `tutor.env` o variables de
entorno tiene prioridad.

1. Verifica **solo nombres**, nunca contenido:
   ```bash
   ls secrets/ | sed -E 's/[0-9]{6,}[^_.]*/<id>/g'
   git check-ignore -q secrets/x && echo ignorada
   ```
2. Reinicia la API con el paso 2 del skill `start-local` (monta `secrets/` en `/secrets`).
3. Comprueba sin exponer valores:
   ```bash
   curl -s http://localhost:5080/api/v1/acceso/proveedores   # configurado: true
   curl -s -X POST http://localhost:5080/api/v1/acceso/google/inicio | python -c "import json,sys,urllib.parse as u;x=u.urlparse(json.load(sys.stdin)['url']);q=u.parse_qs(x.query);print(x.netloc+x.path, q['redirect_uri'][0], q['code_challenge_method'][0], 'client_id presente' if q.get('client_id',[''])[0] else 'SIN client_id')"
   ```
4. En Chrome, `http://localhost:4200` → *Continuar con Google*. El inicio de sesión lo hace la
   persona. La primera vez sale *Soy alumno / Soy profesor*.

## Problemas típicos

| Síntoma | Causa |
| --- | --- |
| `redirect_uri_mismatch` | La URI registrada no es exacta (http vs https, puerto, barra final) |
| `access_denied` / "app no verificada" en Google | Su cuenta no está en *Test users* (modo *Testing*) |
| `AADSTS50020` o "cuenta no existe en el inquilino" | El registro de Microsoft no admite cuentas personales: cambia los tipos de cuenta |
| El botón dice "aún no está configurado" | La API no encontró credenciales: nombre del archivo, carpeta montada o `configurado:false` |
| `acceso_rechazado` tras volver | Secreto equivocado o vencido; crea uno nuevo |
| `configurado:false` aunque el JSON está en `secrets/` | El JSON se dañó (por ejemplo, quedó cortado). Compruébalo **sin leer valores**: `python -c "import json,glob;json.load(open(glob.glob('secrets/client_secret_*.json')[0]))" && echo valido`. Si falla, la persona lo repara o crea un secreto nuevo en el cliente (*Add secret*) |
| Gemini: "La API de Gemini no está activada" | Falta el paso 1b.1 en el proyecto |
| Gemini: `invalid_scope` o falta el permiso al volver | Falta el paso 1b.2 (scopes `cloud-platform` y `generative-language.retriever` en *Data Access*) |
| Gemini: "rechazó esa credencial" y en el log `insufficient authentication scopes` | Falta pedir o conceder `generative-language.retriever`: revisa `Agentes:gemini:Scope` y *Data Access* |
| Gemini: `access_denied` "The developer hasn't given you access" | La app está en *Testing* y el usuario no está en *Test users* (los permisos sensibles lo exigen) |
| Gemini: 429 frecuentes | El plan gratuito del proyecto se agotó para todos: espera o activa facturación en el proyecto |

## Al terminar

Cierra tu pestaña y reporta qué quedó registrado (proyecto, cliente, registro de Microsoft) y
qué falta, sin mencionar ningún valor de las credenciales.

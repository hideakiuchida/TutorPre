# Despliegue en Railway y Vercel

La API (.NET) corre como contenedor en **Railway**, junto a su PostgreSQL. La web
(Angular) se publica como sitio estático en **Vercel**. **Quien despliega es GitHub
Actions**, no las plataformas: el CD (`.github/workflows/cd.yml`) solo arranca cuando el
CI termina en verde sobre un push a `main`, y despliega primero la API y después la web.

Hay un solo entorno, **producción**:

| | |
| --- | --- |
| Repositorio | `hideakiuchida/TutorPre`, rama `main` |
| Railway | proyecto `tutor-preclase`, environment `production`, servicios `api` y `Postgres` |
| API | https://api-production-9a65.up.railway.app |
| Vercel | proyecto `tutor-preclase` (sin conexión a Git), Root Directory `web` |
| Web | https://tutor-preclase.vercel.app |

> **Ningún dominio está en el código.** Las URLs llegan por variables: `API_URL` en
> Vercel; `Cors__Origenes__0`, `Acceso__UrlRetorno` y `Agentes__openrouter__UrlRetorno` en
> Railway. Esta tabla es documentación.

## Qué hay en el repositorio

| Archivo | Para qué |
| --- | --- |
| `Dockerfile` | Imagen de la API. Copia `course-content/` a `/contenido`, migra la base al arrancar y escucha en `$PORT` (8080) |
| `railway.toml` | Builder Dockerfile, healthcheck en `/salud`, reinicio limitado |
| `web/vercel.json` | Build `npm run build:deploy`, salida `dist/web/browser`, rutas SPA, cabeceras |
| `web/scripts/set-env.mjs` | Escribe `environment.prod.ts` con `API_URL`; falla si falta |
| `.github/workflows/ci.yml` | Pruebas .NET, build de la web como en Vercel, build de la imagen |
| `.github/workflows/cd.yml` | `railway up` → espera el estado final → `vercel deploy --prebuilt --prod` |

**Ninguna plataforma está conectada al repositorio.** Railway recibe el código con
`railway up` y Vercel la web ya compilada con `vercel deploy --prebuilt`; así nada se
publica sin el CI en verde y no hay dos desplegadores compitiendo.

## 1. Railway

1. Proyecto `tutor-preclase` con *Database* → *PostgreSQL*.
2. Servicio vacío **`api`** (*Empty Service*), **sin repositorio conectado**.
3. **Volumen** del servicio `api` montado en **`/datos`**: archivos subidos y claves de
   Data Protection, que cifran las credenciales de los alumnos. Sin volumen, cada
   despliegue las pierde y los alumnos tienen que volver a conectar OpenRouter.
4. **Variables** del servicio `api`:

   | Variable | Valor | Quién la pone |
   | --- | --- | --- |
   | `ConnectionStrings__Postgres` | `Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}` | ya está |
   | `Acceso__Administradores__0` | correo del administrador | ya está |
   | `Cors__Origenes__0` | `https://tutor-preclase.vercel.app` | ya está |
   | `Acceso__UrlRetorno` | `https://tutor-preclase.vercel.app/entrar` | ya está |
   | `Agentes__openrouter__UrlRetorno` | `https://tutor-preclase.vercel.app/conectar/openrouter` | ya está |
   | `Acceso__ClaveSesion` | 48 bytes aleatorios en Base64 | la persona |
   | `Acceso__Proveedores__google__ClientId` | del JSON de Google en `secrets/` | la persona |
   | `Acceso__Proveedores__google__ClientSecret` | del JSON de Google en `secrets/` | la persona |

   > **La cadena de conexión va en formato clave=valor**, no como la `DATABASE_URL`
   > (`postgresql://…`) de Railway: Npgsql no acepta esa forma. Con las referencias
   > `${{Postgres.…}}` nadie ve la contraseña.
   >
   > **`Acceso__ClaveSesion` tiene que ser Base64.** Con otro texto la API arranca pero
   > responde 500 a todo. Genérala en tu equipo y pégala directamente en Railway:
   >
   > ```bash
   > python -c "import os,base64;print(base64.b64encode(os.urandom(48)).decode())"
   > ```

5. *Settings* → *Networking* → dominio generado, puerto **8080**.
6. *Project Settings* → *Tokens* → token **de proyecto** del environment `production`
   → secreto `RAILWAY_TOKEN` en GitHub. El token de cuenta (*Account Settings*) no sirve.

## 2. Vercel

1. Proyecto creado sin Git con el CLI (tras `npx vercel login`):

   ```bash
   npx vercel project add tutor-preclase
   npx vercel link --yes --project tutor-preclase   # crea .vercel/ (ignorado por git)
   ```

2. *Settings* → *Build and Deployment* → **Root Directory `web`**.
3. Variable **`API_URL`** = `https://api-production-9a65.up.railway.app/api` en
   *Production*, **no sensible**:

   ```bash
   printf 'https://…/api' | npx vercel env add API_URL production --no-sensitive
   ```

   > El equipo marca las variables como *Sensitive* por defecto, y una variable sensible
   > no la puede leer `vercel pull`: el build fallaría con «Falta API_URL». Compruébalo
   > con `vercel env pull <archivo> --environment=production`: debe salir la URL, no
   > `[SENSITIVE]`.

4. *Account Settings* → *Tokens* → token para el CD → secreto `VERCEL_TOKEN`.
5. `VERCEL_ORG_ID` y `VERCEL_PROJECT_ID` están en `.vercel/project.json` (`orgId` y
   `projectId`).

## 3. GitHub

*Settings* → *Secrets and variables* → *Actions*, a nivel de **repositorio** (en un
repositorio privado del plan gratuito no hay GitHub Environments):

| Tipo | Nombre | Valor |
| --- | --- | --- |
| Secret | `RAILWAY_TOKEN` | token de proyecto de Railway |
| Secret | `VERCEL_TOKEN` | token de Vercel |
| Secret | `VERCEL_ORG_ID` | `orgId` de `.vercel/project.json` |
| Secret | `VERCEL_PROJECT_ID` | `projectId` de `.vercel/project.json` |
| Variable | `RAILWAY_SERVICE` | `api` |

## 4. Google

En el cliente OAuth de Google (skill `setup-oauth`):

- *Authorized JavaScript origins*: `https://tutor-preclase.vercel.app`
- *Authorized redirect URIs*: `https://tutor-preclase.vercel.app/entrar/google`

La API vuelve a `{Acceso__UrlRetorno}/{proveedor}`, así que la URI lleva `/google` al
final; sin ella Google responde `Error 400: redirect_uri_mismatch`. El cambio tarda de
unos minutos a unas horas en aplicarse.

Mientras la app esté en **Testing** (*Google Auth Platform* → *Audience*), solo entran
el propietario del proyecto y los usuarios de prueba que se añadan ahí (hasta 100). Para
abrirla a cualquier cuenta hay que completar *Branding* y pulsar *Publish app*.

OpenRouter no necesita registro: la URL de retorno la manda la API.

## 5. Desplegar

Un push a `main` (o *Re-run* del último CI) lanza el CD. En Actions:
**CI** en verde → **CD** `production · <mensaje>` → *API → Railway* → *Web → Vercel*.
Si falta un secreto, el primer paso lo dice por su nombre.

```bash
curl https://api-production-9a65.up.railway.app/salud                     # {"estado":"ok"}
curl https://api-production-9a65.up.railway.app/api/v1/acceso/proveedores # google: configurado
```

## Notas

- **La base se migra sola al arrancar** (`Base__MigrarAlArrancar=true` en la imagen). Las
  migraciones son de PostgreSQL; se generan con `ASPNETCORE_ENVIRONMENT=Production` para
  que no salgan con tipos de SQLite.
- **PostgreSQL solo guarda fechas en UTC**: `AppDbContext` convierte las horas de clase
  (Lima, -05:00) al guardar, sin cambiar el instante.
- **El contenido del docente viaja en la imagen** (`course-content/`): subir material es
  hacer commit en esa carpeta y dejar que el CD despliegue.
- **Sin `--mount=type=cache` en el Dockerfile**: Railway exige un id de montaje por
  servicio y rompe el build.

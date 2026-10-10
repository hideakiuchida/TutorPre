---
name: start-local
description: Levanta el Tutor Pre-Clase en local — API .NET en Docker en :5080 y el chat Angular en :4200, con base de datos y datos de demo listos. Úsalo cuando pidan arrancar, levantar, correr o abrir el proyecto, cuando necesites la app en marcha para verificar un cambio, o para correr las pruebas .NET.
---

# Levantar el Tutor Pre-Clase

Deja la plataforma corriendo y devuelve las URLs y los identificadores de prueba. Nada más:
no recorre funcionalidades ni toca datos salvo el sembrado de demo.

**Por qué Docker.** En este equipo Windows tiene activado Smart App Control, que bloquea las
DLL recién compiladas (*"Una directiva de Control de aplicaciones bloqueó este archivo"*).
Por eso la API y las pruebas .NET corren dentro de un contenedor con el SDK de .NET 10. No
toques la configuración de seguridad de Windows para evitarlo. La web (Angular) sí corre en
Windows.

Los comandos son para Git Bash: `MSYS_NO_PATHCONV=1` evita que convierta las rutas del
contenedor (`/repo`, `/datos`) en rutas de Windows.

## 1. Comprobar si ya está arriba

```bash
curl -s --max-time 2 http://localhost:5080/salud && echo " <- API ya levantada"
curl -s --max-time 2 -o /dev/null http://localhost:4200 && echo "web ya levantada"
docker ps --filter name=tutor-api --format '{{.Names}} {{.Status}}'
```

Si ya responden, no arranques otra instancia: usa esas y salta al paso 4. Excepción: el
contenedor copia el código **al arrancar**, así que si cambiaste código .NET desde entonces,
repite el paso 2 para que la API lo recoja.

## 2. API (Docker)

```bash
cd <raíz del repo>
docker rm -f tutor-api 2>/dev/null
ENVF=""; [ -f tutor.env ] && ENVF="--env-file tutor.env"
mkdir -p secrets

MSYS_NO_PATHCONV=1 docker run -d --name tutor-api -p 5080:5080 $ENVF \
  -v "$(pwd -W):/repo:ro" -v "$(pwd -W)/course-content:/contenido:ro" -v "$(pwd -W)/secrets:/secrets:ro" \
  -v tutor-datos:/datos -v tutor-claves:/root/.aspnet \
  -e ASPNETCORE_ENVIRONMENT=Development -e ASPNETCORE_URLS=http://0.0.0.0:5080 \
  -e ConnectionStrings__Sqlite="DataSource=/datos/tutorpreclase-dev.db" \
  -e Almacen__Raiz=/datos/almacen -e CarpetaContenido__Ruta=/contenido -e Acceso__CarpetaSecretos=/secrets \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
    rm -rf /work && mkdir /work &&
    tar -C /repo --exclude=node_modules --exclude=bin --exclude=obj --exclude=.git --exclude=secrets \
        --exclude=web --exclude="*.db" --exclude=almacen --exclude=course-content -cf - . | tar -C /work -xf - &&
    cd /work && dotnet run --project src/TutorPreClase.Api --no-launch-profile'

for i in $(seq 1 60); do curl -s --max-time 2 http://localhost:5080/salud && break; sleep 3; done
```

- El repo se monta **solo lectura** y se copia dentro sin `bin/`, `obj/` ni `node_modules`:
  lo compilado en Linux no se mezcla con lo de Windows.
- **`course-content/` se monta aparte y en vivo** (`/contenido`): es la carpeta del docente.
  La API la sincroniza al arrancar y cada 30 s, así que lo que se copie o quite ahí se ve
  sin reiniciar el contenedor. Estructura: `course-content/<correo del profesor>/<CÓDIGO -
  Nombre del curso>/<N - Título de la clase>/<archivos>` (PDF, PPTX, DOCX, XLSX, MD, TXT).
  Cada profesor administra solo su carpeta.
- **`secrets/` se monta solo lectura** (`/secrets`, ignorada por git): de ahí la API lee las
  credenciales OAuth (el JSON de Google y `microsoft.json`). Para registrarlas, skill
  `setup-oauth`. Nunca abras ni muestres esos archivos.
- **Volúmenes:** `tutor-datos` guarda la base SQLite y el contenido subido; `tutor-claves`,
  las claves de cifrado de las credenciales. Sin ese segundo volumen, al recrear el
  contenedor la cuenta de OpenRouter conectada ya no se podría descifrar y habría que
  reconectarla.
- El primer arranque descarga la imagen del SDK (~1 GB) y compila: puede tardar un par de
  minutos. Si tras el bucle no responde, mira `docker logs tutor-api` en vez de reintentar
  a ciegas.

Con PostgreSQL en lugar de SQLite (lo que usa producción): `docker compose up -d` levanta
la base, y la API arranca sin la sección `Base:Proveedor` de `appsettings.Development.json`
y con `ConnectionStrings__Postgres` apuntando al servicio. **Ojo:** la migración
`20260928215909_CredencialesByok` se generó contra SQLite y hoy rompe `database update` en
PostgreSQL; hay que regenerarla antes.

### 2b. Pruebas .NET (Docker)

```bash
MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  mkdir /work &&
  tar -C /repo --exclude=node_modules --exclude=bin --exclude=obj --exclude=.git \
      --exclude=web --exclude="*.db" --exclude=almacen --exclude=course-content -cf - . | tar -C /work -xf - &&
  cd /work && dotnet test'
```

## 3. Frontend (Windows)

```bash
(cd web && npm start > ../web.log 2>&1 &)
for i in $(seq 1 40); do curl -s -o /dev/null http://localhost:4200 && break; sleep 2; done
```

Si `node_modules` no existe, primero `cd web && npm ci`. El servidor de desarrollo reenvía
`/api` a `localhost:5080` (`web/proxy.conf.json`), que es el puerto publicado del contenedor.

## 4. Datos para entrar

```bash
curl -s http://localhost:5080/demo
```

Devuelve `docenteId`, `alumnoId`, `cursoId` y `claseId` del curso sembrado. El `alumnoId` es
lo que se pega en la pantalla de entrada de la web. Los IDs cambian cada vez que se borra el
volumen `tutor-datos`.

Informa al usuario de:

- **Web** → http://localhost:4200: página de entrada, solo OAuth (*Continuar con Google*; necesita su
  `ClientId` en `tutor.env`).
  La primera vez que alguien entra elige si es alumno (y de qué profesor) o profesor. Para
  probar en Chrome sin OAuth: `POST /api/v1/acceso/desarrollo {usuarioId}` da una sesión y
  se guarda en `sessionStorage['tutor.sesion']` (solo desarrollo; la web no lo muestra).
- **API** → http://localhost:5080 (`/salud`, `/demo`), en el contenedor `tutor-api`
- Que el docente sube el material copiándolo en `course-content/` (una subcarpeta por
  clase); el curso, sus clases y sus exámenes salen de ahí. Las preguntas no las escribe
  nadie: la IA genera un examen propio para cada alumno al pulsar *Comenzar examen*. La demo
  solo añade un docente y una alumna matriculados.
- Que por `curl` (solo desarrollo) se sigue pudiendo actuar con las cabeceras
  `X-Usuario-Id: <id>` y `X-Usuario-Rol: Docente|Alumno|Admin`.
- Que para entrar con cuentas reales la persona pega el `ClientId`/secreto de Google
  en `tutor.env` en la raíz del repo (ignorado por git; el paso 2 lo pasa con
  `--env-file`). No hay que dar de alta a nadie: al entrar por primera vez cada persona
  elige si es alumno (y de qué profesor) o profesor. **Nunca pidas ni leas en voz alta esos
  secretos**: no los muestres con `cat`.
- Que para conversar el alumno pulsa **Entrar con OpenRouter (gratis)** en el panel
  "Conecta tu tutor" e inicia sesión él mismo (puede crear la cuenta con su Google). No hay
  claves que pegar. **Nunca inicies sesión ni crees la cuenta tú.**

## 5. Parar

```bash
docker rm -f tutor-api

# Solo el servidor de la web (no todos los node.exe del equipo)
PID=$(netstat -ano | grep ':4200 .*LISTENING' | awk '{print $NF}' | head -1)
[ -n "$PID" ] && taskkill //F //PID "$PID"

rm -f web.log
```

Para empezar de cero — borra también la cuenta conectada y el contenido subido:

```bash
docker volume rm tutor-datos tutor-claves
```

# Distribución web móvil — Towers of Defencopolis

Kit para publicar el juego (build WebGL de Unity) en un enlace **HTTPS público** y un **código QR**, sin depender de
este computador ni de la red Wi‑Fi local. No contiene la build: las builds se generan en
`C:\Users\jerem\GameDevAgent_Builds\WebRelease_<fecha>` (fuera del repositorio).

> **Estado (2026-11-23): PUBLICADO en GitHub Pages** → **https://jeremias0711.github.io/Defensa-de-las-Leyendas/**
> (repositorio `jeremias0711/Defensa-de-las-Leyendas`, rama `main`, solo la build compilada). QR verificado en
> `Distribution/qr/towers-of-defencopolis-qr.png` y `.svg`. Copia local publicada:
> `C:\Users\jerem\GameDevAgent_Builds\Pages_Defensa-de-las-Leyendas` (repositorio git de la web).
>
> **Actualizar la versión publicada:** nueva build Release (sección 3) → copiar su contenido sobre la carpeta
> `Pages_Defensa-de-las-Leyendas` (borrando antes `Build/` e `index.html` antiguos, conservando `.git`, `.nojekyll` y
> `README.md`) → commit y push a `main`. La URL y el QR no cambian. GitHub Pages no admite cabeceras propias: la build
> funciona igual gracias a la descompresión en el navegador (carga algo más lenta que en Cloudflare Pages).

## 1. Arquitectura

```
Teléfono (cámara) ──QR──▶ https://<proyecto>.pages.dev/  (Cloudflare Pages, CDN + HTTPS)
                             │ index.html  → pantalla de bienvenida (ligera, ~0,3 MB)
                             │ Build/*.framework.js / *.wasm   (código, Brotli nativo)
                             └ Build/*.data.*.partN            (datos del juego en partes ≤ 24 MiB)
```

* **Pantalla de bienvenida** (plantilla `Game/Assets/WebGLTemplates/GameDevAgent16x9`): nombre, emblema del Libro
  Sagrado, paleta Bosque, tipografías del juego, barra de carga, botón **Jugar** (se habilita al terminar la carga; ese
  toque desbloquea el audio y, en Android, activa la pantalla completa), consejo de girar el teléfono y aviso si el
  navegador no tiene WebGL/WebAssembly. La descarga empieza en cuanto se abre la página.
* **Doble formato de texturas:** DXT (escritorio) y ASTC (teléfonos y tabletas, formato nativo de sus GPU: menos memoria
  y sin descompresión). Cada dispositivo descarga solo uno.
* **Datos en partes:** Cloudflare Pages admite archivos de hasta 25 MiB; el archivo de datos comprimido (~47 MB) se
  divide en partes de 24 MiB que la página descarga en paralelo y une en memoria; el cargador de Unity lo descomprime
  (Brotli, *decompression fallback*). Los gráficos no se recortan.
* **Caché:** nombres de archivo con hash → `Cache-Control: immutable` (1 año); `index.html` siempre se revalida, así una
  actualización se ve al instante y las partes ya descargadas no se vuelven a bajar.

## 2. Plataforma recomendada: Cloudflare Pages

| Criterio | Cloudflare Pages | itch.io |
|---|---|---|
| HTTPS y CDN | Sí, gratis, `*.pages.dev` | Sí (`*.itch.io`), juego dentro de un iframe de su página |
| URL limpia para el QR | Sí, abre directamente la pantalla de bienvenida | Abre la página de itch.io; hay que pulsar «Run game» |
| WebAssembly / MIME | Sí (`_headers`) | Sí |
| Brotli | Cabeceras propias (`_headers`) | Sin control de cabeceras → descompresión en JS (funciona con esta build) |
| Límite de tamaño | **25 MiB por archivo** (resuelto con las partes), 20 000 archivos | ~200 MB por archivo, ZIP de hasta 1 GB |
| Actualizar | Subir la carpeta de la nueva build (o `wrangler pages deploy`) | Subir un ZIP nuevo |
| Experiencia móvil | La prevista: página propia a pantalla completa | Botón de pantalla completa de itch, controles de su página |

**Elegida: Cloudflare Pages** — enlace limpio que abre directamente el juego, cabeceras configurables, CDN global y
actualización sencilla. El límite de 25 MiB se resuelve con las partes. **Alternativa:** la misma build funciona en
itch.io sin cambios (ZIP de la carpeta, «This file will be played in the browser», orientación horizontal).

No se abre ningún puerto del computador, no hay *port forwarding* y el servidor Python solo se usa en local.

## 3. Generar la build Release

Unity cerrado, desde `Game/`:

```
Unity -batchmode -projectPath Game -executeMethod GameDevAgent.WebTools.WebBuildTools.BuildWebRelease ^
      -gdaOut "C:\Users\jerem\GameDevAgent_Builds\WebRelease_<fecha>" -gdaHost cloudflare
Unity -batchmode -projectPath Game -executeMethod GameDevAgent.WebTools.WebBuildTools.RestoreStandaloneTarget
```

o el menú **GameDevAgent ▸ Web ▸ Build WebGL Release for Cloudflare Pages**. La herramienta compila dos veces (DXT y
ASTC, ~8 min), trocea los datos, escribe `_headers` y restaura la configuración del proyecto. Nunca sobrescribe una
carpeta existente.

## 4. Probar en local antes de publicar

```
python -I Distribution\tools\preview_server.py "C:\Users\jerem\GameDevAgent_Builds\WebRelease_<fecha>" --port 8800
```

Abrir `http://127.0.0.1:8800/`. El servidor aplica `_headers` igual que Cloudflare. Parámetros de prueba (no afectan a
los jugadores): `?gdaMobile=1` (fuerza la interfaz táctil), `?gdaReport=1` (envía hitos y errores al servidor),
`?gdaProbe=persist|scenes` (verifica la persistencia y la carga de todas las escenas).

## 5. Publicar en Cloudflare Pages (cuando esté autorizado)

1. Cuenta de Cloudflare del equipo → **Workers & Pages ▸ Create ▸ Pages ▸ Upload assets**.
2. Nombre del proyecto, p. ej. `towers-of-defencopolis` → URL `https://towers-of-defencopolis.pages.dev/`.
3. Arrastrar **el contenido** de la carpeta `WebRelease_<fecha>` (debe verse `index.html`, `_headers`, `Build/`,
   `TemplateData/`) → **Deploy**.
4. Comprobar en el teléfono con **datos móviles** (no Wi‑Fi del computador) y en otra red.
5. Generar el QR con la URL definitiva:
   ```
   python -I Distribution\tools\make_qr.py https://towers-of-defencopolis.pages.dev/
   ```
   Crea `Distribution/qr/towers-of-defencopolis-qr.png` (alta resolución, margen blanco) y `.svg`, y verifica que el
   código se decodifica exactamente a esa URL. Rechaza `http://`, IPs privadas y `localhost`.

**Actualizar el juego:** nueva build Release (paso 3) → en el proyecto de Pages, **Create new deployment** → subir la
nueva carpeta. La URL y el QR no cambian. Para volver atrás: *Rollback* a un despliegue anterior en el panel.

## 6. Bloqueos antes de publicar

1. ~~**Contenido del curso.**~~ La build incluye código compilado y recursos de la plantilla del curso
   (`Game/Assets/Course/`). **Resuelto (2026-11-23):** el usuario confirmó que la licencia del curso permite
   distribuir públicamente el juego compilado. El código fuente del curso sigue sin publicarse (`.gitignore`).
2. **Cuenta de hosting.** Falta autorización expresa para usar una cuenta de Cloudflare (o itch.io) y publicar.
3. Recursos de terceros: los registrados en `THIRD_PARTY.md` figuran con licencia confirmada por el usuario
   (modelos 3D, paquete de escenarios, audio). Fuentes Fredoka y MedievalSharp: SIL OFL 1.1 (se incluyen sus licencias
   en `TemplateData/fonts/`).

## 7. Medidas (build Release `WebRelease_20261122-2343`)

| Concepto | Valor |
|---|---|
| Descarga por dispositivo (primera visita) | ~54 MB: datos 47,8 MB (2 partes) + código 5,7 MB + página 0,3 MB |
| Carpeta completa a subir (DXT + ASTC) | ~99 MB, 16 archivos, el mayor 24 MiB |
| Antes de optimizar (Release sin cambios) | 77 MB por dispositivo, un archivo de 71,7 MB |
| Pantalla de bienvenida | inmediata (index.html 33 KB + fuentes e imágenes ~0,3 MB) |
| Juego listo (local, sin límite de red, Chrome con WebGL por software) | 4,7–8,8 s |
| Recarga | las partes salen de la caché del navegador; el progreso (PlayerPrefs/IndexedDB) se conserva |

Optimización aplicada con evidencia: la música del curso (≈24 MB) estaba enlazada al gestor de audio pero ya no
sonaba nunca (la gestiona `GameAudio`): se desenlazó (los archivos siguen en el proyecto). Las texturas de personajes no
se recortaron. Estimación con datos móviles: a 10 Mbit/s, unos 45 s la primera vez; las siguientes, segundos.

## 8. Pruebas pendientes en teléfonos reales

Android (Chrome) y iPhone (Safari): abrir el QR con datos móviles, carga completa, botón Jugar y audio, horizontal,
pantalla completa (Android) / sin ella (iPhone), colocar, mejorar y retirar defensores, pausa, tutorial, colección,
recargar y comprobar que el progreso se conserva, memoria estable tras varios niveles.

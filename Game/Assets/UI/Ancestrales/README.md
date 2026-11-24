# Ancestrales · recursos de UI (fase 18)

Recursos del Figma «Ancestrales · El último recuerdo — UI/UX» (archivo `CM7YWQCKca3rtPq4tgNkVJ`), del autor del proyecto. Los importa `HudSkinBuilder.ImportAncestrales` (densidad: 1 téxel = 1 px de Figma salvo los vectores, 3x/4x, y los recortes de la hoja, 2x).

**Fase 20 (nitidez):** la hoja de arte del Figma solo existe a 1536x1024, así que sus recortes se mostraban ampliados unas 2 veces (filtrado bilineal de la GPU). Los recortes `btn_*`, `carta_*`, `arte_*`, `ico_*`, `est_*`, `res_*` y `fx_*` se guardan ahora a 2x, remuestreados con Lanczos (alfa premultiplicado) y un enfoque suave (UnsharpMask radio 1,6, 55 %), e importados con densidad 2: mismo tamaño en pantalla, bordes más definidos. No añade detalle real: la solución definitiva es reexportar la hoja de arte original a 3072x2048 o más. Las piezas de 9 cortes (`tallado_*`, `barra_*`, `cinta_*`) no cambian.

**Fase 20.1 (sin margen negra):** los recortes rectangulares de la hoja arrastraban su fondo oscuro, que se veía como una caja negra alrededor de los botones. En `btn_*`, `carta_*`, `ico_ayuda`, `est_check`, `est_candado` y `est_aviso` ese fondo se elimina por relleno desde los bordes (solo píxeles oscuros conectados con el borde y cercanos al color de fondo, borde suavizado de 1 px), antes de la ampliación 2x; `est_cerrar` recibe una máscara circular. Los retratos `arte_*`, los efectos `fx_*` e `ico_pausa` no se tocan.

## Recortes de la hoja de arte (`Editor/hoja_ancestrales.png`, 1536x1024, imagen `7f7c4` del Figma; no se incluye en la build)

Coordenadas en píxeles de la hoja (x, y, ancho, alto). Máscara redondeada como en Figma.

| Recurso | Recorte | Uso |
|---|---|---|
| `btn_jugar` | 31,62,214,70 | JUGAR (menú, selección de niveles) |
| `btn_coleccion` | 28,148,182,66 | COLECCIÓN |
| `btn_volver` | 259,69,125,58 | VOLVER |
| `btn_siguiente` | 402,70,129,55 | (reserva) |
| `tallado_musgo` | borde 512,231,15,58 + centro 503,231,9,58 | botón tallado Musgo (3 partes, como el componente 32:1821) |
| `tallado_madera` | borde 186,233,13,58 + centro 177,233,9,58 | Madera |
| `tallado_pergamino` | borde 376,68,11,61 + centro 367,68,9,61 | Pergamino |
| `ico_*` (20) | rejilla x 572/629/682/741/796, y 72/136/199/261, 52x52 | iconos generales y de elementos |
| `ico_pausa` | 612,680,44,40 | pausa |
| `res_moneda` / `res_libro` / `res_calavera` | 1306,199,52,52 / 1306,100,42,42 / 29,686,40,40 | HUD |
| `est_check` / `est_candado` / `est_aviso` / `est_cerrar` | 1314/1367/1417/1467,265,46,46 | estados |
| `barra_pista` / `barra_verde` / `barra_oro` | 1340,106,160,34 (relleno vaciado) / 1351,114,96,18 / 1361,157,56,17 | barras |
| `carta_*` | x 28/107/187/266, 898, 72x94 | cartas de guardián (bandeja) |
| `arte_*` | 15/200/340/500, 360, 175/135/155/135 x 222 | arte de cuerpo entero (reserva) |
| `fx_magia` / `fx_mariposas` | 1320,722,95,72 / 1450,715,62,85 | efectos (alfa por luminancia) |

## Capas vectoriales (exportadas en SVG, quitado el fondo del frame y rasterizadas)

`fondo_selva` (Selva andina nocturna, PNG 3x), `libro_altar`, `luz_libro`, `luciernagas`, `olvido_humo`, `rama`, `vineta` (3x), `esquinero`, `cinta_punta` (4x). `cinta_banda` = textura musgo con borde de tinta de 3 px (componente «Cinta de título»).

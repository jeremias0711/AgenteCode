# Recursos de terceros

Inventario de recursos de terceros usados o previstos en GameDevAgent.
El repositorio es público: **ningún recurso se publica mientras su estado no sea "Verificado"**.

No se han inventado autores, licencias ni permisos. Todo lo desconocido figura como *Pendiente de verificación*.

## Estados de verificación
- **Pendiente de verificación**: licencia y procedencia sin revisar. No publicar.
- **En revisión**: se está comprobando.
- **Verificado**: licencia y procedencia comprobadas y anotadas, con enlace a la fuente.
- **No publicable**: la licencia no permite redistribuir en un repositorio público.

## Plantillas de Unity 2021
Aún no adjuntadas. Hasta revisar sus licencias, `templates/originals/` y `templates/working/` están excluidas del repositorio mediante `.gitignore` (decisión provisional).

## Inventario

| Nombre del recurso | Autor o proveedor | URL o fuente | Licencia | Uso permitido | Restricciones de modificación y redistribución | Estado de verificación | Observaciones |
|---|---|---|---|---|---|---|---|
| Plantillas de Unity 2021 (por identificar) | Pendiente de verificación | Pendiente de verificación | Pendiente de verificación | Pendiente de verificación | Pendiente de verificación | Pendiente de verificación | No adjuntadas todavía. |
| Fuente Fredoka (SemiBold y Bold, subconjunto latin) | The Fredoka Project Authors | Archivos: Fontsource (`cdn.jsdelivr.net/fontsource/fonts/fredoka@5.1.0`); licencia: `github.com/google/fonts/blob/main/ofl/fredoka/OFL.txt` | SIL Open Font License 1.1 | Uso, modificación y redistribución, incluso comercial, también incrustada en el juego | No se puede vender la fuente sola; hay que conservar el aviso de copyright y la licencia; las versiones modificadas no pueden usar el nombre reservado | Verificado (2026-11-20) | `Game/Assets/UI/Fonts/` con `OFL-Fredoka.txt`. La usa la UI v2. |
| Fuente MedievalSharp (Regular) | wmk69 | `github.com/google/fonts/tree/main/ofl/medievalsharp` | SIL Open Font License 1.1 (nombre reservado "MedievalSharp") | Igual que la fila anterior | Igual que la fila anterior; el nombre reservado no se puede usar en versiones modificadas | Verificado (2026-11-20) | `Game/Assets/UI/Fonts/` con `OFL-MedievalSharp.txt`. Títulos, cintas y logo. |
| Iconos y adornos de la UI v2 | Diseño propio del usuario (Figma "Towers of Defencopolis — UI/UX") | Exportados como PNG desde el archivo Figma `DFPM40vtHCS1HsxlkvJnTB` | Propiedad del autor del proyecto | Uso en el juego | Sin restricciones de terceros conocidas | Verificado (2026-11-20) | `Game/Assets/UI/Icons/` y `Game/Assets/UI/Ornaments/`. Los paneles de `Game/Assets/UI/Sprites/` se generan por código. |

## Software de la cadena de herramientas (no se redistribuye en el repositorio)

| Nombre | Versión detectada | Observaciones |
|---|---|---|
| Unity Editor | 6000.6.7f1 | Detectado en la máquina; el uso de la licencia no está verificado. |
| Blender | 5.2.0 | Detectado en la máquina; su ejecución y exportación no están verificadas. |

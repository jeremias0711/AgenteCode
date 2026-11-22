# GameDevAgentContent (fase 11)

Recursos derivados de las carpetas elegibles «Tower Defense» / «Tower D» de `3D_Videojuego/`. Los originales nunca se modifican.

| Carpeta | Contenido | Origen |
|---|---|---|
| `Characters/Mohan` | El Mohán (Defensor Cañón): FBX + texturas 1024 px | `Mohán/Tower Defense/Acciones Tower Defense.blend` |
| `Characters/Madremonte` | La Madremonte (Defensor Lento) | `MadreMonte/Tower Defense/TowerDefense_MadreMonte.blend` |
| `Characters/Hojarasquin` | El Hojarasquín (Defensor Bomba) | `Hojarasquín/Tower D/Acciones Tower D. Hojaranquin.blend` |
| `Characters/Patasola` | La Patasola (Defensor Triple), modelo estático: el archivo no trae animaciones y solo la escultura de alta densidad tiene rig | `Personajes/Patasola/.../PatasolaFinalVer_Rig_Texturas.blend` (autorizado por el usuario; `mode=static objs=Patasola,Retopo_Pelo,Retopo_Top,Retopo_Falda`) |
| `Enemies/Ghost_*` | Tres fantasmas enemigos | `Atacantes (TOWER DEFENSE)/PERSONAJES ENEMIGOS_RIG_ANIM.blend` |
| `Props` | 26 props y el Libro Sagrado (texturas 512 px) | `Tower Defense/PROPS TOWER DEFENSE.blend` |
| `Escenarios` | Fase 12: texturas de suelo/losas y FBX de la plaza del Libro del paquete «Niveles campaña e Infinito»; materiales Standard y cintas de camino generadas | paquete externo (subconjunto) + `MapRestyleBuilder` |
| `Animations` | Animator Controllers (Idle, Attack, Hit, Death y Spawn, según existan) | generado |
| `Prefabs` | `Legend_*`, `Ghost_*`, `LibroSagrado` | generado |
| `Portraits` | Retratos de la Colección renderizados desde los modelos | generado |
| `Scripts` | `LegendRoster` (torre → leyenda) y `LegendPresenter` (animaciones; solo presentación) | código |
| `Editor` | `LegendContentBuilder` (importación, prefabs, torres, castillo, enemigos, mapas, retratos) | código |
| `Tools~` | `export_td.py` (exportador de Blender; Unity ignora esta carpeta) | código |


## Regenerar
1. Exportar desde Blender (no guarda los `.blend`):
   `blender -b --factory-startup --disable-autoexec <archivo.blend> -P Tools~/export_td.py -- mode=character out=<carpeta> name=<Nombre> tex=1024 dec=<malla:ratio,...>`
   (`mode=enemies rig=<Rig>` y `mode=props` para los demás).
2. Unity (con el editor cerrado):
   `Unity -batchmode -projectPath Game -executeMethod GameDevAgent.ContentTools.LegendContentBuilder.BuildFromCommandLine [-gdaShots <carpeta>]`
3. Mapas (fase 12): `-executeMethod GameDevAgent.ContentTools.MapRestyleBuilder.BuildFromCommandLine [-gdaShots <carpeta>]`.
4. Pruebas: `CourseLevelFlowSmokeTest -gdaFlow legends` (F14) y `-gdaFlow maps` (F15).

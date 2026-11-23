# GameDevAgentContent (fase 11)

Recursos derivados de las carpetas elegibles «Tower Defense» / «Tower D» de `3D_Videojuego/`. Los originales nunca se modifican.

| Carpeta | Contenido | Origen |
|---|---|---|
| `Characters/Mohan` | El Mohán (Defensor Cañón): FBX + texturas 1024 px | `Mohán/Tower Defense/Acciones Tower Defense.blend` |
| `Characters/Madremonte` | La Madremonte (Defensor Lento) | `MadreMonte/Tower Defense/TowerDefense_MadreMonte.blend` |
| `Characters/Hojarasquin` | El Hojarasquín (Defensor Bomba) | `Hojarasquín/Tower D/Acciones Tower D. Hojaranquin.blend` |
| `Characters/Patasola` | La Patasola (Defensor Triple), modelo estático: el archivo no trae animaciones y solo la escultura de alta densidad tiene rig | `Personajes/Patasola/.../PatasolaFinalVer_Rig_Texturas.blend` (autorizado por el usuario; `mode=static objs=Patasola,Retopo_Pelo,Retopo_Top,Retopo_Falda`) |
| `Characters/PatasolaTD` | Fase 13.1: La Patasola animada (la usa la Torre Triple y la Colección). Idle = `Flotar_InPlace`, Attack = `Ataque_Garras` (impacto 0,37), Hit = `Recibir_Dano`, Death = `Muerte`; nodo de root motion `root.x` (XZ descartado); apoyada en el origen del rig (flota ~0,3 sobre el suelo, como en el original). `Characters/Patasola` (estático) queda como respaldo | `Rigging y animación/Patasola/Patasola_TowerDefense_FBX/.../Patasola_TowerDefense.blend` (autorizado por el usuario; `mode=character name=PatasolaTD tex=1024 dec=Patasola_Pelo:0.45,Patasola_Top:0.7,Patasola_Falda:0.4`) |
| `Enemies/Ghost_*` | Tres fantasmas enemigos | `Atacantes (TOWER DEFENSE)/PERSONAJES ENEMIGOS_RIG_ANIM.blend` |
| `Props` | 26 props y el Libro Sagrado (texturas 512 px) | `Tower Defense/PROPS TOWER DEFENSE.blend` |
| `Escenarios` | Fase 12: texturas de suelo/losas y FBX de la plaza del Libro del paquete «Niveles campaña e Infinito»; materiales Standard y cintas de camino generadas | paquete externo (subconjunto) + `MapRestyleBuilder` |
| `Projectiles` | Fase 14: proyectiles 3D originales generados por código (mallas low-poly facetadas, una paleta compartida de 16 colores y un único material). `MohanEmberProjectile` (carbón con grietas incandescentes + estela), `HojarasquinRockProjectile` (roca + estela de polvo; impacto con polvo y fragmentos visuales), `PatasolaBoomerangLegProjectile` (tres piernas estilizadas que giran como búmeran). Son copias de los prefabs del curso con su lógica intacta; los originales no se tocan | `ProjectileContentBuilder` |
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
5. Solo La Patasola (fase 13.1; no toca otras leyendas ni mapas, y rehace las apariencias de la Colección):
   `-executeMethod GameDevAgent.ContentTools.LegendContentBuilder.BuildPatasolaFromCommandLine [-gdaShots <carpeta>]`; prueba `-gdaFlow patasola` (F21).
6. Proyectiles (fase 14): `-executeMethod GameDevAgent.ContentTools.ProjectileContentBuilder.BuildFromCommandLine [-gdaShots <carpeta>]`; prueba `-gdaFlow projectiles` (F22, comparación directa con los proyectiles del curso).

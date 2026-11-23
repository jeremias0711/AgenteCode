using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDevAgent.ContentTools
{
    /// <summary>
    /// Phase 11 integration of the eligible "Tower Defense" 3D sources (exported by Tools~/export_td.py into
    /// Assets/GameDevAgentContent). Presentation only: gameplay components, stats, colliders, paths, waves and levels are
    /// never modified. Idempotent: running it again rebuilds what it created.
    ///  1. Import settings (textures, FBX, clips without root motion) and Standard materials from the exported maps.
    ///  2. Animator Controllers (Idle/Attack/Hit/Death/Spawn with the clips that really exist) and legend prefabs.
    ///  3. Tower prefabs: the course model renderers are hidden and the legend model is added as "Legend Visual"
    ///     with a LegendPresenter (Cannon=El Mohán, Slow=La Madremonte, Bomb=El Hojarasquín). The triple tower keeps
    ///     its provisional model: no eligible model of La Patasola exists.
    ///  4. Castle prefab: shows the Sacred Book shrine (Base + Libro props); enemies: ghost models from the eligible
    ///     "enemigos" file; maps: course decoration swapped for Tower Defense props (colliders kept).
    ///  5. Portraits rendered from the models for the Collection, then the HUD skin is re-applied (names).
    /// Run: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.ContentTools.LegendContentBuilder.BuildFromCommandLine
    ///      [-gdaShots &lt;dir&gt;]  (no -quit)
    /// </summary>
    public static class LegendContentBuilder
    {
        private const string Tag = "[Legends] ";
        public const string Root = "Assets/GameDevAgentContent";
        public const string PrefabDir = Root + "/Prefabs";
        public const string AnimDir = Root + "/Animations";
        public const string PortraitDir = Root + "/Portraits";
        public const string PropsFbx = Root + "/Props/TD_Props.fbx";
        private const string TowerDir = "Assets/Course/Prefabs/Towers";
        private const string CastlePath = "Assets/Course/Prefabs/Level Essentials/Castle.prefab";
        private const string EnemyDir = "Assets/Course/Prefabs/Enemies";
        private const string CollectionDir = "Assets/UI/Collection";
        private static readonly string[] MapScenes =
        {
            "Assets/Course/Scenes/Test 4.unity", "Assets/Course/Scenes/Test 5.unity", "Assets/Course/Scenes/Test 6.unity",
            "Assets/Course/Scenes/Tutorial.unity",
            // Phase 12.3: the "Nueva partida" scenes share the same look.
            "Assets/Course/Scenes/Test 1.unity", "Assets/Course/Scenes/Test 2.unity", "Assets/Course/Scenes/Test 3.unity",
        };

        public class LegendSpec
        {
            public LegendId id;
            public string folder, fbx, tower;
            public float height, yaw;
            public string idle, attack, hit, death, spawn; // substrings of the Blender action names (null = none)
            public bool isStatic; // unrigged model without animations (only the visual turn towards the target)
            // Phase 13.1: bone used as Generic root-motion node. Its horizontal (XZ) motion is extracted and, with root
            // motion off, discarded: travelling clips stay on the defender's tile (height and rotation stay in the pose).
            public string motionNode;
            // Phase 13.1: normalized time of the attack impact. >= 0 makes LegendPresenter start the Attack clip just
            // before the impact when the tower fires and fit the rest of the clip inside the fire interval.
            public float attackImpact = -1f;
            // Phase 13.1: the rig origin is the authored ground (La Patasola floats ~0.19 m above it in every clip), so
            // the model is placed by its origin instead of by the lowest point of the idle pose.
            public bool groundAtOrigin;
        }

        // Definitive assignment (phase 11). Heights in metres = Unity units (towers are ~2.5 units high).
        public static readonly LegendSpec[] Legends =
        {
            new LegendSpec { id = LegendId.Mohan, folder = Root + "/Characters/Mohan", fbx = "Mohan.fbx", tower = "Cannon Tower",
                height = 2.0f, yaw = 0f, idle = "Espera", attack = "Ataque_Fisga", hit = "Recibir_Golpe", death = "Caida" },
            new LegendSpec { id = LegendId.Madremonte, folder = Root + "/Characters/Madremonte", fbx = "Madremonte.fbx", tower = "Slow Tower",
                height = 2.15f, yaw = 0f, idle = "TD_Idle", attack = "Habilidad", hit = "Recibir_Golpe", death = "Muerte", spawn = "Aparecer" },
            new LegendSpec { id = LegendId.Hojarasquin, folder = Root + "/Characters/Hojarasquin", fbx = "Hojarasquin.fbx", tower = "Bomb Tower",
                height = 1.9f, yaw = 0f, idle = "Espera", attack = "Ataque_Fisga", hit = "Recibir_Golpe", death = "Caida" },
            // Phase 13.1, authorised by the user: Rigging y animación/Patasola/Patasola_TowerDefense_FBX/Patasola_TowerDefense.blend
            // (same four meshes as the phase 11 static model, now with an Auto-Rig Pro rig and 10 actions). Exported to
            // Characters/PatasolaTD; the static Characters/Patasola export stays as it was (see StaticPatasola).
            //  - Idle: Flotar_InPlace (Flotar_Idle travels 2.3 m back and forth, it is not an in-place idle).
            //  - Attack: Ataque_Garras (in place; impact at frame 26/69 = 0.37, measured on the hand bones).
            //  - Hit: Recibir_Dano. Death: Muerte (falls back ~0.5 m: the XZ part is extracted through root.x).
            //  - Not used: Aparecer (starts under the ground and travels 2 m), Flotar (travels), Patada (travels 1 m),
            //    Grito and Celebrar (no matching defender event).
            new LegendSpec { id = LegendId.Patasola, folder = Root + "/Characters/PatasolaTD", fbx = "PatasolaTD.fbx", tower = "Triple Shot Tower",
                height = 2.0f, yaw = 0f, idle = "Flotar_InPlace", attack = "Ataque_Garras", hit = "Recibir_Dano", death = "Muerte",
                motionNode = "root.x", attackImpact = 0.37f, groundAtOrigin = true },
        };

        /// <summary>Phase 11 static La Patasola (unrigged source, no actions). Kept as the fallback when PatasolaTD is missing.</summary>
        public static readonly LegendSpec StaticPatasola = new LegendSpec { id = LegendId.Patasola, folder = Root + "/Characters/Patasola",
            fbx = "Patasola.fbx", tower = "Triple Shot Tower", height = 2.0f, yaw = 180f, isStatic = true }; // no rig: looks along -Z

        // Enemy prefab -> ghost model (eligible "PERSONAJES ENEMIGOS_RIG_ANIM"). Only the look changes.
        public static readonly Dictionary<string, string> EnemyGhosts = new Dictionary<string, string>
        {
            { "Enemy (Alien)", "Ghost_Hooded" }, { "Enemy (Alien Tall)", "Ghost_Halo" }, { "Enemy (Cactus)", "Ghost_Horned" },
            { "Enemy (Flying Skull)", "Ghost_Halo" }, { "Enemy (Big Yeti)", "Ghost_Horned" }, { "Enemy (Big Cyclops)", "Ghost_Hooded" },
        };

        // Course decoration -> Tower Defense props (the decoration object and its collider stay).
        private static readonly (string source, string[] props, float heightFactor)[] DecorSwaps =
        {
            ("detail_tree", new[] { "Arbol", "Arbol2", "Arbol_2" }, 1.25f),
            ("detail_rocksLarge", new[] { "Rocas2" }, 1.1f),
            ("detail_rocks", new[] { "Roca3" }, 1.1f),
            ("detail_crystalLarge", new[] { "Portal" }, 1.15f),
        };

        public static readonly List<string> Report = new List<string>();
        private static string shotsDir;

        public static void BuildFromCommandLine()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-gdaShots") shotsDir = args[i + 1];
            EditorApplication.Exit(Build() ? 0 : 1);
        }

        [MenuItem("Tools/GameDevAgent/Integrate Phase 11 legends")]
        public static void BuildFromMenu() => Build();

        public static bool Build()
        {
            Report.Clear();
            try
            {
                EnsureFolder(PrefabDir);
                EnsureFolder(AnimDir);
                EnsureFolder(PortraitDir);

                var legendPrefabs = new Dictionary<LegendId, GameObject>();
                var deathLengths = new Dictionary<LegendId, float>();
                var usedSpecs = new Dictionary<LegendId, LegendSpec>();
                foreach (LegendSpec wanted in Legends)
                {
                    LegendSpec spec = Resolve(wanted);
                    if (spec == null) continue;
                    GameObject prefab = BuildLegendPrefab(spec, out float death);
                    if (prefab == null) continue;
                    legendPrefabs[spec.id] = prefab;
                    deathLengths[spec.id] = death;
                    usedSpecs[spec.id] = spec;
                }

                var ghostPrefabs = new Dictionary<string, GameObject>();
                foreach (string ghost in EnemyGhosts.Values.Distinct())
                {
                    string folder = Root + "/Enemies/" + ghost;
                    string fbx = folder + "/" + ghost + ".fbx";
                    if (!File.Exists(fbx)) { Log("MISSING " + fbx); continue; }
                    ConfigureTextures(folder + "/Textures", 512);
                    ConfigureModel(fbx, new LegendSpec { idle = "GhostFloat" });
                    RemapMaterials(fbx, folder);
                    AnimatorController ctrl = BuildController(fbx, new LegendSpec { idle = "GhostFloat" }, out _, ghost);
                    ghostPrefabs[ghost] = BuildCharacterPrefab(fbx, ctrl, ghost, 1f, 0f, false);
                }

                Dictionary<string, GameObject> props = BuildPropPrefabs();
                GameObject book = BuildBookShrine(props);

                foreach (var pair in usedSpecs)
                    ApplyLegendToTower(TowerDir + "/" + pair.Value.tower + ".prefab", pair.Value, legendPrefabs[pair.Key], deathLengths[pair.Key]);
                if (!legendPrefabs.ContainsKey(LegendId.Patasola)) Log("Triple Shot Tower: no model of La Patasola: provisional model kept");
                if (book != null) ApplyBookToCastle(book);
                foreach (var pair in EnemyGhosts)
                {
                    if (ghostPrefabs.TryGetValue(pair.Value, out GameObject ghost)) ApplyGhostToEnemy(EnemyDir + "/" + pair.Key + ".prefab", ghost);
                }

                var portraits = new Dictionary<string, Sprite>();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); // nothing else in the shot
                foreach (LegendSpec spec in Legends)
                {
                    if (legendPrefabs.TryGetValue(spec.id, out GameObject prefab))
                        portraits[LegendRoster.CollectionId(spec.id)] = RenderPortrait(prefab, "portrait_" + spec.id, true);
                }
                if (book != null) portraits["libro_sagrado"] = RenderPortrait(book, "portrait_libro_sagrado", false);
                UpdateCollection(portraits);
                AssetDatabase.SaveAssets();

                bool hud = GameDevAgent.UiTools.HudSkinBuilder.Apply();
                Log("HUD skin re-applied (legend names): " + hud);

                foreach (string scene in MapScenes) DecorateScene(scene, props);

                if (!string.IsNullOrEmpty(shotsDir)) Captures(legendPrefabs, ghostPrefabs, book);

                AssetDatabase.SaveAssets();
                foreach (string line in Report) Debug.Log(Tag + line);
                Debug.Log(Tag + "DONE ok");
                return hud;
            }
            catch (System.Exception e)
            {
                foreach (string line in Report) Debug.Log(Tag + line);
                Debug.LogError(Tag + "FAILED: " + e);
                return false;
            }
        }

        private static void Log(string line)
        {
            Report.Add(line);
        }

        private static bool SourceExists(LegendSpec spec)
        {
            string fbx = spec.folder + "/" + spec.fbx;
            return AssetDatabase.LoadAssetAtPath<GameObject>(fbx) != null || File.Exists(fbx);
        }

        /// <summary>The spec to build for a legend: its definitive source or, for La Patasola, the static phase 11 model.</summary>
        private static LegendSpec Resolve(LegendSpec spec)
        {
            if (SourceExists(spec)) return spec;
            if (spec.id == LegendId.Patasola && SourceExists(StaticPatasola))
            {
                Log("MISSING " + spec.folder + "/" + spec.fbx + " -> La Patasola keeps her static phase 11 model");
                return StaticPatasola;
            }
            Log("MISSING " + spec.folder + "/" + spec.fbx + " -> " + spec.tower + " keeps its provisional model");
            return null;
        }

        /// <summary>
        /// Poses a model in edit mode. Rigs with a root-motion node (phase 13.1, La Patasola) are evaluated by their
        /// Animator, exactly as in play mode: AnimationClip.SampleAnimation writes the raw curves and skips the Generic
        /// root-motion handling, which left parts of the model out of place in the captures. Other rigs keep the
        /// phase 11 sampling.
        /// </summary>
        public static void Pose(Animator animator, AnimationClip clip, float normalizedTime)
        {
            if (animator.avatar != null && animator.hasRootMotion && animator.runtimeAnimatorController != null)
            {
                animator.Rebind();
                animator.Play(clip.name, 0, Mathf.Clamp(normalizedTime, 0f, 0.999f));
                animator.Update(0f);
                return;
            }
            clip.SampleAnimation(animator.gameObject, clip.length * normalizedTime);
        }

        /// <summary>Per skinned part: baked bounds in the Idle pose and in the bind pose, bones and weights (diagnostics).</summary>
        private static void LogSkinParts(GameObject prefab)
        {
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Animator a = inst.GetComponentInChildren<Animator>();
            AnimationClip idle = a != null && a.runtimeAnimatorController != null ? a.runtimeAnimatorController.animationClips.FirstOrDefault(c => c.name == LegendPresenter.IdleState) : null;
            if (idle != null) Pose(a, idle, 0f);
            foreach (SkinnedMeshRenderer smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                Bounds lb = baked.bounds;
                Object.DestroyImmediate(baked);
                Mesh m = smr.sharedMesh;
                int maxW = 0;
                if (m != null) { var bpv = m.GetBonesPerVertex(); for (int i = 0; i < bpv.Length; i++) maxW = Mathf.Max(maxW, bpv[i]); }
                Log("  skin " + smr.name + ": baked idle centre " + (smr.transform.position + smr.transform.rotation * lb.center).ToString("F2") + " size " + lb.size.ToString("F2")
                    + ", mesh bind bounds " + (m != null ? m.bounds.size.ToString("F2") : "none") + ", bones " + smr.bones.Length + " (null " + smr.bones.Count(x => x == null) + "), root " + (smr.rootBone != null ? smr.rootBone.name : "none")
                    + ", max weights " + maxW + ", quality " + smr.quality + ", lossyScale " + smr.transform.lossyScale.ToString("F3"));
            }
            Object.DestroyImmediate(inst);
        }

        /// <summary>Import settings, materials, controller and Legend_* prefab of one legend.</summary>
        private static GameObject BuildLegendPrefab(LegendSpec spec, out float deathLength)
        {
            string fbx = spec.folder + "/" + spec.fbx;
            ConfigureTextures(spec.folder + "/Textures", 1024);
            if (spec.isStatic)
            {
                ConfigureModel(fbx, null);
                RemapMaterials(fbx, spec.folder);
                deathLength = 0f;
                return BuildStaticPrefab(fbx, "Legend_" + spec.id, spec.height, spec.yaw);
            }
            ConfigureModel(fbx, spec);
            RemapMaterials(fbx, spec.folder);
            AnimatorController ctrl = BuildController(fbx, spec, out deathLength);
            return BuildCharacterPrefab(fbx, ctrl, "Legend_" + spec.id, spec.height, spec.yaw, true, spec.groundAtOrigin);
        }

        /// <summary>
        /// Phase 13.1: rebuilds ONE legend (model import, controller, Legend_* prefab, its tower visual, its portrait) and
        /// then the sanctuary content (appearances + HUD). Other legends, enemies, props and maps are not touched.
        /// Run: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.ContentTools.LegendContentBuilder.BuildPatasolaFromCommandLine
        ///      [-gdaShots &lt;dir&gt;]
        /// </summary>
        public static void BuildPatasolaFromCommandLine()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-gdaShots") shotsDir = args[i + 1];
            EditorApplication.Exit(BuildSingle(LegendId.Patasola) ? 0 : 1);
        }

        [MenuItem("Tools/GameDevAgent/Rebuild La Patasola (phase 13.1)")]
        public static void BuildPatasolaFromMenu() => BuildSingle(LegendId.Patasola);

        public static bool BuildSingle(LegendId id)
        {
            Report.Clear();
            try
            {
                LegendSpec spec = Resolve(Legends.First(s => s.id == id));
                if (spec == null) throw new System.Exception("no source for " + id);
                GameObject prefab = BuildLegendPrefab(spec, out float death);
                if (prefab == null) throw new System.Exception("prefab not built for " + id);
                ApplyLegendToTower(TowerDir + "/" + spec.tower + ".prefab", spec, prefab, death);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                LogSkinParts(prefab);
                var portraits = new Dictionary<string, Sprite> { { LegendRoster.CollectionId(id), RenderPortrait(prefab, "portrait_" + id, true) } };
                UpdateCollection(portraits);
                if (!string.IsNullOrEmpty(shotsDir))
                {
                    Directory.CreateDirectory(shotsDir);
                    void Save(Texture2D t, string name) { File.WriteAllBytes(System.IO.Path.Combine(shotsDir, name + ".png"), t.EncodeToPNG()); Object.DestroyImmediate(t); }
                    foreach (var shot in new[] { ("idle", LegendPresenter.IdleState, 0.3f, 0f), ("idle_side", LegendPresenter.IdleState, 0.6f, 90f),
                        ("attack_impact", LegendPresenter.AttackState, spec.attackImpact >= 0f ? spec.attackImpact : 0.5f, 30f),
                        ("hit", LegendPresenter.HitState, 0.3f, 30f), ("death_end", LegendPresenter.DeathState, 1f, 30f) })
                        Save(RenderModel(prefab, 420, 560, 1.15f, 0.5f, shot.Item4, shot.Item2, shot.Item3, false, true), "legend_" + id + "_" + shot.Item1);
                    Save(RenderTowerRow(), "towers_row");
                }
                AssetDatabase.SaveAssets();
                foreach (string line in Report) Debug.Log(Tag + line);
                bool sanctuary = CollectionContentBuilder.Build(); // appearances from the new materials + HUD frame
                Debug.Log(Tag + "sanctuary content rebuilt: " + sanctuary);
                Debug.Log(Tag + (sanctuary ? "DONE ok" : "DONE with sanctuary errors"));
                return sanctuary;
            }
            catch (System.Exception e)
            {
                foreach (string line in Report) Debug.Log(Tag + line);
                Debug.LogError(Tag + "FAILED: " + e);
                return false;
            }
        }

        // ------------------------------------------------------------------ import

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }

        private static void ConfigureTextures(string folder, int maxSize)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return;
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) continue;
                bool normal = path.EndsWith("_normal.png");
                bool ms = path.EndsWith("_ms.png");
                bool changed = false;
                void Set<T>(T current, T wanted, System.Action<T> apply) { if (!EqualityComparer<T>.Default.Equals(current, wanted)) { apply(wanted); changed = true; } }
                Set(ti.textureType, normal ? TextureImporterType.NormalMap : TextureImporterType.Default, v => ti.textureType = v);
                Set(ti.sRGBTexture, !(normal || ms), v => ti.sRGBTexture = v);
                Set(ti.maxTextureSize, maxSize, v => ti.maxTextureSize = v);
                Set(ti.mipmapEnabled, true, v => ti.mipmapEnabled = v);
                Set(ti.textureCompression, TextureImporterCompression.Compressed, v => ti.textureCompression = v);
                Set(ti.alphaSource, normal ? TextureImporterAlphaSource.None : TextureImporterAlphaSource.FromInput, v => ti.alphaSource = v);
                if (changed) ti.SaveAndReimport();
            }
        }

        private static void ConfigureModel(string fbx, LegendSpec spec)
        {
            var mi = (ModelImporter)AssetImporter.GetAtPath(fbx);
            bool animated = spec != null;
            mi.globalScale = 1f;
            mi.useFileScale = true;
            mi.bakeAxisConversion = true;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importVisibility = false;
            mi.importBlendShapes = false;
            mi.addCollider = false;
            mi.isReadable = false;
            mi.meshCompression = ModelImporterMeshCompression.Medium;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
            if (!animated)
            {
                mi.animationType = ModelImporterAnimationType.None;
                mi.importAnimation = false;
                mi.SaveAndReimport();
                return;
            }
            mi.animationType = ModelImporterAnimationType.Generic;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.importAnimation = true;
            mi.animationCompression = ModelImporterAnimationCompression.Optimal;
            mi.motionNodeName = string.Empty; // no root motion node: the scripts own every position
            mi.clipAnimations = new ModelImporterClipAnimation[0];
            mi.SaveAndReimport(); // refresh defaultClipAnimations from the file

            mi = (ModelImporter)AssetImporter.GetAtPath(fbx);
            bool extractXZ = false;
            if (!string.IsNullOrEmpty(spec.motionNode))
            {
                string nodePath = TransformPath(AssetDatabase.LoadAssetAtPath<GameObject>(fbx), spec.motionNode);
                if (nodePath == null) Log("WARNING " + System.IO.Path.GetFileName(fbx) + ": motion node '" + spec.motionNode + "' not found: root motion stays in the pose");
                else
                {
                    mi.motionNodeName = nodePath;
                    extractXZ = true;
                    Log(System.IO.Path.GetFileName(fbx) + ": motion node " + nodePath + " (XZ extracted and discarded, height and rotation kept)");
                }
            }
            var clips = new List<ModelImporterClipAnimation>();
            foreach (ModelImporterClipAnimation take in mi.defaultClipAnimations)
            {
                string role = RoleOf(take.takeName, spec);
                if (role == null || clips.Any(c => c.name == role)) continue;
                take.name = role;
                take.loopTime = role == LegendPresenter.IdleState;
                take.loopPose = role == LegendPresenter.IdleState;
                take.lockRootRotation = true;
                take.lockRootHeightY = true;
                take.lockRootPositionXZ = !extractXZ; // false = XZ becomes root motion, dropped because root motion is off
                take.keepOriginalOrientation = true;
                take.keepOriginalPositionY = true;
                take.keepOriginalPositionXZ = true;
                clips.Add(take);
            }
            mi.clipAnimations = clips.ToArray();
            mi.SaveAndReimport();
            Log(System.IO.Path.GetFileName(fbx) + ": clips " + string.Join(", ", clips.Select(c => c.name + "<-" + c.takeName)));
        }

        /// <summary>Path (relative to the model root, "/" separated) of the first transform called <paramref name="name"/>.</summary>
        private static string TransformPath(GameObject model, string name)
        {
            if (model == null) return null;
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != name) continue;
                string path = t.name;
                for (Transform p = t.parent; p != null && p != model.transform; p = p.parent) path = p.name + "/" + path;
                return path;
            }
            return null;
        }

        private static string RoleOf(string takeName, LegendSpec spec)
        {
            string t = takeName.ToLowerInvariant();
            if (spec.idle != null && t.Contains(spec.idle.ToLowerInvariant())) return LegendPresenter.IdleState;
            if (spec.attack != null && t.Contains(spec.attack.ToLowerInvariant())) return LegendPresenter.AttackState;
            if (spec.hit != null && t.Contains(spec.hit.ToLowerInvariant())) return LegendPresenter.HitState;
            if (spec.death != null && t.Contains(spec.death.ToLowerInvariant())) return LegendPresenter.DeathState;
            if (spec.spawn != null && t.Contains(spec.spawn.ToLowerInvariant())) return LegendPresenter.SpawnState;
            return null;
        }

        private static string Safe(string name) => Regex.Replace(name ?? string.Empty, "[^A-Za-z0-9_-]", "_");

        private static void RemapMaterials(string fbx, string folder)
        {
            string manifest = File.Exists(folder + "/manifest.json") ? File.ReadAllText(folder + "/manifest.json")
                : File.Exists(System.IO.Path.GetDirectoryName(folder) + "/manifest.json") ? File.ReadAllText(System.IO.Path.GetDirectoryName(folder) + "/manifest.json") : string.Empty;
            EnsureFolder(folder + "/Materials");
            var mi = (ModelImporter)AssetImporter.GetAtPath(fbx);
            var names = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Material>().Select(m => m.name).ToList();
            foreach (var pair in mi.GetExternalObjectMap())
                if (pair.Key.type == typeof(Material) && !names.Contains(pair.Key.name)) names.Add(pair.Key.name);
            int made = 0;
            foreach (string name in names.Distinct())
            {
                string safe = Safe(name);
                string matPath = folder + "/Materials/" + safe + ".mat";
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Standard"));
                    AssetDatabase.CreateAsset(mat, matPath);
                }
                Texture2D albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/Textures/" + safe + "_albedo.png");
                Texture2D normal = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/Textures/" + safe + "_normal.png");
                Texture2D ms = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/Textures/" + safe + "_ms.png");
                bool cutout = Regex.IsMatch(manifest, "\"" + Regex.Escape(name) + "\": \\{[^}]*\"cutout\": true");
                if (albedo == null) Log("WARNING " + System.IO.Path.GetFileName(fbx) + ": material '" + name + "' has no exported albedo (" + safe + "_albedo.png)");
                SetupStandard(mat, albedo, normal, ms, cutout);
                EditorUtility.SetDirty(mat);
                mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), name), mat);
                made++;
            }
            mi.SaveAndReimport();
            Log(System.IO.Path.GetFileName(fbx) + ": " + made + " Standard materials");
        }

        private static void SetupStandard(Material mat, Texture2D albedo, Texture2D normal, Texture2D ms, bool cutout)
        {
            mat.shader = Shader.Find("Standard");
            mat.SetTexture("_MainTex", albedo);
            mat.SetColor("_Color", Color.white);
            mat.SetTexture("_BumpMap", normal);
            mat.SetFloat("_BumpScale", 1f);
            if (normal != null) mat.EnableKeyword("_NORMALMAP"); else mat.DisableKeyword("_NORMALMAP");
            mat.SetTexture("_MetallicGlossMap", ms);
            if (ms != null) mat.EnableKeyword("_METALLICGLOSSMAP"); else mat.DisableKeyword("_METALLICGLOSSMAP");
            mat.SetFloat("_GlossMapScale", 0.75f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Glossiness", 0.3f);
            if (cutout)
            {
                mat.SetFloat("_Mode", 1f);
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.SetFloat("_Cutoff", 0.5f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
            else
            {
                mat.SetFloat("_Mode", 0f);
                mat.SetOverrideTag("RenderType", "");
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.renderQueue = -1;
            }
            mat.enableInstancing = true;
        }

        // ------------------------------------------------------------------ animation

        private static AnimatorController BuildController(string fbx, LegendSpec spec, out float deathLength, string name = null)
        {
            var clips = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).ToList();
            string path = AnimDir + "/" + (name ?? "Legend_" + spec.id) + ".controller";
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null) AssetDatabase.DeleteAsset(path);
            AnimatorController ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
            AnimatorStateMachine sm = ctrl.layers[0].stateMachine;
            AnimationClip Clip(string role) => clips.FirstOrDefault(c => c.name == role);
            AnimatorState idle = sm.AddState(LegendPresenter.IdleState);
            idle.motion = Clip(LegendPresenter.IdleState);
            sm.defaultState = idle;
            bool syncAttack = spec.attackImpact >= 0f && Clip(LegendPresenter.AttackState) != null;
            if (syncAttack) ctrl.AddParameter(new AnimatorControllerParameter { name = LegendPresenter.AttackSpeedParameter, type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
            foreach (string role in new[] { LegendPresenter.AttackState, LegendPresenter.HitState, LegendPresenter.SpawnState })
            {
                AnimationClip clip = Clip(role);
                if (clip == null) continue;
                AnimatorState st = sm.AddState(role);
                st.motion = clip;
                if (syncAttack && role == LegendPresenter.AttackState)
                {
                    st.speedParameter = LegendPresenter.AttackSpeedParameter; // set by LegendPresenter to fit the fire interval
                    st.speedParameterActive = true;
                }
                AnimatorStateTransition back = st.AddTransition(idle);
                back.hasExitTime = true;
                back.exitTime = 0.9f;
                back.duration = 0.15f;
            }
            AnimationClip deathClip = Clip(LegendPresenter.DeathState);
            deathLength = deathClip != null ? deathClip.length : 0f;
            if (deathClip != null)
            {
                AnimatorState death = sm.AddState(LegendPresenter.DeathState);
                death.motion = deathClip;
            }
            EditorUtility.SetDirty(ctrl);
            Log(System.IO.Path.GetFileName(path) + ": states " + string.Join(", ", sm.states.Select(s => s.state.name + (s.state.motion != null ? "(" + ((AnimationClip)s.state.motion).length.ToString("0.00") + "s)" : "(none)"))));
            return ctrl;
        }

        // ------------------------------------------------------------------ prefabs

        /// <summary>
        /// World bounds of the visible renderers. Skinned meshes are baked in their current pose and measured between the
        /// 0.5 and 99.5 percentiles of each axis, so a few stray (unweighted) vertices cannot distort height or framing.
        /// </summary>
        public static Bounds MeasureBounds(GameObject go) => MeasureBounds(go, true);


        /// <param name="nearSkeleton">Skinned vertices farther than 30% of the skeleton size from the (posed) deform
        /// bones are ignored: they are unweighted leftovers, not part of the visible character.</param>
        public static Bounds MeasureBounds(GameObject go, bool nearSkeleton)
        {
            bool any = false;
            Bounds b = new Bounds(go.transform.position, Vector3.zero);
            var xs = new List<float>();
            var ys = new List<float>();
            var zs = new List<float>();
            Bounds skeleton = default;
            bool hasSkeleton = false;
            foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.bones == null) continue;
                foreach (Transform bone in smr.bones)
                {
                    if (bone == null) continue;
                    if (!hasSkeleton) { skeleton = new Bounds(bone.position, Vector3.zero); hasSkeleton = true; }
                    else skeleton.Encapsulate(bone.position);
                }
            }
            if (hasSkeleton) skeleton.Expand(Mathf.Max(skeleton.size.x, skeleton.size.y, skeleton.size.z) * 0.6f);
            foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!smr.enabled || smr.sharedMesh == null) continue;
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                Transform t = smr.transform;
                foreach (Vector3 v in baked.vertices)
                {
                    Vector3 w = t.position + t.rotation * v;
                    if (nearSkeleton && hasSkeleton && !skeleton.Contains(w)) continue;
                    xs.Add(w.x); ys.Add(w.y); zs.Add(w.z);
                }
                Object.DestroyImmediate(baked);
            }
            if (xs.Count > 0)
            {
                float Pct(List<float> list, float p) => list[Mathf.Clamp(Mathf.RoundToInt(p * (list.Count - 1)), 0, list.Count - 1)];
                xs.Sort(); ys.Sort(); zs.Sort();
                float lo = xs.Count > 400 ? 0.005f : 0f, hi = xs.Count > 400 ? 0.995f : 1f;
                var min = new Vector3(Pct(xs, lo), Pct(ys, lo), Pct(zs, lo));
                var max = new Vector3(Pct(xs, hi), Pct(ys, hi), Pct(zs, hi));
                b = new Bounds((min + max) * 0.5f, max - min);
                any = true;
            }
            foreach (MeshRenderer mr in go.GetComponentsInChildren<MeshRenderer>())
            {
                if (!mr.enabled) continue;
                if (!any) { b = mr.bounds; any = true; } else b.Encapsulate(mr.bounds);
            }
            return b;
        }

        private static GameObject BuildCharacterPrefab(string fbx, AnimatorController ctrl, string prefabName, float height, float yaw, bool legend, bool groundAtOrigin = false)
        {
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var root = new GameObject(prefabName);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            inst.name = "Model";
            inst.transform.SetParent(root.transform, false);
            inst.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            Animator animator = inst.GetComponent<Animator>() ?? inst.AddComponent<Animator>();
            animator.runtimeAnimatorController = ctrl;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            AnimationClip idle = ctrl.animationClips.FirstOrDefault(c => c.name == LegendPresenter.IdleState);
            if (idle != null) idle.SampleAnimation(inst, 0f);
            // Facing from the rig itself (foot -> toe), so the model looks along +Z like the course towers.
            float detected = DetectYaw(inst, out int feet);
            inst.transform.localRotation = Quaternion.Euler(0f, yaw + detected, 0f);
            Bounds b = MeasureBounds(inst);
            Bounds raw = MeasureBounds(inst, false);
            Log(prefabName + ": facing correction " + detected.ToString("0") + " deg from " + feet + " left/right bone pair(s); bounds " +
                b.size.ToString("F2") + " (all vertices: " + raw.size.ToString("F2") + ")");
            float s = b.size.y > 0.001f ? height / b.size.y : 1f;
            inst.transform.localScale = Vector3.one * s;
            inst.transform.localPosition = new Vector3(-b.center.x * s, groundAtOrigin ? 0f : -b.min.y * s, -b.center.z * s);
            if (groundAtOrigin) Log(prefabName + ": placed by the rig origin (authored ground); idle lowest point " + (b.min.y * s).ToString("0.00") + " above it");
            foreach (SkinnedMeshRenderer smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                smr.updateWhenOffscreen = false;
                smr.skinnedMotionVectors = false;
            }
            if (groundAtOrigin) FitCullingBounds(root.transform, height);
            string path = PrefabDir + "/" + prefabName + ".prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            int tris = 0;
            foreach (SkinnedMeshRenderer smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>()) tris += smr.sharedMesh != null ? smr.sharedMesh.triangles.Length / 3 : 0;
            Log(prefabName + ".prefab: source height " + b.size.y.ToString("0.00") + " -> " + height + " (scale " + s.ToString("0.000") + "), " + tris + " tris");
            return prefab;
        }

        /// <summary>
        /// Phase 13.1: the imported culling bounds of La Patasola's parts are relative to a control bone (c_pos) and do
        /// not enclose the character, so close cameras culled the skirt or the whole model. Every part gets the same
        /// cube around the defender (side 2x its height, centred at 0.4x its height): it encloses every clip, including the
        /// death pose lying on the ground. A cube is used because it stays valid whatever the root bone's rotation is.
        /// </summary>
        private static void FitCullingBounds(Transform root, float height)
        {
            // Centred low: the source death pose lies on the ground with the hanging leg ~0.8 m under it.
            Vector3 worldCentre = root.position + Vector3.up * height * 0.4f;
            float side = height * 2f;
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Transform space = smr.rootBone != null ? smr.rootBone : smr.transform;
                Vector3 s = space.lossyScale;
                float scale = Mathf.Max(0.0001f, Mathf.Min(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
                smr.localBounds = new Bounds(space.InverseTransformPoint(worldCentre), Vector3.one * side / scale);
            }
            Log(root.name + ": culling bounds of " + root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length + " skinned parts set to a " + side.ToString("0.0") + " m cube around the defender");
        }

        private static GameObject BuildStaticPrefab(string fbx, string prefabName, float height, float yaw)
        {
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var root = new GameObject(prefabName);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            inst.name = "Model";
            inst.transform.SetParent(root.transform, false);
            inst.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            Bounds b = MeasureBounds(inst);
            float s = b.size.y > 0.001f ? height / b.size.y : 1f;
            inst.transform.localScale = Vector3.one * s;
            inst.transform.localPosition = new Vector3(-b.center.x * s, -b.min.y * s, -b.center.z * s);
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            string path = PrefabDir + "/" + prefabName + ".prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            int tris = 0;
            foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>()) tris += mf.sharedMesh != null ? mf.sharedMesh.triangles.Length / 3 : 0;
            Log(prefabName + ".prefab (static, no animations in the source): bounds " + b.size.ToString("F2") + " -> height " + height + " (scale " + s.ToString("0.000") + "), " + tris + " tris");
            return prefab;
        }

        /// <summary>
        /// Yaw (degrees) that makes the rig face +Z. The character's right side is found from every left/right bone pair
        /// (".l"/".r", "_l"/"_r", "Left"/"Right"): right = R - L, forward = right x up. Returns 0 when the rig has no pairs.
        /// </summary>
        public static float DetectYaw(GameObject inst, out int pairs)
        {
            var bones = new Dictionary<string, Transform>();
            foreach (Transform t in inst.GetComponentsInChildren<Transform>()) bones[t.name] = t;
            Vector3 right = Vector3.zero;
            pairs = 0;
            foreach (var pair in bones)
            {
                string n = pair.Key, other = null;
                if (n.EndsWith(".l")) other = n.Substring(0, n.Length - 2) + ".r";
                else if (n.EndsWith("_l")) other = n.Substring(0, n.Length - 2) + "_r";
                else if (n.EndsWith(".L")) other = n.Substring(0, n.Length - 2) + ".R";
                else if (n.Contains("Left")) other = n.Replace("Left", "Right");
                if (other == null || !bones.TryGetValue(other, out Transform r)) continue;
                Vector3 v = r.position - pair.Value.position;
                v.y = 0f;
                if (v.sqrMagnitude < 1e-8f) continue;
                right += v.normalized;
                pairs++;
            }
            if (pairs == 0 || right.sqrMagnitude < 1e-6f) return 0f;
            Vector3 forward = Vector3.Cross(right.normalized, Vector3.up);
            return Vector3.SignedAngle(forward, Vector3.forward, Vector3.up);
        }

        private static Dictionary<string, GameObject> BuildPropPrefabs()
        {
            var result = new Dictionary<string, GameObject>();
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PropsFbx) == null) { Log("MISSING " + PropsFbx); return result; }
            ConfigureTextures(Root + "/Props/Textures", 512);
            ConfigureModel(PropsFbx, null);
            RemapMaterials(PropsFbx, Root + "/Props");
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(PropsFbx);
            EnsureFolder(Root + "/Props/Prefabs");
            foreach (Transform child in model.transform)
            {
                if (!child.name.StartsWith("GDA_") || child.GetComponent<MeshFilter>() == null) continue;
                string name = child.name.Substring(4);
                var root = new GameObject("Prop_" + name);
                GameObject copy = Object.Instantiate(child.gameObject);
                copy.name = name;
                copy.transform.SetParent(root.transform, false);
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = child.localRotation;
                copy.transform.localScale = child.localScale;
                Bounds b = MeasureBounds(copy);
                copy.transform.localPosition = new Vector3(-b.center.x, -b.min.y, -b.center.z);
                foreach (Collider c in root.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, Root + "/Props/Prefabs/" + name + ".prefab");
                Object.DestroyImmediate(root);
                result[name] = prefab;
            }
            Log("Props: " + result.Count + " prefabs (" + string.Join(", ", result.Keys) + ")");
            return result;
        }

        private static GameObject Place(GameObject prefab, Transform parent, string name, float height)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = name;
            Bounds b = MeasureBounds(go);
            float s = b.size.y > 0.001f ? height / b.size.y : 1f;
            go.transform.localScale = Vector3.one * s;
            return go;
        }

        private static GameObject BuildBookShrine(Dictionary<string, GameObject> props)
        {
            // Phase 12: the package plaza (INF_Book_Center pieces) replaces the phase 11 shrine when it is available.
            GameObject plaza = MapRestyleBuilder.BuildBookPlaza();
            if (plaza != null) return plaza;
            if (!props.TryGetValue("Base", out GameObject basePrefab) || !props.TryGetValue("Libro", out GameObject bookPrefab))
            {
                Log("Libro Sagrado: Base/Libro props missing -> castle keeps its model");
                return null;
            }
            var root = new GameObject("LibroSagrado");
            GameObject pedestal = Place(basePrefab, root.transform, "Base", 0.45f);
            Bounds pb = MeasureBounds(pedestal);
            float footprint = Mathf.Max(pb.size.x, pb.size.z);
            pedestal.transform.localScale *= 2.2f / footprint; // matches the 2-unit-wide castle footprint
            pb = MeasureBounds(pedestal);
            var bookPivot = new GameObject("Book Pivot");
            bookPivot.transform.SetParent(root.transform, false);
            bookPivot.transform.localPosition = new Vector3(0f, pb.max.y + 0.05f, 0f);
            bookPivot.AddComponent<SimpleRotate>().rotateSpeed = 12f; // decoration only
            GameObject book = Place(bookPrefab, bookPivot.transform, "Libro", 1.35f);
            book.transform.localPosition = Vector3.zero;
            if (props.TryGetValue("Pilar", out GameObject pillar))
            {
                foreach (float z in new[] { -1.55f, 1.55f })
                {
                    GameObject p = Place(pillar, root.transform, "Pilar", 1.6f);
                    p.transform.localPosition = new Vector3(0f, 0f, z);
                }
            }
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabDir + "/LibroSagrado.prefab");
            Object.DestroyImmediate(root);
            Log("LibroSagrado.prefab: Base + Libro (+2 Pilar), footprint 2.2");
            return prefab;
        }

        private static bool IsUnder(Transform t, Transform parent)
        {
            if (parent == null) return false;
            for (Transform p = t; p != null; p = p.parent) if (p == parent) return true;
            return false;
        }

        private static void ApplyLegendToTower(string path, LegendSpec spec, GameObject legendPrefab, float deathLength)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform old = root.transform.Find("Legend Visual");
                if (old != null) Object.DestroyImmediate(old.gameObject);
                Tower tower = root.GetComponent<Tower>();
                SlowdownTower slow = root.GetComponent<SlowdownTower>();
                Transform range = tower != null && tower.rangeModel != null ? tower.rangeModel.transform : null;
                Transform ring = slow != null ? slow.effectRing : null;
                int hidden = 0;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer || IsUnder(r.transform, range) || IsUnder(r.transform, ring)) continue;
                    if (r.enabled) { r.enabled = false; hidden++; }
                }
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(legendPrefab, root.transform);
                visual.name = "Legend Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                LegendPresenter presenter = root.GetComponent<LegendPresenter>() ?? root.AddComponent<LegendPresenter>();
                presenter.legend = spec.id;
                presenter.visualRoot = visual.transform;
                presenter.animator = visual.GetComponentInChildren<Animator>();
                presenter.deathDuration = deathLength > 0f ? deathLength + 0.3f : 0f;
                presenter.attackImpact = spec.attackImpact; // < 0: Attack plays from its start, as in phase 11
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Log(System.IO.Path.GetFileName(path) + " -> " + LegendRoster.DisplayName(spec.id) + " (" + hidden + " course renderers hidden, gameplay components untouched)");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void ApplyBookToCastle(GameObject book)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(CastlePath);
            try
            {
                Transform old = root.transform.Find("Libro Sagrado");
                if (old != null) Object.DestroyImmediate(old.gameObject);
                Transform model = root.transform.Find("Model");
                int hidden = 0;
                if (model != null)
                {
                    foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                        if (r.enabled) { r.enabled = false; hidden++; }
                }
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(book, root.transform);
                visual.name = "Libro Sagrado";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                PrefabUtility.SaveAsPrefabAsset(root, CastlePath);
                Log("Castle.prefab -> Libro Sagrado (" + hidden + " castle renderers hidden; Castle, colliders, attack points and health bar untouched)");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void ApplyGhostToEnemy(string path, GameObject ghost)
        {
            if (!File.Exists(path)) { Log("MISSING " + path); return; }
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform old = root.transform.Find("Legend Visual");
                if (old != null) Object.DestroyImmediate(old.gameObject);
                Transform model = root.transform.Find("Model");
                if (model == null) { Log(path + ": no Model"); return; }
                // Course enemy models: renderer bounds (baking their skinned meshes outside a scene is unreliable).
                Bounds b = default;
                bool anyBounds = false;
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = true;
                    if (r is SkinnedMeshRenderer smrB) smrB.updateWhenOffscreen = true;
                    Bounds rb = r.bounds;
                    if (!anyBounds) { b = rb; anyBounds = true; } else b.Encapsulate(rb);
                }
                int hidden = 0;
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = false;
                    if (r is SkinnedMeshRenderer smrH) smrH.updateWhenOffscreen = false;
                    hidden++;
                }
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(ghost, root.transform);
                visual.name = "Legend Visual";
                visual.transform.position = new Vector3(b.center.x, b.min.y, b.center.z);
                visual.transform.localRotation = Quaternion.identity;
                float height = Mathf.Clamp(b.size.y, 0.6f, 3f);
                Log(System.IO.Path.GetFileName(path) + ": course model bounds " + b.size.ToString("F2") + " at " + b.center.ToString("F2"));
                visual.transform.localScale = Vector3.one * height / Mathf.Max(0.001f, root.transform.lossyScale.y);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Log(System.IO.Path.GetFileName(path) + " -> " + ghost.name + " (height " + height.ToString("0.00") + ", " + hidden + " renderers hidden)");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ------------------------------------------------------------------ maps

        public static void DecorateScene(string scenePath, Dictionary<string, GameObject> props)
        {
            if (!File.Exists(scenePath)) return;
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            int swapped = 0;
            var counters = new Dictionary<string, int>();
            foreach (GameObject go in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).ToList())
            {
                if (go == null) continue; // part of a previous "TD Prop" destroyed in this loop
                if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go)) continue;
                string src = System.IO.Path.GetFileNameWithoutExtension(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) ?? string.Empty);
                var swap = DecorSwaps.FirstOrDefault(s => s.source == src);
                if (swap.source == null) continue;
                Transform old = go.transform.Find("TD Prop");
                if (old != null) Object.DestroyImmediate(old.gameObject);
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                Bounds b = MeasureBounds(go);
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                counters.TryGetValue(swap.source, out int n);
                counters[swap.source] = n + 1;
                string propName = swap.props[n % swap.props.Length];
                if (!props.TryGetValue(propName, out GameObject prefab)) continue;
                var prop = (GameObject)PrefabUtility.InstantiatePrefab(prefab, go.transform);
                prop.name = "TD Prop";
                Bounds pb = MeasureBounds(prop);
                float s = pb.size.y > 0.001f ? b.size.y * swap.heightFactor / pb.size.y : 1f;
                float foot = Mathf.Max(pb.size.x, pb.size.z) * s;
                float maxFoot = Mathf.Max(b.size.x, b.size.z) * 1.6f;
                if (foot > maxFoot && foot > 0.001f) s *= maxFoot / foot;
                Vector3 parentScale = go.transform.lossyScale;
                prop.transform.localScale = new Vector3(s / parentScale.x, s / parentScale.y, s / parentScale.z);
                prop.transform.rotation = Quaternion.Euler(0f, (go.transform.position.x * 37f + go.transform.position.z * 91f) % 360f, 0f);
                prop.transform.position = new Vector3(b.center.x, b.min.y, b.center.z);
                foreach (Collider c in prop.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
                GameObjectUtility.SetStaticEditorFlags(prop, StaticEditorFlags.BatchingStatic);
                foreach (Transform t in prop.GetComponentsInChildren<Transform>(true)) GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);
                swapped++;
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Log(System.IO.Path.GetFileName(scenePath) + ": " + swapped + " decoration objects now show Tower Defense props (" +
                string.Join(", ", counters.Select(p => p.Key + " x" + p.Value)) + "); colliders, paths and placement untouched");
        }

        // ------------------------------------------------------------------ collection

        private static void UpdateCollection(Dictionary<string, Sprite> portraits)
        {
            int updated = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:CollectionItemDefinition", new[] { CollectionDir }))
            {
                var item = AssetDatabase.LoadAssetAtPath<CollectionItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (item == null || !portraits.TryGetValue(item.id, out Sprite portrait) || portrait == null) continue;
                item.portrait = portrait;
                item.placeholderArt = false;
                EditorUtility.SetDirty(item);
                updated++;
            }
            Log("Collection: " + updated + " cards use model portraits (ids, unlock rules and progress unchanged)");
        }

        private static Sprite RenderPortrait(GameObject prefab, string file, bool closeUp)
        {
            string path = PortraitDir + "/" + file + ".png";
            Texture2D tex = RenderModel(prefab, 512, 512, closeUp ? 0.55f : 1.15f, closeUp ? 0.74f : 0.5f, 0f, LegendPresenter.IdleState, 0.3f, true);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.maxTextureSize = 256;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        /// <summary>Renders a prefab alone (temporary scene objects, removed afterwards) from its front (+Z).</summary>
        public static Texture2D RenderModel(GameObject prefab, int width, int height, float frameFraction, float focusFraction,
            float yaw, string state, float normalizedTime, bool transparent, bool allVertices = false)
        {
            // Freshly imported materials would otherwise render invisible while their shader variants compile in the
            // background (phase 13.1: empty La Patasola portrait, skirt missing in the first captures).
            bool asyncShaders = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            try { return RenderModelSync(prefab, width, height, frameFraction, focusFraction, yaw, state, normalizedTime, transparent, allVertices); }
            finally { ShaderUtil.allowAsyncCompilation = asyncShaders; }
        }

        private static Texture2D RenderModelSync(GameObject prefab, int width, int height, float frameFraction, float focusFraction,
            float yaw, string state, float normalizedTime, bool transparent, bool allVertices)
        {
            var temp = new GameObject("__gda_render");
            temp.transform.position = new Vector3(5000f, 0f, 5000f);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, temp.transform);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            Animator animator = go.GetComponentInChildren<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                AnimationClip clip = animator.runtimeAnimatorController.animationClips.FirstOrDefault(c => c.name == state)
                    ?? animator.runtimeAnimatorController.animationClips.FirstOrDefault(c => c.name == LegendPresenter.IdleState);
                if (clip != null) Pose(animator, clip, normalizedTime);
            }
            Bounds b = MeasureBounds(go, !allVertices);
            var camGo = new GameObject("cam");
            camGo.transform.SetParent(temp.transform, false);
            Camera cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 22f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = transparent ? new Color(0f, 0f, 0f, 0f) : new Color(0.62f, 0.70f, 0.58f, 1f);
            float frameH = Mathf.Max(b.size.y, Mathf.Max(b.size.x, b.size.z) * 0.8f) * frameFraction;
            float dist = frameH * 0.5f / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) + Mathf.Max(b.extents.x, b.extents.z);
            Vector3 focus = new Vector3(b.center.x, b.min.y + b.size.y * focusFraction, b.center.z);
            cam.transform.position = focus + new Vector3(0f, b.size.y * 0.06f, dist);
            cam.transform.LookAt(focus);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = dist * 4f + 10f;
            var key = new GameObject("key").AddComponent<Light>();
            key.transform.SetParent(temp.transform, false);
            key.type = LightType.Directional;
            key.intensity = 1.15f;
            key.transform.rotation = Quaternion.Euler(35f, 200f, 0f);
            var fill = new GameObject("fill").AddComponent<Light>();
            fill.transform.SetParent(temp.transform, false);
            fill.type = LightType.Directional;
            fill.intensity = 0.45f;
            fill.shadows = LightShadows.None;
            fill.transform.rotation = Quaternion.Euler(20f, 120f, 0f);
            var oldAmbient = RenderSettings.ambientLight;
            var oldMode = RenderSettings.ambientMode;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.cullingMask = ~0;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            RenderSettings.ambientLight = oldAmbient;
            RenderSettings.ambientMode = oldMode;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(temp);
            return tex;
        }

        /// <summary>The four tower prefabs side by side at their real scale (legends in a mid-idle pose) and the castle.</summary>
        private static Texture2D RenderTowerRow()
        {
            var temp = new GameObject("__gda_row");
            temp.transform.position = new Vector3(6000f, 0f, 6000f);
            string[] names = { "Cannon Tower", "Slow Tower", "Bomb Tower", "Triple Shot Tower" };
            for (int i = 0; i < names.Length; i++)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TowerDir + "/" + names[i] + ".prefab");
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, temp.transform);
                go.transform.localPosition = new Vector3((i - 1.5f) * 2.6f, 0f, 0f);
                go.transform.localRotation = Quaternion.identity;
                Animator a = go.GetComponentInChildren<Animator>();
                if (a != null && a.runtimeAnimatorController != null)
                {
                    AnimationClip idle = a.runtimeAnimatorController.animationClips.FirstOrDefault(c => c.name == LegendPresenter.IdleState);
                    if (idle != null) Pose(a, idle, 0.3f);
                    Bounds lb = MeasureBounds(a.gameObject);
                    Log("  row " + names[i] + ": legend height in game " + lb.size.y.ToString("F2") + " (collider capsule height 2.46)");
                }
            }
            GameObject castle = AssetDatabase.LoadAssetAtPath<GameObject>(CastlePath);
            var c = (GameObject)PrefabUtility.InstantiatePrefab(castle, temp.transform);
            c.transform.localPosition = new Vector3(7.5f, 0f, 0f);
            var camGo = new GameObject("cam");
            camGo.transform.SetParent(temp.transform, false);
            Camera cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 30f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.70f, 0.58f, 1f);
            cam.transform.localPosition = new Vector3(1.5f, 5.5f, 13f);
            cam.transform.LookAt(temp.transform.position + new Vector3(1.5f, 0.9f, 0f));
            var key = new GameObject("key").AddComponent<Light>();
            key.transform.SetParent(temp.transform, false);
            key.type = LightType.Directional;
            key.transform.rotation = Quaternion.Euler(40f, 200f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
            var rt = new RenderTexture(1600, 700, 24) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1600, 700, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1600, 700), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(temp);
            return tex;
        }

        // ------------------------------------------------------------------ verification captures

        private static void Captures(Dictionary<LegendId, GameObject> legends, Dictionary<string, GameObject> ghosts, GameObject book)
        {
            Directory.CreateDirectory(shotsDir);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            void Save(Texture2D t, string name) { File.WriteAllBytes(System.IO.Path.Combine(shotsDir, name + ".png"), t.EncodeToPNG()); Object.DestroyImmediate(t); }
            foreach (var pair in legends)
            {
                Save(RenderModel(pair.Value, 420, 560, 1.15f, 0.5f, 0f, LegendPresenter.IdleState, 0.3f, false), "legend_" + pair.Key + "_front");
                Save(RenderModel(pair.Value, 420, 560, 1.15f, 0.5f, 90f, LegendPresenter.IdleState, 0.3f, false), "legend_" + pair.Key + "_side");
                Save(RenderModel(pair.Value, 420, 560, 1.15f, 0.5f, 30f, LegendPresenter.AttackState, 0.5f, false), "legend_" + pair.Key + "_attack");
                Save(RenderModel(pair.Value, 420, 560, 1.15f, 0.5f, 30f, LegendPresenter.DeathState, 1f, false), "legend_" + pair.Key + "_death");
                Save(RenderModel(pair.Value, 420, 560, 1.15f, 0.5f, 0f, LegendPresenter.IdleState, 0.3f, false, true), "legend_" + pair.Key + "_wide");
            }
            foreach (var pair in ghosts) Save(RenderModel(pair.Value, 320, 420, 1.2f, 0.5f, 0f, LegendPresenter.IdleState, 0.4f, false), pair.Key);
            if (book != null) Save(RenderModel(book, 560, 420, 1.0f, 0.5f, 30f, null, 0f, false), "libro_sagrado");
            Save(RenderTowerRow(), "towers_row");
            foreach (string scenePath in MapScenes)
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                Camera cam = Camera.main;
                if (cam == null) continue;
                var rt = new RenderTexture(1600, 900, 24) { antiAliasing = 4 };
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Save(tex, "map_" + System.IO.Path.GetFileNameWithoutExtension(scenePath).Replace(" ", ""));
            }
            Log("captures written to " + shotsDir);
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDevAgent.ContentTools
{
    /// <summary>
    /// Phase 12: visual renewal of the three maps with resources of the "Niveles campaña e Infinito" package
    /// (copied subset in Assets/GameDevAgentContent/Escenarios; the package itself is URP, so its materials are rebuilt
    /// here as Built-in Standard materials). Presentation only: paths, waypoints, spawners, colliders, castle components,
    /// level configs and waves are never touched.
    ///  - Ground: the course "grass" material of each map is replaced by the package forest ground (one tint per map).
    ///  - Roads: a stone-slab ribbon ("MAT_Losa_Camino") is generated exactly over every route polyline (no collider).
    ///  - Lighting: one mood per map (directional light colour/intensity, trilight ambient from the package scenes).
    ///  - Decoration: extra Tower Defense props only in the top screen band where TowerManager never places towers.
    ///  - Sacred Book: plaza (podiums, lectern, book) taken from the package INF_Book_Center pieces (Nivel_01_Mohan_Movil.fbx).
    /// Idempotent. Run: Unity -batchmode -projectPath Game -executeMethod
    ///   GameDevAgent.ContentTools.MapRestyleBuilder.BuildFromCommandLine [-gdaShots dir]   (no -quit)
    /// </summary>
    public static class MapRestyleBuilder
    {
        private const string Tag = "[Maps] ";
        public const string Root = LegendContentBuilder.Root + "/Escenarios";
        public const string PlazaFbx = Root + "/Modelos/Nivel_01_Mohan_Movil.fbx";
        public const string RibbonName = "GDA Camino";
        public const string DecorRootName = "GDA Escenario";

        public class MapStyle
        {
            public string scene;
            public Color groundTint;
            public Color lightColor;
            public float lightIntensity;
            public Vector3 lightEuler;
            public Color ambientSky, ambientEquator, ambientGround;
            public string[] decor;
            public int decorCount;
        }

        // Map 1 (levels 1-2): bright, simple, few props. Map 2 (levels 3-4): deeper forest, richer props.
        // Map 3 (levels 5-6): warm dusk with ruins around the central Sacred Book.
        public static readonly MapStyle[] Styles =
        {
            new MapStyle { scene = "Assets/Course/Scenes/Test 4.unity", groundTint = new Color(1f, 1f, 1f), lightColor = new Color(1f, 0.96f, 0.88f),
                lightIntensity = 1.05f, lightEuler = new Vector3(50f, -30f, 0f),
                ambientSky = new Color(0.55f, 0.60f, 0.57f), ambientEquator = new Color(0.42f, 0.47f, 0.42f), ambientGround = new Color(0.26f, 0.26f, 0.23f),
                decor = new[] { "Arbol", "Arbol2", "Rocas", "Hongos" }, decorCount = 6 },
            new MapStyle { scene = "Assets/Course/Scenes/Test 5.unity", groundTint = new Color(0.86f, 0.94f, 0.78f), lightColor = new Color(0.94f, 1f, 0.86f),
                lightIntensity = 0.98f, lightEuler = new Vector3(55f, -45f, 0f),
                ambientSky = new Color(0.45f, 0.56f, 0.47f), ambientEquator = new Color(0.33f, 0.42f, 0.33f), ambientGround = new Color(0.20f, 0.22f, 0.17f),
                decor = new[] { "Arbol", "Arbol2", "Arbol_2", "Hongos", "Planta", "Tronco", "Rocas2" }, decorCount = 11 },
            new MapStyle { scene = "Assets/Course/Scenes/Test 6.unity", groundTint = new Color(0.90f, 0.95f, 0.86f), lightColor = new Color(1f, 0.91f, 0.80f),
                lightIntensity = 1.0f, lightEuler = new Vector3(40f, -20f, 0f),
                ambientSky = new Color(0.52f, 0.50f, 0.56f), ambientEquator = new Color(0.42f, 0.38f, 0.36f), ambientGround = new Color(0.22f, 0.20f, 0.18f),
                decor = new[] { "Muro", "Arco", "Pilar", "Poste", "Roca3", "Rocas" }, decorCount = 9 },
            // The tutorial runs on a copy of map 1: same look as levels 1-2.
            new MapStyle { scene = "Assets/Course/Scenes/Tutorial.unity", groundTint = new Color(1f, 1f, 1f), lightColor = new Color(1f, 0.96f, 0.88f),
                lightIntensity = 1.05f, lightEuler = new Vector3(50f, -30f, 0f),
                ambientSky = new Color(0.55f, 0.60f, 0.57f), ambientEquator = new Color(0.42f, 0.47f, 0.42f), ambientGround = new Color(0.26f, 0.26f, 0.23f),
                decor = new[] { "Arbol", "Arbol2", "Rocas", "Hongos" }, decorCount = 6 },
        };

        private static readonly Dictionary<string, float> DecorHeight = new Dictionary<string, float>
        {
            { "Arbol", 1.7f }, { "Arbol2", 1.7f }, { "Arbol_2", 1.6f }, { "Rocas", 0.7f }, { "Rocas2", 0.8f }, { "Roca3", 0.8f },
            { "Hongos", 0.6f }, { "Planta", 0.8f }, { "Tronco", 0.55f }, { "Muro", 1.1f }, { "Arco", 1.8f }, { "Pilar", 1.4f }, { "Poste", 1.2f },
        };

        public static readonly List<string> Report = new List<string>();
        private static string shotsDir;

        public static void BuildFromCommandLine()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-gdaShots") shotsDir = args[i + 1];
            EditorApplication.Exit(Build() ? 0 : 1);
        }

        [MenuItem("Tools/GameDevAgent/Restyle maps (phase 12)")]
        public static void BuildFromMenu() => Build();

        public static bool Build()
        {
            Report.Clear();
            try
            {
                ConfigureTextures();
                Material path = PathMaterial();
                var props = new Dictionary<string, GameObject>();
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { LegendContentBuilder.Root + "/Props/Prefabs" }))
                {
                    var p = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    props[p.name] = p;
                }
                GameObject plaza = BuildBookPlaza();
                if (plaza != null) RefreshBookPortrait(plaza);
                foreach (MapStyle style in Styles) RestyleScene(style, path, props);
                AssetDatabase.SaveAssets();
                if (!string.IsNullOrEmpty(shotsDir)) Captures(plaza);
                foreach (string line in Report) Debug.Log(Tag + line);
                Debug.Log(Tag + "DONE ok");
                return true;
            }
            catch (System.Exception e)
            {
                foreach (string line in Report) Debug.Log(Tag + line);
                Debug.LogError(Tag + "FAILED: " + e);
                return false;
            }
        }

        // ------------------------------------------------------------------ materials (URP Lit -> Standard)

        private static void ConfigureTextures()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Root + "/Texturas" }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                var ti = (TextureImporter)AssetImporter.GetAtPath(p);
                bool normal = p.EndsWith("_Normal.png"), linear = p.EndsWith("_MetalSmooth.png");
                ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = !(normal || linear);
                ti.wrapMode = TextureWrapMode.Repeat;
                ti.maxTextureSize = 1024;
                ti.mipmapEnabled = true;
                ti.textureCompression = TextureImporterCompression.Compressed;
                ti.SaveAndReimport();
            }
        }

        private static Material StandardFrom(string file, string baseName, Color tint, Vector2 tiling, float smoothScale, bool detailMaps = true)
        {
            string matDir = Root + "/Materiales";
            if (!AssetDatabase.IsValidFolder(matDir)) AssetDatabase.CreateFolder(Root, "Materiales");
            string path = matDir + "/" + file + ".mat";
            Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = Shader.Find("Standard");
            Texture2D albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Texturas/" + baseName + "_BaseColor.png");
            // The ground normal/metal maps of the package are baked for its own terrain mesh (not tileable): colour only there.
            Texture2D normal = detailMaps ? AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Texturas/" + baseName + "_Normal.png") : null;
            Texture2D ms = detailMaps ? AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Texturas/" + baseName + "_MetalSmooth.png") : null;
            m.SetTexture("_MainTex", albedo);
            m.SetTextureScale("_MainTex", tiling);
            m.SetColor("_Color", tint);
            m.SetTexture("_BumpMap", normal);
            if (normal != null) m.EnableKeyword("_NORMALMAP"); else m.DisableKeyword("_NORMALMAP");
            m.SetTexture("_MetallicGlossMap", ms);
            if (ms != null) m.EnableKeyword("_METALLICGLOSSMAP"); else m.DisableKeyword("_METALLICGLOSSMAP");
            m.SetFloat("_GlossMapScale", smoothScale);
            m.SetFloat("_Glossiness", 0.2f);
            m.SetFloat("_Metallic", 0f);
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        private static Material PathMaterial() => StandardFrom("GDA_Camino_Losas", "MAT_Losa_Camino", Color.white, Vector2.one, 0.5f);

        private static Material GroundMaterial(MapStyle style)
        {
            string name = "GDA_Suelo_" + System.IO.Path.GetFileNameWithoutExtension(style.scene).Replace(" ", "");
            return StandardFrom(name, "MAT_Suelo_Plano", style.groundTint, Vector2.one, 0.3f, false);
        }

        // ------------------------------------------------------------------ Sacred Book plaza

        private static readonly string[] PlazaParts =
        {
            "V06_Movil_base_circular_L3_plaza", "V06_Movil_base_circular_L2_podio1", "V06_Movil_base_circular_L2_podio2",
            "V06_Movil_base_circular_L2_podio3", "V06_Movil_columna_pedestal_L2_atril", "V06_Movil_libro_L1_libro",
        };

        /// <summary>
        /// Rebuilds LibroSagrado.prefab (same asset, so the Castle prefab and every scene update) from the plaza pieces of
        /// the package (the ones INF_Book_Center uses). Materials are the Standard versions created in phase 11
        /// (same Pedestal/Base/Libro textures). Returns null and keeps the phase 11 shrine if a piece is missing.
        /// </summary>
        public static GameObject BuildBookPlaza()
        {
            if (!File.Exists(PlazaFbx)) { Report.Add("Libro Sagrado: plaza FBX missing, phase 11 shrine kept"); return null; }
            var mi = (ModelImporter)AssetImporter.GetAtPath(PlazaFbx);
            mi.animationType = ModelImporterAnimationType.None;
            mi.importAnimation = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.addCollider = false;
            mi.bakeAxisConversion = true;
            mi.meshCompression = ModelImporterMeshCompression.Medium;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
            string propMats = LegendContentBuilder.Root + "/Props/Materials/";
            var remap = new Dictionary<string, string>
            {
                { "Pedestal_Baked", "Pedestal_Baked" }, { "Base_Baked_Pt2", "Base_Baked_001" }, { "Libro_Baked", "Libro_Baked" },
            };
            foreach (var pair in remap)
            {
                Material target = AssetDatabase.LoadAssetAtPath<Material>(propMats + pair.Value + ".mat");
                if (target != null) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), pair.Key), target);
            }
            mi.SaveAndReimport();

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(PlazaFbx);
            var found = new List<Transform>();
            foreach (string part in PlazaParts)
            {
                // In the FBX the pieces are named without the "V06_" prefix the package prefab gives its instances.
                string bare = part.Replace("V06_", "");
                Transform t = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == part || x.name == bare);
                if (t == null) { Report.Add("Libro Sagrado: piece '" + part + "' not found, phase 11 shrine kept"); return null; }
                found.Add(t);
            }
            var root = new GameObject("LibroSagrado");
            var content = new GameObject("Plaza");
            content.transform.SetParent(root.transform, false);
            foreach (Transform t in found)
            {
                GameObject copy = Object.Instantiate(t.gameObject);
                copy.name = t.name.Replace("V06_", "").Replace("Movil_", "");
                copy.transform.SetParent(content.transform, false);
                copy.transform.position = t.position;
                copy.transform.rotation = t.rotation;
                copy.transform.localScale = t.lossyScale;
                foreach (Transform child in copy.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);
            }
            foreach (Renderer r in content.GetComponentsInChildren<Renderer>())
            {
                foreach (Material m in r.sharedMaterials)
                    if (m == null || m.shader == null || m.shader.name != "Standard") Report.Add("WARNING plaza piece " + r.name + " material " + (m != null ? m.name : "null") + " is not a Standard phase-11 material");
            }
            Bounds plaza = LegendContentBuilder.MeasureBounds(content.transform.Find("base_circular_L3_plaza").gameObject);
            float s = 3.2f / Mathf.Max(plaza.size.x, plaza.size.z); // plaza diameter ~ the 2 x 4 castle footprint
            content.transform.localScale = Vector3.one * s;
            plaza = LegendContentBuilder.MeasureBounds(content.transform.Find("base_circular_L3_plaza").gameObject);
            content.transform.position -= new Vector3(plaza.center.x, plaza.min.y, plaza.center.z);
            // The package book is tiny at game scale: enlarge it around its own base so the goal stays readable on phones.
            Transform book = content.transform.Find("libro_L1_libro");
            if (book != null)
            {
                Bounds bb = LegendContentBuilder.MeasureBounds(book.gameObject);
                Vector3 anchor = new Vector3(bb.center.x, bb.min.y, bb.center.z);
                book.localScale *= 2.4f;
                Bounds after = LegendContentBuilder.MeasureBounds(book.gameObject);
                book.position += anchor - new Vector3(after.center.x, after.min.y, after.center.z);
            }
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            Bounds all = LegendContentBuilder.MeasureBounds(root);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, LegendContentBuilder.PrefabDir + "/LibroSagrado.prefab");
            Object.DestroyImmediate(root);
            int tris = prefab.GetComponentsInChildren<MeshFilter>().Sum(f => f.sharedMesh != null ? f.sharedMesh.triangles.Length / 3 : 0);
            Report.Add("LibroSagrado.prefab: package plaza (" + found.Count + " pieces, " + tris + " tris), size " + all.size.ToString("F2") +
                "; Castle health, colliders and attack points untouched");
            return prefab;
        }

        private static void RefreshBookPortrait(GameObject plaza)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Texture2D tex = LegendContentBuilder.RenderModel(plaza, 512, 512, 1.0f, 0.5f, 30f, null, 0f, true);
            string path = LegendContentBuilder.PortraitDir + "/portrait_libro_sagrado.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            Report.Add("Collection: libro_sagrado portrait re-rendered from the plaza");
        }

        // ------------------------------------------------------------------ scenes

        private static List<List<Vector3>> Routes()
        {
            var routes = new List<List<Vector3>>();
            foreach (EnemyWaveSpawner s in Object.FindObjectsByType<EnemyWaveSpawner>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!s.enabled) continue;
                var paths = new List<Path> { s.thePath };
                if (s.extraPaths != null) paths.AddRange(s.extraPaths);
                foreach (Path p in paths)
                {
                    if (p == null || p.points == null || p.points.Length < 2) continue;
                    var poly = p.points.Where(t => t != null).Select(t => t.position).ToList();
                    if (!routes.Any(r => r.Count == poly.Count && r.Zip(poly, (a, b) => (a - b).sqrMagnitude).Sum() < 1e-4f)) routes.Add(poly);
                }
            }
            return routes;
        }

        public static float DistanceToRoutes(Vector3 p, List<List<Vector3>> routes)
        {
            float best = float.MaxValue;
            foreach (List<Vector3> r in routes)
            {
                for (int i = 0; i < r.Count - 1; i++)
                {
                    Vector2 a = new Vector2(r[i].x, r[i].z), b = new Vector2(r[i + 1].x, r[i + 1].z), q = new Vector2(p.x, p.z);
                    Vector2 ab = b - a;
                    float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(q - a, ab) / ab.sqrMagnitude) : 0f;
                    best = Mathf.Min(best, Vector2.Distance(q, a + ab * t));
                }
            }
            return best;
        }

        private static void RestyleScene(MapStyle style, Material pathMat, Dictionary<string, GameObject> props)
        {
            if (!File.Exists(style.scene)) return;
            Scene scene = EditorSceneManager.OpenScene(style.scene, OpenSceneMode.Single);
            Material ground = GroundMaterial(style);
            List<List<Vector3>> routes = Routes();

            // Remove what a previous run generated.
            foreach (GameObject go in scene.GetRootGameObjects().ToList())
                if (go.name == RibbonName || go.name == DecorRootName) Object.DestroyImmediate(go);

            // 1. Ground: the course "grass" and the old road tiles ("dirt"/"dirtDark", including dead-end stubs that belong to no
            //    route) and the bare ground planes -> package forest ground. The visible road is now only the stone ribbon,
            //    which follows the enemy polylines exactly. Colliders and layers are not touched.
            int groundSlots = 0;
            float roadTop = float.MinValue;
            foreach (Renderer r in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Renderer>(true)))
            {
                if (r is ParticleSystemRenderer || r.GetComponentInParent<Canvas>() != null) continue;
                if (r.GetComponentInParent<Castle>() != null || r.GetComponentInParent<Tower>() != null || r.GetComponentInParent<EnemyController>() != null) continue;
                Material[] mats = r.sharedMaterials;
                bool changed = false, isRoad = false;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                bool plane = mf != null && mf.sharedMesh != null && mf.sharedMesh.name == "Plane";
                for (int i = 0; i < mats.Length; i++)
                {
                    string n = mats[i] != null ? mats[i].name : string.Empty;
                    bool road = n == "dirt" || n == "dirtDark";
                    if (road) isRoad = true;
                    // Bare ground planes only: untextured opaque material (selection/range rings are textured or transparent).
                    bool flat = plane || (r.bounds.size.y < 0.6f && Mathf.Max(r.bounds.size.x, r.bounds.size.z) > 2.5f);
                    bool bare = flat && (mats[i] == null || (mats[i].mainTexture == null && mats[i].renderQueue < 2450));
                    if (n == "grass" || n == ground.name || road || bare) { mats[i] = ground; changed = true; groundSlots++; }
                }
                if (changed) r.sharedMaterials = mats;
                if (isRoad && DistanceToRoutes(r.bounds.center, routes) < 1.2f) roadTop = Mathf.Max(roadTop, r.bounds.max.y);
            }

            // 2. Stone road ribbon over every route (exactly the enemy polylines).
            var ribbonRoot = new GameObject(RibbonName);
            float baseY = roadTop > float.MinValue ? roadTop + 0.01f : routes.SelectMany(r => r).Select(p => p.y).DefaultIfEmpty(0f).Max() + 0.03f;
            for (int i = 0; i < routes.Count; i++)
            {
                Mesh mesh = RibbonMesh(routes[i], 1.0f, baseY + i * 0.003f, 1.5f);
                string meshPath = Root + "/Generated/" + System.IO.Path.GetFileNameWithoutExtension(style.scene).Replace(" ", "") + "_Camino_" + i + ".asset";
                if (!AssetDatabase.IsValidFolder(Root + "/Generated")) AssetDatabase.CreateFolder(Root, "Generated");
                if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null) AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(mesh, meshPath);
                var seg = new GameObject("Ruta " + (i + 1), typeof(MeshFilter), typeof(MeshRenderer));
                seg.transform.SetParent(ribbonRoot.transform, false);
                seg.GetComponent<MeshFilter>().sharedMesh = mesh;
                var mr = seg.GetComponent<MeshRenderer>();
                mr.sharedMaterial = pathMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                // Not batching-static: one or two meshes per map, and they stay inspectable (tests read their vertices).
            }

            // 3. Lighting mood.
            Light sun = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(l => l.type == LightType.Directional);
            if (sun != null)
            {
                sun.color = style.lightColor;
                sun.intensity = style.lightIntensity;
                sun.transform.rotation = Quaternion.Euler(style.lightEuler);
            }
            // Camera background = the package backdrop colour (M_Fondo_v06): ground gaps (no-build holes) read as dark forest.
            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                mainCam.clearFlags = CameraClearFlags.SolidColor;
                mainCam.backgroundColor = new Color(0.24f, 0.29f, 0.18f, 1f);
            }
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = style.ambientSky;
            RenderSettings.ambientEquatorColor = style.ambientEquator;
            RenderSettings.ambientGroundColor = style.ambientGround;

            // 4. Extra decoration in the band TowerManager never builds in (top topSafePercent of the screen).
            int placed = PlaceDecor(style, props, routes);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Report.Add(System.IO.Path.GetFileName(style.scene) + ": ground " + groundSlots + " material slots -> " + ground.name + ", " + routes.Count +
                " stone road ribbon(s) at y " + baseY.ToString("0.00") + ", light " + (sun != null ? "set" : "missing") + ", " + placed + " decoration props in the no-build band");
        }

        private static Mesh RibbonMesh(List<Vector3> poly, float width, float y, float tile)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float along = 0f;
            float half = width * 0.5f;
            // Extend both ends by half a width so the road reaches under the spawn and into the castle area.
            var pts = new List<Vector3>(poly);
            Vector3 d0 = (pts[1] - pts[0]); d0.y = 0f; d0.Normalize();
            Vector3 dn = (pts[pts.Count - 1] - pts[pts.Count - 2]); dn.y = 0f; dn.Normalize();
            pts[0] -= d0 * half;
            pts[pts.Count - 1] += dn * half;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 prev = i > 0 ? pts[i] - pts[i - 1] : pts[1] - pts[0];
                Vector3 next = i < pts.Count - 1 ? pts[i + 1] - pts[i] : prev;
                prev.y = next.y = 0f;
                prev.Normalize(); next.Normalize();
                Vector3 tangent = (prev + next).sqrMagnitude > 1e-6f ? (prev + next).normalized : next;
                Vector3 side = Vector3.Cross(Vector3.up, tangent).normalized;
                float miter = 1f / Mathf.Max(0.35f, Vector3.Dot(side, Vector3.Cross(Vector3.up, next).normalized));
                if (i > 0) along += Vector3.Distance(new Vector3(pts[i].x, 0f, pts[i].z), new Vector3(pts[i - 1].x, 0f, pts[i - 1].z));
                Vector3 c = new Vector3(pts[i].x, y, pts[i].z);
                verts.Add(c - side * half * miter);
                verts.Add(c + side * half * miter);
                uvs.Add(new Vector2(0f, along / tile));
                uvs.Add(new Vector2(width / tile, along / tile));
                if (i > 0)
                {
                    int b = verts.Count - 4;
                    tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
                }
            }
            var mesh = new Mesh { name = "GDA_Camino" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            // Make every triangle face up whatever the winding.
            Vector3[] n = mesh.normals;
            if (n.Length > 0 && n[0].y < 0f)
            {
                for (int i = 0; i < tris.Count; i += 3) { int t = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = t; }
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
            }
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static int PlaceDecor(MapStyle style, Dictionary<string, GameObject> props, List<List<Vector3>> routes)
        {
            Camera cam = Camera.main;
            TowerManager tm = Object.FindAnyObjectByType<TowerManager>();
            if (cam == null || style.decor == null || style.decorCount <= 0) return 0;
            float safe = tm != null ? tm.topSafePercent / 100f : 0.15f;
            float oldAspect = cam.aspect;
            cam.aspect = 16f / 9f;
            Castle castle = Object.FindAnyObjectByType<Castle>();
            var existing = Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Where(t => t.name == "TD Prop").Select(t => t.position).ToList();
            var candidates = new List<Vector3>();
            for (float sy = 1f - safe * 0.35f; sy > 1f - safe * 0.95f; sy -= safe * 0.3f)
            {
                for (float sx = 0.06f; sx <= 0.94f; sx += 0.055f)
                {
                    Ray ray = cam.ViewportPointToRay(new Vector3(sx, sy, 0f));
                    if (Mathf.Abs(ray.direction.y) < 1e-4f) continue;
                    float t = -ray.origin.y / ray.direction.y;
                    if (t <= 0f) continue;
                    candidates.Add(ray.origin + ray.direction * t);
                }
            }
            cam.aspect = oldAspect;
            var root = new GameObject(DecorRootName);
            var chosen = new List<Vector3>();
            int n = 0;
            int k = 0;
            foreach (Vector3 c in candidates.OrderBy(p => Mathf.Abs(Mathf.Sin(p.x * 12.9898f + p.z * 78.233f) * 43758.5453f) % 1f))
            {
                if (n >= style.decorCount) break;
                if (DistanceToRoutes(c, routes) < 1.8f) continue;
                if (castle != null && Vector2.Distance(new Vector2(c.x, c.z), new Vector2(castle.transform.position.x, castle.transform.position.z)) < 3.2f) continue;
                if (existing.Any(e => Vector2.Distance(new Vector2(e.x, e.z), new Vector2(c.x, c.z)) < 1.4f)) continue;
                if (chosen.Any(e => Vector2.Distance(new Vector2(e.x, e.z), new Vector2(c.x, c.z)) < 1.5f)) continue;
                string propName = style.decor[k++ % style.decor.Length];
                if (!props.TryGetValue(propName, out GameObject prefab)) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
                go.name = "GDA " + propName;
                Bounds b = LegendContentBuilder.MeasureBounds(go);
                float h = DecorHeight.TryGetValue(propName, out float hh) ? hh : 1f;
                go.transform.localScale = Vector3.one * (b.size.y > 0.001f ? h / b.size.y : 1f);
                go.transform.position = new Vector3(c.x, 0f, c.z);
                go.transform.rotation = Quaternion.Euler(0f, (c.x * 53f + c.z * 97f) % 360f, 0f);
                foreach (Collider col in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
                foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);
                chosen.Add(c);
                n++;
            }
            return n;
        }

        // ------------------------------------------------------------------ captures

        private static void Captures(GameObject plaza)
        {
            Directory.CreateDirectory(shotsDir);
            foreach (MapStyle style in Styles)
            {
                EditorSceneManager.OpenScene(style.scene, OpenSceneMode.Single);
                Camera cam = Camera.main;
                if (cam == null) continue;
                var rt = new RenderTexture(1600, 900, 24) { antiAliasing = 4 };
                float aspect = cam.aspect;
                cam.aspect = 16f / 9f;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;
                cam.aspect = aspect;
                Object.DestroyImmediate(rt);
                File.WriteAllBytes(System.IO.Path.Combine(shotsDir, "map12_" + System.IO.Path.GetFileNameWithoutExtension(style.scene).Replace(" ", "") + ".png"), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
            }
            if (plaza != null)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Texture2D t = LegendContentBuilder.RenderModel(plaza, 640, 480, 1.0f, 0.4f, 30f, null, 0f, false);
                File.WriteAllBytes(System.IO.Path.Combine(shotsDir, "libro_plaza.png"), t.EncodeToPNG());
                Object.DestroyImmediate(t);
            }
            Report.Add("captures written to " + shotsDir);
        }
    }
}

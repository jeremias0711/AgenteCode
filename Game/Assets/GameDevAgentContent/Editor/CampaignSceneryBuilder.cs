using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GameDevAgent.ContentTools
{
    /// <summary>
    /// Phase 19: the campaign sceneries of the package "Niveles armados campaña" (Unity 6000.6.8f1 + URP) in this
    /// built-in render pipeline project.
    ///   1. URP Lit materials of Assets/Niveles/v06 become Standard (same maps; transparent water, emissive fire).
    ///   2. Textures and models: Web-friendly sizes / compression, readable meshes (runtime static batching).
    ///   3. Each approved composition (N1-N3 Móvil, N4-N5 PC, N5 Móvil as the sixth variant) becomes an environment prefab
    ///      (Resources) with its lights, placement terrain (MeshCollider + PlacementSurface), and placement blockers
    ///      under the ground (roads, water, props: they stop the placement ray, never projectiles), plus a
    ///      CampaignSceneryData asset (routes spliced into full spawn-to-Book routes, Book, attack points, camera,
    ///      atmosphere). The package scenes are only read, never saved.
    ///   4. The gameplay scenes get a CampaignScenery (level -> scenery) and their old visual roots are disabled.
    /// Batch: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.ContentTools.CampaignSceneryBuilder.BuildFromCommandLine
    ///   [-gdaShots dir] (reference renders of the package compositions and of the integrated levels).
    /// </summary>
    public static class CampaignSceneryBuilder
    {
        private const string Tag = "[Scenery] ";
        private const string PackageScenes = "Assets/Niveles/EntregaEscenarios/";
        private const string MaterialDir = "Assets/Niveles/v06/Materiales";
        private const string OutDir = "Assets/GameDevAgentContent/Escenarios/Campana";
        private const string PrefabDir = OutDir + "/Resources/Escenarios";
        private const string DataDir = OutDir + "/Datos";
        private const float RoadHalfWidth = 1.0f;
        // Uniform scale of the sceneries: the package maps are 1.3-3.4x longer than the maps the defenders (range 3 m),
        // enemies and waves were balanced for, and the legends were barely visible on a phone. At 0.6 the composition
        // is kept whole, the characters read 1.67x bigger and the routes come close to the earlier travel times.
        public const float SceneryScale = 0.6f;

        private struct Spec
        {
            public string id, scene, title;
            public Spec(string id, string scene, string title) { this.id = id; this.scene = scene; this.title = title; }
        }

        // Approved compositions (LEEME): Móvil for N1-N3, PC for N4-N5; the N5 Móvil derived variant is the sixth.
        private static readonly Spec[] Specs =
        {
            new Spec("N1_Mohan", "Nivel_01_Mohan_Movil", "Nivel 01 · Mohán"),
            new Spec("N2_Patasola", "Nivel_02_Patasola_Movil", "Nivel 02 · Patasola"),
            new Spec("N3_Hojarasquin", "Nivel_03_Hojarasquin_Movil", "Nivel 03 · Hojarasquín"),
            new Spec("N4_Madremonte", "Nivel_04_Madremonte_PC", "Nivel 04 · Madremonte"),
            new Spec("N5_Final", "Nivel_05_Final_PC", "Nivel 05 · Final"),
            new Spec("N5_Final_Variante", "Nivel_05_Final_Movil", "Nivel 05 · Final (variante)"),
            new Spec("N4_Madremonte_Movil", "Nivel_04_Madremonte_Movil", "Nivel 04 · Madremonte (móvil)"),
        };

        // Mobile browsers get the Móvil composition of the PC sceneries (same scenery, ~7x fewer triangles).
        private static readonly (string pc, string mobile)[] MobileVariants = { ("N4_Madremonte", "N4_Madremonte_Movil"), ("N5_Final", "N5_Final_Variante") };

        private struct SceneMap
        {
            public string scene;
            public (int level, string id)[] levels;
            public string fallback;
        }

        // Levels 1-6 keep their scenes (Test 4/5/6, two levels each); each level gets its own scenery.
        private static readonly SceneMap[] Scenes =
        {
            new SceneMap { scene = "Assets/Course/Scenes/Test 4.unity", levels = new[] { (1, "N1_Mohan"), (2, "N2_Patasola") }, fallback = "N1_Mohan" },
            new SceneMap { scene = "Assets/Course/Scenes/Test 5.unity", levels = new[] { (3, "N3_Hojarasquin"), (4, "N4_Madremonte") }, fallback = "N3_Hojarasquin" },
            new SceneMap { scene = "Assets/Course/Scenes/Test 6.unity", levels = new[] { (5, "N5_Final"), (6, "N5_Final_Variante") }, fallback = "N5_Final" },
            new SceneMap { scene = "Assets/Course/Scenes/Test 1.unity", levels = new (int, string)[0], fallback = "N1_Mohan" },
            new SceneMap { scene = "Assets/Course/Scenes/Test 2.unity", levels = new (int, string)[0], fallback = "N2_Patasola" },
            new SceneMap { scene = "Assets/Course/Scenes/Test 3.unity", levels = new (int, string)[0], fallback = "N3_Hojarasquin" },
            new SceneMap { scene = "Assets/Course/Scenes/Tutorial.unity", levels = new (int, string)[0], fallback = "N1_Mohan" },
        };

        private static readonly List<string> Report = new List<string>();
        private static string shots;

        public static void BuildFromCommandLine()
        {
            int code = 1;
            try
            {
                string[] args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-gdaShots") shots = args[i + 1];
                if (!string.IsNullOrEmpty(shots)) Directory.CreateDirectory(shots);
                if (args.Contains("-gdaCaptureOnly"))
                {
                    var data = Specs.ToDictionary(s => s.id, s => AssetDatabase.LoadAssetAtPath<CampaignSceneryData>(DataDir + "/" + s.id + ".asset"));
                    CaptureLevels(data);
                }
                else Build();
                code = 0;
            }
            catch (System.Exception e)
            {
                Debug.LogError(Tag + "FAILED: " + e);
            }
            foreach (string r in Report) Debug.Log(Tag + r);
            EditorApplication.Exit(code);
        }

        public static void Build()
        {
            Report.Clear();
            ConvertMaterials();
            ConfigureTextures();
            ConfigureModels();
            AssetDatabase.SaveAssets();
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(DataDir);
            var data = new Dictionary<string, CampaignSceneryData>();
            foreach (Spec spec in Specs) data[spec.id] = BuildScenery(spec);
            data = Reload();
            foreach (var v in MobileVariants)
            {
                data[v.pc].mobileVariant = data[v.mobile];
                EditorUtility.SetDirty(data[v.pc]);
            }
            AssetDatabase.SaveAssets();
            foreach (SceneMap map in Scenes) Integrate(map, data);
            if (!string.IsNullOrEmpty(shots)) CaptureLevels(data);
            Debug.Log(Tag + "DONE");
        }

        // ------------------------------------------------------------------ 1. materials (URP Lit -> Standard)

        private static void ConvertMaterials()
        {
            Shader standard = Shader.Find("Standard");
            int converted = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { MaterialDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) continue;
                var so = new SerializedObject(m);
                Texture baseMap = Tex(so, "_BaseMap") ?? Tex(so, "_MainTex");
                Texture normal = Tex(so, "_BumpMap");
                Texture metal = Tex(so, "_MetallicGlossMap");
                Texture emissionMap = Tex(so, "_EmissionMap");
                Color baseColor = Col(so, "_BaseColor", Col(so, "_Color", Color.white));
                Color emission = Col(so, "_EmissionColor", Color.black);
                float smooth = Flt(so, "_Smoothness", Flt(so, "_Glossiness", 0.3f));
                float metallic = Flt(so, "_Metallic", 0f);
                float bumpScale = Flt(so, "_BumpScale", 1f);
                bool transparent = Flt(so, "_Surface", 0f) > 0.5f;
                bool cutout = Flt(so, "_AlphaClip", 0f) > 0.5f;
                float cutoff = Flt(so, "_Cutoff", 0.5f);
                Vector2 scale = TexScale(so, "_BaseMap");

                m.shader = standard;
                m.SetTexture("_MainTex", baseMap);
                m.SetTextureScale("_MainTex", scale);
                m.SetColor("_Color", baseColor);
                m.SetTexture("_BumpMap", normal);
                m.SetFloat("_BumpScale", bumpScale);
                SetKeyword(m, "_NORMALMAP", normal != null);
                m.SetTexture("_MetallicGlossMap", metal);
                SetKeyword(m, "_METALLICGLOSSMAP", metal != null);
                m.SetFloat("_Metallic", metallic);
                m.SetFloat("_Glossiness", smooth);
                m.SetFloat("_GlossMapScale", smooth);
                bool emits = emission.maxColorComponent > 0.01f || emissionMap != null;
                m.SetColor("_EmissionColor", emission);
                m.SetTexture("_EmissionMap", emissionMap);
                SetKeyword(m, "_EMISSION", emits);
                m.globalIlluminationFlags = emits ? MaterialGlobalIlluminationFlags.RealtimeEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                if (transparent) SetMode(m, 2, cutoff);      // Fade (water, flames)
                else if (cutout) SetMode(m, 1, cutoff);      // Cutout (leaves)
                else SetMode(m, 0, cutoff);
                EditorUtility.SetDirty(m);
                converted++;
            }
            Report.Add("materials: " + converted + " URP Lit -> Standard");
        }

        private static SerializedProperty Pair(SerializedObject so, string list, string name)
        {
            SerializedProperty arr = so.FindProperty("m_SavedProperties." + list);
            if (arr == null) return null;
            for (int i = 0; i < arr.arraySize; i++)
            {
                SerializedProperty e = arr.GetArrayElementAtIndex(i);
                if (e.FindPropertyRelative("first").stringValue == name) return e.FindPropertyRelative("second");
            }
            return null;
        }

        private static Texture Tex(SerializedObject so, string n) => Pair(so, "m_TexEnvs", n)?.FindPropertyRelative("m_Texture").objectReferenceValue as Texture;
        private static Vector2 TexScale(SerializedObject so, string n) { SerializedProperty p = Pair(so, "m_TexEnvs", n); return p != null ? p.FindPropertyRelative("m_Scale").vector2Value : Vector2.one; }
        private static float Flt(SerializedObject so, string n, float d) { SerializedProperty p = Pair(so, "m_Floats", n); return p != null ? p.floatValue : d; }
        private static Color Col(SerializedObject so, string n, Color d) { SerializedProperty p = Pair(so, "m_Colors", n); return p != null ? p.colorValue : d; }

        private static void SetKeyword(Material m, string k, bool on) { if (on) m.EnableKeyword(k); else m.DisableKeyword(k); }

        // Standard shader blend modes (same values as StandardShaderGUI).
        private static void SetMode(Material m, int mode, float cutoff)
        {
            m.SetFloat("_Mode", mode);
            m.SetFloat("_Cutoff", cutoff);
            switch (mode)
            {
                case 1:
                    m.SetOverrideTag("RenderType", "TransparentCutout");
                    m.SetInt("_SrcBlend", (int)BlendMode.One); m.SetInt("_DstBlend", (int)BlendMode.Zero); m.SetInt("_ZWrite", 1);
                    m.EnableKeyword("_ALPHATEST_ON"); m.DisableKeyword("_ALPHABLEND_ON"); m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    m.renderQueue = (int)RenderQueue.AlphaTest;
                    break;
                case 2:
                    m.SetOverrideTag("RenderType", "Transparent");
                    m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha); m.SetInt("_ZWrite", 0);
                    m.DisableKeyword("_ALPHATEST_ON"); m.EnableKeyword("_ALPHABLEND_ON"); m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    m.renderQueue = (int)RenderQueue.Transparent;
                    break;
                default:
                    m.SetOverrideTag("RenderType", "");
                    m.SetInt("_SrcBlend", (int)BlendMode.One); m.SetInt("_DstBlend", (int)BlendMode.Zero); m.SetInt("_ZWrite", 1);
                    m.DisableKeyword("_ALPHATEST_ON"); m.DisableKeyword("_ALPHABLEND_ON"); m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    m.renderQueue = -1;
                    break;
            }
        }

        // ------------------------------------------------------------------ 2. textures and models

        private static void ConfigureTextures()
        {
            int n = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Niveles/v06" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                bool normal = name.EndsWith("_Normal");
                bool mask = name.EndsWith("_MetalSmooth");
                // Web mobile budget: colour 1024, normal / metal-smoothness 512 (measured with the build size).
                int size = normal || mask ? 512 : 1024;
                bool changed = imp.maxTextureSize != size || imp.textureCompression != TextureImporterCompression.Compressed
                    || (normal && imp.textureType != TextureImporterType.NormalMap);
                if (!changed) continue;
                imp.maxTextureSize = size;
                imp.textureCompression = TextureImporterCompression.Compressed;
                if (normal) imp.textureType = TextureImporterType.NormalMap;
                imp.mipmapEnabled = true;
                imp.SaveAndReimport();
                n++;
            }
            Report.Add("textures reconfigured: " + n);
        }

        private static void ConfigureModels()
        {
            int n = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { "Assets/Niveles/v06", "Assets/GameDevAgentContent/Escenarios/Modelos" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var imp = AssetImporter.GetAtPath(path) as ModelImporter;
                if (imp == null || (imp.isReadable && !imp.addCollider)) continue;
                imp.isReadable = true;       // runtime static batching of the loaded scenery
                imp.addCollider = false;
                imp.SaveAndReimport();
                n++;
            }
            Report.Add("models reconfigured: " + n);
        }

        // ------------------------------------------------------------------ 3. sceneries

        private static CampaignSceneryData BuildScenery(Spec spec)
        {
            Scene scene = EditorSceneManager.OpenScene(PackageScenes + spec.scene + ".unity", OpenSceneMode.Single);
            GameObject[] roots = scene.GetRootGameObjects();
            GameObject env = roots.FirstOrDefault(r => r.name.StartsWith("Nivel_0"));
            Transform castle = roots.FirstOrDefault(r => r.name == "Castle")?.transform;
            Camera cam = roots.Select(r => r.GetComponent<Camera>()).FirstOrDefault(c => c != null);
            if (env == null || castle == null || cam == null) throw new System.Exception(spec.scene + ": environment / Castle / camera not found");

            // Reference render of the approved composition (materials already converted).
            if (!string.IsNullOrEmpty(shots)) Render(cam, System.IO.Path.Combine(shots, "paquete_" + spec.id + ".png"), 1688, 780);

            string dataPath = DataDir + "/" + spec.id + ".asset";
            CampaignSceneryData data = AssetDatabase.LoadAssetAtPath<CampaignSceneryData>(dataPath);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<CampaignSceneryData>();
                AssetDatabase.CreateAsset(data, dataPath);
            }
            data.id = spec.id;
            data.displayName = spec.title;
            data.prefabResource = "Escenarios/" + spec.id;

            // Routes: Path_A is the main route; the others become full spawn-to-Book routes (spliced onto Path_A).
            var paths = roots.Where(r => r.name.StartsWith("Path_")).OrderBy(r => r.name == "Path_A" ? 0 : 1).ThenBy(r => r.name).ToList();
            var routes = new List<CampaignSceneryData.Route>();
            List<Vector3> main = null;
            var notes = new List<string>();
            foreach (GameObject p in paths)
            {
                List<Vector3> pts = p.transform.Cast<Transform>().OrderBy(t => t.name).Select(t => t.position).ToList();
                if (pts.Count < 2) continue;
                if (main == null) main = pts;
                else pts = Splice(pts, main, castle.position, notes, p.name);
                routes.Add(new CampaignSceneryData.Route { name = p.name, points = pts.ToArray() });
            }
            data.routes = routes.ToArray();
            data.mainRouteLength = main != null ? Length(main) : 0f;
            data.castlePosition = castle.position;
            data.castleRotation = castle.rotation;
            Transform attack = castle.Find("Attack Points (v06)") ?? castle.Find("Attack Points");
            data.attackPoints = attack != null ? attack.Cast<Transform>().Select(t => t.position).ToArray() : new Vector3[0];

            data.cameraPosition = cam.transform.position;
            data.cameraRotation = cam.transform.rotation;
            data.orthographic = cam.orthographic;
            data.orthographicSize = cam.orthographicSize;
            data.fieldOfView = cam.fieldOfView;
            data.nearClip = cam.nearClipPlane;
            data.farClip = cam.farClipPlane;
            data.clearFlags = cam.clearFlags == CameraClearFlags.Skybox && RenderSettings.skybox == null ? CameraClearFlags.SolidColor : cam.clearFlags;
            data.background = cam.backgroundColor;
            data.ambientMode = RenderSettings.ambientMode;
            data.ambientSky = RenderSettings.ambientSkyColor;
            data.ambientEquator = RenderSettings.ambientEquatorColor;
            data.ambientGround = RenderSettings.ambientGroundColor;
            data.ambientLight = RenderSettings.ambientLight;
            data.ambientIntensity = RenderSettings.ambientIntensity;
            data.fog = RenderSettings.fog;
            data.fogMode = RenderSettings.fogMode;
            data.fogColor = RenderSettings.fogColor;
            data.fogDensity = RenderSettings.fogDensity;
            data.fogStart = RenderSettings.fogStartDistance;
            data.fogEnd = RenderSettings.fogEndDistance;

            // Environment prefab: the composition + its lights, gameplay markers hidden, colliders for placement.
            var root = new GameObject(spec.id);
            env.transform.SetParent(root.transform, true);
            foreach (GameObject r in roots)
            {
                if (r != null && r.GetComponent<Light>() != null) r.transform.SetParent(root.transform, true);
            }
            data.bookObjects = env.transform.Cast<Transform>()
                .Where(t => { string n = t.name.ToLowerInvariant(); return !n.StartsWith("mk_") && (n.Contains("libro") || n.Contains("atril")); })
                .Select(t => t.name).ToArray();

            // Meshes generated inside the package scene (the visual road "RutaCampana_Visual") are not assets: a prefab
            // would lose them, so they are saved next to the sceneries.
            int saved = 0, unresolved = 0;
            foreach (MeshFilter mf in env.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) { if (mf.GetComponent<Renderer>() != null) unresolved++; continue; }
                if (AssetDatabase.Contains(mf.sharedMesh)) continue;
                Directory.CreateDirectory(OutDir + "/Mallas");
                string meshPath = OutDir + "/Mallas/" + spec.id + "_" + mf.name + ".asset";
                Mesh copy = Object.Instantiate(mf.sharedMesh);
                copy.name = spec.id + "_" + mf.name;
                if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null) AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(copy, meshPath);
                mf.sharedMesh = copy;
                saved++;
            }

            int markers = 0;
            foreach (Renderer rd in env.GetComponentsInChildren<Renderer>(true))
            {
                if (rd.name.StartsWith("MK_") || rd.transform.parent != null && rd.transform.parent.name.StartsWith("MK_")) { rd.enabled = false; markers++; }
            }
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);

            int ground = 0, blockers = 0;
            int groundLayer = LayerMask.NameToLayer("Ground"), pathLayer = LayerMask.NameToLayer("Path");
            var blockRoot = new GameObject("Bloqueos de colocación").transform;
            blockRoot.SetParent(root.transform, false);
            foreach (MeshFilter mf in env.GetComponentsInChildren<MeshFilter>(true))
            {
                // Name of the object and its ancestors inside the composition ("DecoracionCampana_ArbolBosquete/..." ).
                string n = string.Join("/", mf.GetComponentsInParent<Transform>(true).TakeWhile(t => t != env.transform).Select(t => t.name)).ToLowerInvariant();
                Renderer rd = mf.GetComponent<Renderer>();
                if (mf.sharedMesh == null || rd == null) continue;
                if (n.Contains("terreno") && !n.Contains("adaptado_backdrop"))
                {
                    // Placement terrain (relief): towers stand on its surface.
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    mf.gameObject.layer = groundLayer;
                    mf.gameObject.AddComponent<PlacementSurface>();
                    ground++;
                    continue;
                }
                if (IsFloor(n)) continue;
                Bounds b = rd.bounds;
                bool water = n.Contains("rio") || n.Contains("charca") || n.Contains("agua");
                bool tree = n.Contains("arbol");
                float k = water ? 1f : tree ? 0.45f : 0.8f;
                if (!water && b.size.y < 0.25f) continue; // flat decals
                if (b.size.x > 6f || b.size.z > 6f)
                {
                    // Large meshes (a winding river, combined walls): their box would block the whole map, so the
                    // blockers follow the mesh itself on a 1 m grid.
                    blockers += GridBlockers(blockRoot, mf, b, pathLayer);
                    continue;
                }
                Vector2 foot = new Vector2(b.size.x, b.size.z) * k;
                if (foot.x < 0.3f || foot.y < 0.3f) foot = Vector2.Max(foot, new Vector2(0.5f, 0.5f));
                AddBlocker(blockRoot, new Vector3(b.center.x, b.min.y - 1f, b.center.z), new Vector3(foot.x, 1.6f, foot.y), Quaternion.identity, pathLayer);
                blockers++;
            }
            int roadBoxes = 0;
            foreach (CampaignSceneryData.Route r in data.routes)
            {
                for (int i = 1; i < r.points.Length; i++)
                {
                    Vector3 a = r.points[i - 1], b = r.points[i];
                    Vector3 d = b - a; d.y = 0f;
                    if (d.sqrMagnitude < 0.0001f) continue;
                    // Follows the slope of the segment (roads climb to the raised altars), 1 m under the road surface.
                    Vector3 along = b - a;
                    Quaternion rot = Quaternion.LookRotation(along, Vector3.up);
                    Vector3 c = (a + b) * 0.5f - rot * Vector3.up * 1f;
                    AddBlocker(blockRoot, c, new Vector3(RoadHalfWidth * 2f, 1.6f, along.magnitude + RoadHalfWidth * 2f), rot, pathLayer);
                    roadBoxes++;
                }
            }

            int tris = 0, renderers = 0;
            var mats = new HashSet<Material>();
            foreach (MeshRenderer mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!mr.enabled) continue;
                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                renderers++;
                tris += (int)(mf.sharedMesh.triangles.Length / 3);
                foreach (Material m in mr.sharedMaterials) if (m != null) mats.Add(m);
            }
            data.triangles = tris;
            int pink = mats.Count(m => m.shader == null || m.shader.name == "Hidden/InternalErrorShader" || !m.shader.isSupported);

            // Uniform scale about the origin: the prefab root, and every position / size of the data with it.
            float s = SceneryScale;
            root.transform.localScale = Vector3.one * s;
            foreach (CampaignSceneryData.Route r in data.routes) for (int i = 0; i < r.points.Length; i++) r.points[i] *= s;
            data.castlePosition *= s;
            for (int i = 0; i < data.attackPoints.Length; i++) data.attackPoints[i] *= s;
            data.cameraPosition *= s;
            data.orthographicSize *= s;
            data.fogStart *= s;
            data.fogEnd *= s;
            data.mainRouteLength *= s;

            string prefabPath = PrefabDir + "/" + spec.id + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets(); // before the next scene is opened (it unloads unreferenced assets)
            Report.Add(spec.id + " (" + spec.scene + ", scale " + s + "): routes " + string.Join(", ", data.routes.Select(r => r.name + " " + r.points.Length + " pts " + Length(r.points.ToList()).ToString("0.0") + " m"))
                + (notes.Count > 0 ? " [" + string.Join("; ", notes) + "]" : "")
                + "; Book " + data.castlePosition.ToString("0.0") + " (" + string.Join("/", data.bookObjects) + "), " + data.attackPoints.Length + " attack points"
                + "; camera " + (data.orthographic ? "ortho " + data.orthographicSize.ToString("0.0") : "fov " + data.fieldOfView.ToString("0"))
                + "; " + renderers + " meshes, " + tris + " tris, " + mats.Count + " materials (" + pink + " unsupported); terrain colliders " + ground
                + ", prop/water blockers " + blockers + ", road blockers " + roadBoxes + ", markers hidden " + markers
                + ", scene meshes saved " + saved + ", unresolved meshes " + unresolved
                + "; fog " + data.fog + ", ambient " + data.ambientMode);
            return data;
        }

        private static bool IsFloor(string n)
        {
            foreach (string f in new[] { "losa", "piso", "panel_terreno", "backdrop", "ruta", "camino", "terreno", "mk_", "suelo" })
            {
                if (n.Contains(f)) return true;
            }
            return false;
        }

        // 1 m cells where the mesh is (raycast on a temporary collider), merged along x, 1 m under its lowest point there.
        private static int GridBlockers(Transform parent, MeshFilter mf, Bounds b, int layer)
        {
            var temp = mf.gameObject.AddComponent<MeshCollider>();
            temp.sharedMesh = mf.sharedMesh;
            Physics.SyncTransforms();
            int boxes = 0;
            for (float z = b.min.z + 0.5f; z < b.max.z; z += 1f)
            {
                float runStart = float.NaN, runY = 0f;
                for (float x = b.min.x + 0.5f; x < b.max.x + 1f; x += 1f)
                {
                    RaycastHit h = default;
                    bool hit = x < b.max.x && temp.Raycast(new Ray(new Vector3(x, b.max.y + 5f, z), Vector3.down), out h, b.size.y + 10f);
                    if (hit)
                    {
                        if (float.IsNaN(runStart)) { runStart = x; runY = h.point.y; }
                        else runY = Mathf.Min(runY, h.point.y);
                    }
                    else if (!float.IsNaN(runStart))
                    {
                        float x0 = runStart - 0.5f, x1 = x - 0.5f;
                        AddBlocker(parent, new Vector3((x0 + x1) * 0.5f, runY - 1f, z), new Vector3(x1 - x0, 1.6f, 1f), Quaternion.identity, layer);
                        boxes++;
                        runStart = float.NaN;
                    }
                }
            }
            Object.DestroyImmediate(temp);
            return boxes;
        }

        private static void AddBlocker(Transform parent, Vector3 center, Vector3 size, Quaternion rotation, int layer)
        {
            var go = new GameObject("Bloqueo");
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(center, rotation);
            var box = go.AddComponent<BoxCollider>();
            box.size = size;
        }

        // A secondary path becomes a full route: it keeps its own start when it is a separate entry (else it starts on
        // Path_A), and it ends at the Book (Path_A's tail is appended when it joins Path_A before the Book).
        private static List<Vector3> Splice(List<Vector3> pts, List<Vector3> main, Vector3 book, List<string> notes, string name)
        {
            const float Join = 3f;
            var result = new List<Vector3>(pts);
            int startJoin = Nearest(main, pts[0], out float ds);
            if (ds < Join && startJoin > 0)
            {
                result.InsertRange(0, main.GetRange(0, startJoin));
                notes.Add(name + " starts on Path_A (#" + startJoin + ")");
            }
            if (Flat(result[result.Count - 1] - book) > 4f)
            {
                int endJoin = Nearest(main, result[result.Count - 1], out float de);
                if (de < Join && endJoin < main.Count - 1)
                {
                    result.AddRange(main.GetRange(endJoin + 1, main.Count - endJoin - 1));
                    notes.Add(name + " rejoins Path_A (#" + endJoin + ")");
                }
                else
                {
                    // The route may end beside a straight stretch of Path_A (a T junction, no vertex close by):
                    // it continues from the projection onto that segment, so its enemies follow the road to the Book.
                    Vector3 end = result[result.Count - 1];
                    int seg = -1; float best = float.MaxValue; Vector3 proj = end;
                    for (int i = 0; i < main.Count - 1; i++)
                    {
                        Vector3 a = main[i], b = main[i + 1];
                        Vector3 ab = b - a; ab.y = 0f;
                        float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(new Vector3(end.x - a.x, 0f, end.z - a.z), ab) / ab.sqrMagnitude) : 0f;
                        Vector3 q = Vector3.Lerp(a, b, t);
                        float d = Flat(q - end);
                        if (d < best) { best = d; seg = i; proj = q; }
                    }
                    if (seg >= 0 && best < Join)
                    {
                        if (best > 0.05f) result.Add(proj);
                        result.AddRange(main.GetRange(seg + 1, main.Count - seg - 1));
                        notes.Add(name + " rejoins Path_A on segment #" + seg + " (" + best.ToString("0.0") + " m)");
                    }
                }
            }
            return result;
        }

        private static int Nearest(List<Vector3> pts, Vector3 p, out float dist)
        {
            int best = 0; dist = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                float d = Flat(pts[i] - p);
                if (d < dist) { dist = d; best = i; }
            }
            return best;
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        private static float Length(List<Vector3> pts)
        {
            float l = 0f;
            for (int i = 1; i < pts.Count; i++) l += Flat(pts[i] - pts[i - 1]);
            return l;
        }

        // ------------------------------------------------------------------ 4. gameplay scenes

        private static readonly HashSet<string> LegacyNames = new HashSet<string> { "Environment", "GDA Camino", "GDA Escenario", "GDA Selva", "Directional Light" };

        private static void Integrate(SceneMap map, Dictionary<string, CampaignSceneryData> data)
        {
            Scene scene = EditorSceneManager.OpenScene(map.scene, OpenSceneMode.Single);
            data = Reload(); // opening a scene unloads assets only referenced from C#
            GameObject holder = scene.GetRootGameObjects().FirstOrDefault(r => r.name == "Escenario de campaña") ?? new GameObject("Escenario de campaña");
            var scenery = holder.GetComponent<CampaignScenery>() ?? holder.AddComponent<CampaignScenery>();
            scenery.entries = map.levels.Select(l => new CampaignScenery.Entry { levelIds = new[] { l.level }, data = data[l.id] }).ToArray();
            scenery.sceneDefault = data[map.fallback];

            var legacy = new List<GameObject>(scenery.legacyRoots.Where(g => g != null));
            foreach (GameObject r in scene.GetRootGameObjects())
            {
                if (r == holder || legacy.Contains(r)) continue;
                bool named = LegacyNames.Contains(r.name);
                bool visualOnly = r.GetComponentsInChildren<MonoBehaviour>(true).Length == 0 && r.GetComponentInChildren<Camera>(true) == null
                    && (r.GetComponentInChildren<Renderer>(true) != null || r.GetComponentInChildren<Collider>(true) != null || r.GetComponentInChildren<Light>(true) != null);
                if (named || visualOnly) legacy.Add(r);
            }
            foreach (GameObject g in legacy) g.SetActive(false);
            scenery.legacyRoots = legacy.ToArray();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Report.Add(System.IO.Path.GetFileNameWithoutExtension(map.scene) + ": " + string.Join(", ", map.levels.Select(l => "L" + l.level + " " + l.id))
                + (map.levels.Length > 0 ? "; " : "") + "default " + map.fallback + "; old visual roots disabled: " + string.Join(", ", legacy.Select(g => g.name)));
        }

        private static Dictionary<string, CampaignSceneryData> Reload()
        {
            return Specs.ToDictionary(s => s.id, s => AssetDatabase.LoadAssetAtPath<CampaignSceneryData>(DataDir + "/" + s.id + ".asset"));
        }

        // ------------------------------------------------------------------ captures

        private static void CaptureLevels(Dictionary<string, CampaignSceneryData> data)
        {
            foreach (SceneMap map in Scenes)
            {
                var ids = map.levels.Length > 0 ? map.levels.Select(l => (tag: "L" + l.level, id: l.id)).ToArray() : new[] { (tag: System.IO.Path.GetFileNameWithoutExtension(map.scene).Replace(" ", ""), id: map.fallback) };
                foreach (var entry in ids)
                {
                    EditorSceneManager.OpenScene(map.scene, OpenSceneMode.Single);
                    data = Reload();
                    CampaignScenery scenery = Object.FindAnyObjectByType<CampaignScenery>(FindObjectsInactive.Include);
                    data.TryGetValue(entry.id, out CampaignSceneryData d);
                    Debug.Log(Tag + "capture " + map.scene + " " + entry.tag + ": scenery " + (scenery != null) + ", data " + (d != null ? d.id : "NULL"));
                    if (scenery == null || d == null) continue;
                    scenery.Apply(d);
                    foreach (Vector2Int size in new[] { new Vector2Int(1688, 780), new Vector2Int(1920, 1080) })
                    {
                        Camera cam = Camera.main;
                        Render(cam, null, size.x, size.y, true, () =>
                        {
                            // Same HUD fit as at runtime, measured on the HUD laid out for this resolution.
                            scenery.FitCameraToHud(cam, CampaignScenery.HudViewportRects(cam));
                            Debug.Log(Tag + entry.tag + " " + size.x + "x" + size.y + " camera fit: " + scenery.LastFit);
                        });
                        Render(cam, System.IO.Path.Combine(shots, "juego_" + entry.tag + "_" + entry.id + (size.x == 1688 ? "" : "_1920") + ".png"), size.x, size.y, true);
                    }
                }
            }
        }

        private static void Render(Camera cam, string path, int w, int h, bool withUi = false, System.Action beforeShot = null)
        {
            if (withUi)
            {
                foreach (Canvas canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = cam;
                    canvas.planeDistance = cam.nearClipPlane + 0.5f;
                }
            }
            var rt = new RenderTexture(w, h, 24) { antiAliasing = 4 };
            RenderTexture prev = cam.targetTexture;
            float aspect = cam.aspect;
            cam.targetTexture = rt;
            cam.aspect = (float)w / h;
            for (int pass = 0; pass < 2; pass++) { Canvas.ForceUpdateCanvases(); cam.Render(); }
            if (beforeShot != null)
            {
                beforeShot();
                cam.targetTexture = prev;
                cam.aspect = aspect;
                Object.DestroyImmediate(rt);
                return;
            }
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = prev;
            cam.aspect = aspect;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }
    }
}

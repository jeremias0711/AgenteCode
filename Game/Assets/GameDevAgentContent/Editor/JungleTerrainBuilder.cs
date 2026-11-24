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
    /// Phase 17: Colombian jungle terrain for the maps. Presentation only, on a separate visual layer:
    ///  - A relief mesh ("GDA Selva/Relieve", no collider) replaces the look of the flat ground. It is EXACTLY flat at the
    ///    ground height wherever the game is played: within ~3.4 m of every enemy route (where defenders are built: a
    ///    defender farther than its 3 m range from the road can never shoot), around the Sacred Book and under every
    ///    existing prop. Beyond that it rises into hills, ridges and hollows, and climbs at the map edges for depth.
    ///    Vertex colours (custom GDA/JungleGround shader) paint moss, earthy road edges, a lighter clearing where
    ///    defenders stand, shaded hollows, rocky slopes and a warm glow around the Book.
    ///  - The original ground renderers are only hidden: their colliders, layers, the routes, waypoints, spawners, castle,
    ///    towers and every gameplay value stay as they were.
    ///  - Low obstacle boxes (the obstacle layer TowerManager already checks) under the hills, so a defender can never be
    ///    placed sunk into a hill. They lie below 0.2 m: projectiles flying over them are not affected.
    ///  - Composition: low pebbles and plants along the road edges, the play clearing kept free, dense vegetation /
    ///    rocky zones / clearings on the hills (noise-driven, deterministic), an outer tree line, ruins sites, and the
    ///    "Terreno" root clearing under the Sacred Book. Trees are capped so they never hide the road or the Book from the
    ///    game camera. No colliders on props, static batching, shared materials.
    ///  - Light: soft shadows (no black shadows) and a light linear fog for depth.
    /// Run: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.ContentTools.JungleTerrainBuilder.BuildFromCommandLine
    ///      [-gdaShots dir] [-gdaShotTag before|after] [-gdaCaptureOnly]
    /// </summary>
    public static class JungleTerrainBuilder
    {
        private const string Tag = "[Jungle] ";
        public const string RootName = "GDA Selva";
        public const string ReliefName = "Relieve";
        public const string BlockName = "Bloqueo relieve";
        public const float PlayRadius = 3.4f;       // flat ground within this distance of a route (tower range 3 m + margin)
        public const float BookRadius = 4.2f;       // flat ground around the Sacred Book
        public const float RoadHalf = 0.5f;         // stone ribbon half width (MapRestyleBuilder)
        private const string PropDir = LegendContentBuilder.Root + "/Props/Prefabs/";

        public class Style
        {
            public string scene, title;
            public int seed;
            public float hillAmp, edgeRise, vegetation, rockiness;
            public int ruins;
            public Color moss, dirt, clearing, shade;
            public Color fog;
            public string[] trees, plants, rocks;
        }

        private static readonly string[] Trees = { "Arbol", "Arbol2", "Arbol_2" };

        public static readonly Style[] Styles =
        {
            // Map 1 (levels 1-2) and the tutorial: "Claro del Mohán" — gentle green hills, mixed forest, one ruin.
            new Style { scene = "Assets/Course/Scenes/Test 4.unity", title = "Claro del Mohán", seed = 41, hillAmp = 1.1f, edgeRise = 2.2f, vegetation = 0.55f, rockiness = 0.35f, ruins = 1,
                moss = new Color(0.47f, 0.56f, 0.40f), dirt = new Color(0.60f, 0.49f, 0.36f), clearing = new Color(0.55f, 0.60f, 0.44f), shade = new Color(0.36f, 0.46f, 0.34f), fog = new Color(0.55f, 0.63f, 0.52f),
                trees = Trees, plants = new[] { "Planta", "Hongos" }, rocks = new[] { "Rocas", "Roca3" } },
            new Style { scene = "Assets/Course/Scenes/Tutorial.unity", title = "Claro del Mohán (tutorial)", seed = 41, hillAmp = 1.1f, edgeRise = 2.2f, vegetation = 0.55f, rockiness = 0.35f, ruins = 1,
                moss = new Color(0.47f, 0.56f, 0.40f), dirt = new Color(0.60f, 0.49f, 0.36f), clearing = new Color(0.55f, 0.60f, 0.44f), shade = new Color(0.36f, 0.46f, 0.34f), fog = new Color(0.55f, 0.63f, 0.52f),
                trees = Trees, plants = new[] { "Planta", "Hongos" }, rocks = new[] { "Rocas", "Roca3" } },
            // Map 2 (levels 3-4): "Selva profunda" — higher hills, dense dark forest, mushrooms and fallen trunks.
            new Style { scene = "Assets/Course/Scenes/Test 5.unity", title = "Selva profunda", seed = 52, hillAmp = 1.6f, edgeRise = 2.8f, vegetation = 0.75f, rockiness = 0.25f, ruins = 1,
                moss = new Color(0.40f, 0.52f, 0.36f), dirt = new Color(0.55f, 0.44f, 0.32f), clearing = new Color(0.50f, 0.57f, 0.41f), shade = new Color(0.30f, 0.40f, 0.30f), fog = new Color(0.47f, 0.57f, 0.47f),
                trees = Trees, plants = new[] { "Planta", "Hongos", "Tronco", "Planta" }, rocks = new[] { "Rocas2", "Rocas" } },
            // Map 3 (levels 5-6): "Ruinas del Olvido" — rocky warm hills and two ruin sites around the central Book.
            new Style { scene = "Assets/Course/Scenes/Test 6.unity", title = "Ruinas del Olvido", seed = 63, hillAmp = 1.35f, edgeRise = 2.5f, vegetation = 0.45f, rockiness = 0.65f, ruins = 2,
                moss = new Color(0.50f, 0.54f, 0.40f), dirt = new Color(0.63f, 0.50f, 0.37f), clearing = new Color(0.58f, 0.58f, 0.45f), shade = new Color(0.40f, 0.42f, 0.34f), fog = new Color(0.62f, 0.60f, 0.55f),
                trees = Trees, plants = new[] { "Hongos", "Planta" }, rocks = new[] { "Roca3", "Rocas", "Rocas2" } },
            // "Nueva partida" (Test 1-3): the map 1 language, gentle and sparse, richer step by step.
            new Style { scene = "Assets/Course/Scenes/Test 1.unity", title = "Sendero inicial", seed = 11, hillAmp = 0.8f, edgeRise = 1.8f, vegetation = 0.40f, rockiness = 0.25f, ruins = 0,
                moss = new Color(0.49f, 0.58f, 0.42f), dirt = new Color(0.62f, 0.50f, 0.37f), clearing = new Color(0.57f, 0.62f, 0.46f), shade = new Color(0.38f, 0.48f, 0.36f), fog = new Color(0.58f, 0.65f, 0.55f),
                trees = Trees, plants = new[] { "Planta", "Hongos" }, rocks = new[] { "Rocas", "Roca3" } },
            new Style { scene = "Assets/Course/Scenes/Test 2.unity", title = "Sendero del bosque", seed = 22, hillAmp = 1.0f, edgeRise = 2.0f, vegetation = 0.50f, rockiness = 0.30f, ruins = 0,
                moss = new Color(0.47f, 0.57f, 0.41f), dirt = new Color(0.61f, 0.49f, 0.36f), clearing = new Color(0.56f, 0.61f, 0.45f), shade = new Color(0.37f, 0.47f, 0.35f), fog = new Color(0.56f, 0.64f, 0.53f),
                trees = Trees, plants = new[] { "Planta", "Hongos", "Tronco" }, rocks = new[] { "Rocas", "Roca3" } },
            new Style { scene = "Assets/Course/Scenes/Test 3.unity", title = "Puerta de la selva", seed = 33, hillAmp = 1.2f, edgeRise = 2.2f, vegetation = 0.60f, rockiness = 0.35f, ruins = 1,
                moss = new Color(0.45f, 0.55f, 0.39f), dirt = new Color(0.60f, 0.48f, 0.35f), clearing = new Color(0.55f, 0.60f, 0.44f), shade = new Color(0.35f, 0.45f, 0.34f), fog = new Color(0.54f, 0.62f, 0.51f),
                trees = Trees, plants = new[] { "Planta", "Hongos", "Tronco" }, rocks = new[] { "Rocas", "Rocas2", "Roca3" } },
        };

        public static readonly List<string> Report = new List<string>();
        private static string shotsDir, shotTag = "after";
        private static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, float> baseHeights = new Dictionary<string, float>();

        public static void BuildFromCommandLine()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            bool captureOnly = args.Contains("-gdaCaptureOnly");
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-gdaShots") shotsDir = args[i + 1];
                if (args[i] == "-gdaShotTag") shotTag = args[i + 1];
            }
            EditorApplication.Exit(Build(captureOnly) ? 0 : 1);
        }

        [MenuItem("Tools/GameDevAgent/Jungle terrain (phase 17)")]
        public static void BuildFromMenu() => Build(false);

        public static bool Build(bool captureOnly)
        {
            Report.Clear();
            try
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { LegendContentBuilder.Root + "/Props/Prefabs" }))
                {
                    var p = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    prefabs[p.name] = p;
                }
                foreach (Style style in Styles)
                {
                    if (!File.Exists(style.scene)) { Report.Add("missing " + style.scene); continue; }
                    Scene scene = EditorSceneManager.OpenScene(style.scene, OpenSceneMode.Single);
                    if (!captureOnly)
                    {
                        BuildScene(style);
                        EditorSceneManager.MarkSceneDirty(scene);
                        EditorSceneManager.SaveScene(scene);
                    }
                    if (!string.IsNullOrEmpty(shotsDir)) Capture(style);
                }
                AssetDatabase.SaveAssets();
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

        // ------------------------------------------------------------------ scene analysis

        private class MapInfo
        {
            public List<List<Vector3>> routes;
            public Vector3 castle;
            public bool hasCastle;
            public float groundY;
            public Rect ground;     // xz extent of the original ground
            public Rect extent;     // relief extent (visible area + margin)
            public List<Rect> keepFlat = new List<Rect>(); // existing props / scene objects (xz, expanded)
            public Camera cam;
            public int obstacleLayer;
            public float bandStart = 1f;   // viewport y where the no-build top band starts (TowerManager.topSafePercent)
            public List<Vector3> sightTargets; // road points and the Book, which the relief and the props must never hide
            public Vector3 sunDir = new Vector3(0.3f, -0.8f, 0.5f);

            /// <summary>Viewport position of a ground point for the game camera at a given aspect.</summary>
            public Vector3 Viewport(Vector3 p, float aspect)
            {
                if (cam == null) return new Vector3(0.5f, 0.5f, 1f);
                float old = cam.aspect;
                cam.aspect = aspect;
                Vector3 v = cam.WorldToViewportPoint(p);
                cam.aspect = old;
                return v;
            }

            /// <summary>Seen by the game camera on any screen from 16:9 to the widest phone (2.4:1).</summary>
            public bool InView(Vector3 p, float margin = 0.03f)
            {
                Vector3 v = Viewport(p, 2.4f);
                return v.z > 0f && v.x > -margin && v.x < 1f + margin && v.y > -margin && v.y < 1f + margin;
            }
        }

        private static bool IsGround(Renderer r) => r.sharedMaterials.Any(m => m != null && m.name.StartsWith("GDA_Suelo_"));

        private static MapInfo Analyze(Style style)
        {
            var info = new MapInfo { routes = MapRestyleBuilder.Routes(), cam = Camera.main };
            Castle castle = Object.FindAnyObjectByType<Castle>();
            info.hasCastle = castle != null;
            if (castle != null) info.castle = castle.transform.position;
            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            var ground = roots.SelectMany(g => g.GetComponentsInChildren<Renderer>(true)).Where(IsGround).ToList();
            var tops = ground.Select(r => r.bounds.max.y).OrderBy(y => y).ToList();
            info.groundY = tops.Count > 0 ? tops[tops.Count / 2] : 0f;
            if (ground.Count > 0)
            {
                Bounds b = ground[0].bounds;
                foreach (Renderer r in ground) b.Encapsulate(r.bounds);
                info.ground = Rect.MinMaxRect(b.min.x, b.min.z, b.max.x, b.max.z);
            }
            // Visible area of the game camera (widest phone aspect) on the ground plane.
            Rect view = info.ground;
            if (info.cam != null)
            {
                float old = info.cam.aspect;
                var pts = new List<Vector3>();
                foreach (float aspect in new[] { 16f / 9f, 2.4f })
                {
                    info.cam.aspect = aspect;
                    foreach (Vector2 v in new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 0) })
                    {
                        Ray ray = info.cam.ViewportPointToRay(new Vector3(v.x, v.y, 0f));
                        if (ray.direction.y >= -1e-3f) continue;
                        pts.Add(ray.origin + ray.direction * ((info.groundY - ray.origin.y) / ray.direction.y));
                    }
                }
                info.cam.aspect = old;
                if (pts.Count > 0)
                {
                    float x0 = Mathf.Min(view.xMin, pts.Min(p => p.x)), x1 = Mathf.Max(view.xMax, pts.Max(p => p.x));
                    float z0 = Mathf.Min(view.yMin, pts.Min(p => p.z)), z1 = Mathf.Max(view.yMax, pts.Max(p => p.z));
                    view = Rect.MinMaxRect(x0, z0, x1, z1);
                }
            }
            // Relief only where it can be seen: the visible area, but never far beyond the map (the top rays reach the horizon).
            float margin = 3f;
            info.extent = Rect.MinMaxRect(Mathf.Max(view.xMin, info.ground.xMin - 8f) - margin, Mathf.Max(view.yMin, info.ground.yMin - 8f) - margin,
                Mathf.Min(view.xMax, info.ground.xMax + 8f) + margin, Mathf.Min(view.yMax, info.ground.yMax + 8f) + margin);
            // Everything already standing in the scene keeps flat ground under it (course decoration and its colliders,
            // the phase 11/12 props, spawners, castle pieces, path markers...).
            foreach (GameObject go in roots)
            {
                if (go.name == RootName || go.name == MapRestyleBuilder.RibbonName) continue;
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (IsGround(r) || r is ParticleSystemRenderer || r.GetComponentInParent<Canvas>() != null) continue;
                    Bounds b = r.bounds;
                    if (b.size.x > 12f || b.size.z > 12f || b.max.y < info.groundY + 0.05f) continue;
                    info.keepFlat.Add(Rect.MinMaxRect(b.min.x - 0.5f, b.min.z - 0.5f, b.max.x + 0.5f, b.max.z + 0.5f));
                }
            }
            TowerManager tm = Object.FindAnyObjectByType<TowerManager>();
            int mask = tm != null ? tm.whatIsObstacle.value : 0;
            if (tm != null) info.bandStart = 1f - tm.topSafePercent / 100f;
            // The "Path" layer is the non-buildable ground layer TowerManager already checks (the road): same meaning here.
            int path = LayerMask.NameToLayer("Path");
            info.obstacleLayer = path >= 0 && ((1 << path) & mask) != 0 ? path : -1;
            if (info.obstacleLayer < 0) for (int l = 0; l < 32; l++) if (((1 << l) & mask) != 0) { info.obstacleLayer = l; break; }
            return info;
        }

        private static float Smooth(float a, float b, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, x));

        private static float DistToRect(Rect r, float x, float z)
        {
            float dx = Mathf.Max(r.xMin - x, 0f, x - r.xMax), dz = Mathf.Max(r.yMin - z, 0f, z - r.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>0 where the game is played or something stands, 1 where the relief is free to rise.</summary>
        private static float Freedom(MapInfo m, float x, float z, out float d)
        {
            var p = new Vector3(x, m.groundY, z);
            d = MapRestyleBuilder.DistanceToRoutes(p, m.routes);
            float f = Smooth(PlayRadius, PlayRadius + 1.4f, d);
            // The top screen band is never buildable (TowerManager): the relief may rise there too, clear of the road.
            float vy = m.Viewport(p, 16f / 9f).y;
            float band = Smooth(m.bandStart - 0.01f, m.bandStart + 0.05f, vy) * Smooth(1.1f, 2.1f, d);
            f = Mathf.Max(f, band);
            if (m.hasCastle) f *= Smooth(BookRadius, BookRadius + 1.6f, Vector2.Distance(new Vector2(x, z), new Vector2(m.castle.x, m.castle.z)));
            foreach (Rect r in m.keepFlat)
            {
                float dr = DistToRect(r, x, z);
                if (dr < 0.8f) f *= Smooth(0f, 0.8f, dr);
                if (f <= 0f) break;
            }
            return f;
        }

        private static float Fbm(float x, float z, int seed)
        {
            float s = seed * 17.13f;
            return 0.6f * Mathf.PerlinNoise(x * 0.085f + s, z * 0.085f + s * 0.7f)
                 + 0.3f * Mathf.PerlinNoise(x * 0.19f + s * 1.3f, z * 0.19f - s)
                 + 0.1f * Mathf.PerlinNoise(x * 0.45f - s, z * 0.45f + s * 0.4f);
        }

        private static float Height(Style st, MapInfo m, float x, float z, out float freedom, out float d)
        {
            freedom = Freedom(m, x, z, out d);
            if (freedom <= 0f) return m.groundY;
            float n = Fbm(x, z, st.seed);
            float hill = st.hillAmp * Mathf.Pow(Mathf.Clamp01((n - 0.30f) / 0.45f), 1.2f) * 2.2f;
            float hollow = -0.32f * Smooth(0.36f, 0.22f, n);
            float outside = DistToRect(m.ground, x, z);
            float edge = Mathf.Min(outside * 0.45f, st.edgeRise);
            // Towards the top of the screen the forest climbs: a ridge that gives the view a horizon.
            float vy = m.Viewport(new Vector3(x, m.groundY, z), 16f / 9f).y;
            float ridge = st.hillAmp * 1.3f * Smooth(m.bandStart, 1.08f, vy) * (0.75f + 0.5f * n);
            float h = freedom * (hill + hollow + edge + ridge);
            // Never hide a road point or the Book from the game camera.
            if (h > 0f) h = Mathf.Min(h, CameraCap(m, new Vector3(x, m.groundY, z), m.groundY));
            return m.groundY + h;
        }

        // ------------------------------------------------------------------ build

        private static void BuildScene(Style st)
        {
            foreach (GameObject go in SceneManager.GetActiveScene().GetRootGameObjects().Where(g => g.name == RootName).ToList()) Object.DestroyImmediate(go);
            MapInfo m = Analyze(st);
            var root = new GameObject(RootName);
            var rng = new System.Random(st.seed);

            // 0. Natural clearings in the play area (painted into the ground and ringed with pebbles later): good-looking
            //    spots to invoke a defender, never on the road and never raised.
            var clearingSpots = new List<Vector3>();
            var crng = new System.Random(st.seed * 7 + 3);
            for (int tries = 0; tries < 2000 && clearingSpots.Count < 7; tries++)
            {
                var p = new Vector3(Mathf.Lerp(m.extent.xMin, m.extent.xMax, (float)crng.NextDouble()), m.groundY, Mathf.Lerp(m.extent.yMin, m.extent.yMax, (float)crng.NextDouble()));
                float d = MapRestyleBuilder.DistanceToRoutes(p, m.routes);
                if (d < 1.75f || d > 3.0f || !m.InView(p, -0.03f) || m.Viewport(p, 16f / 9f).y > m.bandStart - 0.03f) continue;
                if (m.keepFlat.Any(r => DistToRect(r, p.x, p.z) < 0.5f)) continue;
                if (m.hasCastle && Vector2.Distance(new Vector2(p.x, p.z), new Vector2(m.castle.x, m.castle.z)) < BookRadius + 0.8f) continue;
                if (clearingSpots.Any(s => Vector2.Distance(new Vector2(s.x, s.z), new Vector2(p.x, p.z)) < 4f)) continue;
                clearingSpots.Add(p);
            }

            // 1. Relief mesh.
            float step = 0.5f;
            int nx = Mathf.CeilToInt(m.extent.width / step) + 1, nz = Mathf.CeilToInt(m.extent.height / step) + 1;
            while (nx * nz > 60000) { step += 0.1f; nx = Mathf.CeilToInt(m.extent.width / step) + 1; nz = Mathf.CeilToInt(m.extent.height / step) + 1; }
            var verts = new Vector3[nx * nz];
            var free = new float[nx * nz];
            var dist = new float[nx * nz];
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    float x = m.extent.xMin + i * step, z = m.extent.yMin + j * step;
                    float h = Height(st, m, x, z, out float f, out float d);
                    verts[j * nx + i] = new Vector3(x, h, z);
                    free[j * nx + i] = f;
                    dist[j * nx + i] = d;
                }
            var colors = new Color[verts.Length];
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i;
                    Vector3 v = verts[k];
                    minH = Mathf.Min(minH, v.y); maxH = Mathf.Max(maxH, v.y);
                    float hx = verts[j * nx + Mathf.Min(i + 1, nx - 1)].y - verts[j * nx + Mathf.Max(i - 1, 0)].y;
                    float hz = verts[Mathf.Min(j + 1, nz - 1) * nx + i].y - verts[Mathf.Max(j - 1, 0) * nx + i].y;
                    float slope = new Vector2(hx, hz).magnitude / (2f * step);
                    float d = dist[k];
                    float rel = v.y - m.groundY;
                    float variation = Mathf.PerlinNoise(v.x * 0.35f + st.seed, v.z * 0.35f) - 0.5f;
                    float patch = Mathf.PerlinNoise(v.x * 0.07f - st.seed, v.z * 0.07f + st.seed * 0.5f);
                    float litter = Mathf.PerlinNoise(v.x * 0.16f + st.seed * 2f, v.z * 0.16f - st.seed);
                    Color c = st.moss * (1f + variation * 0.22f);
                    c = Color.Lerp(c, st.clearing, Smooth(PlayRadius + 0.8f, 1.5f, d) * 0.3f);                 // defenders' clearing
                    c = Color.Lerp(c, st.shade, Smooth(0.4f, 0.72f, patch) * 0.75f);                          // large mossy shade patches
                    c = Color.Lerp(c, st.dirt * 0.85f, Smooth(0.58f, 0.8f, litter) * 0.5f);                    // leaf litter / bare earth
                    c = Color.Lerp(c, st.dirt, Smooth(1.3f, 0.6f, d) * (0.55f + 0.45f * litter));              // earthy road edges
                    foreach (Vector3 s in clearingSpots)                                                         // natural root clearings
                    {
                        float ds = Vector2.Distance(new Vector2(v.x, v.z), new Vector2(s.x, s.z)) + variation * 0.5f;
                        c = Color.Lerp(c, Color.Lerp(st.dirt, st.shade, 0.35f + litter * 0.3f), Smooth(1.15f, 0.55f, ds) * 0.8f);
                    }
                    c = Color.Lerp(c, st.shade, Smooth(0f, -0.25f, rel));                                        // hollows
                    c = Color.Lerp(c, st.shade, Smooth(0.6f, 2.2f, rel) * 0.35f);                                 // high dense forest
                    if (m.hasCastle)
                    {
                        float dc = Vector2.Distance(new Vector2(v.x, v.z), new Vector2(m.castle.x, m.castle.z));
                        c = Color.Lerp(c, new Color(0.66f, 0.60f, 0.45f), Smooth(5.5f, 2.0f, dc) * 0.55f);     // warm glow of the Book
                    }
                    float rock = Mathf.Clamp01(Smooth(0.45f, 1.1f, slope) * 0.85f + Smooth(st.hillAmp * 0.55f, st.hillAmp * 1.2f, rel) * st.rockiness);
                    // Stylised, not neon: a little desaturated and darker than the raw tints (0.5 = texture as is).
                    float grey = (c.r + c.g + c.b) / 3f;
                    c = Color.Lerp(new Color(grey, grey, grey), c, 0.75f) * 0.8f; // the forest floor texture carries the hue
                    c.a = rock * free[k];
                    colors[k] = c;
                }
            var tris = new List<int>((nx - 1) * (nz - 1) * 6);
            for (int j = 0; j < nz - 1; j++)
                for (int i = 0; i < nx - 1; i++)
                {
                    int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                    tris.AddRange(new[] { a, c, b, b, c, d });
                }
            var mesh = new Mesh { name = "GDA_Relieve" };
            mesh.indexFormat = verts.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            string sceneName = System.IO.Path.GetFileNameWithoutExtension(st.scene).Replace(" ", "");
            string meshPath = MapRestyleBuilder.Root + "/Generated/" + sceneName + "_Relieve.asset";
            if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null) AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);
            var relief = new GameObject(ReliefName, typeof(MeshFilter), typeof(MeshRenderer));
            relief.transform.SetParent(root.transform, false);
            relief.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mr = relief.GetComponent<MeshRenderer>();
            mr.sharedMaterial = GroundMaterial(sceneName);
            mr.receiveShadows = true;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

            // 2. Hide the flat ground (renderers only: colliders and layers stay).
            int hidden = 0;
            foreach (Renderer r in SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Renderer>(true)))
                if (IsGround(r) && r.enabled) { r.enabled = false; hidden++; }

            // 3. Obstacle boxes under the hills (1 m cells merged in runs along x).
            var block = new GameObject(BlockName);
            block.transform.SetParent(root.transform, false);
            block.layer = m.obstacleLayer;
            int boxes = 0;
            for (float z = m.ground.yMin + 0.5f; z < m.ground.yMax; z += 1f)
            {
                float runStart = float.NaN;
                for (float x = m.ground.xMin + 0.5f; x <= m.ground.xMax + 0.01f; x += 1f)
                {
                    bool high = x < m.ground.xMax && Height(st, m, x, z, out float f, out float d) > m.groundY + 0.12f && d > PlayRadius + 0.2f;
                    if (high && float.IsNaN(runStart)) runStart = x;
                    if (!high && !float.IsNaN(runStart))
                    {
                        var box = block.AddComponent<BoxCollider>();
                        float x0 = runStart - 0.5f, x1 = x - 0.5f;
                        box.center = new Vector3((x0 + x1) * 0.5f, m.groundY - 0.15f, z);
                        box.size = new Vector3(x1 - x0, 0.7f, 1f);
                        boxes++;
                        runStart = float.NaN;
                    }
                }
            }

            // 4. Props.
            var veg = Child(root, "Vegetacion");
            var rocks = Child(root, "Rocas");
            var edges = Child(root, "Bordes del camino");
            var ruins = Child(root, "Ruinas");
            var shrine = Child(root, "Santuario");
            int nVeg = 0, nRock = 0, nEdge = 0, nRuin = 0, nShrine = 0;
            var taken = new List<Vector3>();
            bool FarFromTaken(Vector3 p, float r) => taken.All(t => (new Vector2(t.x - p.x, t.z - p.z)).sqrMagnitude > r * r);

            // 4a. Road edges: pebbles and low plants, outside the stone ribbon.
            foreach (List<Vector3> route in m.routes)
            {
                float carry = 0f;
                int sideSign = 1;
                for (int s = 0; s < route.Count - 1; s++)
                {
                    Vector3 a = route[s], b = route[s + 1];
                    Vector3 dir = b - a; dir.y = 0f;
                    float len = dir.magnitude;
                    if (len < 0.01f) continue;
                    dir /= len;
                    Vector3 side = Vector3.Cross(Vector3.up, dir);
                    for (float t = carry; t < len; t += 0.9f + (float)rng.NextDouble() * 0.7f)
                    {
                        sideSign = (float)rng.NextDouble() < 0.7 ? -sideSign : sideSign;
                        Vector3 p = a + dir * t + side * sideSign * (0.70f + (float)rng.NextDouble() * 0.3f);
                        p.y = m.groundY;
                        if (MapRestyleBuilder.DistanceToRoutes(p, m.routes) < 0.66f || !m.InView(p)) continue;
                        if (m.hasCastle && Vector2.Distance(new Vector2(p.x, p.z), new Vector2(m.castle.x, m.castle.z)) < BookRadius) continue;
                        if (m.keepFlat.Any(r => DistToRect(r, p.x, p.z) < 0.2f)) continue;
                        double roll = rng.NextDouble();
                        // Pebbles, low plants and roots (thin trunks half buried) along the stone road.
                        string name = roll < 0.5 ? Pick(st.rocks, rng) : roll < 0.75 ? "Planta" : roll < 0.88 ? "Tronco" : "Hongos";
                        float h = roll < 0.5 ? 0.10f + (float)rng.NextDouble() * 0.12f : roll < 0.75 ? 0.22f + (float)rng.NextDouble() * 0.12f : roll < 0.88 ? 0.16f : 0.16f;
                        GameObject g = Place(name, p, h, rng, m.groundY, roll < 0.5 ? 0.3f : roll < 0.75 ? 0f : 0.45f, edges.transform, false);
                        if (g != null)
                        {
                            if (name == "Tronco") g.transform.rotation = Quaternion.LookRotation(dir) * Quaternion.Euler(0f, 0f, 90f); // lying along the road
                            nEdge++;
                        }
                        carry = 0f;
                    }
                }
            }

            // 4b. Hills: dense vegetation, rocky zones and clearings (noise). Only what the game camera can see.
            for (float z = m.extent.yMin + 0.5f; z < m.extent.yMax; z += 1.1f)
                for (float x = m.extent.xMin + 0.5f; x < m.extent.xMax; x += 1.1f)
                {
                    var p = new Vector3(x + ((float)rng.NextDouble() - 0.5f) * 0.9f, 0f, z + ((float)rng.NextDouble() - 0.5f) * 0.9f);
                    float y = Height(st, m, p.x, p.z, out float f, out float d);
                    if (f < 0.45f || d < 1.6f || !FarFromTaken(p, 0.95f) || !m.InView(new Vector3(p.x, y, p.z), 0.06f)) continue;
                    bool outside = DistToRect(m.ground, p.x, p.z) > 0.5f;
                    float nv = Mathf.PerlinNoise(p.x * 0.13f + st.seed * 3.1f, p.z * 0.13f);
                    float nr = Mathf.PerlinNoise(p.x * 0.11f - st.seed, p.z * 0.11f + st.seed * 2.3f);
                    double roll = rng.NextDouble();
                    float cap = CameraCap(m, p, y);
                    if (nr > 0.66f - st.rockiness * 0.12f && !outside)
                    {
                        float h = (0.55f + (float)rng.NextDouble() * 0.9f) * (1f + st.rockiness * 0.35f);
                        if (Place(Pick(st.rocks, rng), new Vector3(p.x, y, p.z), Mathf.Min(h, cap), rng, y, 0.22f, rocks.transform, true) != null) { nRock++; taken.Add(p); }
                    }
                    else if (outside ? roll < 0.85 : nv > 0.5f - st.vegetation * 0.3f || roll < 0.3)
                    {
                        bool tree = outside || roll < 0.5 + st.vegetation * 0.25;
                        string name = tree ? Pick(st.trees, rng) : Pick(st.plants, rng);
                        float h = tree ? 1.6f + (float)rng.NextDouble() * (outside ? 1.6f : 1.1f) : name == "Tronco" ? 0.45f : name == "Hongos" ? 0.45f + (float)rng.NextDouble() * 0.3f : 0.7f + (float)rng.NextDouble() * 0.5f;
                        h = Mathf.Min(h, cap);
                        if (h < 0.35f) continue;
                        if (Place(name, new Vector3(p.x, y, p.z), h, rng, y, 0.04f, veg.transform, tree) != null) { nVeg++; taken.Add(p); }
                    }
                    else if (roll < 0.5)
                    {
                        if (Place("Hongos", new Vector3(p.x, y, p.z), Mathf.Min(0.4f, cap), rng, y, 0.03f, veg.transform, false) != null) { nVeg++; taken.Add(p); }
                    }
                }

            // 4f. Ground cover in the play clearing: sparse, very low (<= 28 cm, no collider) mushrooms, small plants and
            //     pebbles in noise patches, so the buildable ground is not a flat green sheet. Never near the road.
            var cover = Child(root, "Cobertura");
            int nCover = 0;
            for (float z = m.extent.yMin + 0.4f; z < m.extent.yMax; z += 1.3f)
                for (float x = m.extent.xMin + 0.4f; x < m.extent.xMax; x += 1.3f)
                {
                    var p = new Vector3(x + ((float)rng.NextDouble() - 0.5f) * 1.1f, m.groundY, z + ((float)rng.NextDouble() - 0.5f) * 1.1f);
                    float d = MapRestyleBuilder.DistanceToRoutes(p, m.routes);
                    if (d < 1.25f || d > PlayRadius || !m.InView(p, -0.02f) || !FarFromTaken(p, 0.8f)) continue;
                    if (clearingSpots.Any(s => Vector2.Distance(new Vector2(s.x, s.z), new Vector2(p.x, p.z)) < 1.4f)) continue;
                    if (m.keepFlat.Any(r => DistToRect(r, p.x, p.z) < 0.3f)) continue;
                    if (m.hasCastle && Vector2.Distance(new Vector2(p.x, p.z), new Vector2(m.castle.x, m.castle.z)) < BookRadius) continue;
                    float patch = Mathf.PerlinNoise(p.x * 0.21f + st.seed * 1.7f, p.z * 0.21f - st.seed);
                    if (patch < 0.52f || rng.NextDouble() > 0.55 + st.vegetation * 0.3) continue;
                    double roll = rng.NextDouble();
                    string name = roll < 0.4 ? "Hongos" : roll < 0.75 ? "Planta" : Pick(st.rocks, rng);
                    float h = roll < 0.4 ? 0.18f + (float)rng.NextDouble() * 0.1f : roll < 0.75 ? 0.2f + (float)rng.NextDouble() * 0.08f : 0.1f + (float)rng.NextDouble() * 0.1f;
                    if (Place(name, p, h, rng, m.groundY, roll < 0.75 ? 0.02f : 0.3f, cover.transform, false) != null) { nCover++; taken.Add(p); }
                }

            // 4e. Natural clearings (painted in step 1): a broken ring of pebbles, a root and a mushroom around each one.
            var platforms = Child(root, "Claros");
            int nPlat = 0;
            foreach (Vector3 s in clearingSpots)
            {
                int pieces = 4 + rng.Next(3);
                float a0 = (float)rng.NextDouble() * 360f;
                for (int k = 0; k < pieces; k++)
                {
                    float ang = a0 + k * (300f / pieces) + (float)rng.NextDouble() * 20f;
                    Vector3 p = s + Quaternion.Euler(0f, ang, 0f) * Vector3.forward * (0.95f + (float)rng.NextDouble() * 0.25f);
                    p.y = m.groundY;
                    if (MapRestyleBuilder.DistanceToRoutes(p, m.routes) < 1.25f) continue;
                    double roll = rng.NextDouble();
                    string name = k == 0 ? "Tronco" : roll < 0.65 ? Pick(st.rocks, rng) : roll < 0.85 ? "Hongos" : "Planta";
                    float h = name == "Tronco" ? 0.14f : name == "Hongos" ? 0.2f : name == "Planta" ? 0.22f : 0.1f + (float)rng.NextDouble() * 0.1f;
                    GameObject g = Place(name, p, h, rng, m.groundY, name == "Tronco" ? 0.45f : name == "Hongos" || name == "Planta" ? 0.02f : 0.3f, platforms.transform, false);
                    if (g == null) continue;
                    if (name == "Tronco") g.transform.rotation = Quaternion.Euler(0f, ang + 90f, 90f);
                    taken.Add(p);
                }
                nPlat++;
            }

            // 4c. Ruins: the visible hill spots farthest from the roads.
            var sites = new List<Vector3>();
            for (int r = 0; r < st.ruins; r++)
            {
                Vector3 best = Vector3.zero; float bestScore = -1f;
                for (float z = m.ground.yMin + 2f; z < m.ground.yMax - 2f; z += 0.8f)
                    for (float x = m.ground.xMin + 2f; x < m.ground.xMax - 2f; x += 0.8f)
                    {
                        float y = Height(st, m, x, z, out float f, out float d);
                        if (f < 0.8f || d < 2.4f || !m.InView(new Vector3(x, y, z), -0.02f)) continue;
                        if (sites.Any(s => Vector2.Distance(new Vector2(s.x, s.z), new Vector2(x, z)) < 7f)) continue;
                        if (m.hasCastle && Vector2.Distance(new Vector2(x, z), new Vector2(m.castle.x, m.castle.z)) < 6f) continue;
                        float score = d - Mathf.Abs(y - m.groundY - st.hillAmp * 0.4f) * 2f;
                        if (score > bestScore) { bestScore = score; best = new Vector3(x, y, z); }
                    }
                if (bestScore < 0f) break;
                sites.Add(best);
                float yaw = (float)rng.NextDouble() * 360f;
                Quaternion q = Quaternion.Euler(0f, yaw, 0f);
                var site = Child(ruins, "Ruina " + (r + 1));
                void Ruin(string name, Vector3 local, float h, float sink, float extraYaw)
                {
                    Vector3 w = best + q * local;
                    float yy = Height(st, m, w.x, w.z, out _, out _);
                    GameObject g = Place(name, new Vector3(w.x, yy, w.z), Mathf.Min(h, CameraCap(m, w, yy)), rng, yy, sink, site.transform, true);
                    if (g != null) { g.transform.rotation = Quaternion.Euler(0f, yaw + extraYaw, 0f); nRuin++; taken.Add(w); }
                }
                Ruin("Losa", Vector3.zero, 0.3f, 0.12f, 0f);
                Ruin("Arco", new Vector3(0f, 0f, 0.4f), 1.9f, 0.05f, 0f);
                Ruin("Muro", new Vector3(-1.5f, 0f, 0.2f), 1.0f, 0.15f, 12f);
                Ruin("Muro_2", new Vector3(1.6f, 0f, -0.2f), 0.9f, 0.2f, -18f);
                Ruin("Pilar", new Vector3(-1.2f, 0f, -1.3f), 1.3f, 0.1f, 0f);
                Ruin("Poste", new Vector3(1.3f, 0f, 1.4f), 1.0f, 0.1f, 0f);
                Ruin("Hongos", new Vector3(0.6f, 0f, -1.1f), 0.45f, 0.02f, 0f);
                Ruin("Planta", new Vector3(-0.4f, 0f, 1.6f), 0.8f, 0.02f, 0f);
            }

            // 4d. Sacred Book: the "Terreno" root clearing under the plaza and stone guardians away from the roads.
            if (m.hasCastle)
            {
                GameObject island = Place("TerrenoIsla", new Vector3(m.castle.x, m.groundY, m.castle.z), 0f, rng, m.groundY, 0f, shrine.transform, false, 7.5f);
                if (island != null) nShrine++;
                var entries = m.routes.Select(r => { Vector3 e = r[r.Count - 2] - r[r.Count - 1]; e.y = 0f; return e.normalized; }).ToList();
                int placedGuardians = 0;
                for (int a = 0; a < 16 && placedGuardians < 4; a++)
                {
                    float ang = a * 22.5f + st.seed;
                    Vector3 dir = Quaternion.Euler(0f, ang, 0f) * Vector3.forward;
                    if (entries.Any(e => Vector3.Angle(e, dir) < 55f)) continue;
                    Vector3 p = m.castle + dir * 3.4f;
                    if (MapRestyleBuilder.DistanceToRoutes(p, m.routes) < 1.4f || m.keepFlat.Any(r => DistToRect(r, p.x, p.z) < 0.1f && !r.Contains(new Vector2(m.castle.x, m.castle.z)))) continue;
                    string name = placedGuardians % 2 == 0 ? "Pilar" : "Poste";
                    GameObject g = Place(name, new Vector3(p.x, m.groundY, p.z), placedGuardians % 2 == 0 ? 1.2f : 0.9f, rng, m.groundY, 0.05f, shrine.transform, true);
                    if (g != null) { nShrine++; placedGuardians++; a += 2; Place("Planta", p + dir * 0.6f, 0.5f, rng, m.groundY, 0.02f, shrine.transform, false); nShrine++; }
                }
            }

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.GetComponent<Collider>() == null && t.name != ReliefName) GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);

            // 5. Light: soft, never black shadows, and a light fog for depth.
            Light sun = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(l => l.type == LightType.Directional);
            if (sun != null)
            {
                sun.shadows = LightShadows.Soft;
                sun.shadowStrength = 0.6f;
            }
            float camDist = m.cam != null ? Vector3.Distance(m.cam.transform.position, new Vector3(m.ground.center.x, m.groundY, m.ground.center.y)) : 20f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = st.fog;
            RenderSettings.fogStartDistance = camDist * 1.25f;
            RenderSettings.fogEndDistance = camDist * 3.2f;

            int reliefTris = mesh.triangles.Length / 3;
            int propTris = root.GetComponentsInChildren<MeshFilter>().Where(f => f.gameObject != relief && f.sharedMesh != null).Sum(f => f.sharedMesh.triangles.Length / 3);
            Report.Add(System.IO.Path.GetFileName(st.scene) + " \"" + st.title + "\": relief " + nx + "x" + nz + " (" + step.ToString("0.0") + " m, " + reliefTris + " tris) height "
                + (minH - m.groundY).ToString("0.00") + ".." + (maxH - m.groundY).ToString("0.00") + " m; ground y " + m.groundY.ToString("0.00") + ", " + hidden + " flat ground renderers hidden; "
                + boxes + " hill obstacle boxes (layer " + LayerMask.LayerToName(m.obstacleLayer) + "); props: vegetation " + nVeg + ", rocks " + nRock + ", road edges " + nEdge
                + ", ruins " + nRuin + ", Book " + nShrine + ", platforms " + nPlat + ", ground cover " + nCover + " (" + propTris + " tris); kept flat under " + m.keepFlat.Count + " scene objects; fog " + RenderSettings.fogStartDistance.ToString("0") + "-" + RenderSettings.fogEndDistance.ToString("0") + " m");
        }

        private static GameObject Child(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static string Pick(string[] list, System.Random rng) => list[rng.Next(list.Length)];

        /// <summary>
        /// Highest a prop at p may be without hiding a road point or the Book from the game camera: the camera ray to any
        /// target within 9 m that passes near p sets the cap (80 % of the ray height there).
        /// </summary>
        private static float CameraCap(MapInfo m, Vector3 p, float groundAtP)
        {
            if (m.cam == null) return 10f;
            Vector3 eye = m.cam.transform.position;
            if (m.sightTargets == null)
            {
                m.sightTargets = new List<Vector3>();
                foreach (List<Vector3> r in m.routes)
                    for (int i = 0; i < r.Count - 1; i++)
                    {
                        float len = Vector3.Distance(r[i], r[i + 1]);
                        for (float t = 0f; t < len; t += 0.8f) m.sightTargets.Add(Vector3.Lerp(r[i], r[i + 1], t / len) + Vector3.up * 0.6f);
                    }
                if (m.hasCastle) m.sightTargets.Add(m.castle + Vector3.up * 1.2f);
            }
            float cap = 10f;
            foreach (Vector3 q in m.sightTargets)
            {
                if ((new Vector2(q.x - p.x, q.z - p.z)).sqrMagnitude > 81f) continue;
                Vector2 a = new Vector2(eye.x, eye.z), b = new Vector2(q.x, q.z), c = new Vector2(p.x, p.z);
                Vector2 ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(c - a, ab) / ab.sqrMagnitude);
                if (t >= 0.999f || Vector2.Distance(a + ab * t, c) > 0.9f) continue;
                float rayY = Mathf.Lerp(eye.y, q.y, t);
                cap = Mathf.Min(cap, (rayY - groundAtP) * 0.8f);
            }
            return Mathf.Max(0f, cap);
        }

        private static float BaseHeight(string name)
        {
            if (baseHeights.TryGetValue(name, out float h)) return h;
            var temp = (GameObject)PrefabUtility.InstantiatePrefab(prefabs[name]);
            Bounds b = LegendContentBuilder.MeasureBounds(temp);
            Object.DestroyImmediate(temp);
            baseHeights[name] = Mathf.Max(0.001f, b.size.y);
            return baseHeights[name];
        }

        /// <summary>Places a prop: height h (or footprint width when width &gt; 0), sunk by sink×h, random yaw, no collider.</summary>
        private static GameObject Place(string name, Vector3 p, float h, System.Random rng, float groundY, float sink, Transform parent, bool shadows, float width = 0f, float flatHeight = 0f)
        {
            if (!prefabs.ContainsKey(name)) return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefabs[name], parent);
            go.name = name;
            float s;
            if (width > 0f)
            {
                // Flattened clearing: footprint "width"; height flatHeight (or 35 % of the uniform scale).
                Bounds b0 = LegendContentBuilder.MeasureBounds(go);
                s = width / Mathf.Max(0.001f, Mathf.Max(b0.size.x, b0.size.z));
                float sy = flatHeight > 0f ? flatHeight / Mathf.Max(0.001f, b0.size.y) : s * 0.35f;
                go.transform.localScale = new Vector3(s, sy, s);
                go.transform.position = new Vector3(p.x, 0f, p.z);
                Bounds b1 = LegendContentBuilder.MeasureBounds(go);
                go.transform.position += Vector3.up * (groundY + 0.015f - b1.max.y + b1.size.y * 0.25f);
            }
            else
            {
                s = h / BaseHeight(name);
                go.transform.localScale = Vector3.one * s;
                go.transform.position = new Vector3(p.x, groundY - sink * h, p.z);
                go.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
            }
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = true;
            }
            return go;
        }

        private static Material GroundMaterial(string sceneName)
        {
            string dir = MapRestyleBuilder.Root + "/Materiales";
            string path = dir + "/GDA_Selva_" + sceneName + ".mat";
            Shader shader = Shader.Find("GDA/JungleGround");
            Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
            m.shader = shader;
            m.SetTexture("_MainTex", ForestFloorTexture());
            m.SetTexture("_RockTex", AssetDatabase.LoadAssetAtPath<Texture2D>(LegendContentBuilder.Root + "/Props/Textures/Rocas_Baked_albedo.png"));
            m.SetFloat("_Tiling", 3.5f);
            m.SetFloat("_RockTiling", 2.5f);
            m.SetFloat("_Glossiness", 0.1f);
            m.enableInstancing = false;
            EditorUtility.SetDirty(m);
            return m;
        }

        // Original, procedurally generated tileable forest floor (moss, bare earth, fallen leaves and grass blades),
        // 512 px RGB + macro variation in alpha. The course ground texture is a plain green, so it is not used here.
        private const string FloorTexPath = MapRestyleBuilder.Root + "/Generated/GDA_SueloSelva.png";
        private static Texture2D floorTex;

        private static Texture2D ForestFloorTexture()
        {
            if (floorTex != null) return floorTex;
            const int N = 512;
            var px = new Color[N * N];
            float Wrap(float a) => (a % N + N) % N;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = x / (float)N, v = y / (float)N;
                    float moss = PeriodicFbm(u, v, 6, 4, 101);
                    float earth = PeriodicFbm(u, v, 4, 3, 202);
                    float fine = PeriodicFbm(u, v, 32, 2, 303);
                    Color c = Color.Lerp(new Color(0.22f, 0.30f, 0.16f), new Color(0.38f, 0.45f, 0.25f), Smooth(0.3f, 0.75f, moss));
                    c = Color.Lerp(c, new Color(0.36f, 0.29f, 0.19f), Smooth(0.6f, 0.78f, earth) * 0.45f);
                    c *= 0.85f + fine * 0.3f;
                    c.a = PeriodicFbm(u, v, 2, 2, 404);
                    px[y * N + x] = c;
                }
            var rnd = new System.Random(17);
            Color[] leaf = { new Color(0.30f, 0.36f, 0.14f), new Color(0.46f, 0.38f, 0.17f), new Color(0.52f, 0.46f, 0.20f), new Color(0.27f, 0.20f, 0.12f), new Color(0.18f, 0.26f, 0.12f) };
            void Blot(float cx, float cy, float rx, float ry, float ang, Color col, float strength)
            {
                float ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                int r = Mathf.CeilToInt(Mathf.Max(rx, ry)) + 1;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        float lx = (dx * ca + dy * sa) / rx, ly = (-dx * sa + dy * ca) / ry;
                        float q = lx * lx + ly * ly;
                        if (q > 1f) continue;
                        int ix = (int)Wrap(cx + dx), iy = (int)Wrap(cy + dy);
                        Color o = px[iy * N + ix];
                        float a = o.a;
                        o = Color.Lerp(o, col * (0.9f + 0.2f * (1f - q)), strength * Smooth(1f, 0.6f, q));
                        o.a = a;
                        px[iy * N + ix] = o;
                    }
            }
            for (int i = 0; i < 1600; i++)   // fallen leaves
            {
                float s = 2f + (float)rnd.NextDouble() * 4f;
                Blot((float)rnd.NextDouble() * N, (float)rnd.NextDouble() * N, s, s * 0.45f, (float)rnd.NextDouble() * 6.28f, leaf[rnd.Next(leaf.Length)], 0.8f);
            }
            for (int i = 0; i < 3200; i++)   // grass blades
            {
                float len = 2.5f + (float)rnd.NextDouble() * 3.5f;
                Color g = Color.Lerp(new Color(0.34f, 0.48f, 0.18f), new Color(0.50f, 0.60f, 0.26f), (float)rnd.NextDouble());
                Blot((float)rnd.NextDouble() * N, (float)rnd.NextDouble() * N, len, 0.7f, (float)rnd.NextDouble() * 6.28f, g, 0.7f);
            }
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            tex.SetPixels(px);
            tex.Apply();
            System.IO.File.WriteAllBytes(FloorTexPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(FloorTexPath, ImportAssetOptions.ForceUpdate);
            var imp = (TextureImporter)AssetImporter.GetAtPath(FloorTexPath);
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.mipmapEnabled = true;
            imp.maxTextureSize = 512;
            imp.alphaIsTransparency = false;
            imp.anisoLevel = 2;
            imp.SaveAndReimport();
            floorTex = AssetDatabase.LoadAssetAtPath<Texture2D>(FloorTexPath);
            return floorTex;
        }

        // Tileable fbm in [0,1]: value noise on a lattice that wraps every 'period' cells.
        private static float PeriodicFbm(float u, float v, int period, int octaves, int seed)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                int p = period << o;
                sum += amp * PeriodicValue(u * p, v * p, p, seed + o * 31);
                norm += amp;
                amp *= 0.5f;
            }
            return sum / norm;
        }

        private static float PeriodicValue(float x, float y, int period, int seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            float H(int i, int j)
            {
                i = ((i % period) + period) % period; j = ((j % period) + period) % period;
                unchecked
                {
                    int h = i * 374761393 + j * 668265263 + seed * 1442695041;
                    h = (h ^ (h >> 13)) * 1274126177;
                    return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
                }
            }
            return Mathf.Lerp(Mathf.Lerp(H(x0, y0), H(x0 + 1, y0), fx), Mathf.Lerp(H(x0, y0 + 1), H(x0 + 1, y0 + 1), fx), fy);
        }

        // ------------------------------------------------------------------ captures

        private static void Capture(Style st)
        {
            Directory.CreateDirectory(shotsDir);
            string sceneName = System.IO.Path.GetFileNameWithoutExtension(st.scene).Replace(" ", "");
            Camera cam = Camera.main;
            if (cam == null) return;
            bool asyncShaders = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            try
            {
                Render(cam, sceneName + "_" + shotTag + "_overview", 1600, 900);
                Render(cam, sceneName + "_" + shotTag + "_mobile", 1688, 780);
                // Low angled view across the map towards the Book: shows the relief.
                MapInfo m = Analyze(st);
                var go = new GameObject("__jungle_cam");
                Camera c = go.AddComponent<Camera>();
                c.CopyFrom(cam);
                Vector3 target = m.hasCastle ? m.castle : new Vector3(m.ground.center.x, m.groundY, m.ground.center.y);
                Vector3 center = new Vector3(m.ground.center.x, m.groundY, m.ground.center.y);
                Vector3 away = (center - target); away.y = 0f;
                if (away.sqrMagnitude < 1f) away = new Vector3(0f, 0f, -1f);
                go.transform.position = target + away.normalized * 13f + Vector3.up * 5.5f;
                go.transform.LookAt(target + Vector3.up * 0.5f);
                c.fieldOfView = 50f;
                Render(c, sceneName + "_" + shotTag + "_angle", 1600, 900);
                Object.DestroyImmediate(go);
            }
            finally { ShaderUtil.allowAsyncCompilation = asyncShaders; }
        }

        private static void Render(Camera cam, string file, int w, int h)
        {
            var rt = new RenderTexture(w, h, 24) { antiAliasing = 4 };
            RenderTexture prev = cam.targetTexture;
            float aspect = cam.aspect;
            cam.targetTexture = rt;
            cam.aspect = (float)w / h;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = prev;
            cam.aspect = aspect;
            File.WriteAllBytes(System.IO.Path.Combine(shotsDir, file + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(rt);
        }
    }
}

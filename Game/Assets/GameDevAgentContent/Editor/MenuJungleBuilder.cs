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
    /// Phase 12.4: Colombian jungle backdrops for the Main Menu and Level Select, and the Sacred Book emblem.
    /// Presentation only (menu scenes and a new ornament): no gameplay scene, level data or navigation is touched.
    ///  - Emblem: Assets/UI/Ornaments/emblem_libro.png, drawn in the flat Figma icon language over the medallion/leaves of
    ///    the original emblem (which is kept untouched); HudSkinBuilder uses it for the brand block.
    ///  - Backdrops: the course 3D content of the menu scenes is hidden (renderers only) and a jungle diorama is composed
    ///    in each camera's real framing with the Tower Defense props (trees, mossy rocks, plants, mushrooms, logs, ruins),
    ///    the package ground/slab materials, fog for depth and soft green lighting. Main Menu: a clearing with the Sacred
    ///    Book plaza lit by a warm focal light. Level Select: a long winding trail with ruins ("territories").
    /// Idempotent. Run: Unity -batchmode -projectPath Game -executeMethod
    ///   GameDevAgent.ContentTools.MenuJungleBuilder.BuildFromCommandLine   (no -quit)
    /// </summary>
    public static class MenuJungleBuilder
    {
        private const string Tag = "[Jungle] ";
        public const string RootName = "GDA Selva";
        public const string EmblemPath = "Assets/UI/Ornaments/emblem_libro.png";
        private const string SourceEmblem = "Assets/UI/Ornaments/emblem.png";
        private const string MainMenuScene = "Assets/Course/Scenes/Main Menu.unity";
        private const string LevelSelectScene = "Assets/Course/Scenes/Level Select.unity";
        private static readonly List<string> Report = new List<string>();

        public static void BuildFromCommandLine() => EditorApplication.Exit(Build() ? 0 : 1);

        [MenuItem("Tools/GameDevAgent/Jungle menus (phase 12.4)")]
        public static void BuildFromMenu() => Build();

        public static bool Build()
        {
            Report.Clear();
            try
            {
                MakeEmblem();
                // HUD/menu skin (brand block now uses emblem_libro); it saves Main Menu and Level Select.
                bool hud = GameDevAgent.UiTools.HudSkinBuilder.Apply();
                Report.Add("HUD/menu skin re-applied: " + hud);
                var props = new Dictionary<string, GameObject>();
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { LegendContentBuilder.Root + "/Props/Prefabs" }))
                {
                    var p = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    props[p.name] = p;
                }
                DressMainMenu(props);
                DressLevelSelect(props);
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

        // ------------------------------------------------------------------ emblem

        private delegate float Sdf(Vector2 p);

        private static float RoundBox(Vector2 p, Vector2 c, Vector2 half, float r)
        {
            Vector2 q = new Vector2(Mathf.Abs(p.x - c.x), Mathf.Abs(p.y - c.y)) - half + new Vector2(r, r);
            return new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
        }

        private static float Circle(Vector2 p, Vector2 c, float r) => (p - c).magnitude - r;

        // Convex polygon, counter-clockwise points.
        private static float Polygon(Vector2 p, Vector2[] pts)
        {
            float d = float.MinValue;
            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % pts.Length];
                Vector2 e = (b - a).normalized;
                Vector2 n = new Vector2(e.y, -e.x); // outward for CCW
                d = Mathf.Max(d, Vector2.Dot(p - a, n));
            }
            return d;
        }

        /// <summary>Paints a shape: contour colour where d &lt; 0, fill where d &lt; -stroke (3x3 supersampled).</summary>
        private static void Paint(Color[] px, int w, int h, Sdf sdf, Color fill, float stroke, Color contour, Rect bounds)
        {
            int x0 = Mathf.Max(0, (int)bounds.xMin - 2), x1 = Mathf.Min(w - 1, (int)bounds.xMax + 2);
            int y0 = Mathf.Max(0, (int)bounds.yMin - 2), y1 = Mathf.Min(h - 1, (int)bounds.yMax + 2);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float outline = 0f, inner = 0f;
                    for (int sy = 0; sy < 3; sy++)
                        for (int sx = 0; sx < 3; sx++)
                        {
                            float d = sdf(new Vector2(x + (sx + 0.5f) / 3f, y + (sy + 0.5f) / 3f));
                            if (d < 0f) outline += 1f / 9f;
                            if (d < -stroke) inner += 1f / 9f;
                        }
                    if (outline <= 0f) continue;
                    int i = y * w + x;
                    Color c = px[i];
                    c = Color.Lerp(c, new Color(contour.r, contour.g, contour.b, 1f), outline);
                    c.a = Mathf.Max(px[i].a, outline);
                    if (inner > 0f) c = Color.Lerp(c, new Color(fill.r, fill.g, fill.b, 1f), inner);
                    px[i] = c;
                }
            }
        }

        private static void MakeEmblem()
        {
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            src.LoadImage(File.ReadAllBytes(SourceEmblem));
            int w = src.width, h = src.height;
            Color[] px = src.GetPixels();
            Color cream = HudTheme.Surface;

            // Locate the cream ring of the medallion: its centre and inner radius.
            float sx = 0f, sy = 0f; int n = 0;
            var ring = new List<Vector2>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color c = px[y * w + x];
                    if (c.a > 0.9f && Mathf.Abs(c.r - cream.r) < 0.06f && Mathf.Abs(c.g - cream.g) < 0.06f && Mathf.Abs(c.b - cream.b) < 0.06f)
                    { sx += x; sy += y; n++; ring.Add(new Vector2(x, y)); }
                }
            if (n < 100) throw new System.Exception("emblem medallion ring not found");
            Vector2 centre = new Vector2(sx / n, sy / n);
            float rIn = ring.Min(p => (p - centre).magnitude);
            // Disc colour sampled just inside the ring, on the left.
            Color disc = px[(int)centre.y * w + (int)(centre.x - rIn + 6f)];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = (new Vector2(x + 0.5f, y + 0.5f) - centre).magnitude;
                    if (d < rIn - 0.5f) px[y * w + x] = new Color(disc.r, disc.g, disc.b, 1f); // removes the castle
                    else if (d < rIn + 0.5f) px[y * w + x] = Color.Lerp(new Color(disc.r, disc.g, disc.b, 1f), px[y * w + x], d - (rIn - 0.5f));
                }

            float s = rIn / 125f;
            Color contour = HudTheme.Contour, gold = HudTheme.Gold, sage = HudTheme.Primary;
            Color sageDark = new Color(sage.r * 0.78f, sage.g * 0.78f, sage.b * 0.78f, 1f);
            Vector2 bc = centre + new Vector2(0f, -2f * s);
            Vector2 coverC = bc + new Vector2(-6f * s, 4f * s), coverH = new Vector2(62f * s, 82f * s);
            Vector2 pagesC = bc + new Vector2(8f * s, -6f * s);
            Rect all = new Rect(centre.x - rIn, centre.y - rIn, rIn * 2f, rIn * 2f);

            // Pages (behind the cover), with two page-edge lines.
            Paint(px, w, h, p => RoundBox(p, pagesC, coverH, 9f * s), cream, 7f * s, contour, all);
            float right = pagesC.x + coverH.x;
            foreach (float off in new[] { 7f, 13f })
            {
                float xLine = right - off * s;
                Paint(px, w, h, p => RoundBox(p, new Vector2(xLine, pagesC.y - 6f * s), new Vector2(1.4f * s, coverH.y - 18f * s), 1.4f * s),
                    HudTheme.BoneShade, 0f, HudTheme.BoneShade, all);
            }
            // Cover.
            Paint(px, w, h, p => RoundBox(p, coverC, coverH, 10f * s), sage, 8f * s, contour, all);
            // Spine band (inside the cover) and its seam.
            float coverLeft = coverC.x - coverH.x;
            Paint(px, w, h, p => Mathf.Max(RoundBox(p, coverC, coverH - new Vector2(8f * s, 8f * s), 4f * s),
                RoundBox(p, new Vector2(coverLeft + 17f * s, coverC.y), new Vector2(11f * s, coverH.y), 0f)), sageDark, 0f, sageDark, all);
            Paint(px, w, h, p => RoundBox(p, new Vector2(coverLeft + 29f * s, coverC.y), new Vector2(2.2f * s, coverH.y - 6f * s), 1f * s), contour, 0f, contour, all);
            // Gold corner caps on the outer corners.
            float top = coverC.y + coverH.y, bottom = coverC.y - coverH.y, coverRight = coverC.x + coverH.x;
            Paint(px, w, h, p => Polygon(p, new[] { new Vector2(coverRight - 30f * s, top + 1f * s), new Vector2(coverRight + 1f * s, top - 30f * s), new Vector2(coverRight + 1f * s, top + 1f * s) }),
                gold, 4.5f * s, contour, all);
            Paint(px, w, h, p => Polygon(p, new[] { new Vector2(coverRight - 30f * s, bottom - 1f * s), new Vector2(coverRight + 1f * s, bottom - 1f * s), new Vector2(coverRight + 1f * s, bottom + 30f * s) }),
                gold, 4.5f * s, contour, all);
            // Gold title plates.
            Vector2 face = new Vector2(coverC.x + 14f * s, coverC.y);
            foreach (float dy in new[] { 56f, -56f })
                Paint(px, w, h, p => RoundBox(p, face + new Vector2(0f, dy * s), new Vector2(30f * s, 5.5f * s), 3f * s), gold, 3.2f * s, contour, all);
            // Central rune: a Muisca-style sun (rays, ring, core) engraved in gold.
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a)), side = new Vector2(-dir.y, dir.x);
                Vector2 tip = face + dir * 42f * s, b1 = face + dir * 28f * s + side * 6f * s, b2 = face + dir * 28f * s - side * 6f * s;
                Paint(px, w, h, p => Polygon(p, new[] { b2, tip, b1 }.Reverse().ToArray()), gold, 3f * s, contour, all);
            }
            Paint(px, w, h, p => Circle(p, face, 30f * s), gold, 4f * s, contour, all);
            Paint(px, w, h, p => Circle(p, face, 19f * s), sage, 3.5f * s, contour, all);
            Paint(px, w, h, p => Circle(p, face, 9f * s), gold, 3f * s, contour, all);
            // Bookmark ribbon hanging from the bottom edge.
            float rx = coverC.x + 26f * s;
            Paint(px, w, h, p => Polygon(p, new[] { new Vector2(rx - 7f * s, bottom - 26f * s), new Vector2(rx, bottom - 18f * s), new Vector2(rx + 7f * s, bottom - 26f * s),
                new Vector2(rx + 7f * s, bottom + 6f * s), new Vector2(rx - 7f * s, bottom + 6f * s) }), gold, 3.5f * s, contour, all);

            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            outTex.SetPixels(px);
            outTex.Apply();
            File.WriteAllBytes(EmblemPath, outTex.EncodeToPNG());
            Object.DestroyImmediate(outTex);
            Object.DestroyImmediate(src);
            AssetDatabase.ImportAsset(EmblemPath);
            var srcImporter = (TextureImporter)AssetImporter.GetAtPath(SourceEmblem);
            var ti = (TextureImporter)AssetImporter.GetAtPath(EmblemPath);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.spritePixelsPerUnit = srcImporter.spritePixelsPerUnit;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = srcImporter.mipmapEnabled;
            ti.maxTextureSize = srcImporter.maxTextureSize;
            ti.textureCompression = srcImporter.textureCompression;
            ti.SaveAndReimport();
            Report.Add("Emblem: emblem_libro.png (" + w + "x" + h + "), medallion ring centre " + centre.ToString("F0") + " r " + rIn.ToString("0") + "; the castle emblem.png is kept untouched");
        }

        // ------------------------------------------------------------------ backdrops

        private class Dresser
        {
            public Camera cam;
            public Transform root;
            public Dictionary<string, GameObject> props;
            public System.Random rng;
            public int count;

            public Vector3 Ground(float vx, float vy)
            {
                Ray ray = cam.ViewportPointToRay(new Vector3(vx, vy, 0f));
                float t = ray.direction.y < -1e-4f ? -ray.origin.y / ray.direction.y : 60f;
                return ray.origin + ray.direction * Mathf.Min(t, 60f);
            }

            /// <summary>World units per viewport height at a ground point (perspective).</summary>
            public float ViewHeight(Vector3 p) => 2f * Vector3.Distance(cam.transform.position, p) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

            public float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

            public GameObject Place(string prop, Vector3 at, float height, float yaw)
            {
                if (!props.TryGetValue(prop, out GameObject prefab)) return null;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
                go.name = prop;
                Bounds b = LegendContentBuilder.MeasureBounds(go);
                go.transform.localScale = Vector3.one * (b.size.y > 0.001f ? height / b.size.y : 1f);
                go.transform.position = new Vector3(at.x, 0f, at.z);
                go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
                foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);
                count++;
                return go;
            }
        }

        private static Dresser Begin(string scenePath, out Scene scene)
        {
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            foreach (GameObject go in scene.GetRootGameObjects().Where(g => g.name == RootName).ToList()) Object.DestroyImmediate(go);
            Camera cam = Camera.main ?? Object.FindAnyObjectByType<Camera>();
            if (cam == null) throw new System.Exception(scenePath + ": no camera");
            cam.aspect = 16f / 9f;
            // Hide the course 3D content (trees, tiles, towers, castle pieces): renderers only, objects and scripts stay.
            int hidden = 0;
            foreach (Renderer r in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Renderer>(true)))
            {
                if (r.GetComponentInParent<Canvas>() != null || r is ParticleSystemRenderer) continue;
                if (r.enabled) { r.enabled = false; hidden++; }
            }
            Report.Add(System.IO.Path.GetFileName(scenePath) + ": " + hidden + " course renderers hidden");
            var root = new GameObject(RootName);
            return new Dresser { cam = cam, root = root.transform, rng = new System.Random(scenePath.Length * 7919) };
        }

        private static Material GroundMaterial(string name, Color tint)
        {
            string path = MapRestyleBuilder.Root + "/Materiales/" + name + ".mat";
            Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Standard")); AssetDatabase.CreateAsset(m, path); }
            m.shader = Shader.Find("Standard");
            m.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(MapRestyleBuilder.Root + "/Texturas/MAT_Suelo_Plano_BaseColor.png"));
            m.SetColor("_Color", tint);
            m.SetFloat("_Glossiness", 0.1f);
            m.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(m);
            return m;
        }

        private static void GroundQuad(Dresser d, Material mat, float size)
        {
            Vector3 c = d.Ground(0.5f, 0.55f);
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "Suelo selva";
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(d.root, false);
            go.transform.position = new Vector3(c.x, -0.02f, c.z);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale = new Vector3(size, size, 1f);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private static void Trail(Dresser d, string name, IEnumerable<Vector2> viewport, float width)
        {
            var poly = viewport.Select(v => d.Ground(v.x, v.y)).ToList();
            Mesh mesh = MapRestyleBuilder.BuildRibbon(poly, width, 0.0f, 1.5f);
            string meshPath = MapRestyleBuilder.Root + "/Generated/" + name + ".asset";
            if (!AssetDatabase.IsValidFolder(MapRestyleBuilder.Root + "/Generated")) AssetDatabase.CreateFolder(MapRestyleBuilder.Root, "Generated");
            if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null) AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);
            var go = new GameObject("Sendero", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(d.root, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(MapRestyleBuilder.Root + "/Materiales/GDA_Camino_Losas.mat");
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private static void Mood(Camera cam, Color fog, float fogStart, float fogEnd, Color sky, Color equator, Color groundAmb, Color sunColor, float sunIntensity, Vector3 sunEuler)
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = fog;
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogEnd;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = sky;
            RenderSettings.ambientEquatorColor = equator;
            RenderSettings.ambientGroundColor = groundAmb;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = fog;
            Light sun = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(l => l.type == LightType.Directional);
            if (sun != null)
            {
                sun.color = sunColor;
                sun.intensity = sunIntensity;
                sun.shadowStrength = 0.55f; // soft, never black
                sun.transform.rotation = Quaternion.Euler(sunEuler);
            }
        }

        private static readonly string[] Trees = { "Arbol", "Arbol2", "Arbol_2" };
        private static readonly string[] Undergrowth = { "Planta", "Hongos", "Rocas2", "Roca3", "Rocas", "Tronco", "Planta" };

        private static void Scatter(Dresser d, string[] pool, int n, float vx0, float vx1, float vy0, float vy1, float hFrac0, float hFrac1,
            System.Func<float, float, bool> keepOut)
        {
            int placed = 0, tries = 0;
            while (placed < n && tries++ < n * 30)
            {
                float vx = d.R(vx0, vx1), vy = d.R(vy0, vy1);
                if (keepOut != null && keepOut(vx, vy)) continue;
                Vector3 at = d.Ground(vx, vy);
                float h = d.ViewHeight(at) * d.R(hFrac0, hFrac1);
                if (d.Place(pool[d.rng.Next(pool.Length)], at, h, d.R(0f, 360f)) != null) placed++;
            }
        }

        private static float DistToPolyline(Vector2 q, Vector2[] pts)
        {
            float best = float.MaxValue;
            for (int i = 0; i < pts.Length - 1; i++)
            {
                Vector2 a = pts[i], ab = pts[i + 1] - a;
                float t = Mathf.Clamp01(Vector2.Dot(q - a, ab) / ab.sqrMagnitude);
                best = Mathf.Min(best, (q - (a + ab * t)).magnitude);
            }
            return best;
        }

        private static void DressMainMenu(Dictionary<string, GameObject> props)
        {
            Dresser d = Begin(MainMenuScene, out Scene scene);
            d.props = props;
            Mood(d.cam, new Color(0.13f, 0.21f, 0.16f), 6f, 30f, new Color(0.40f, 0.52f, 0.42f), new Color(0.26f, 0.36f, 0.28f),
                new Color(0.12f, 0.16f, 0.12f), new Color(1f, 0.93f, 0.80f), 0.8f, new Vector3(42f, -35f, 0f));
            GroundQuad(d, GroundMaterial("GDA_Suelo_Selva_Menu", new Color(0.62f, 0.78f, 0.55f)), 140f);

            // The clearing between the brand block (left) and the buttons (right): Sacred Book plaza, ruins, warm light.
            const float bx = 0.54f, by = 0.44f;
            Vector3 bookAt = d.Ground(bx, by);
            GameObject plazaPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LegendContentBuilder.PrefabDir + "/LibroSagrado.prefab");
            if (plazaPrefab != null)
            {
                var book = (GameObject)PrefabUtility.InstantiatePrefab(plazaPrefab, d.root);
                book.name = "Libro Sagrado";
                Bounds bb = LegendContentBuilder.MeasureBounds(book);
                float viewW = d.ViewHeight(bookAt) * 16f / 9f;
                book.transform.localScale = Vector3.one * (0.15f * viewW / Mathf.Max(bb.size.x, bb.size.z));
                book.transform.position = bookAt;
                book.transform.rotation = Quaternion.Euler(0f, 200f, 0f);
                var lightGo = new GameObject("Luz del Libro");
                lightGo.transform.SetParent(d.root, false);
                lightGo.transform.position = bookAt + Vector3.up * 1.6f + (d.cam.transform.position - bookAt).normalized * 0.6f;
                Light l = lightGo.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(1f, 0.82f, 0.50f);
                l.intensity = 2.2f;
                l.range = 4.5f;
                l.shadows = LightShadows.None;
            }
            float hv = d.ViewHeight(bookAt);
            d.Place("Arco", d.Ground(bx, by + 0.13f), hv * 0.26f, 180f);
            d.Place("Pilar", d.Ground(bx - 0.075f, by + 0.07f), hv * 0.17f, 20f);
            d.Place("Pilar", d.Ground(bx + 0.075f, by + 0.07f), hv * 0.15f, 160f);
            d.Place("Hongos", d.Ground(bx - 0.06f, by - 0.04f), hv * 0.06f, 30f);
            d.Place("Hongos", d.Ground(bx + 0.065f, by - 0.035f), hv * 0.05f, 200f);
            var trail = new[] { new Vector2(0.50f, -0.05f), new Vector2(0.47f, 0.10f), new Vector2(0.56f, 0.24f), new Vector2(0.53f, 0.36f) };
            Trail(d, "Menu_Sendero", trail, 0.9f);

            bool Clearing(float vx, float vy) => (new Vector2((vx - bx) * 1.6f, vy - by)).magnitude < 0.15f || DistToPolyline(new Vector2(vx, vy), trail) < 0.075f;
            // Trees never stand in the central column (their crowns would cross the clearing and the arch).
            bool TreeFree(float vx, float vy) => vx > 0.33f && vx < 0.73f && vy < 0.80f;
            // Far canopy wall, fading into the fog.
            Scatter(d, Trees, 30, -0.08f, 1.08f, 0.74f, 1.04f, 0.50f, 0.80f, TreeFree);
            // Side groves, behind the brand block (left) and the button column (right).
            Scatter(d, Trees, 10, -0.06f, 0.30f, 0.38f, 0.76f, 0.34f, 0.52f, TreeFree);
            Scatter(d, Trees, 10, 0.75f, 1.06f, 0.38f, 0.76f, 0.34f, 0.52f, TreeFree);
            // Undergrowth: mossy rocks, plants, mushrooms, fallen logs (low, off the trail and the clearing).
            Scatter(d, Undergrowth, 26, 0.0f, 1.0f, 0.12f, 0.66f, 0.04f, 0.085f, Clearing);
            // Foreground framing at the lower corners (low props, no giant mushrooms).
            var front = new[] { "Planta", "Rocas2", "Tronco", "Roca3" };
            Scatter(d, front, 7, -0.04f, 0.28f, -0.04f, 0.13f, 0.06f, 0.11f, null);
            Scatter(d, front, 7, 0.72f, 1.04f, -0.04f, 0.13f, 0.06f, 0.11f, null);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Report.Add("Main Menu: jungle clearing with the Sacred Book plaza, " + d.count + " props, fog and focal light");
        }

        private static void DressLevelSelect(Dictionary<string, GameObject> props)
        {
            Dresser d = Begin(LevelSelectScene, out Scene scene);
            d.props = props;
            Mood(d.cam, new Color(0.18f, 0.27f, 0.23f), 7f, 26f, new Color(0.45f, 0.56f, 0.50f), new Color(0.30f, 0.40f, 0.34f),
                new Color(0.14f, 0.18f, 0.15f), new Color(0.96f, 1f, 0.86f), 0.85f, new Vector3(55f, 30f, 0f));
            GroundQuad(d, GroundMaterial("GDA_Suelo_Selva_Niveles", new Color(0.58f, 0.74f, 0.56f)), 140f);

            // A long winding trail through the territories of the legends: it runs under the cards and climbs at the sides,
            // with ruins where the interface leaves the ground visible.
            var trail = new[] { new Vector2(-0.05f, 0.16f), new Vector2(0.22f, 0.12f), new Vector2(0.50f, 0.20f), new Vector2(0.78f, 0.12f),
                new Vector2(0.95f, 0.30f), new Vector2(0.96f, 0.62f), new Vector2(0.86f, 0.92f), new Vector2(0.62f, 1.05f) };
            Trail(d, "Niveles_Sendero", trail, 1.0f);
            bool OnTrail(float vx, float vy) => DistToPolyline(new Vector2(vx, vy), trail) < 0.08f;
            // Trees only at the borders (no crowns crossing the gaps between the level cards).
            bool TreeFree(float vx, float vy) => OnTrail(vx, vy) || (vx > 0.08f && vx < 0.90f && vy > 0.26f && vy < 0.90f);
            Vector3 p1 = d.Ground(0.36f, 0.07f), p2 = d.Ground(0.66f, 0.25f), p3 = d.Ground(0.94f, 0.45f);
            d.Place("Arco", p1, d.ViewHeight(p1) * 0.15f, 170f);
            d.Place("Portal", p2, d.ViewHeight(p2) * 0.14f, 200f);
            d.Place("Pilar", p3, d.ViewHeight(p3) * 0.11f, 30f);
            d.Place("Pilar", d.Ground(0.03f, 0.40f), d.ViewHeight(p3) * 0.10f, 60f);
            d.Place("Muro", d.Ground(0.18f, 0.24f), d.ViewHeight(p1) * 0.08f, 100f);
            foreach (Vector3 ruin in new[] { p1, p2, p3 })
            {
                d.Place("Hongos", ruin + new Vector3(0.6f, 0f, -0.3f), d.ViewHeight(ruin) * 0.035f, 40f);
                d.Place("Planta", ruin + new Vector3(-0.6f, 0f, 0.2f), d.ViewHeight(ruin) * 0.05f, 140f);
            }
            Scatter(d, Trees, 22, -0.08f, 1.08f, -0.05f, 1.08f, 0.16f, 0.26f, TreeFree);
            Scatter(d, Undergrowth, 34, -0.02f, 1.02f, -0.02f, 1.02f, 0.03f, 0.06f, OnTrail);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Report.Add("Level Select: winding jungle trail with ruins, " + d.count + " props, fog");
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GameDevAgent.ContentTools
{
    /// <summary>
    /// Phase 14: original 3D projectiles for three legends, built procedurally (real low-poly meshes, flat shaded, one
    /// shared palette material). Presentation only: every projectile prefab is a COPY of the course prefab with all its
    /// gameplay components and values (speed, damage, trigger, rigidbody, area radius, layers) kept as they are; only
    /// the visual child is replaced. The course prefabs are never modified.
    ///  - El Mohán (Cannon Tower): MohanEmberProjectile = Cannonball copy, glowing ember + short trail, ember impact.
    ///  - El Hojarasquín (Bomb Tower): HojarasquinRockProjectile = Bomb copy, tumbling rock + dust trail; impact = dust
    ///    (recoloured bomb effect) and a few visual rock chips (particles, no collider, no damage).
    ///  - La Patasola (Triple Shot Tower): PatasolaBoomerangLegProjectile = Triple Cannonball copy, each of the three
    ///    projectiles shows a stylised leg spinning flat like a boomerang (no return path).
    /// Missed shots are removed after 6 s (ProjectileLifetime): the course Projectile never got OnBecameInvisible.
    /// Run: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.ContentTools.ProjectileContentBuilder.BuildFromCommandLine
    ///      [-gdaShots &lt;dir&gt;]
    /// </summary>
    public static class ProjectileContentBuilder
    {
        private const string Tag = "[Projectiles] ";
        public const string Dir = LegendContentBuilder.Root + "/Projectiles";
        private const string CourseProjectiles = "Assets/Course/Prefabs/Projectiles/";
        private const string CourseEffects = "Assets/Course/Prefabs/Effects/";
        private const string TowerDir = "Assets/Course/Prefabs/Towers/";

        public const string EmberName = "MohanEmberProjectile";
        public const string RockName = "HojarasquinRockProjectile";
        public const string LegName = "PatasolaBoomerangLegProjectile";
        public const string EmberImpactName = "MohanEmberImpact";
        public const string RockImpactName = "HojarasquinRockImpact";
        public const string VisualName = "Visual";
        public const float LifetimeSeconds = 6f;

        // ------------------------------------------------------------------ palette (one 16x1 texture for all)
        private enum S { CharDark, CharMid, EmberRed, EmberOrange, EmberYellow, RockGrey, RockEarth, RockDark, RockLight, Skin, SkinShadow, Cloth, Glow, SkinPale }

        private static readonly Color[] Albedo =
        {
            new Color(0.09f, 0.07f, 0.06f), new Color(0.17f, 0.13f, 0.11f), new Color(0.80f, 0.16f, 0.05f), new Color(1.00f, 0.47f, 0.08f),
            new Color(1.00f, 0.82f, 0.32f), new Color(0.35f, 0.33f, 0.30f), new Color(0.40f, 0.32f, 0.23f), new Color(0.23f, 0.21f, 0.19f),
            new Color(0.49f, 0.46f, 0.41f), new Color(0.62f, 0.63f, 0.66f), new Color(0.47f, 0.49f, 0.53f), new Color(0.16f, 0.18f, 0.27f),
            new Color(0.35f, 1.00f, 0.45f), new Color(0.78f, 0.76f, 0.72f),
        };

        private static readonly HashSet<S> Emissive = new HashSet<S> { S.EmberRed, S.EmberOrange, S.EmberYellow, S.Glow };
        private const int PaletteSize = 16;

        private static Vector2 Uv(S s) => new Vector2(((int)s + 0.5f) / PaletteSize, 0.5f);

        public static readonly List<string> Report = new List<string>();
        private static string shotsDir;

        public static void BuildFromCommandLine()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-gdaShots") shotsDir = args[i + 1];
            EditorApplication.Exit(Build() ? 0 : 1);
        }

        [MenuItem("Tools/GameDevAgent/Projectiles (phase 14)")]
        public static void BuildFromMenu() => Build();

        public static bool Build()
        {
            Report.Clear();
            try
            {
                foreach (string sub in new[] { "", "/Meshes", "/Textures", "/Materials", "/Prefabs" }) EnsureFolder(Dir + sub);

                Material shared = BuildSharedMaterial();
                Material emberTrail = TrailMaterial("GDA_EmberTrail", true);
                Material dustTrail = TrailMaterial("GDA_DustTrail", false);

                Mesh ember = SaveMesh(BuildEmber(), "Ember");
                Mesh rock = SaveMesh(BuildRock(0.21f, 1, 0.24f, 11), "Rock");
                Mesh chip = SaveMesh(BuildRock(0.06f, 0, 0.3f, 23), "RockChip");
                Mesh leg = SaveMesh(BuildLeg(), "BoomerangLeg");

                GameObject emberImpact = BuildEmberImpact();
                GameObject rockImpact = BuildRockImpact(chip, shared, dustTrail);

                GameObject emberPrefab = CopyPrefab(CourseProjectiles + "Cannonball.prefab", EmberName, root =>
                {
                    foreach (Projectile p in root.GetComponentsInChildren<Projectile>(true))
                    {
                        ReplaceVisual(p.transform, ember, shared, new Vector3(0.35f, 1f, 0.2f), 260f);
                        p.impactEffect = emberImpact;
                        AddTrail(p.transform, emberTrail, 0.16f, 0.13f, new Color(1f, 0.55f, 0.12f, 0.9f), new Color(1f, 0.2f, 0.05f, 0f));
                    }
                    root.AddComponent<ProjectileLifetime>().seconds = LifetimeSeconds;
                });
                GameObject legPrefab = CopyPrefab(CourseProjectiles + "Triple Cannonball.prefab", LegName, root =>
                {
                    foreach (Projectile p in root.GetComponentsInChildren<Projectile>(true))
                    {
                        ReplaceVisual(p.transform, leg, shared, Vector3.up, 900f); // flat boomerang spin
                        // The two side projectiles leave their parent in the first physics step (course behaviour), so
                        // each projectile carries its own cleanup.
                        p.gameObject.AddComponent<ProjectileLifetime>().seconds = LifetimeSeconds;
                    }
                });
                GameObject rockPrefab = CopyPrefab(CourseProjectiles + "Bomb.prefab", RockName, root =>
                {
                    Bomb bomb = root.GetComponent<Bomb>();
                    // The course bomb is drawn by a nested bomb model and a fuse particle system: the rock replaces both
                    // (renderers off, emission off; the objects and transforms stay, Bomb still moves its model).
                    foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        var em = ps.emission;
                        em.enabled = false;
                    }
                    int hidden = 0;
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                        if (r.enabled) { r.enabled = false; hidden++; }
                    Log("  " + RockName + ": " + hidden + " course bomb renderer(s) hidden");
                    var visual = new GameObject(VisualName);
                    visual.layer = bomb.model.gameObject.layer;
                    visual.transform.SetParent(bomb.model, false);
                    visual.AddComponent<MeshFilter>().sharedMesh = rock;
                    var mr = visual.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = shared;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    var spin = visual.AddComponent<ProjectileSpin>();
                    spin.axis = new Vector3(1f, 0.35f, 0.2f).normalized;
                    spin.degreesPerSecond = 300f;
                    AddTrail(bomb.model, dustTrail, 0.22f, 0.2f, new Color(0.55f, 0.5f, 0.42f, 0.45f), new Color(0.55f, 0.5f, 0.42f, 0f));
                    bomb.explodeEffect = rockImpact;
                });

                AssignTowers(emberPrefab, rockPrefab, legPrefab);
                AssetDatabase.SaveAssets();
                if (!string.IsNullOrEmpty(shotsDir)) RenderLineup(new[] { ember, rock, leg }, shared);
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

        private static void Log(string line) => Report.Add(line);

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }

        // ------------------------------------------------------------------ materials

        private static Texture2D PaletteTexture(string file, bool emission)
        {
            string path = Dir + "/Textures/" + file + ".png";
            var tex = new Texture2D(PaletteSize, 1, TextureFormat.RGBA32, false);
            for (int i = 0; i < PaletteSize; i++)
            {
                Color c = i < Albedo.Length ? Albedo[i] : Color.black;
                if (emission) c = i < Albedo.Length && Emissive.Contains((S)i) ? c : Color.black;
                tex.SetPixel(i, 0, c);
            }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.mipmapEnabled = false;
            ti.filterMode = FilterMode.Point;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material LoadOrCreate(string name, string shader)
        {
            string path = Dir + "/Materials/" + name + ".mat";
            Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader s = Shader.Find(shader);
            if (m == null) { m = new Material(s); AssetDatabase.CreateAsset(m, path); }
            m.shader = s;
            return m;
        }

        private static Material BuildSharedMaterial()
        {
            Texture2D albedo = PaletteTexture("GDA_ProjectilePalette", false);
            Texture2D emission = PaletteTexture("GDA_ProjectilePalette_Emission", true);
            Material m = LoadOrCreate("GDA_Projectiles", "Standard");
            m.SetTexture("_MainTex", albedo);
            m.SetColor("_Color", Color.white);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Glossiness", 0.22f);
            m.SetTexture("_EmissionMap", emission);
            m.SetColor("_EmissionColor", Color.white * 1.8f);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            Log("material GDA_Projectiles (Standard, 16-colour palette + emission palette) shared by the three projectiles and the rock chips");
            return m;
        }

        private static Material TrailMaterial(string name, bool additive)
        {
            string wanted = additive ? "Legacy Shaders/Particles/Additive" : "Legacy Shaders/Particles/Alpha Blended";
            Material m = LoadOrCreate(name, Shader.Find(wanted) != null ? wanted : "Sprites/Default");
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            EditorUtility.SetDirty(m);
            Log("material " + name + ": " + m.shader.name);
            return m;
        }

        // ------------------------------------------------------------------ meshes

        private class FlatMesh
        {
            public readonly List<Vector3> v = new List<Vector3>();
            public readonly List<Vector2> uv = new List<Vector2>();
            public readonly List<int> t = new List<int>();

            /// <summary>Adds a flat-shaded triangle whose front face looks away from <paramref name="inside"/>.</summary>
            public void Tri(Vector3 a, Vector3 b, Vector3 c, S swatch, Vector3 inside)
            {
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(n, (a + b + c) / 3f - inside) < 0f) { Vector3 x = b; b = c; c = x; }
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c);
                Vector2 u = Uv(swatch);
                uv.Add(u); uv.Add(u); uv.Add(u);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
            }

            public Mesh ToMesh(string name, bool centre)
            {
                var m = new Mesh { name = name };
                List<Vector3> verts = v;
                if (centre)
                {
                    var b = new Bounds(v[0], Vector3.zero);
                    foreach (Vector3 p in v) b.Encapsulate(p);
                    verts = v.Select(p => p - b.center).ToList(); // pivot at the centre: the spin stays on the trajectory
                }
                m.SetVertices(verts);
                m.SetUVs(0, uv);
                m.SetTriangles(t, 0);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }
        }

        private static void Icosphere(int subdivisions, out List<Vector3> verts, out List<int[]> faces)
        {
            float g = (1f + Mathf.Sqrt(5f)) * 0.5f;
            verts = new List<Vector3>
            {
                new Vector3(-1, g, 0), new Vector3(1, g, 0), new Vector3(-1, -g, 0), new Vector3(1, -g, 0),
                new Vector3(0, -1, g), new Vector3(0, 1, g), new Vector3(0, -1, -g), new Vector3(0, 1, -g),
                new Vector3(g, 0, -1), new Vector3(g, 0, 1), new Vector3(-g, 0, -1), new Vector3(-g, 0, 1),
            }.Select(p => p.normalized).ToList();
            faces = new List<int[]>
            {
                new[] { 0, 11, 5 }, new[] { 0, 5, 1 }, new[] { 0, 1, 7 }, new[] { 0, 7, 10 }, new[] { 0, 10, 11 },
                new[] { 1, 5, 9 }, new[] { 5, 11, 4 }, new[] { 11, 10, 2 }, new[] { 10, 7, 6 }, new[] { 7, 1, 8 },
                new[] { 3, 9, 4 }, new[] { 3, 4, 2 }, new[] { 3, 2, 6 }, new[] { 3, 6, 8 }, new[] { 3, 8, 9 },
                new[] { 4, 9, 5 }, new[] { 2, 4, 11 }, new[] { 6, 2, 10 }, new[] { 8, 6, 7 }, new[] { 9, 8, 1 },
            };
            for (int s = 0; s < subdivisions; s++)
            {
                var cache = new Dictionary<long, int>();
                var vs = verts;
                int Mid(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (cache.TryGetValue(key, out int idx)) return idx;
                    vs.Add(((vs[a] + vs[b]) * 0.5f).normalized);
                    cache[key] = vs.Count - 1;
                    return vs.Count - 1;
                }
                var next = new List<int[]>();
                foreach (int[] f in faces)
                {
                    int ab = Mid(f[0], f[1]), bc = Mid(f[1], f[2]), ca = Mid(f[2], f[0]);
                    next.Add(new[] { f[0], ab, ca }); next.Add(new[] { f[1], bc, ab }); next.Add(new[] { f[2], ca, bc }); next.Add(new[] { ab, bc, ca });
                }
                faces = next;
            }
        }

        // Smooth deterministic noise on the unit sphere, in [-1, 1].
        private static float Noise(Vector3 d, int seed)
        {
            float s = seed * 1.618f;
            return (Mathf.Sin(Vector3.Dot(d, new Vector3(3.1f, 1.7f, 2.3f)) * 2.1f + s)
                  + 0.6f * Mathf.Sin(Vector3.Dot(d, new Vector3(-1.3f, 4.2f, 0.9f)) * 2.7f + s * 2f)
                  + 0.4f * Mathf.Sin(Vector3.Dot(d, new Vector3(2.2f, -0.8f, 4.6f)) * 3.9f + s * 3f)) / 2f;
        }

        /// <summary>Irregular low-poly rock: displaced icosphere, faceted, grey / earthy facets, lighter on top.</summary>
        private static Mesh BuildRock(float radius, int subdivisions, float roughness, int seed)
        {
            Icosphere(subdivisions, out List<Vector3> unit, out List<int[]> faces);
            var squash = new Vector3(1.1f, 0.8f, 0.95f);
            var pts = unit.Select(d => Vector3.Scale(d * radius * (1f + roughness * Noise(d, seed)), squash)).ToList();
            var fm = new FlatMesh();
            int k = 0;
            foreach (int[] f in faces)
            {
                Vector3 a = pts[f[0]], b = pts[f[1]], c = pts[f[2]];
                Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                Vector3 centroid = (a + b + c) / 3f;
                if (Vector3.Dot(n, centroid) < 0f) n = -n;
                float h = Noise(centroid.normalized * 1.7f, seed + 5);
                S sw = n.y > 0.55f ? S.RockLight : h > 0.35f ? S.RockEarth : h < -0.35f ? S.RockDark : S.RockGrey;
                if ((k++ % 7) == 3 && sw == S.RockGrey) sw = S.RockEarth;
                fm.Tri(a, b, c, sw, Vector3.zero);
            }
            Mesh m = fm.ToMesh("GDA_Rock_" + seed, false);
            Log("mesh rock r" + radius + ": " + faces.Count + " tris");
            return m;
        }

        /// <summary>Hot ember: dark faceted charcoal crossed by glowing cracks (yellow at the core, orange, red at the edge).</summary>
        private static Mesh BuildEmber()
        {
            Icosphere(2, out List<Vector3> unit, out List<int[]> faces);
            const float radius = 0.14f;
            var squash = new Vector3(1f, 0.82f, 0.92f);
            var pts = unit.Select(d => Vector3.Scale(d * radius * (1f + 0.17f * Noise(d, 3)), squash)).ToList();
            Vector3[] planes = { new Vector3(0.3f, 1f, 0.1f).normalized, new Vector3(1f, -0.2f, 0.5f).normalized, new Vector3(-0.4f, 0.3f, 1f).normalized, new Vector3(0.7f, 0.6f, -0.5f).normalized };
            Vector3[] arcs = { new Vector3(1f, 0f, 0.3f), new Vector3(0f, 1f, -0.4f), new Vector3(0.2f, -1f, 0.6f), new Vector3(-0.6f, -0.2f, -1f) };
            var fm = new FlatMesh();
            int glowing = 0;
            foreach (int[] f in faces)
            {
                Vector3 a = pts[f[0]], b = pts[f[1]], c = pts[f[2]];
                Vector3 d = ((unit[f[0]] + unit[f[1]] + unit[f[2]]) / 3f).normalized;
                float crack = float.MaxValue;
                for (int i = 0; i < planes.Length; i++)
                    if (Vector3.Dot(d, arcs[i].normalized) > -0.25f) crack = Mathf.Min(crack, Mathf.Abs(Vector3.Dot(d, planes[i])));
                S sw = crack < 0.035f ? S.EmberYellow : crack < 0.075f ? S.EmberOrange : crack < 0.11f ? S.EmberRed
                    : Noise(d * 2f, 9) > 0.1f ? S.CharMid : S.CharDark;
                if (Emissive.Contains(sw)) glowing++;
                fm.Tri(a, b, c, sw, Vector3.zero);
            }
            Mesh m = fm.ToMesh("GDA_Ember", false);
            Log("mesh ember: " + faces.Count + " tris (" + glowing + " glowing crack facets)");
            return m;
        }

        private struct Station
        {
            public Vector3 p; public float r, flat; public S swatch;
            public Station(Vector3 p, float r, float flat, S swatch) { this.p = p; this.r = r; this.flat = flat; this.swatch = swatch; }
        }

        /// <summary>
        /// Stylised leg, lying flat (XZ plane) so a spin around Y reads as a boomerang: thigh wrapped in La Patasola's
        /// dark cloth (no cut flesh), a green glowing band like her eyes, bent knee, calf, glowing anklet and a foot at
        /// a right angle. Faceted 8-sided loft, pivot at the centre of its bounds.
        /// </summary>
        private static Mesh BuildLeg()
        {
            const int sides = 8;
            Vector3 knee = new Vector3(0.25f, 0f, 0f);
            Vector3 shin = new Vector3(Mathf.Cos(40f * Mathf.Deg2Rad), 0f, Mathf.Sin(40f * Mathf.Deg2Rad));
            Vector3 ankle = knee + shin * 0.25f;
            Vector3 foot = Quaternion.AngleAxis(90f, Vector3.up) * shin;
            var st = new List<Station>
            {
                new Station(new Vector3(0f, 0f, 0f), 0.085f, 0.95f, S.Cloth),
                new Station(new Vector3(0.05f, 0f, 0f), 0.089f, 0.95f, S.Cloth),
                new Station(new Vector3(0.072f, 0f, 0f), 0.087f, 0.95f, S.Glow),
                new Station(new Vector3(0.088f, 0f, 0f), 0.084f, 0.95f, S.Skin),
                new Station(new Vector3(0.17f, 0f, 0f), 0.074f, 0.95f, S.SkinShadow),
                new Station(knee, 0.062f, 0.95f, S.Skin),
                new Station(knee + shin * 0.06f, 0.066f, 0.95f, S.Skin),
                new Station(knee + shin * 0.13f, 0.058f, 0.95f, S.SkinShadow),
                new Station(knee + shin * 0.2f, 0.046f, 0.95f, S.Skin),
                new Station(knee + shin * 0.228f, 0.041f, 0.95f, S.Glow),
                new Station(ankle, 0.04f, 0.9f, S.Skin),
                new Station(ankle + foot * 0.035f, 0.042f, 0.7f, S.Skin),
                new Station(ankle + foot * 0.09f, 0.035f, 0.6f, S.SkinPale),
                new Station(ankle + foot * 0.135f, 0.022f, 0.55f, S.SkinPale),
            };
            var rings = new List<Vector3[]>();
            for (int i = 0; i < st.Count; i++)
            {
                Vector3 prev = st[Mathf.Max(0, i - 1)].p, next = st[Mathf.Min(st.Count - 1, i + 1)].p;
                Vector3 tan = (next - prev).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, tan).normalized;
                var ring = new Vector3[sides];
                for (int k = 0; k < sides; k++)
                {
                    float a = (k + 0.5f) * Mathf.PI * 2f / sides;
                    ring[k] = st[i].p + side * Mathf.Cos(a) * st[i].r + Vector3.up * Mathf.Sin(a) * st[i].r * st[i].flat;
                }
                rings.Add(ring);
            }
            var fm = new FlatMesh();
            for (int i = 0; i < st.Count - 1; i++)
            {
                Vector3 axis = (st[i].p + st[i + 1].p) * 0.5f;
                for (int k = 0; k < sides; k++)
                {
                    int k2 = (k + 1) % sides;
                    fm.Tri(rings[i][k], rings[i + 1][k], rings[i + 1][k2], st[i].swatch, axis);
                    fm.Tri(rings[i][k], rings[i + 1][k2], rings[i][k2], st[i].swatch, axis);
                }
            }
            // Caps: the wrapped thigh end (cloth) and the toes.
            Vector3 t0 = (st[1].p - st[0].p).normalized, tn = (st[st.Count - 1].p - st[st.Count - 2].p).normalized;
            Vector3 c0 = st[0].p - t0 * 0.02f, cn = st[st.Count - 1].p + tn * 0.012f;
            for (int k = 0; k < sides; k++)
            {
                int k2 = (k + 1) % sides;
                fm.Tri(rings[0][k], rings[0][k2], c0, S.Cloth, st[0].p + t0 * 0.03f);
                fm.Tri(rings[st.Count - 1][k], rings[st.Count - 1][k2], cn, S.SkinPale, st[st.Count - 1].p - tn * 0.03f);
            }
            Mesh m = fm.ToMesh("GDA_BoomerangLeg", true);
            Log("mesh boomerang leg: " + fm.t.Count / 3 + " tris, size " + m.bounds.size.ToString("F2"));
            return m;
        }

        private static Mesh SaveMesh(Mesh built, string file)
        {
            string path = Dir + "/Meshes/" + file + ".asset";
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null) { AssetDatabase.CreateAsset(built, path); return built; }
            existing.Clear();
            existing.name = built.name;
            existing.SetVertices(built.vertices);
            existing.SetUVs(0, built.uv);
            existing.SetTriangles(built.triangles, 0);
            existing.SetNormals(built.normals);
            existing.RecalculateBounds();
            Object.DestroyImmediate(built);
            EditorUtility.SetDirty(existing);
            return existing; // same asset (and GUID) as before
        }

        // ------------------------------------------------------------------ effects

        private static GameObject CopyPrefab(string source, string name, System.Action<GameObject> edit, string folder = null)
        {
            string path = (folder ?? Dir + "/Prefabs") + "/" + name + ".prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(source);
            try
            {
                root.name = name;
                edit(root);
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
                Log(name + ".prefab <- copy of " + System.IO.Path.GetFileName(source) + " (gameplay components and values kept)");
                return saved;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// The course effects draw Standard-shaded mesh particles (the particle colour is ignored by that shader), so the
        /// copies get their own Standard material. Emission rate, sizes, lifetimes and stop action stay as they were.
        /// </summary>
        private static void Recolour(GameObject root, Color a, Color b, Material replacement)
        {
            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.startColor = new ParticleSystem.MinMaxGradient(a, b);
                var r = ps.GetComponent<ParticleSystemRenderer>();
                string shader = r != null && r.sharedMaterial != null ? r.sharedMaterial.shader.name : "none";
                if (replacement != null && r != null) r.sharedMaterial = replacement;
                Log("  " + root.name + "/" + ps.name + ": " + main.maxParticles + " max particles, lifetime " + main.startLifetime.constantMax.ToString("0.00")
                    + "s, stop " + main.stopAction + ", render " + (r != null ? r.renderMode.ToString() : "-") + ", material " + shader + " -> " + (replacement != null ? replacement.name : "kept"));
            }
        }

        private static Material EffectMaterial(string name, Color albedo, Color emission, float smoothness)
        {
            Material m = LoadOrCreate(name, "Standard");
            m.SetColor("_Color", albedo);
            m.SetTexture("_MainTex", null);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Glossiness", smoothness);
            if (emission.maxColorComponent > 0f)
            {
                m.SetColor("_EmissionColor", emission);
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            else
            {
                m.SetColor("_EmissionColor", Color.black);
                m.DisableKeyword("_EMISSION");
            }
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        private static GameObject BuildEmberImpact()
        {
            Material sparks = EffectMaterial("GDA_EmberSparks", new Color(1f, 0.55f, 0.15f), new Color(1f, 0.42f, 0.08f) * 2.2f, 0.1f);
            return CopyPrefab(CourseEffects + "Impact Effect.prefab", EmberImpactName,
                root => Recolour(root, new Color(1f, 0.85f, 0.35f, 1f), new Color(1f, 0.35f, 0.06f, 1f), sparks));
        }

        private static GameObject BuildRockImpact(Mesh chip, Material shared, Material dust)
        {
            Material dustMat = EffectMaterial("GDA_RockDust", new Color(0.52f, 0.46f, 0.38f), Color.black, 0f);
            return CopyPrefab(CourseEffects + "Bomb Effect.prefab", RockImpactName, root =>
            {
                Recolour(root, new Color(0.62f, 0.56f, 0.46f, 0.85f), new Color(0.42f, 0.38f, 0.32f, 0.7f), dustMat);
                float rootLife = 0.6f;
                ParticleSystem rootPs = root.GetComponent<ParticleSystem>();
                if (rootPs != null) rootLife = Mathf.Max(0.3f, rootPs.main.duration + rootPs.main.startLifetime.constantMax * 0.5f);
                // A few rock chips: visual particles only (no collision module, no collider, no damage).
                var chips = new GameObject("Rock Chips");
                chips.transform.SetParent(root.transform, false);
                chips.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // hemisphere opens upwards
                var ps = chips.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                main.duration = 0.4f;
                main.loop = false;
                main.playOnAwake = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(Mathf.Min(0.4f, rootLife * 0.8f), Mathf.Min(0.7f, rootLife));
                main.startSpeed = new ParticleSystem.MinMaxCurve(1.6f, 3.2f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.7f, 1.3f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.gravityModifier = 1.6f;
                main.maxParticles = 8;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.stopAction = ParticleSystemStopAction.None;
                var emission = ps.emission;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 7) });
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 0.15f;
                var rot = ps.rotationOverLifetime;
                rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(-8f, 8f);
                var r = chips.GetComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Mesh;
                r.mesh = chip;
                r.sharedMaterial = shared;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                Log("  " + RockImpactName + "/Rock Chips: 7 mesh particles, lifetime <= " + Mathf.Min(0.7f, rootLife).ToString("0.00") + " s, no collision");
            });
        }

        // ------------------------------------------------------------------ projectile visuals

        private static void ReplaceVisual(Transform projectile, Mesh mesh, Material material, Vector3 axis, float speed)
        {
            foreach (Transform child in projectile.Cast<Transform>().ToList())
            {
                if (child.GetComponent<Projectile>() != null) continue; // the other projectiles of the triple shot
                if (child.GetComponent<MeshRenderer>() != null || child.name == VisualName) Object.DestroyImmediate(child.gameObject); // course sphere
            }
            var visual = new GameObject(VisualName);
            visual.layer = projectile.gameObject.layer;
            visual.transform.SetParent(projectile, false);
            float s = projectile.lossyScale.x;
            visual.transform.localScale = Vector3.one / Mathf.Max(0.0001f, s); // the mesh is authored in metres
            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = visual.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var spin = visual.AddComponent<ProjectileSpin>();
            spin.axis = axis.normalized;
            spin.degreesPerSecond = speed;
        }

        private static void AddTrail(Transform parent, Material material, float time, float width, Color start, Color end)
        {
            Transform old = parent.Find("Trail");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var go = new GameObject("Trail");
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            var tr = go.AddComponent<TrailRenderer>();
            tr.sharedMaterial = material;
            tr.time = time;
            tr.minVertexDistance = 0.06f;
            tr.widthMultiplier = width;
            tr.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(start, 0f), new GradientColorKey(end, 1f) }, new[] { new GradientAlphaKey(start.a, 0f), new GradientAlphaKey(0f, 1f) });
            tr.colorGradient = g;
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tr.receiveShadows = false;
            tr.numCapVertices = 0;
            tr.autodestruct = false;
        }

        private static void AssignTowers(GameObject ember, GameObject rock, GameObject leg)
        {
            void Edit(string tower, System.Action<GameObject> set)
            {
                string path = TowerDir + tower + ".prefab";
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try { set(root); PrefabUtility.SaveAsPrefabAsset(root, path); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
                Log(tower + ".prefab -> new projectile (stats, fire point, targeting untouched)");
            }
            Edit("Cannon Tower", root => root.GetComponent<ProjectileTower>().projectile = ember);
            Edit("Triple Shot Tower", root => root.GetComponent<ProjectileTower>().projectile = leg);
            Edit("Bomb Tower", root => root.GetComponent<BombTower>().theBomb = rock.GetComponent<Bomb>());
        }

        // ------------------------------------------------------------------ captures

        private static void RenderLineup(Mesh[] meshes, Material material)
        {
            Directory.CreateDirectory(shotsDir);
            bool asyncShaders = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            var temp = new GameObject("__gda_projectiles");
            temp.transform.position = new Vector3(7000f, 0f, 7000f);
            try
            {
                // The camera looks along -Z, so +X is on the left of the image: ember, rock, leg from left to right.
                float x = 1.0f;
                foreach (Mesh m in meshes)
                {
                    var go = new GameObject(m.name);
                    go.transform.SetParent(temp.transform, false);
                    go.transform.localPosition = new Vector3(x, 0f, 0f);
                    go.transform.localRotation = Quaternion.Euler(m.name.Contains("Leg") ? 60f : 15f, 25f, 0f);
                    go.transform.localScale = Vector3.one * (m.name.Contains("Leg") ? 1.5f : m.name.Contains("Ember") ? 2.2f : 1.6f);
                    go.AddComponent<MeshFilter>().sharedMesh = m;
                    go.AddComponent<MeshRenderer>().sharedMaterial = material;
                    x -= 1.0f;
                }
                var cam = new GameObject("cam").AddComponent<Camera>();
                cam.transform.SetParent(temp.transform, false);
                cam.transform.localPosition = new Vector3(0f, 0.35f, 3.4f);
                cam.transform.LookAt(temp.transform.position);
                cam.fieldOfView = 34f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.42f, 0.52f, 0.40f);
                var light = new GameObject("key").AddComponent<Light>();
                light.transform.SetParent(temp.transform, false);
                light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(40f, 200f, 0f);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.5f);
                var rt = new RenderTexture(1500, 600, 24) { antiAliasing = 4 };
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1500, 600, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1500, 600), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;
                File.WriteAllBytes(System.IO.Path.Combine(shotsDir, "projectiles_lineup.png"), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(rt);
            }
            finally
            {
                Object.DestroyImmediate(temp);
                ShaderUtil.allowAsyncCompilation = asyncShaders;
            }
        }
    }
}

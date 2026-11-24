using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameDevAgent.ContentTools
{
    /// <summary>
    /// Phase 16: visual scale of the antagonists. Measures the real visible height of the four legends (posed in
    /// their idle, skinned bounds trimmed of stray vertices) and of each enemy's ghost model ("Legend Visual"), then
    /// scales ONLY that visual container so that a normal enemy is ~50 % of the legends' average height (big enemies
    /// keep a moderate size advantage). The enemy root (movement, collider, attacks, stats) is never touched; the
    /// visual's pivot is at its feet, so it stays on the ground. The health bar is raised above the new head.
    /// Run: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.ContentTools.EnemyScaleBuilder.BuildFromCommandLine
    ///      [-gdaMeasureOnly] [-gdaShots &lt;dir&gt; [-gdaShotTag before|after]]
    /// </summary>
    public static class EnemyScaleBuilder
    {
        private const string Tag = "[EnemyScale] ";
        private const string TowerDir = "Assets/Course/Prefabs/Towers/";
        private const string EnemyDir = "Assets/Course/Prefabs/Enemies/";
        public static readonly string[] Towers = { "Cannon Tower", "Slow Tower", "Bomb Tower", "Triple Shot Tower" };
        public static readonly string[] Enemies = { "Alien", "Alien Tall", "Cactus", "Flying Skull", "Big Yeti", "Big Cyclops" };
        public static readonly HashSet<string> BigEnemies = new HashSet<string> { "Big Yeti", "Big Cyclops" };
        public const float NormalRatio = 0.5f;  // normal enemy height / legend reference height
        public const float BigRatio = 0.65f;    // big enemies stay visibly bigger than the normal ones
        public const float HealthBarGap = 0.12f; // health bar just above the head (metres)

        /// <summary>
        /// Heights measured in PLAY mode by the F24 test (-gdaHeightsOut): "reference=H" and "Enemy=height;top".
        /// Edit-mode sampling of the ghost rigs is not reliable (their idle is only evaluated by the Animator), so the
        /// factors always come from the running game.
        /// </summary>
        private static Dictionary<string, float[]> heights;

        public static void BuildFromCommandLine()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            bool measureOnly = args.Contains("-gdaMeasureOnly");
            string shots = null, shotTag = "after", file = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-gdaShots") shots = args[i + 1];
                if (args[i] == "-gdaShotTag") shotTag = args[i + 1];
                if (args[i] == "-gdaHeights") file = args[i + 1];
            }
            if (!measureOnly && (file == null || !File.Exists(file)))
            {
                Debug.LogError(Tag + "Apply needs -gdaHeights <file> written by CourseLevelFlowSmokeTest -gdaFlow enemyscale -gdaHeightsOut <file>");
                EditorApplication.Exit(1);
                return;
            }
            heights = new Dictionary<string, float[]>();
            if (file != null)
                foreach (string line in File.ReadAllLines(file))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    heights[kv[0]] = kv[1].Split(';').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                }
            EditorApplication.Exit(Build(measureOnly, shots, shotTag) ? 0 : 1);
        }

        private static float PosedHeight(GameObject visualRoot, out Bounds bounds)
        {
            Animator a = visualRoot.GetComponentInChildren<Animator>();
            if (a != null && a.runtimeAnimatorController != null)
            {
                AnimationClip idle = a.runtimeAnimatorController.animationClips.FirstOrDefault(c => c.name == LegendPresenter.IdleState)
                    ?? a.runtimeAnimatorController.animationClips.FirstOrDefault();
                if (idle != null) LegendContentBuilder.Pose(a, idle, 0.3f);
            }
            bounds = LegendContentBuilder.MeasureBounds(visualRoot);
            return bounds.size.y;
        }

        public static float LegendReference(List<string> report)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var heights = new List<float>();
            foreach (string t in Towers)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TowerDir + t + ".prefab");
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                Transform visual = inst.transform.Find("Legend Visual");
                float h = visual != null ? PosedHeight(visual.gameObject, out _) : 0f;
                heights.Add(h);
                report.Add("legend " + t + ": " + h.ToString("0.00") + " m");
                Object.DestroyImmediate(inst);
            }
            float reference = heights.Average();
            report.Add("legend reference (average of the four, idle pose): " + reference.ToString("0.00") + " m (min " + heights.Min().ToString("0.00") + ", max " + heights.Max().ToString("0.00") + ")");
            return reference;
        }

        public static bool Build(bool measureOnly, string shots, string shotTag)
        {
            var report = new List<string>();
            try
            {
                float reference = heights != null && heights.ContainsKey("reference") ? heights["reference"][0] : LegendReference(report);
                report.Add("legend reference height (play mode, average of the four legends): " + reference.ToString("0.00") + " m");
                if (!string.IsNullOrEmpty(shots)) Lineup(shots, "lineup_" + shotTag, report);
                foreach (string e in Enemies)
                {
                    string path = EnemyDir + "Enemy (" + e + ").prefab";
                    GameObject root = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        Transform visual = root.transform.Find("Legend Visual");
                        EnemyHealthController health = root.GetComponent<EnemyHealthController>();
                        Collider col = root.GetComponent<Collider>();
                        if (visual == null) { report.Add(e + ": no Legend Visual"); continue; }
                        if (heights == null || !heights.ContainsKey(e)) { report.Add(e + ": no play-mode measurement"); continue; }
                        float h = heights[e][0], top = heights[e].Length > 1 ? heights[e][1] : h;
                        float target = reference * (BigEnemies.Contains(e) ? BigRatio : NormalRatio);
                        float factor = h > 0.001f ? target / h : 1f;
                        Transform bar = health != null && health.healthBar != null ? health.healthBar.GetComponentInParent<Canvas>().rootCanvas.transform : null;
                        float barY = bar != null ? bar.position.y - root.transform.position.y : float.NaN;
                        float pivot = visual.position.y - root.transform.position.y; // the ghost's feet
                        string line = e + ": visual height " + h.ToString("0.00") + " m (" + (h / reference * 100f).ToString("0") + " % of the legends), top "
                            + top.ToString("0.00") + ", visual scale " + visual.localScale.x.ToString("0.000") + ", pivot " + pivot.ToString("0.00")
                            + ", health bar at " + barY.ToString("0.00") + ", collider " + DescribeCollider(col);
                        if (measureOnly) { report.Add(line); continue; }
                        Vector3 basePos = visual.localPosition;
                        if (Mathf.Abs(factor - 1f) > 0.01f) visual.localScale *= factor; // already at the target: no rescale
                        else factor = 1f;
                        visual.localPosition = basePos; // scaled around its pivot (the feet)
                        // The ghost's robe reaches below its pivot: lift the model so its lowest point rests on the ground.
                        float feet = top - h;
                        float newFeet = pivot + (feet - pivot) * factor;
                        float lift = newFeet < -0.02f ? -newFeet + 0.01f : 0f;
                        if (lift > 0f) visual.position += Vector3.up * lift;
                        float newTop = pivot + (top - pivot) * factor + lift;
                        float newBar = barY;
                        if (bar != null && Mathf.Abs(barY - (newTop + HealthBarGap)) > 0.02f)
                        {
                            newBar = newTop + HealthBarGap;
                            bar.position = new Vector3(bar.position.x, root.transform.position.y + newBar, bar.position.z);
                        }
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                        report.Add(line + " -> visual x" + factor.ToString("0.00") + " = " + (h * factor).ToString("0.00") + " m (" + (h * factor / reference * 100f).ToString("0")
                            + " %), lifted " + lift.ToString("0.00") + " m (lowest point " + (newFeet + lift).ToString("0.00") + "), top " + newTop.ToString("0.00") + ", health bar " + barY.ToString("0.00") + " -> " + newBar.ToString("0.00")
                            + ", collider unchanged " + DescribeCollider(col));
                    }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                }
                AssetDatabase.SaveAssets();
                foreach (string l in report) Debug.Log(Tag + l);
                Debug.Log(Tag + "DONE ok");
                return true;
            }
            catch (System.Exception ex)
            {
                foreach (string l in report) Debug.Log(Tag + l);
                Debug.LogError(Tag + "FAILED: " + ex);
                return false;
            }
        }

        private static string DescribeCollider(Collider c)
        {
            if (c is CapsuleCollider cc) return "capsule r" + cc.radius.ToString("0.00") + " h" + cc.height.ToString("0.00") + " c" + cc.center.y.ToString("0.00");
            if (c is SphereCollider sc) return "sphere r" + sc.radius.ToString("0.00");
            if (c is BoxCollider bc) return "box " + bc.size.ToString("F2");
            return c != null ? c.GetType().Name : "none";
        }

        /// <summary>The four legends and the six enemies side by side, real scale, idle pose, same light.</summary>
        private static void Lineup(string dir, string file, List<string> report)
        {
            Directory.CreateDirectory(dir);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var temp = new GameObject("__lineup");
            temp.transform.position = new Vector3(8000f, 0f, 8000f);
            float x = -6.5f;
            foreach (string t in Towers)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(TowerDir + t + ".prefab"), temp.transform);
                go.transform.localPosition = new Vector3(x, 0f, 0f);
                Transform v = go.transform.Find("Legend Visual");
                if (v != null) PosedHeight(v.gameObject, out _);
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>()) if (v == null || !r.transform.IsChildOf(v)) r.enabled = false; // only the legend
                x += 1.5f;
            }
            x += 0.4f;
            foreach (string e in Enemies)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(EnemyDir + "Enemy (" + e + ").prefab"), temp.transform);
                go.transform.localPosition = new Vector3(x, 0f, 0f);
                go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                Transform v = go.transform.Find("Legend Visual");
                if (v != null) PosedHeight(v.gameObject, out _);
                foreach (Canvas c in go.GetComponentsInChildren<Canvas>(true)) c.gameObject.SetActive(false);
                x += 1.25f;
            }
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.SetParent(temp.transform, false);
            ground.transform.localScale = new Vector3(3f, 1f, 1f);
            ground.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = new Color(0.45f, 0.55f, 0.38f) };
            var cam = new GameObject("cam").AddComponent<Camera>();
            cam.transform.SetParent(temp.transform, false);
            cam.transform.localPosition = new Vector3(0.2f, 1.6f, 9.5f);
            cam.transform.LookAt(temp.transform.position + new Vector3(0.2f, 0.9f, 0f));
            cam.fieldOfView = 40f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.70f, 0.58f);
            var light = new GameObject("key").AddComponent<Light>();
            light.transform.SetParent(temp.transform, false);
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(40f, 200f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
            bool asyncShaders = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            foreach (SkinnedMeshRenderer smr in temp.GetComponentsInChildren<SkinnedMeshRenderer>()) smr.updateWhenOffscreen = true;
            var rt = new RenderTexture(1800, 600, 24) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1800, 600, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1800, 600), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            File.WriteAllBytes(System.IO.Path.Combine(dir, file + ".png"), tex.EncodeToPNG()); // the course has a global "Path" class
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(temp);
            ShaderUtil.allowAsyncCompilation = asyncShaders;
            report.Add("lineup written: " + file + ".png");
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameDevAgent.ContentTools
{
    /// <summary>
    /// Phase 13: content of the "Santuario de leyendas".
    ///  - Derived Standard materials for the three appearances of each legend (piedra / bronce / oro). They keep the normal
    ///    map of every original material (the sculpted relief), use a stone texture of the project for mineral variation,
    ///    and keep the alpha cut of cutout materials. The original materials are never modified.
    ///  - Catalog: preview prefab (visual-only Legend_* / LibroSagrado), function label and the three appearances with
    ///    stable ids. Unlock rules, ids and progress are untouched.
    ///  - Re-applies the HUD/menu skin (rebuilds the sanctuary frame) and connects the viewer pedestal.
    /// Run: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.ContentTools.CollectionContentBuilder.BuildFromCommandLine
    /// </summary>
    public static class CollectionContentBuilder
    {
        private const string Tag = "[Sanctuary] ";
        private const string MatDir = LegendContentBuilder.Root + "/Collection/Materials";
        private const string PropTex = LegendContentBuilder.Root + "/Props/Textures/";
        private const string MainMenuScene = "Assets/Course/Scenes/Main Menu.unity";

        private class Mode
        {
            public string id, name, texture;
            public Color color, swatch;
            public float metallic, smoothness, tiling;
        }

        private static readonly Mode[] Modes =
        {
            new Mode { id = "piedra", name = "piedra", texture = "Roca3_Baked_albedo.png", color = new Color(0.86f, 0.84f, 0.80f),
                swatch = new Color(0.62f, 0.60f, 0.56f), metallic = 0f, smoothness = 0.12f, tiling = 3f },
            new Mode { id = "bronce", name = "bronce", texture = "Rocas_Baked_albedo.png", color = new Color(0.78f, 0.50f, 0.28f),
                swatch = new Color(0.72f, 0.45f, 0.24f), metallic = 0.7f, smoothness = 0.48f, tiling = 2f },
            new Mode { id = "oro", name = "oro", texture = null, color = new Color(1f, 0.79f, 0.36f),
                swatch = new Color(0.95f, 0.76f, 0.32f), metallic = 0.88f, smoothness = 0.66f, tiling = 1f },
        };

        private static readonly (LegendId id, string function)[] Legends =
        {
            (LegendId.Mohan, "cañón · ataque individual"),
            (LegendId.Madremonte, "lenta · ralentiza a los enemigos"),
            (LegendId.Hojarasquin, "bomba · daño de área"),
            (LegendId.Patasola, "triple · tres proyectiles"),
        };

        public static void BuildFromCommandLine() => EditorApplication.Exit(Build() ? 0 : 1);

        [MenuItem("Tools/GameDevAgent/Sanctuary content (phase 13)")]
        public static void BuildFromMenu() => Build();

        public static bool Build()
        {
            var report = new List<string>();
            try
            {
                EnsureFolder(MatDir);
                CollectionCatalog catalog = AssetDatabase.LoadAssetAtPath<CollectionCatalog>("Assets/UI/Collection/CollectionCatalog.asset");
                if (catalog == null) throw new System.Exception("CollectionCatalog not found");
                foreach (var legend in Legends)
                {
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LegendContentBuilder.PrefabDir + "/Legend_" + legend.id + ".prefab");
                    CollectionItemDefinition item = catalog.Get(LegendRoster.CollectionId(legend.id));
                    if (prefab == null || item == null) { report.Add("MISSING prefab/item for " + legend.id); continue; }
                    List<Material> originals = prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
                    var appearances = new List<CollectionAppearance>();
                    foreach (Mode mode in Modes)
                    {
                        var replacements = originals.Select(o => Derived(legend.id.ToString(), o, mode)).ToArray();
                        appearances.Add(new CollectionAppearance
                        {
                            id = mode.id, displayName = mode.name, swatch = mode.swatch,
                            originals = originals.ToArray(), replacements = replacements,
                            unlockHint = "se podrá equipar cuando existan las apariencias en partida",
                        });
                    }
                    item.previewPrefab = prefab;
                    item.functionLabel = legend.function;
                    item.appearances = appearances.ToArray();
                    EditorUtility.SetDirty(item);
                    report.Add(item.id + ": preview " + prefab.name + ", " + originals.Count + " original materials x 3 appearances");
                }
                CollectionItemDefinition book = catalog.Get("libro_sagrado");
                if (book != null)
                {
                    book.previewPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SacredBookBuilder.PrefabPath); // phase 20.1: runic book
                    book.functionLabel = "el objetivo que defienden las leyendas";
                    book.appearances = new CollectionAppearance[0];
                    EditorUtility.SetDirty(book);
                }
                AssetDatabase.SaveAssets();

                bool hud = GameDevAgent.UiTools.HudSkinBuilder.Apply(); // rebuilds the sanctuary frame in Main Menu
                report.Add("HUD/menu skin re-applied: " + hud);

                var scene = EditorSceneManager.OpenScene(MainMenuScene, OpenSceneMode.Single);
                CollectionController col = Object.FindAnyObjectByType<CollectionController>(FindObjectsInactive.Include);
                if (col == null || col.viewer == null) throw new System.Exception("sanctuary viewer not found in Main Menu");
                col.viewer.pedestalPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LegendContentBuilder.Root + "/Props/Prefabs/Base.prefab");
                EditorUtility.SetDirty(col.viewer);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                report.Add("viewer pedestal: " + (col.viewer.pedestalPrefab != null ? col.viewer.pedestalPrefab.name : "none"));
                foreach (string line in report) Debug.Log(Tag + line);
                Debug.Log(Tag + "DONE ok");
                return hud;
            }
            catch (System.Exception e)
            {
                foreach (string line in report) Debug.Log(Tag + line);
                Debug.LogError(Tag + "FAILED: " + e);
                return false;
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }

        private static Material Derived(string legend, Material original, Mode mode)
        {
            string safe = System.Text.RegularExpressions.Regex.Replace(original.name, "[^A-Za-z0-9_-]", "_");
            string path = MatDir + "/" + legend + "_" + safe + "_" + mode.id + ".mat";
            Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Standard")); AssetDatabase.CreateAsset(m, path); }
            m.shader = Shader.Find("Standard");
            bool cutout = original.IsKeywordEnabled("_ALPHATEST_ON");
            Texture normal = original.HasProperty("_BumpMap") ? original.GetTexture("_BumpMap") : null;
            Texture albedo = cutout ? original.GetTexture("_MainTex")
                : mode.texture != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(PropTex + mode.texture) : null;
            m.SetTexture("_MainTex", albedo);
            m.SetTextureScale("_MainTex", cutout ? Vector2.one : Vector2.one * mode.tiling);
            m.SetColor("_Color", mode.color);
            m.SetTexture("_BumpMap", normal);
            m.SetFloat("_BumpScale", 1f);
            if (normal != null) m.EnableKeyword("_NORMALMAP"); else m.DisableKeyword("_NORMALMAP");
            m.SetTexture("_MetallicGlossMap", null);
            m.DisableKeyword("_METALLICGLOSSMAP");
            m.SetFloat("_Metallic", mode.metallic);
            m.SetFloat("_Glossiness", mode.smoothness);
            if (cutout)
            {
                m.SetFloat("_Mode", 1f);
                m.SetOverrideTag("RenderType", "TransparentCutout");
                m.SetFloat("_Cutoff", original.GetFloat("_Cutoff"));
                m.EnableKeyword("_ALPHATEST_ON");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
            else
            {
                m.SetFloat("_Mode", 0f);
                m.SetOverrideTag("RenderType", "");
                m.DisableKeyword("_ALPHATEST_ON");
                m.renderQueue = -1;
            }
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}

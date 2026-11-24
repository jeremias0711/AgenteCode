using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameDevAgent.ContentTools
{
    /// <summary>
    /// Phase 20.1: the runic Sacred Book (book floating over a carved stone pedestal) from
    /// "Libro y base animacion.blend", exported by Tools~/export_libro_runico.py to Props/LibroRunico
    /// (decimated meshes with new UVs, normal maps baked from the source, albedo = base colour x baked occlusion).
    /// Builds LibroSagradoRunico.prefab and makes it the Sacred Book of the Castle (visual child only: Castle, colliders,
    /// attack points and health are untouched), of the collection (preview + card portrait).
    /// Batch: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.ContentTools.SacredBookBuilder.BuildFromCommandLine [-gdaYaw 180]
    /// </summary>
    public static class SacredBookBuilder
    {
        private const string Tag = "[SacredBook] ";
        private const string Dir = LegendContentBuilder.Root + "/Props/LibroRunico";
        private const string ModelPath = Dir + "/LibroRunico.fbx";
        public const string PrefabPath = LegendContentBuilder.PrefabDir + "/LibroSagradoRunico.prefab";
        private const string CastlePath = "Assets/Course/Prefabs/Level Essentials/Castle.prefab";
        private const string CatalogItemPath = "Assets/UI/Collection/Items/libro_sagrado.asset";

        public static void BuildFromCommandLine()
        {
            int code = 1;
            try
            {
                float yaw = 0f;
                string[] args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-gdaYaw") float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out yaw);
                Build(yaw);
                code = 0;
            }
            catch (System.Exception e)
            {
                Debug.LogError(Tag + "FAILED: " + e);
            }
            EditorApplication.Exit(code);
        }

        public static void Build(float yaw)
        {
            ConfigureImport();
            Material pedestal = MakeMaterial("MAT_Libro_Pedestal", "Pedestal", 0.12f);
            Material cover = MakeMaterial("MAT_Libro_Cubierta", "Libro", 0.3f);
            GameObject prefab = MakePrefab(pedestal, cover, yaw);
            ApplyToCastle(prefab);
            ApplyToCollection(prefab);
            RenderVariantStones();
            AssetDatabase.SaveAssets();
            Debug.Log(Tag + "DONE");
        }

        private static void ConfigureImport()
        {
            var model = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
            model.materialImportMode = ModelImporterMaterialImportMode.None;
            model.importAnimation = false;
            model.animationType = ModelImporterAnimationType.None;
            model.addCollider = false;
            model.isReadable = false;
            model.meshCompression = ModelImporterMeshCompression.Medium;
            model.importNormals = ModelImporterNormals.Import;
            model.importTangents = ModelImporterTangents.CalculateMikk; // same tangent basis as Blender's baked normal maps
            model.SaveAndReimport();
            foreach (string part in new[] { "Pedestal", "Libro" })
            {
                foreach (string kind in new[] { "Albedo", "Normal" })
                {
                    var t = (TextureImporter)AssetImporter.GetAtPath(Dir + "/" + part + "_" + kind + ".png");
                    t.textureType = kind == "Normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    t.sRGBTexture = kind != "Normal";
                    t.maxTextureSize = 1024;
                    t.mipmapEnabled = true;
                    t.textureCompression = TextureImporterCompression.Compressed;
                    t.SaveAndReimport();
                }
            }
        }

        private static Material MakeMaterial(string name, string part, float gloss)
        {
            string path = Dir + "/" + name + ".mat";
            Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = Shader.Find("Standard");
            m.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "/" + part + "_Albedo.png"));
            m.color = Color.white;
            m.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "/" + part + "_Normal.png"));
            m.SetFloat("_BumpScale", 1.2f);
            m.EnableKeyword("_NORMALMAP");
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Glossiness", gloss);
            EditorUtility.SetDirty(m);
            return m;
        }

        private static GameObject MakePrefab(Material pedestal, Material cover, float yaw)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            var root = new GameObject("LibroSagradoRunico");
            try
            {
                var model = (GameObject)Object.Instantiate(source, root.transform);
                model.name = "Modelo";
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                model.transform.localScale = Vector3.one;
                int tris = 0;
                foreach (MeshRenderer r in model.GetComponentsInChildren<MeshRenderer>(true))
                {
                    bool isBook = r.name.Contains("Libro");
                    r.sharedMaterial = isBook ? cover : pedestal;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    if (isBook && r.GetComponent<SacredBookFloat>() == null) r.gameObject.AddComponent<SacredBookFloat>();
                    MeshFilter f = r.GetComponent<MeshFilter>();
                    if (f != null && f.sharedMesh != null) tris += f.sharedMesh.triangles.Length / 3;
                }
                foreach (Collider c in root.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
                root.AddComponent<CollectionPreviewHints>();
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Bounds b = LegendContentBuilder.MeasureBounds(prefab, false);
                Debug.Log(Tag + "prefab " + PrefabPath + ": " + tris + " tris, size " + b.size.ToString("F2") + ", yaw " + yaw);
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void ApplyToCastle(GameObject book)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(CastlePath);
            try
            {
                Transform old = root.transform.Find("Libro Sagrado");
                if (old != null) Object.DestroyImmediate(old.gameObject);
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(book, root.transform);
                visual.name = "Libro Sagrado";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one * (CampaignScenery.BookDiameter / CampaignScenery.BookModelDiameter);
                PrefabUtility.SaveAsPrefabAsset(root, CastlePath);
                Debug.Log(Tag + "Castle.prefab -> Libro Sagrado = LibroSagradoRunico (Castle, collider, attack points and health untouched)");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // Variant selectors of the collection: the jungle rock (Props/Prefabs/Roca3) in three materials, rendered to
        // UI/Icons/variante_<id>.png (HudSkinBuilder copies UI/Icons into the runtime skin).
        private static void RenderVariantStones()
        {
            GameObject rock = AssetDatabase.LoadAssetAtPath<GameObject>(LegendContentBuilder.Root + "/Props/Prefabs/Roca3.prefab");
            if (rock == null) throw new System.Exception("Roca3.prefab not found");
            Material baseMat = rock.GetComponentInChildren<Renderer>().sharedMaterial;
            var looks = new (string id, Color tint, float metal, float gloss, Color emission)[]
            {
                ("piedra", new Color(0.78f, 0.78f, 0.74f), 0f, 0.12f, Color.black),
                ("bronce", new Color(0.80f, 0.42f, 0.20f), 0.7f, 0.45f, new Color(0.10f, 0.04f, 0.01f)),
                ("oro", new Color(1f, 0.76f, 0.18f), 0.85f, 0.72f, new Color(0.30f, 0.20f, 0.02f)),
            };
            const string tmpDir = Dir + "/_tmp";
            Directory.CreateDirectory(tmpDir);
            try
            {
                foreach (var look in looks)
                {
                    var m = new Material(baseMat);
                    m.color = look.tint;
                    m.SetFloat("_Metallic", look.metal);
                    m.SetFloat("_Glossiness", look.gloss);
                    if (look.emission.maxColorComponent > 0f) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", look.emission); }
                    AssetDatabase.CreateAsset(m, tmpDir + "/" + look.id + ".mat");
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(rock);
                    foreach (Renderer r in go.GetComponentsInChildren<Renderer>()) r.sharedMaterial = m;
                    GameObject tmpPrefab = PrefabUtility.SaveAsPrefabAsset(go, tmpDir + "/" + look.id + ".prefab");
                    Object.DestroyImmediate(go);
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
                    RenderSettings.ambientSkyColor = new Color(0.75f, 0.78f, 0.72f);
                    RenderSettings.ambientEquatorColor = new Color(0.45f, 0.46f, 0.40f);
                    RenderSettings.ambientGroundColor = new Color(0.25f, 0.22f, 0.18f);
                    Texture2D tex = LegendContentBuilder.RenderModel(tmpPrefab, 256, 208, 1.05f, 0.5f, 35f, null, 0f, true, true);
                    string path = "Assets/UI/Icons/variante_" + look.id + ".png";
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                    Object.DestroyImmediate(tex);
                    AssetDatabase.ImportAsset(path);
                    var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                    ti.textureType = TextureImporterType.Sprite;
                    ti.alphaIsTransparency = true;
                    ti.mipmapEnabled = false;
                    ti.textureCompression = TextureImporterCompression.Uncompressed;
                    ti.SaveAndReimport();
                    Debug.Log(Tag + "variant stone " + path);
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(tmpDir); // only this builder's own temporary materials and prefabs
            }
        }

        private static void ApplyToCollection(GameObject book)
        {
            var item = AssetDatabase.LoadAssetAtPath<CollectionItemDefinition>(CatalogItemPath);
            if (item == null) throw new System.Exception("collection item " + CatalogItemPath + " not found");
            item.previewPrefab = book;
            EditorUtility.SetDirty(item);

            // Card portrait: the book fills the card (tighter frame than the full-body legends).
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Texture2D tex = LegendContentBuilder.RenderModel(book, 512, 512, 0.6f, 0.72f, 25f, null, 0f, true, true); // the book itself fills the card
            string path = LegendContentBuilder.PortraitDir + "/portrait_libro_sagrado.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.maxTextureSize = 512;
            ti.SaveAndReimport();
            Debug.Log(Tag + "collection: libro_sagrado preview + portrait from LibroSagradoRunico");
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameDevAgent.ContentTools
{
    /// <summary>
    /// Phase 20.5: El Silbón and El Hombre Caimán in the Collection's "Variantes" page, announced for a future update
    /// (CollectionUnlockRule.ComingSoon: viewable in the sanctuary viewer, never unlocked, selected or playable).
    /// Static posed copies exported by Tools~/export_preview_static.py from the team's sources in 3D_Videojuego/
    /// (the sources have no idle animation: El Silbón in its rest pose, El Hombre Caimán with lowered arms).
    /// Builds visual-only prefabs (no gameplay components), card portraits and the two catalog items.
    /// Batch: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.ContentTools.VariantCharacterBuilder.BuildFromCommandLine
    /// </summary>
    public static class VariantCharacterBuilder
    {
        private const string Tag = "[Variants] ";
        private const string CatalogPath = "Assets/UI/Collection/CollectionCatalog.asset";
        private const string ItemsDir = "Assets/UI/Collection/Items";

        private struct Spec
        {
            public string folder, id, display, subtitle, lore;
        }

        private static readonly Spec[] Specs =
        {
            new Spec
            {
                folder = "Silbon", id = "silbon", display = "el silbón", subtitle = "alma en pena del llano",
                lore = "Espíritu errante de los Llanos Orientales. Recorre los caminos de noche con un costal de huesos al hombro " +
                       "y anuncia su llegada con un silbido que sube y baja. Dicen los llaneros que, cuando el silbido se oye " +
                       "cerca, el Silbón está lejos; y cuando se oye lejos, está muy cerca. Su historia llegará al Santuario " +
                       "en una futura actualización.",
            },
            new Spec
            {
                folder = "HombreCaiman", id = "hombre_caiman", display = "el hombre caimán", subtitle = "leyenda del río magdalena",
                lore = "En las orillas del río Magdalena se cuenta que un pescador usó una pócima para transformarse en caimán y " +
                       "acercarse sin ser visto. El hechizo quedó a medias y desde entonces vaga entre las aguas, mitad hombre " +
                       "y mitad caimán, esperando volver a ser quien era. Su historia llegará al Santuario en una futura " +
                       "actualización.",
            },
        };

        public static void BuildFromCommandLine()
        {
            int code = 1;
            try { Build(); code = 0; }
            catch (System.Exception e) { Debug.LogError(Tag + "FAILED: " + e); }
            EditorApplication.Exit(code);
        }

        public static void Build()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<CollectionCatalog>(CatalogPath);
            if (catalog == null) throw new System.Exception("catalog not found: " + CatalogPath);
            foreach (Spec spec in Specs)
            {
                string dir = LegendContentBuilder.Root + "/Characters/" + spec.folder;
                GameObject prefab = MakePrefab(spec, dir);
                Sprite portrait = RenderPortrait(prefab, spec.folder);
                CollectionItemDefinition item = MakeItem(spec, prefab, portrait);
                catalog = AssetDatabase.LoadAssetAtPath<CollectionCatalog>(CatalogPath); // the portrait scene unloads it
                if (!catalog.items.Contains(item)) catalog.items.Add(item);
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();
            }
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            Debug.Log(Tag + "DONE: catalog " + catalog.items.Count + " items");
        }

        private static GameObject MakePrefab(Spec spec, string dir)
        {
            string fbx = dir + "/" + spec.folder + ".fbx";
            var model = (ModelImporter)AssetImporter.GetAtPath(fbx);
            if (model == null) throw new System.Exception("model not found: " + fbx);
            model.materialImportMode = ModelImporterMaterialImportMode.None;
            model.importAnimation = false;
            model.animationType = ModelImporterAnimationType.None;
            model.addCollider = false;
            model.meshCompression = ModelImporterMeshCompression.Medium;
            model.importTangents = ModelImporterTangents.CalculateMikk;
            model.SaveAndReimport();

            var textures = new Dictionary<string, (Texture2D albedo, Texture2D normal)>();
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { dir + "/Textures" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string file = System.IO.Path.GetFileNameWithoutExtension(path);
                bool isNormal = file.EndsWith("_Normal");
                var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                ti.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = !isNormal;
                ti.maxTextureSize = 1024;
                ti.mipmapEnabled = true;
                ti.textureCompression = TextureImporterCompression.Compressed;
                ti.SaveAndReimport();
                string key = Key(file.Substring(0, file.LastIndexOf('_')).Substring(spec.folder.Length + 1));
                var entry = textures.TryGetValue(key, out var e) ? e : (null, null);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (isNormal) entry.normal = tex; else entry.albedo = tex;
                textures[key] = entry;
            }

            var root = new GameObject("Variant_" + spec.folder);
            try
            {
                var go = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(fbx), root.transform);
                go.name = "Modelo";
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                int tris = 0;
                foreach (MeshRenderer r in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    string key = Key(r.name);
                    var match = textures.FirstOrDefault(t => key.Contains(t.Key) || t.Key.Contains(key));
                    if (match.Value.albedo == null && textures.Count == 1) match = textures.First();
                    r.sharedMaterial = MakeMaterial(dir, spec.folder + "_" + r.name, match.Value.albedo, match.Value.normal);
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    MeshFilter f = r.GetComponent<MeshFilter>();
                    if (f != null && f.sharedMesh != null) tris += f.sharedMesh.triangles.Length / 3;
                }
                // Stand the model up when the export arrives lying down (height along Z), feet on the ground.
                Bounds lb = Measure(root);
                if (lb.size.z > lb.size.y * 1.15f)
                {
                    foreach (float angle in new[] { -90f, 90f })
                    {
                        go.transform.localRotation = Quaternion.Euler(angle, 0f, 0f);
                        lb = Measure(root);
                        if (lb.size.y > lb.size.z) break;
                    }
                    // Face the camera (+Z): the side with the tail goes to the back.
                }
                lb = Measure(root);
                go.transform.localPosition -= new Vector3(lb.center.x, lb.min.y, lb.center.z);
                foreach (Collider c in root.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
                // Framed by its height and radius (the Caimán's tail would otherwise shrink it); no extra pedestal.
                var hints = root.AddComponent<CollectionPreviewHints>();
                hints.ownPedestal = true;
                hints.margin = 1.12f;
                string prefabPath = LegendContentBuilder.PrefabDir + "/Variant_" + spec.folder + ".prefab";
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                Bounds b = LegendContentBuilder.MeasureBounds(prefab, false);
                Debug.Log(Tag + prefabPath + ": " + tris + " tris, size " + b.size.ToString("F2") + ", textures " + textures.Count);
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static Bounds Measure(GameObject root)
        {
            var rs = root.GetComponentsInChildren<Renderer>();
            Bounds b = rs[0].bounds;
            foreach (Renderer r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        private static string Key(string s)
        {
            return new string(s.ToLowerInvariant().Where(char.IsLetter).ToArray()).Replace("silbon", "");
        }

        private static Material MakeMaterial(string dir, string name, Texture2D albedo, Texture2D normal)
        {
            string clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            string path = dir + "/MAT_" + clean + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Standard")); AssetDatabase.CreateAsset(m, path); }
            m.shader = Shader.Find("Standard");
            m.SetTexture("_MainTex", albedo);
            m.color = Color.white;
            if (normal != null) { m.SetTexture("_BumpMap", normal); m.EnableKeyword("_NORMALMAP"); }
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Glossiness", 0.2f);
            EditorUtility.SetDirty(m);
            return m;
        }

        private static Sprite RenderPortrait(GameObject prefab, string folder)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Same framing as the legends' card portraits (full body, front view, transparent background).
            Texture2D tex = LegendContentBuilder.RenderModel(prefab, 512, 512, 1.15f, 0.5f, 0f, null, 0f, true, true);
            string path = LegendContentBuilder.PortraitDir + "/portrait_" + folder + ".png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.maxTextureSize = 512;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static CollectionItemDefinition MakeItem(Spec spec, GameObject prefab, Sprite portrait)
        {
            string path = ItemsDir + "/variante_" + spec.id + ".asset";
            var item = AssetDatabase.LoadAssetAtPath<CollectionItemDefinition>(path);
            if (item == null) { item = ScriptableObject.CreateInstance<CollectionItemDefinition>(); AssetDatabase.CreateAsset(item, path); }
            item.id = spec.id;
            item.displayName = spec.display;
            item.category = CollectionCategory.Variantes;
            item.subtitle = spec.subtitle;
            item.lore = spec.lore;
            item.icon = portrait;
            item.portrait = portrait;
            item.placeholderArt = false;
            item.unlockRule = CollectionUnlockRule.ComingSoon;
            item.unlockLevelId = 0;
            item.unlockHint = "Disponible en una futura actualización.";
            item.selectable = false;
            item.ownerItemId = string.Empty;
            item.contentKey = string.Empty;
            item.previewPrefab = prefab;
            item.functionLabel = "futura incorporación · no jugable todavía";
            item.appearances = new CollectionAppearance[0];
            EditorUtility.SetDirty(item);
            return item;
        }
    }
}

using System;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace GameDevAgent.TowerDefense
{
    /// <summary>
    /// Batch-mode setup for the third-party course project in Assets/Course.
    /// Run: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.TowerDefense.CourseSetup.&lt;Method&gt;
    /// (no -quit: each method calls EditorApplication.Exit itself).
    /// </summary>
    public static class CourseSetup
    {
        private const string Tag = "[CourseSetup] ";
        private const string TagManagerPath = "ProjectSettings/TagManager.asset";
        private const string DynamicsManagerPath = "ProjectSettings/DynamicsManager.asset";
        private const string EnemyTag = "Enemy";
        private const string TmpSettingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";
        private const string TmpPackageRelativePath = "Package Resources/TMP Essential Resources.unitypackage";
        private const double ImportTimeoutSeconds = 120.0;

        private static readonly (int index, string name)[] RequiredLayers =
        {
            (6, "Enemy"), (7, "Ground"), (8, "Tower"), (9, "Castle"), (10, "Path"), (11, "Projectile"),
        };

        private static readonly string[] BuildScenes =
        {
            "Assets/Course/Scenes/Test 4.unity",
            "Assets/Course/Scenes/Test 5.unity",
            "Assets/Scenes/Main.unity",
        };

        private static double importStart;
        private static bool importDone;

        public static void ConfigureTagsAndLayersFromCommandLine()
        {
            try
            {
                var assets = AssetDatabase.LoadAllAssetsAtPath(TagManagerPath);
                if (assets == null || assets.Length == 0)
                    throw new InvalidOperationException("Cannot load " + TagManagerPath);

                var so = new SerializedObject(assets[0]);
                var tags = so.FindProperty("tags");
                var layers = so.FindProperty("layers");
                if (tags == null || layers == null)
                    throw new InvalidOperationException("TagManager has no 'tags'/'layers' properties");

                // Validate every layer first so a conflict leaves the asset untouched.
                foreach (var (index, name) in RequiredLayers)
                {
                    var current = layers.GetArrayElementAtIndex(index).stringValue;
                    if (!string.IsNullOrEmpty(current) && current != name)
                        throw new InvalidOperationException(
                            $"Layer {index} already named '{current}', expected '{name}'. Not overwriting.");
                }

                var changed = false;

                var hasTag = false;
                for (var i = 0; i < tags.arraySize; i++)
                {
                    if (tags.GetArrayElementAtIndex(i).stringValue == EnemyTag) { hasTag = true; break; }
                }
                if (!hasTag)
                {
                    tags.InsertArrayElementAtIndex(tags.arraySize);
                    tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = EnemyTag;
                    changed = true;
                    Debug.Log(Tag + "Added tag '" + EnemyTag + "'.");
                }

                foreach (var (index, name) in RequiredLayers)
                {
                    var element = layers.GetArrayElementAtIndex(index);
                    if (element.stringValue == name) continue;
                    element.stringValue = name;
                    changed = true;
                    Debug.Log(Tag + $"Set layer {index} = '{name}'.");
                }

                if (changed)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.SaveAssets();
                }
                Debug.Log(Tag + (changed ? "Tags and layers configured." : "Tags and layers already configured."));
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "ConfigureTagsAndLayers failed: " + e.Message);
                EditorApplication.Exit(1);
            }
        }

        public static void ImportTmpEssentialsFromCommandLine()
        {
            try
            {
                if (File.Exists(TmpSettingsPath))
                {
                    Debug.Log(Tag + "TMP Essential Resources already present, skipping.");
                    EditorApplication.Exit(0);
                    return;
                }

                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TMPro.TMP_Text).Assembly);
                if (info == null)
                    throw new InvalidOperationException("Cannot resolve the package that contains TextMeshPro.");

                var packagePath = System.IO.Path.Combine(info.resolvedPath, TmpPackageRelativePath);
                if (!File.Exists(packagePath))
                    throw new FileNotFoundException("TMP Essential Resources package not found", packagePath);

                importDone = false;
                importStart = EditorApplication.timeSinceStartup;
                AssetDatabase.importPackageCompleted += OnImportCompleted;
                AssetDatabase.importPackageFailed += OnImportFailed;
                AssetDatabase.importPackageCancelled += OnImportCancelled;
                EditorApplication.update += PollImport;

                Debug.Log(Tag + "Importing " + packagePath);
                AssetDatabase.ImportPackage(packagePath, false);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "ImportTmpEssentials failed: " + e.Message);
                EditorApplication.Exit(1);
            }
        }

        public static void ConfigureBuildSettingsFromCommandLine()
        {
            try
            {
                var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>();
                foreach (var path in BuildScenes)
                {
                    if (!File.Exists(path))
                    {
                        Debug.LogWarning(Tag + "Scene not found, skipping: " + path);
                        continue;
                    }
                    scenes.Add(new EditorBuildSettingsScene(path, true));
                }

                if (scenes.Count == 0)
                    throw new InvalidOperationException("None of the expected scenes exist.");

                EditorBuildSettings.scenes = scenes.ToArray();
                AssetDatabase.SaveAssets();
                for (var i = 0; i < scenes.Count; i++)
                    Debug.Log(Tag + $"Build scene {i}: {scenes[i].path}");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "ConfigureBuildSettings failed: " + e.Message);
                EditorApplication.Exit(1);
            }
        }

        public static void ConfigurePhysicsFromCommandLine()
        {
            try
            {
                const int towerLayer = 8, projectileLayer = 11;

                var assets = AssetDatabase.LoadAllAssetsAtPath(DynamicsManagerPath);
                if (assets == null || assets.Length == 0)
                    throw new InvalidOperationException("Cannot load " + DynamicsManagerPath);

                var so = new SerializedObject(assets[0]);
                var matrix = so.FindProperty("m_LayerCollisionMatrix");
                if (matrix == null)
                    throw new InvalidOperationException("DynamicsManager has no 'm_LayerCollisionMatrix' property");
                Debug.Log(Tag + $"m_LayerCollisionMatrix propertyType={matrix.propertyType} isArray={matrix.isArray} size={(matrix.isArray ? matrix.arraySize : -1)}");

                var before = DescribeMatrix(matrix);
                Debug.Log(Tag + "Matrix before: " + before);

                // Physics.IgnoreLayerCollision writes the bit layout Unity itself uses, so we do not hand-compute bits.
                Physics.IgnoreLayerCollision(towerLayer, projectileLayer, true);
                Physics.IgnoreLayerCollision(projectileLayer, projectileLayer, true);

                so.Update();
                var after = DescribeMatrix(so.FindProperty("m_LayerCollisionMatrix"));
                Debug.Log(Tag + "Matrix after:  " + after);
                Debug.Log(Tag + (before == after
                    ? "Serialized matrix unchanged (already configured, or the change was not reflected)."
                    : "Serialized matrix changed."));

                EditorUtility.SetDirty(assets[0]);
                AssetDatabase.SaveAssets();

                var towerProjectile = Physics.GetIgnoreLayerCollision(towerLayer, projectileLayer);
                var projectileProjectile = Physics.GetIgnoreLayerCollision(projectileLayer, projectileLayer);
                Debug.Log(Tag + $"GetIgnoreLayerCollision Tower-Projectile={towerProjectile} Projectile-Projectile={projectileProjectile}");
                if (!towerProjectile || !projectileProjectile)
                    throw new InvalidOperationException("Layer collision pairs are not ignored after configuration.");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "ConfigurePhysics failed: " + e.Message);
                EditorApplication.Exit(1);
            }
        }

        private static string DescribeMatrix(SerializedProperty matrix)
        {
            if (matrix == null || !matrix.isArray) return "<not an array>";
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < matrix.arraySize; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(matrix.GetArrayElementAtIndex(i).longValue.ToString("X"));
            }
            return sb.ToString();
        }

        private static void PollImport()
        {
            if (importDone) return;
            if (EditorApplication.timeSinceStartup - importStart < ImportTimeoutSeconds) return;
            Debug.LogError(Tag + $"TMP import timed out after {ImportTimeoutSeconds:0} s.");
            Finish(1);
        }

        private static void OnImportCompleted(string packageName)
        {
            Debug.Log(Tag + "Import completed: " + packageName);
            AssetDatabase.SaveAssets();
            Finish(File.Exists(TmpSettingsPath) ? 0 : 1);
        }

        private static void OnImportFailed(string packageName, string errorMessage)
        {
            Debug.LogError(Tag + $"Import failed: {packageName}: {errorMessage}");
            Finish(1);
        }

        private static void OnImportCancelled(string packageName)
        {
            Debug.LogError(Tag + "Import cancelled: " + packageName);
            Finish(1);
        }

        private static void Finish(int exitCode)
        {
            if (importDone) return;
            importDone = true;
            AssetDatabase.importPackageCompleted -= OnImportCompleted;
            AssetDatabase.importPackageFailed -= OnImportFailed;
            AssetDatabase.importPackageCancelled -= OnImportCancelled;
            EditorApplication.update -= PollImport;
            if (exitCode != 0 && !File.Exists(TmpSettingsPath))
                Debug.LogError(Tag + "TMP Settings.asset missing after import.");
            EditorApplication.Exit(exitCode);
        }
    }
}

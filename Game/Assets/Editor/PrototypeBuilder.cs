using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameDevAgent.TowerDefense
{
    /// <summary>Builds the whole prototype scene (map, path, book, enemy prefab, systems) from code.</summary>
    public static class PrototypeBuilder
    {
        private const int GridSize = 14;
        private const float WaypointHeight = 0.6f;
        private const string MaterialsFolder = "Assets/Materials";
        private const string PrefabsFolder = "Assets/Prefabs";
        private const string ScenesFolder = "Assets/Scenes";
        private const string ScenePath = "Assets/Scenes/Main.unity";
        private const string EnemyPrefabPath = "Assets/Prefabs/Enemy.prefab";

        private static readonly Vector2Int[] PathCorners =
        {
            new Vector2Int(0, 3), new Vector2Int(5, 3), new Vector2Int(5, 10),
            new Vector2Int(10, 10), new Vector2Int(10, 3), new Vector2Int(13, 3)
        };

        [MenuItem("GameDevAgent/Build Prototype Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets", "Materials");
            EnsureFolder("Assets", "Prefabs");
            EnsureFolder("Assets", "Scenes");

            Material ground = GetOrCreateMaterial("Ground", new Color(0.30f, 0.62f, 0.25f));
            Material pathMat = GetOrCreateMaterial("Path", new Color(0.86f, 0.76f, 0.52f));
            Material book = GetOrCreateMaterial("Book", new Color(0.4f, 0.05f, 0.05f));
            Material enemyMat = GetOrCreateMaterial("Enemy", new Color(1f, 0.55f, 0.1f));
            Material spawn = GetOrCreateMaterial("Spawn", new Color(0.15f, 0.35f, 0.95f));

            BuildMap(pathMat, ground);
            PathWaypoints path = BuildPath();
            BaseHealth baseHealth = BuildBook(book);
            BuildSpawnMarker(spawn);
            Enemy enemyPrefab = GetOrCreateEnemyPrefab(enemyMat);
            BuildSystems(enemyPrefab, path, baseHealth);
            BuildLightAndCamera();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            PlayerSettings.productName = "GameDevAgent Tower Defense";
            AssetDatabase.SaveAssets();
        }

        public static void BuildFromCommandLine()
        {
            try
            {
                Build();
                Debug.Log("[PrototypeBuilder] OK");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[PrototypeBuilder] " + e);
                EditorApplication.Exit(1);
            }
        }

        // ---- Assets -------------------------------------------------------------------------

        private static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + name))
                AssetDatabase.CreateFolder(parent, name);
        }

        private static Material GetOrCreateMaterial(string name, Color color)
        {
            string assetPath = MaterialsFolder + "/" + name + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null) return existing;

            var material = new Material(Shader.Find("Standard")) { color = color };
            AssetDatabase.CreateAsset(material, assetPath);
            return material;
        }

        private static Enemy GetOrCreateEnemyPrefab(Material enemyMat)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(EnemyPrefabPath);
            if (existing == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "Enemy";
                go.transform.localScale = Vector3.one * 0.6f;
                go.GetComponent<Renderer>().sharedMaterial = enemyMat;
                go.AddComponent<Enemy>();
                PrefabUtility.SaveAsPrefabAsset(go, EnemyPrefabPath);
                UnityEngine.Object.DestroyImmediate(go);
                existing = AssetDatabase.LoadAssetAtPath<GameObject>(EnemyPrefabPath);
            }

            var enemy = existing != null ? existing.GetComponent<Enemy>() : null;
            if (enemy == null)
                throw new InvalidOperationException("Enemy prefab at " + EnemyPrefabPath + " has no Enemy component.");
            return enemy;
        }

        // ---- Scene content ------------------------------------------------------------------

        private static HashSet<Vector2Int> ComputePathCells()
        {
            var cells = new HashSet<Vector2Int>();
            for (int i = 0; i < PathCorners.Length - 1; i++)
            {
                Vector2Int a = PathCorners[i];
                Vector2Int b = PathCorners[i + 1];
                int dx = Math.Sign(b.x - a.x);
                int dz = Math.Sign(b.y - a.y);
                Vector2Int c = a;
                cells.Add(c);
                while (c != b)
                {
                    c = new Vector2Int(c.x + dx, c.y + dz);
                    cells.Add(c);
                }
            }
            return cells;
        }

        private static void BuildMap(Material pathMat, Material groundMat)
        {
            var map = new GameObject("Map");
            HashSet<Vector2Int> pathCells = ComputePathCells();

            for (int x = 0; x < GridSize; x++)
            {
                for (int z = 0; z < GridSize; z++)
                {
                    var tile = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    tile.name = "Tile_" + x + "_" + z;
                    tile.transform.SetParent(map.transform, false);
                    tile.transform.localScale = new Vector3(1f, 0.2f, 1f);
                    tile.transform.position = new Vector3(x, 0f, z);
                    tile.GetComponent<Renderer>().sharedMaterial =
                        pathCells.Contains(new Vector2Int(x, z)) ? pathMat : groundMat;
                }
            }
        }

        private static PathWaypoints BuildPath()
        {
            var pathObject = new GameObject("Path");
            var path = pathObject.AddComponent<PathWaypoints>();
            for (int i = 0; i < PathCorners.Length; i++)
            {
                var wp = new GameObject("WP_" + i.ToString("00"));
                wp.transform.SetParent(pathObject.transform, false);
                wp.transform.position = CellToWorld(PathCorners[i], WaypointHeight);
            }
            return path;
        }

        private static BaseHealth BuildBook(Material bookMat)
        {
            Vector2Int cell = PathCorners[PathCorners.Length - 1];
            var bookObject = new GameObject("Book");
            bookObject.transform.position = CellToWorld(cell, 0f);

            var cover = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cover.name = "Cover";
            cover.transform.SetParent(bookObject.transform, false);
            cover.transform.localScale = new Vector3(0.9f, 0.15f, 0.7f);
            cover.transform.localPosition = new Vector3(0f, 0.18f, 0f);
            cover.GetComponent<Renderer>().sharedMaterial = bookMat;

            var pages = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pages.name = "Pages";
            pages.transform.SetParent(bookObject.transform, false);
            pages.transform.localScale = new Vector3(0.8f, 0.1f, 0.6f);
            pages.transform.localPosition = new Vector3(0f, 0.3f, 0f);
            pages.GetComponent<Renderer>().sharedMaterial = bookMat;

            var health = bookObject.AddComponent<BaseHealth>();
            health.Configure(20);
            return health;
        }

        private static void BuildSpawnMarker(Material spawnMat)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "SpawnPoint";
            marker.transform.localScale = new Vector3(0.4f, 0.3f, 0.4f);
            marker.transform.position = CellToWorld(PathCorners[0], 0.25f);
            marker.GetComponent<Renderer>().sharedMaterial = spawnMat;
        }

        private static void BuildSystems(Enemy enemyPrefab, PathWaypoints path, BaseHealth baseHealth)
        {
            var systems = new GameObject("Systems");

            var spawner = systems.AddComponent<WaveSpawner>();
            var waves = new[]
            {
                new WaveDefinition { enemyCount = 5, spawnInterval = 1.2f, enemySpeed = 2.0f, enemyHealth = 3 },
                new WaveDefinition { enemyCount = 8, spawnInterval = 1.0f, enemySpeed = 2.5f, enemyHealth = 4 },
                new WaveDefinition { enemyCount = 10, spawnInterval = 0.8f, enemySpeed = 3.0f, enemyHealth = 5 }
            };
            spawner.Configure(enemyPrefab, path, baseHealth, waves, 3f, 5f);

            var game = systems.AddComponent<GameManager>();
            game.Configure(baseHealth, spawner);

            var hud = systems.AddComponent<GameHud>();
            hud.Configure(game, baseHealth, spawner);
        }

        private static void BuildLightAndCamera()
        {
            var lightObject = new GameObject("Directional Light");
            var lightComponent = lightObject.AddComponent<Light>();
            lightComponent.type = LightType.Directional;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
            cameraObject.transform.position = new Vector3(6.5f, 14f, -4f);
            cameraObject.transform.rotation = Quaternion.Euler(60f, 0f, 0f);
        }

        private static Vector3 CellToWorld(Vector2Int cell, float y)
        {
            return new Vector3(cell.x, y, cell.y);
        }
    }
}

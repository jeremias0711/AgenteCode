using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameDevAgent.TowerDefense
{
    /// <summary>
    /// Edit-mode self tests (no Test Framework package installed).
    /// Run: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.TowerDefense.SelfTests.RunFromCommandLine
    /// </summary>
    public static class SelfTests
    {
        private const float Dt = 0.1f;
        private const string ScenePath = "Assets/Scenes/Main.unity";

        private sealed class SkipException : Exception
        {
            public SkipException(string message) : base(message) { }
        }

        private static readonly List<GameObject> Created = new List<GameObject>();

        [MenuItem("GameDevAgent/Run Self Tests")]
        public static void RunFromMenu()
        {
            Run();
        }

        public static void RunFromCommandLine()
        {
            int failed = Run();
            EditorApplication.Exit(failed == 0 ? 0 : 1);
        }

        private static int Run()
        {
            var tests = new List<KeyValuePair<string, Action>>
            {
                Entry("BaseHealth", TestBaseHealth),
                Entry("EnemyMovement", TestEnemyMovement),
                Entry("EnemyReachesBase", TestEnemyReachesBase),
                Entry("EnemyKilled", TestEnemyKilled),
                Entry("WaveSpawner", TestWaveSpawner),
                Entry("GameManager", TestGameManager),
                Entry("MainScene", TestMainScene),
            };

            int passed = 0, failed = 0;
            foreach (var test in tests)
            {
                try
                {
                    test.Value();
                    Debug.Log("[SelfTest] PASS " + test.Key);
                    passed++;
                }
                catch (SkipException skip)
                {
                    Debug.Log("[SelfTest] SKIP " + test.Key + ": " + skip.Message);
                }
                catch (Exception e)
                {
                    Debug.Log("[SelfTest] FAIL " + test.Key + ": " + e.Message);
                    failed++;
                }
                finally
                {
                    Cleanup();
                }
            }

            Debug.Log("[SelfTest] RESULT passed=" + passed + " failed=" + failed);
            return failed;
        }

        private static KeyValuePair<string, Action> Entry(string name, Action action)
        {
            return new KeyValuePair<string, Action>(name, action);
        }

        // ---------- helpers ----------

        private static T Make<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            Created.Add(go);
            return go.AddComponent<T>();
        }

        private static void Cleanup()
        {
            foreach (var go in Created)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            Created.Clear();

            // Enemies cloned by the spawner are not tracked in Created.
            foreach (var enemy in UnityEngine.Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None))
                if (enemy != null && enemy.name.StartsWith("SelfTest"))
                    UnityEngine.Object.DestroyImmediate(enemy.gameObject);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(what + ": expected " + expected + " but was " + actual);
        }

        private static PathWaypoints MakePath(params Vector3[] points)
        {
            var path = Make<PathWaypoints>("SelfTestPath");
            for (int i = 0; i < points.Length; i++)
            {
                var child = new GameObject("WP" + i);
                child.transform.SetParent(path.transform, false);
                child.transform.position = points[i];
            }
            return path;
        }

        private static PathWaypoints MakeStandardPath()
        {
            return MakePath(new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 0, 10));
        }

        private static BaseHealth MakeBase(int health)
        {
            var b = Make<BaseHealth>("SelfTestBase");
            b.Configure(health);
            return b;
        }

        private static Enemy MakeEnemy(PathWaypoints path, BaseHealth target, float speed, int health)
        {
            var enemy = Make<Enemy>("SelfTestEnemy");
            enemy.Init(path, target, speed, health);
            return enemy;
        }

        // ---------- tests ----------

        private static void TestBaseHealth()
        {
            var b = MakeBase(5);
            int changed = 0, destroyed = 0, lastCurrent = -1, lastMax = -1;
            b.HealthChanged += (c, m) => { changed++; lastCurrent = c; lastMax = m; };
            b.Destroyed += () => destroyed++;

            Equal(5, b.CurrentHealth, "initial health");
            b.TakeDamage(2);
            Equal(3, b.CurrentHealth, "health after 2 damage");
            Equal(1, changed, "HealthChanged count");
            Equal(3, lastCurrent, "HealthChanged current");
            Equal(5, lastMax, "HealthChanged max");

            b.TakeDamage(0);
            b.TakeDamage(-4);
            Equal(3, b.CurrentHealth, "health after non-positive damage");
            Equal(1, changed, "HealthChanged after non-positive damage");

            b.TakeDamage(100);
            Equal(0, b.CurrentHealth, "health never negative");
            Check(b.IsDead, "IsDead at 0");
            Equal(1, destroyed, "Destroyed count");

            b.TakeDamage(1);
            Equal(0, b.CurrentHealth, "health after extra damage");
            Equal(1, destroyed, "Destroyed count after extra damage");
        }

        private static void TestEnemyMovement()
        {
            var path = MakeStandardPath();
            Check(Mathf.Abs(path.TotalLength - 20f) < 0.001f, "path length should be 20, was " + path.TotalLength);
            var enemy = MakeEnemy(path, MakeBase(10), 5f, 3);

            for (int i = 0; i < 20; i++) enemy.Tick(Dt);
            float x = enemy.transform.position.x;
            Check(Mathf.Abs(x - 10f) <= 0.2f, "after 2s x should be ~10, was " + x);
            Check(Mathf.Abs(enemy.transform.position.z) <= 0.2f, "after 2s z should be ~0");

            bool reached = false;
            enemy.ReachedBase += _ => reached = true;
            for (int i = 0; i < 20; i++) enemy.Tick(Dt);
            Check(reached, "enemy should have reached the end after 4s");
        }

        private static void TestEnemyReachesBase()
        {
            var b = MakeBase(10);
            var enemy = MakeEnemy(MakeStandardPath(), b, 5f, 3);
            int damage = enemy.DamageToBase;
            int reached = 0, died = 0;
            enemy.ReachedBase += _ => reached++;
            enemy.Died += _ => died++;

            for (int i = 0; i < 60; i++)
            {
                if (reached > 0) break; // enemy is destroyed after arriving
                enemy.Tick(Dt);
            }

            Equal(1, reached, "ReachedBase count");
            Equal(0, died, "Died count");
            Equal(10 - damage, b.CurrentHealth, "base health after arrival");
        }

        private static void TestEnemyKilled()
        {
            var b = MakeBase(10);
            var enemy = MakeEnemy(MakeStandardPath(), b, 5f, 3);
            int reached = 0, died = 0;
            enemy.ReachedBase += _ => reached++;
            enemy.Died += _ => died++;

            enemy.TakeDamage(2);
            Equal(0, died, "Died before lethal damage");
            enemy.TakeDamage(5);
            Equal(1, died, "Died count");
            Equal(0, reached, "ReachedBase count");
            Equal(10, b.CurrentHealth, "base health after enemy died");
        }

        private static WaveDefinition Wave(int count)
        {
            return new WaveDefinition { enemyCount = count, spawnInterval = 0.1f, enemySpeed = 0.01f, enemyHealth = 1 };
        }

        private static List<Enemy> LiveEnemies(Enemy template)
        {
            var list = new List<Enemy>();
            foreach (var e in UnityEngine.Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None))
                if (e != null && e != template) list.Add(e);
            return list;
        }

        private static Enemy MakeTemplate()
        {
            var template = Make<Enemy>("SelfTestEnemyPrefab");
            return template;
        }

        private static void TestWaveSpawner()
        {
            var path = MakeStandardPath();
            var b = MakeBase(10);
            var template = MakeTemplate();
            var spawner = Make<WaveSpawner>("SelfTestSpawner");
            spawner.Configure(template, path, b, new[] { Wave(3), Wave(2) }, 0f, 0.1f);

            int started = 0, completed = 0;
            spawner.WaveStarted += _ => started++;
            spawner.AllWavesCompleted += () => completed++;

            int ticks = 0;
            while (!spawner.AllWavesSpawned && ticks++ < 200) spawner.Tick(Dt);

            Check(spawner.AllWavesSpawned, "all waves should be spawned");
            // Rename clones so Cleanup can find them.
            var enemies = LiveEnemies(template);
            foreach (var e in enemies) e.name = "SelfTestEnemyClone";
            Equal(5, enemies.Count, "total spawned");
            Equal(5, spawner.AliveEnemies, "AliveEnemies");
            Equal(2, spawner.CurrentWave, "CurrentWave");
            Equal(2, started, "WaveStarted count");
            Equal(0, completed, "AllWavesCompleted while enemies alive");

            for (int i = 0; i < 10; i++) spawner.Tick(Dt);
            Equal(0, completed, "AllWavesCompleted after more ticks while alive");

            foreach (var e in enemies) e.TakeDamage(999);
            Equal(0, spawner.AliveEnemies, "AliveEnemies after kills");
            Equal(1, completed, "AllWavesCompleted after kills");

            for (int i = 0; i < 10; i++) spawner.Tick(Dt);
            Equal(1, completed, "AllWavesCompleted should fire only once");
        }

        private static void TestGameManager()
        {
            // Lost: base destroyed.
            {
                var b = MakeBase(3);
                var spawner = Make<WaveSpawner>("SelfTestSpawner");
                spawner.Configure(null, MakeStandardPath(), b, new[] { Wave(1) }, 0f, 0.1f);
                var gm = Make<GameManager>("SelfTestGameManager");
                gm.Configure(b, spawner);
                var states = new List<GameState>();
                gm.StateChanged += s => states.Add(s);

                Equal(GameState.Playing, gm.State, "initial state");
                b.TakeDamage(3);
                Equal(GameState.Lost, gm.State, "state after base destroyed");
                for (int i = 0; i < 20; i++) spawner.Tick(Dt);
                Equal(GameState.Lost, gm.State, "state stays Lost");
                Equal(1, states.Count, "StateChanged count (lost)");
                Equal(GameState.Lost, states[0], "StateChanged value (lost)");
            }

            // Won: all waves completed (null prefab -> nothing alive).
            {
                var b = MakeBase(3);
                var spawner = Make<WaveSpawner>("SelfTestSpawner");
                spawner.Configure(null, MakeStandardPath(), b, new[] { Wave(2) }, 0f, 0.1f);
                var gm = Make<GameManager>("SelfTestGameManager");
                gm.Configure(b, spawner);
                var states = new List<GameState>();
                gm.StateChanged += s => states.Add(s);

                int ticks = 0;
                while (gm.State == GameState.Playing && ticks++ < 200) spawner.Tick(Dt);
                Equal(GameState.Won, gm.State, "state after waves completed");
                b.TakeDamage(3);
                for (int i = 0; i < 5; i++) spawner.Tick(Dt);
                Equal(GameState.Won, gm.State, "state stays Won");
                Equal(1, states.Count, "StateChanged count (won)");
                Equal(GameState.Won, states[0], "StateChanged value (won)");
            }
        }

        private static void TestMainScene()
        {
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "Scenes", "Main.unity"));
            if (!File.Exists(full)) throw new SkipException(ScenePath + " does not exist yet");

            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new SkipException("scene change cancelled by user");

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            Equal(1, UnityEngine.Object.FindObjectsByType<BaseHealth>(FindObjectsSortMode.None).Length, "BaseHealth count");
            var spawners = UnityEngine.Object.FindObjectsByType<WaveSpawner>(FindObjectsSortMode.None);
            Equal(1, spawners.Length, "WaveSpawner count");
            Equal(3, spawners[0].TotalWaves, "WaveSpawner.TotalWaves");
            Equal(1, UnityEngine.Object.FindObjectsByType<GameManager>(FindObjectsSortMode.None).Length, "GameManager count");
            Equal(1, UnityEngine.Object.FindObjectsByType<GameHud>(FindObjectsSortMode.None).Length, "GameHud count");

            var paths = UnityEngine.Object.FindObjectsByType<PathWaypoints>(FindObjectsSortMode.None);
            bool pathOk = false;
            foreach (var p in paths) if (p.Count >= 5) pathOk = true;
            Check(pathOk, "no PathWaypoints with Count >= 5");

            bool cameraOk = false;
            foreach (var c in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
                if (c.name == "Main Camera" && c.enabled && c.gameObject.activeInHierarchy) cameraOk = true;
            Check(cameraOk, "no enabled Main Camera");
        }
    }
}

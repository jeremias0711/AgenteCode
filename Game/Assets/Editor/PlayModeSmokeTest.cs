using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameDevAgent.TowerDefense
{
    /// <summary>
    /// Play mode smoke test of the real scene. Survives the play-mode domain reload through SessionState.
    /// Run: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.TowerDefense.PlayModeSmokeTest.RunFromCommandLine
    /// (no -quit: the test calls EditorApplication.Exit itself).
    /// </summary>
    [InitializeOnLoad]
    public static class PlayModeSmokeTest
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";
        private const string ActiveKey = "gda_smoke_active";
        private const string ExitCodeKey = "gda_smoke_exit";
        private const string LaunchTimeKey = "gda_smoke_launch";
        private const float BudgetSeconds = 90f;
        private const float PlayStartTimeoutSeconds = 120f;
        private const float LogInterval = 5f;
        private const float TimeScale = 10f;

        private static bool running;
        private static bool hooked;
        private static float startTime;
        private static float nextLogTime;
        private static int maxAlive;
        private static int maxWave;
        private static int errorCount;
        private static string firstError;

        private static GameManager manager;
        private static WaveSpawner spawner;
        private static BaseHealth baseHealth;

        static PlayModeSmokeTest()
        {
            if (SessionState.GetBool(ActiveKey, false) || SessionState.GetInt(ExitCodeKey, -1) >= 0)
            {
                EditorApplication.update -= OnUpdate;
                EditorApplication.update += OnUpdate;
            }
        }

        [MenuItem("GameDevAgent/Run Play Mode Smoke Test")]
        public static void RunFromMenu()
        {
            RunFromCommandLine();
        }

        public static void RunFromCommandLine()
        {
            SessionState.SetInt(ExitCodeKey, -1);
            SessionState.SetFloat(LaunchTimeKey, (float)EditorApplication.timeSinceStartup);
            try
            {
                EditorSceneManager.OpenScene(ScenePath);
            }
            catch (System.Exception e)
            {
                Debug.Log("[PlaySmoke] FAIL cannot open scene " + ScenePath + ": " + e.Message);
                EditorApplication.Exit(1);
                return;
            }

            SessionState.SetBool(ActiveKey, true);
            EditorApplication.update -= OnUpdate;
            EditorApplication.update += OnUpdate;
            EditorApplication.isPlaying = true;
        }

        private static void OnUpdate()
        {
            int pendingExit = SessionState.GetInt(ExitCodeKey, -1);
            if (pendingExit >= 0)
            {
                // Wait until play mode has fully stopped, then exit.
                if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    EditorApplication.update -= OnUpdate;
                    EditorApplication.Exit(pendingExit);
                }
                return;
            }

            if (!SessionState.GetBool(ActiveKey, false)) return;

            if (!EditorApplication.isPlaying)
            {
                // Still waiting for play mode to start (domain reload in progress).
                float waited = (float)EditorApplication.timeSinceStartup - SessionState.GetFloat(LaunchTimeKey, 0f);
                if (!EditorApplication.isPlayingOrWillChangePlaymode && waited > PlayStartTimeoutSeconds)
                    Finish(1, "play mode did not start");
                return;
            }

            if (!running)
            {
                if (!Begin()) return;
            }

            Poll();
        }

        private static bool Begin()
        {
            manager = Object.FindFirstObjectByType<GameManager>();
            spawner = Object.FindFirstObjectByType<WaveSpawner>();
            baseHealth = Object.FindFirstObjectByType<BaseHealth>();
            if (manager == null || spawner == null || baseHealth == null)
            {
                // Scene objects may not be ready on the first frame; give up only on the timeout.
                float waited = (float)EditorApplication.timeSinceStartup - SessionState.GetFloat(LaunchTimeKey, 0f);
                if (waited > PlayStartTimeoutSeconds)
                    Finish(1, "missing scene objects (GameManager/WaveSpawner/BaseHealth)");
                return false;
            }

            running = true;
            startTime = Time.realtimeSinceStartup;
            nextLogTime = startTime + LogInterval;
            maxAlive = 0;
            maxWave = 0;
            errorCount = 0;
            firstError = null;
            Time.timeScale = TimeScale;
            if (!hooked)
            {
                Application.logMessageReceived += OnLog;
                hooked = true;
            }
            return true;
        }

        private static void Poll()
        {
            float elapsed = Time.realtimeSinceStartup - startTime;
            if (spawner.AliveEnemies > maxAlive) maxAlive = spawner.AliveEnemies;
            if (spawner.CurrentWave > maxWave) maxWave = spawner.CurrentWave;

            if (Time.realtimeSinceStartup >= nextLogTime)
            {
                nextLogTime += LogInterval;
                LogStatus(elapsed);
            }

            if (manager.State != GameState.Playing)
            {
                LogStatus(elapsed);
                Evaluate();
            }
            else if (elapsed >= BudgetSeconds)
            {
                LogStatus(elapsed);
                Finish(1, "game state still Playing after " + BudgetSeconds + "s budget");
            }
        }

        private static void Evaluate()
        {
            var reasons = new StringBuilder();
            if (maxWave < 1) reasons.Append("no wave started; ");
            if (maxAlive < 1) reasons.Append("no enemy spawned; ");
            if (errorCount > 0) reasons.Append(errorCount + " error log(s), first: " + firstError + "; ");

            if (reasons.Length == 0)
            {
                Debug.Log("[PlaySmoke] RESULT ok (final state " + manager.State + ")");
                Finish(0, null);
            }
            else
            {
                Finish(1, reasons.ToString());
            }
        }

        private static void LogStatus(float elapsed)
        {
            Debug.Log("[PlaySmoke] t=" + elapsed.ToString("F1")
                + " state=" + manager.State
                + " wave=" + spawner.CurrentWave + "/" + spawner.TotalWaves
                + " alive=" + spawner.AliveEnemies
                + " baseHP=" + baseHealth.CurrentHealth + "/" + baseHealth.MaxHealth);
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (condition != null && condition.StartsWith("[PlaySmoke]")) return;
            if (IsEditorInternal(stackTrace))
            {
                Debug.Log("[PlaySmoke] IGNORED editor-internal: " + FirstLine(condition));
                return;
            }
            errorCount++;
            if (firstError == null) firstError = condition;
        }

        // Editor/engine-internal noise: Unity's search indexer, or a trace that never touches our code.
        private static bool IsEditorInternal(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return true;
            if (stackTrace.Contains("UnityEditor.Search")) return true;
            return !(stackTrace.Contains("GameDevAgent")
                || stackTrace.Contains("Assembly-CSharp")
                || stackTrace.Contains("Assets/"));
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int end = text.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? text : text.Substring(0, end);
        }

        private static void Finish(int code, string failReason)
        {
            if (failReason != null) Debug.Log("[PlaySmoke] FAIL " + failReason);

            if (hooked)
            {
                Application.logMessageReceived -= OnLog;
                hooked = false;
            }
            running = false;
            Time.timeScale = 1f;
            SessionState.SetBool(ActiveKey, false);
            SessionState.SetInt(ExitCodeKey, code);

            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false; // OnUpdate exits once play mode has stopped
            else
            {
                EditorApplication.update -= OnUpdate;
                EditorApplication.Exit(code);
            }
        }
    }
}

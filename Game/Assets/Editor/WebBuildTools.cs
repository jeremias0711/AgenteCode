using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace GameDevAgent.WebTools
{
    /// <summary>
    /// Web (WebGL/WebAssembly) Development build helpers. Independent of the course classes.
    /// Does not change PlayerSettings; uses the existing configuration and enabled EditorBuildSettings scenes.
    /// Batch: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.WebTools.WebBuildTools.BuildWebDevelopment -gdaOut "C:\path\out"
    /// Restore: ... -executeMethod GameDevAgent.WebTools.WebBuildTools.RestoreStandaloneTarget
    /// (no -quit: each batch entry point calls EditorApplication.Exit itself.)
    /// NOTE: the project has a global class 'Path' (course), so System.IO.Path is always written out in full.
    /// </summary>
    public static class WebBuildTools
    {
        private const string Tag = "[WebBuild] ";

        [MenuItem("GameDevAgent/Web/Build WebGL Development (-gdaOut or prompt)")]
        public static void BuildWebDevelopmentMenu()
        {
            string dir = EditorUtility.SaveFolderPanel("WebGL Development build output (new folder)", "", "WebDev");
            if (string.IsNullOrEmpty(dir)) return;
            Run(dir, exitAfter: false);
        }

        [MenuItem("GameDevAgent/Web/Restore StandaloneWindows64 target")]
        public static void RestoreStandaloneTargetMenu() => Restore(exitAfter: false);

        public static void BuildWebDevelopment()
        {
            string dir = GetArg("-gdaOut");
            if (string.IsNullOrEmpty(dir))
            {
                UnityEngine.Debug.LogError(Tag + "Missing -gdaOut <folder> argument.");
                EditorApplication.Exit(1);
                return;
            }
            Run(dir, exitAfter: true);
        }

        public static void RestoreStandaloneTarget() => Restore(exitAfter: true);

        private static void Run(string outDir, bool exitAfter)
        {
            int code = 1;
            try
            {
                var scenes = new List<string>();
                foreach (var s in EditorBuildSettings.scenes)
                    if (s.enabled) scenes.Add(s.path);
                UnityEngine.Debug.Log(Tag + "Enabled scenes: " + scenes.Count);
                foreach (var s in scenes) UnityEngine.Debug.Log(Tag + "  scene: " + s);
                if (scenes.Count == 0)
                    throw new InvalidOperationException("No enabled scenes in EditorBuildSettings.");

                if (System.IO.Directory.Exists(outDir) && System.IO.Directory.GetFileSystemEntries(outDir).Length > 0)
                    throw new InvalidOperationException("Output folder exists and is not empty (refusing to overwrite): " + outDir);

                if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
                    throw new InvalidOperationException("WebGL build target is not supported (Web Build Support module missing?).");

                var options = new BuildPlayerOptions
                {
                    scenes = scenes.ToArray(),
                    locationPathName = outDir,
                    target = BuildTarget.WebGL,
                    targetGroup = BuildTargetGroup.WebGL,
                    options = BuildOptions.Development,
                };

                var sw = Stopwatch.StartNew();
                UnityEngine.Debug.Log(Tag + "Starting WebGL Development build -> " + outDir);
                BuildReport report = BuildPipeline.BuildPlayer(options);
                sw.Stop();
                BuildSummary sum = report.summary;
                UnityEngine.Debug.Log(Tag + "Result: " + sum.result + " | errors=" + sum.totalErrors +
                    " warnings=" + sum.totalWarnings + " | size=" + sum.totalSize + " bytes | buildTime=" +
                    sum.totalTime + " | wallClock=" + sw.Elapsed);
                code = sum.result == BuildResult.Succeeded ? 0 : 1;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError(Tag + "Exception: " + e);
                code = 1;
            }
            UnityEngine.Debug.Log(Tag + "Exit code " + code);
            if (exitAfter) EditorApplication.Exit(code);
        }

        /// <summary>
        /// Windows (StandaloneWindows64, Mono) Development build, used to check that the Windows player keeps its behaviour
        /// (Phase 1.1). Uses the enabled EditorBuildSettings scenes and the existing PlayerSettings.
        /// Batch: ... -executeMethod GameDevAgent.WebTools.WebBuildTools.BuildWindowsDevelopment -gdaOut "C:\path\out"
        /// </summary>
        public static void BuildWindowsDevelopment()
        {
            int code = 1;
            try
            {
                string dir = GetArg("-gdaOut");
                if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("Missing -gdaOut <folder> argument.");
                if (System.IO.Directory.Exists(dir) && System.IO.Directory.GetFileSystemEntries(dir).Length > 0)
                    throw new InvalidOperationException("Output folder exists and is not empty (refusing to overwrite): " + dir);
                var scenes = new List<string>();
                foreach (var s in EditorBuildSettings.scenes)
                    if (s.enabled) scenes.Add(s.path);
                for (int i = 0; i < scenes.Count; i++) UnityEngine.Debug.Log(Tag + "  scene[" + i + "]: " + scenes[i]);
                var options = new BuildPlayerOptions
                {
                    scenes = scenes.ToArray(),
                    locationPathName = System.IO.Path.Combine(dir, "GameDevAgent.exe"),
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.Development,
                };
                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary sum = report.summary;
                UnityEngine.Debug.Log(Tag + "Windows result: " + sum.result + " | errors=" + sum.totalErrors + " warnings=" +
                    sum.totalWarnings + " | size=" + sum.totalSize + " bytes | buildTime=" + sum.totalTime);
                code = sum.result == BuildResult.Succeeded ? 0 : 1;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError(Tag + "Exception: " + e);
            }
            UnityEngine.Debug.Log(Tag + "Exit code " + code);
            EditorApplication.Exit(code);
        }

        private static void Restore(bool exitAfter)
        {
            int code = 1;
            try
            {
                var sw = Stopwatch.StartNew();
                bool ok = EditorUserBuildSettings.SwitchActiveBuildTarget(NamedBuildTarget.Standalone, BuildTarget.StandaloneWindows64);
                sw.Stop();
                UnityEngine.Debug.Log(Tag + "Switch to StandaloneWindows64: " + ok + " in " + sw.Elapsed +
                    " | active=" + EditorUserBuildSettings.activeBuildTarget);
                code = ok && EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64 ? 0 : 1;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError(Tag + "Exception: " + e);
            }
            UnityEngine.Debug.Log(Tag + "Exit code " + code);
            if (exitAfter) EditorApplication.Exit(code);
        }

        private static string GetArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}

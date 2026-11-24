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

        // ------------------------------------------------------------------ release (public distribution)

        /// <summary>Cloudflare Pages: maximum size of one deployed file.</summary>
        public const long CloudflareMaxFileBytes = 25L * 1024 * 1024;

        /// <summary>
        /// WebGL Release build for public distribution (QR / HTTPS link). IL2CPP + WebAssembly (the only WebGL backend),
        /// no Development flag, same enabled scenes (Main Menu first). For the build only, it sets the distribution
        /// options and restores the project's PlayerSettings afterwards:
        ///  - -gdaHost cloudflare (default): Brotli, no decompression fallback (the host sends Content-Encoding: br via
        ///    the generated _headers file), content-hashed file names (cached for a year, safe to update).
        ///  - -gdaHost itch: Brotli WITH decompression fallback (hosts that cannot set Content-Encoding headers).
        /// Batch: ... -executeMethod GameDevAgent.WebTools.WebBuildTools.BuildWebRelease -gdaOut "C:\path\out" [-gdaHost cloudflare|itch]
        /// </summary>
        public static void BuildWebRelease()
        {
            string dir = GetArg("-gdaOut");
            string host = (GetArg("-gdaHost") ?? "cloudflare").ToLowerInvariant();
            if (string.IsNullOrEmpty(dir))
            {
                UnityEngine.Debug.LogError(Tag + "Missing -gdaOut <folder> argument.");
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(RunRelease(dir, host) ? 0 : 1);
        }

        [MenuItem("GameDevAgent/Web/Build WebGL Release for Cloudflare Pages")]
        public static void BuildWebReleaseMenu()
        {
            string dir = EditorUtility.SaveFolderPanel("WebGL Release build output (new folder)", "", "WebRelease");
            if (!string.IsNullOrEmpty(dir)) RunRelease(dir, "cloudflare");
        }

        private static bool RunRelease(string outDir, string host)
        {
            var compression = PlayerSettings.WebGL.compressionFormat;
            bool fallback = PlayerSettings.WebGL.decompressionFallback;
            bool hashes = PlayerSettings.WebGL.nameFilesAsHashes;
            bool caching = PlayerSettings.WebGL.dataCaching;
            var symbols = PlayerSettings.WebGL.debugSymbolMode;
            var subtarget = EditorUserBuildSettings.webGLBuildSubtarget;
            bool ok = false;
            try
            {
                var scenes = new List<string>();
                foreach (var s in EditorBuildSettings.scenes) if (s.enabled) scenes.Add(s.path);
                if (scenes.Count == 0 || !scenes[0].EndsWith("Main Menu.unity"))
                    throw new InvalidOperationException("Main Menu must be the first enabled scene (found: " + (scenes.Count > 0 ? scenes[0] : "none") + ")");
                if (System.IO.Directory.Exists(outDir) && System.IO.Directory.GetFileSystemEntries(outDir).Length > 0)
                    throw new InvalidOperationException("Output folder exists and is not empty (refusing to overwrite): " + outDir);

                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
                // Fallback on: the loader can decompress in JavaScript, which the chunked game data needs (see
                // SplitLargeData) and which makes the build work on hosts without Content-Encoding control.
                PlayerSettings.WebGL.decompressionFallback = true;
                PlayerSettings.WebGL.nameFilesAsHashes = true;
                PlayerSettings.WebGL.dataCaching = true;
                PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.Off;
                UnityEngine.Debug.Log(Tag + "Release (" + host + "): Brotli, fallback=" + PlayerSettings.WebGL.decompressionFallback + ", hashed names, data caching; scenes " + scenes.Count + " (first " + scenes[0] + ")");

                // 1) Desktop build (DXT textures): code, loader, template and the default data file.
                ok = BuildOnce(scenes, outDir, WebGLTextureSubtarget.DXT);
                // 2) Same project with ASTC textures (native on iOS and Android GPUs): only its data file is kept; the
                //    page picks it when the device supports ASTC (Unity's documented multi-format approach).
                string tmp = outDir.TrimEnd('\\', '/') + "_astc_tmp";
                if (ok)
                {
                    if (System.IO.Directory.Exists(tmp)) throw new InvalidOperationException("Temporary folder already exists: " + tmp);
                    ok = BuildOnce(scenes, tmp, WebGLTextureSubtarget.ASTC);
                }
                if (ok)
                {
                    string astcData = System.IO.Directory.GetFiles(System.IO.Path.Combine(tmp, "Build"), "*.data*")[0];
                    string name = System.IO.Path.GetFileName(astcData);
                    string dest = System.IO.Path.Combine(outDir, "Build", name);
                    if (System.IO.File.Exists(dest)) throw new InvalidOperationException("ASTC data has the same name as the DXT data: " + name);
                    System.IO.File.Copy(astcData, dest);
                    string index = System.IO.Path.Combine(outDir, "index.html");
                    string html = System.IO.File.ReadAllText(index);
                    const string marker = "/*GDA_ASTC_DATA*/\"\"";
                    if (!html.Contains(marker)) throw new InvalidOperationException("index.html has no ASTC data marker (template changed?)");
                    System.IO.File.WriteAllText(index, html.Replace(marker, "\"" + name + "\""));
                    System.IO.Directory.Delete(tmp, true); // temporary folder created by this run
                    UnityEngine.Debug.Log(Tag + "ASTC data added: " + name);
                }
                if (ok) SplitLargeData(outDir);
                if (ok) ok = WriteHostingFiles(outDir, host);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError(Tag + "Exception: " + e);
                ok = false;
            }
            finally
            {
                PlayerSettings.WebGL.compressionFormat = compression;
                PlayerSettings.WebGL.decompressionFallback = fallback;
                PlayerSettings.WebGL.nameFilesAsHashes = hashes;
                PlayerSettings.WebGL.dataCaching = caching;
                PlayerSettings.WebGL.debugSymbolMode = symbols;
                EditorUserBuildSettings.webGLBuildSubtarget = subtarget;
                UnityEngine.Debug.Log(Tag + "PlayerSettings restored (compression " + compression + ", fallback " + fallback + ", hashes " + hashes + ")");
            }
            UnityEngine.Debug.Log(Tag + "Exit code " + (ok ? 0 : 1));
            return ok;
        }

        /// <summary>Size of one game data part (below the 25 MiB Cloudflare Pages limit).</summary>
        public const long DataPartBytes = 24L * 1024 * 1024;

        /// <summary>
        /// Splits every game data file larger than <see cref="DataPartBytes"/> into ".partN" files and removes the whole
        /// file from the distribution. The page downloads the parts in parallel, joins them in memory and gives Unity the
        /// joined (still Brotli-compressed) data; the loader's fallback decompresses it. index.html receives the list.
        /// </summary>
        private static void SplitLargeData(string outDir)
        {
            string build = System.IO.Path.Combine(outDir, "Build");
            var manifest = new List<string>();
            foreach (string file in System.IO.Directory.GetFiles(build, "*.data*"))
            {
                if (file.Contains(".part")) continue;
                long length = new System.IO.FileInfo(file).Length;
                string name = System.IO.Path.GetFileName(file);
                if (length <= DataPartBytes) continue;
                var parts = new List<string>();
                byte[] buffer = new byte[1 << 20];
                using (var input = System.IO.File.OpenRead(file))
                {
                    int partIndex = 0;
                    while (input.Position < length)
                    {
                        string partName = name + ".part" + partIndex++;
                        using (var output = System.IO.File.Create(System.IO.Path.Combine(build, partName)))
                        {
                            long left = Math.Min(DataPartBytes, length - input.Position);
                            while (left > 0)
                            {
                                int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                                if (read <= 0) break;
                                output.Write(buffer, 0, read);
                                left -= read;
                            }
                        }
                        parts.Add(partName);
                    }
                }
                System.IO.File.Delete(file); // replaced by its parts (this build's own output)
                manifest.Add("\"" + name + "\":{\"bytes\":" + length + ",\"parts\":[\"" + string.Join("\",\"", parts) + "\"]}");
                UnityEngine.Debug.Log(Tag + "Split " + name + " (" + (length / 1024) + " KB) into " + parts.Count + " parts");
            }
            string index = System.IO.Path.Combine(outDir, "index.html");
            string html = System.IO.File.ReadAllText(index);
            const string marker = "/*GDA_DATA_PARTS*/null";
            if (!html.Contains(marker)) throw new InvalidOperationException("index.html has no data parts marker (template changed?)");
            System.IO.File.WriteAllText(index, html.Replace(marker, manifest.Count > 0 ? "{" + string.Join(",", manifest) + "}" : "null"));
        }

        private static bool BuildOnce(List<string> scenes, string dir, WebGLTextureSubtarget textures)
        {
            EditorUserBuildSettings.webGLBuildSubtarget = textures;
            var sw = Stopwatch.StartNew();
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = dir,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.None,
            });
            sw.Stop();
            BuildSummary sum = report.summary;
            UnityEngine.Debug.Log(Tag + "Result (" + textures + "): " + sum.result + " | errors=" + sum.totalErrors + " warnings=" + sum.totalWarnings +
                " | buildTime=" + sum.totalTime + " | wallClock=" + sw.Elapsed);
            return sum.result == BuildResult.Succeeded;
        }

        /// <summary>
        /// Host configuration next to index.html, and a size report. Cloudflare Pages reads "_headers": the Brotli files
        /// get Content-Encoding and the right MIME type, hashed build files are cached for a year, index.html is always
        /// revalidated (a new deployment is picked up at once).
        /// </summary>
        private static bool WriteHostingFiles(string outDir, string host)
        {
            long total = 0;
            bool fits = true;
            foreach (string f in System.IO.Directory.GetFiles(outDir, "*", System.IO.SearchOption.AllDirectories))
            {
                long len = new System.IO.FileInfo(f).Length;
                total += len;
                string rel = f.Substring(outDir.Length).TrimStart('\\', '/');
                UnityEngine.Debug.Log(Tag + "  file " + rel + " " + (len / 1024) + " KB");
                if (host == "cloudflare" && len > CloudflareMaxFileBytes) { fits = false; UnityEngine.Debug.LogError(Tag + "  " + rel + " exceeds the 25 MiB Cloudflare Pages file limit"); }
            }
            UnityEngine.Debug.Log(Tag + "Download size (all files): " + (total / (1024 * 1024.0)).ToString("0.0") + " MB");
            if (host != "cloudflare") return true;
            // Code files: decoded natively by the browser (fast, streaming WebAssembly compilation). Data parts: sent as
            // they are (no Content-Encoding), joined by the page and decompressed by the Unity loader.
            string headers =
                "# Cloudflare Pages headers for the Unity WebGL release (generated by WebBuildTools).\n" +
                "/Build/*.wasm.unityweb\n  Content-Encoding: br\n  Content-Type: application/wasm\n" +
                "/Build/*.framework.js.unityweb\n  Content-Encoding: br\n  Content-Type: application/javascript\n" +
                "/Build/*.data.unityweb\n  Content-Encoding: br\n  Content-Type: application/octet-stream\n" +
                "/Build/*.part*\n  Content-Type: application/octet-stream\n" +
                "/Build/*\n  Cache-Control: public, max-age=31536000, immutable\n" +
                "/TemplateData/*\n  Cache-Control: public, max-age=604800\n" +
                "/\n  Cache-Control: no-cache\n" +
                "/index.html\n  Cache-Control: no-cache\n" +
                "/*\n  X-Content-Type-Options: nosniff\n  Referrer-Policy: strict-origin-when-cross-origin\n";
            System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "_headers"), headers);
            UnityEngine.Debug.Log(Tag + "Wrote _headers (Cloudflare Pages)");
            return fits;
        }

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

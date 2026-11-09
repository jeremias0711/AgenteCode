using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace GameDevAgent.WebTools
{
    /// <summary>
    /// Read-only UI capture at several resolutions (Web phase 1.1). Nothing is saved: Canvas changes are in memory only.
    /// Unity (WITHOUT -nographics) -projectPath Game -executeMethod GameDevAgent.WebTools.WebUiInspect.CaptureSizes
    ///   -gdaScene "Test 4" -gdaOutDir "C:/tmp/shots" -gdaSizes "960x600,1280x720" [-gdaShow "Tower Upgrade Panel;Pause Screen"] [-gdaTag before]
    /// The report lists, per size, the screen rectangle of every active Button/TMP_Text of the canvas and flags the ones
    /// that fall (even partly) outside the screen. NOTE: System.IO.Path is written in full (global class 'Path' in the course).
    /// </summary>
    public static class WebUiInspect
    {
        private const string Tag = "[WebUi] ";
        private const string ScenesFolder = "Assets/Course/Scenes/";

        public static void CaptureSizes()
        {
            int code = 1;
            try
            {
                string sceneName = GetArg("-gdaScene");
                string outDir = GetArg("-gdaOutDir");
                string sizesArg = GetArg("-gdaSizes") ?? "1920x1080";
                string show = GetArg("-gdaShow") ?? "";
                string tag = GetArg("-gdaTag") ?? "shot";
                if (string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(outDir))
                    throw new ArgumentException("-gdaScene and -gdaOutDir are required.");
                Directory.CreateDirectory(outDir);

                EditorSceneManager.OpenScene(ScenesFolder + sceneName + ".unity", OpenSceneMode.Single);
                Camera cam = Camera.main;
                if (cam == null) throw new InvalidOperationException("No Camera.main.");

                foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = cam;
                    canvas.planeDistance = 1f;
                }

                if (!string.IsNullOrEmpty(show))
                {
                    foreach (string n in show.Split(';'))
                    {
                        bool found = false;
                        foreach (Transform t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                            if (t.name == n.Trim() && t.GetComponentInParent<Canvas>(true) != null) { t.gameObject.SetActive(true); found = true; }
                        Debug.Log(Tag + "show '" + n + "': " + (found ? "activated (in memory)" : "NOT FOUND"));
                    }
                }

                foreach (string s in sizesArg.Split(','))
                {
                    string[] wh = s.Trim().Split('x');
                    int w = int.Parse(wh[0]), h = int.Parse(wh[1]);
                    Render(cam, w, h, System.IO.Path.Combine(outDir, tag + "_" + sceneName.Replace(' ', '_') + "_" + w + "x" + h + ".png"));
                }
                code = 0;
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "Failed: " + e);
            }
            EditorApplication.Exit(code);
        }

        /// <summary>Read-only dump of the Level Select scene (camera, limits, level buttons).</summary>
        public static void DumpLevelSelect()
        {
            int code = 1;
            try
            {
                EditorSceneManager.OpenScene(ScenesFolder + "Level Select.unity", OpenSceneMode.Single);
                Camera cam = Camera.main;
                var sb = new StringBuilder();
                sb.AppendLine(Tag + "camera '" + cam.name + "' pos=" + cam.transform.position + " rot=" + cam.transform.eulerAngles +
                              " ortho=" + cam.orthographic + " fov=" + cam.fieldOfView + " parent=" + (cam.transform.parent != null ? cam.transform.parent.name : "-"));
                var ctrl = UnityEngine.Object.FindFirstObjectByType<LevelSelectController>();
                sb.AppendLine(Tag + "controller theCam=" + ctrl.theCam.name + " pos=" + ctrl.theCam.position + " speed=" + ctrl.moveSpeed +
                              " min=" + ctrl.minPos.position + " max=" + ctrl.maxPos.position);
                foreach (var b in UnityEngine.Object.FindObjectsByType<LevelSelectButton>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    Collider col = b.GetComponentInChildren<Collider>();
                    sb.AppendLine(Tag + "button '" + b.name + "' levelId=" + b.levelId + " pos=" + b.transform.position +
                                  " collider=" + (col != null ? col.GetType().Name + " " + col.bounds.size : "none") +
                                  " canvasMode=" + (b.GetComponentInParent<Canvas>() != null ? b.GetComponentInParent<Canvas>().renderMode.ToString() : "none"));
                }
                Debug.Log(sb.ToString());
                code = 0;
            }
            catch (Exception e) { Debug.LogError(Tag + "Failed: " + e); }
            EditorApplication.Exit(code);
        }

        private static void Render(Camera cam, int w, int h, string file)
        {
            RenderTexture prevTarget = cam.targetTexture, prevActive = RenderTexture.active;
            RenderTexture rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                // CanvasScaler recomputes in Update(), which never runs inside executeMethod: call its Handle() by reflection.
                MethodInfo handle = typeof(CanvasScaler).GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (CanvasScaler cs in UnityEngine.Object.FindObjectsByType<CanvasScaler>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (handle != null && cs.isActiveAndEnabled) handle.Invoke(cs, null);
                Canvas.ForceUpdateCanvases();
                foreach (CanvasScaler cs in UnityEngine.Object.FindObjectsByType<CanvasScaler>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (handle != null && cs.isActiveAndEnabled) handle.Invoke(cs, null);
                Canvas.ForceUpdateCanvases();

                LevelSelectController ls = UnityEngine.Object.FindFirstObjectByType<LevelSelectController>();
                if (ls != null)
                {
                    // Start() does not run in executeMethod: run the framing the same way Start/Update would.
                    typeof(LevelSelectController).GetMethod("FitCamera", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ls, null);
                    bool fit = (bool)typeof(LevelSelectController).GetField("allFit", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ls);
                    Debug.Log(Tag + "LevelSelect allFit=" + fit + " holder=" + ls.theCam.position + " camLocal=" + cam.transform.localPosition);
                    var sbl = new StringBuilder();
                    foreach (LevelSelectButton b in UnityEngine.Object.FindObjectsByType<LevelSelectButton>(FindObjectsSortMode.None))
                        Describe(cam, b.transform as RectTransform, "LevelButton" + b.levelId, w, h, sbl);
                    Debug.Log(Tag + "LevelSelect buttons at " + w + "x" + h + ": " + sbl);
                }
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(file, tex.EncodeToPNG());
                Debug.Log(Tag + "Wrote " + file);
                Report(cam, w, h);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static void Report(Camera cam, int w, int h)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Tag + "--- REPORT " + w + "x" + h + " (screen px, origin bottom-left) ---");
            foreach (Canvas c in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!c.isRootCanvas || c.renderMode != RenderMode.ScreenSpaceCamera) continue;
                RectTransform crt = c.transform as RectTransform;
                sb.AppendLine(Tag + "canvas '" + c.name + "' size=" + crt.rect.size + " scaleFactor=" + c.scaleFactor);
                foreach (Selectable b in c.GetComponentsInChildren<Selectable>(false))
                    Describe(cam, b.transform as RectTransform, "Button", w, h, sb);
                foreach (TMP_Text t in c.GetComponentsInChildren<TMP_Text>(false))
                    if (t.GetComponentInParent<Selectable>() == null) Describe(cam, t.transform as RectTransform, "Text", w, h, sb);
                foreach (Image im in c.GetComponentsInChildren<Image>(false))
                    if (im.GetComponent<Selectable>() == null && im.transform.parent == c.transform) Describe(cam, im.transform as RectTransform, "Panel", w, h, sb);
            }
            Debug.Log(sb.ToString());
        }

        private static void Describe(Camera cam, RectTransform rt, string kind, int w, int h, StringBuilder sb)
        {
            if (rt == null) return;
            Vector3[] wc = new Vector3[4];
            rt.GetWorldCorners(wc);
            Vector3 a = cam.WorldToScreenPoint(wc[0]), b = cam.WorldToScreenPoint(wc[2]);
            float x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x), y0 = Mathf.Min(a.y, b.y), y1 = Mathf.Max(a.y, b.y);
            bool inside = x0 >= -1 && y0 >= -1 && x1 <= w + 1 && y1 <= h + 1;
            string label = "";
            TMP_Text t = rt.GetComponentInChildren<TMP_Text>();
            if (t != null) label = " \"" + t.text.Replace("\n", " ") + "\"";
            sb.AppendLine(Tag + (inside ? "ok   " : "OUT  ") + kind + " '" + rt.name + "'" + label +
                          " x=[" + x0.ToString("0") + "," + x1.ToString("0") + "] y=[" + y0.ToString("0") + "," + y1.ToString("0") + "]");
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

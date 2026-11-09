using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace GameDevAgent.WebTools
{
    /// <summary>
    /// One-off fix (Web phase 1.1, problem 2): re-anchors the HUD elements of the 'UI Canvas' prefab that were
    /// anchored with offsets computed for a 1920-px-wide canvas (they were clipped at 16:10 and other aspect ratios).
    /// The on-screen rectangle at the 1920x1080 reference is preserved exactly; only the anchors change, so the
    /// layout no longer depends on the canvas width. Idempotent. Batch (no -nographics needed):
    /// Unity -batchmode -projectPath Game -executeMethod GameDevAgent.WebTools.WebUiFix.ApplyFromCommandLine
    /// </summary>
    public static class WebUiFix
    {
        private const string Tag = "[WebUiFix] ";
        private const string PrefabPath = "Assets/Course/Prefabs/Level Essentials/UI Canvas.prefab";

        // name of a direct child of the canvas -> new anchor (min = max = point)
        private static readonly (string name, Vector2 anchor)[] Targets =
        {
            ("Gold Panel", new Vector2(0f, 1f)),            // top-left
            ("Pause Button", new Vector2(1f, 1f)),          // top-right
            ("Tower Upgrade Panel", new Vector2(0.5f, 1f)), // top-center (its buttons are already top-center)
            ("Level Complete", new Vector2(0.5f, 0.5f)),    // centered, fixed size
            ("Level Failed", new Vector2(0.5f, 0.5f)),
        };

        [MenuItem("GameDevAgent/Web/Apply HUD responsive anchors (UI Canvas prefab)")]
        public static void ApplyFromMenu() => Apply();

        public static void ApplyFromCommandLine() => EditorApplication.Exit(Apply() ? 0 : 1);

        private static bool Apply()
        {
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(PrefabPath);
                Vector2 reference = root.GetComponent<CanvasScaler>().referenceResolution;
                Debug.Log(Tag + "reference resolution " + reference + ", match=" + root.GetComponent<CanvasScaler>().matchWidthOrHeight);
                foreach (var (name, anchor) in Targets)
                {
                    Transform t = root.transform.Find(name);
                    if (t == null) throw new InvalidOperationException("Child not found: " + name);
                    ReAnchor((RectTransform)t, anchor, reference);
                }
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log(Tag + "Saved " + PrefabPath);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError(Tag + "Failed: " + e);
                return false;
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // Rect in canvas coordinates (origin bottom-left, canvas of size 'reference') before and after; pivot untouched.
        private static void ReAnchor(RectTransform rt, Vector2 anchor, Vector2 reference)
        {
            Vector2 min = Vector2.Scale(rt.anchorMin, reference) + rt.offsetMin;
            Vector2 max = Vector2.Scale(rt.anchorMax, reference) + rt.offsetMax;
            Vector2 before = min;
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            Vector2 a = Vector2.Scale(anchor, reference);
            rt.offsetMin = min - a;
            rt.offsetMax = max - a;
            Debug.Log(Tag + rt.name + ": rect at reference min=" + before + " max=" + max + " -> anchors " + anchor +
                      " anchoredPosition=" + rt.anchoredPosition + " sizeDelta=" + rt.sizeDelta);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Phase 19: puts the campaign scenery of the running level in place before any gameplay component starts.
/// The scene keeps all its logic (systems, UI, castle, spawner, towers); this component only:
///   1. loads the environment prefab of the level (Resources) and static-batches it;
///   2. creates the enemy routes of the scenery and hands them to the spawner (its spawn point moves to the start);
///   3. moves the Castle (Sacred Book: health, damage, destruction unchanged) to the scenery altar, with its attack
///      points, and puts the book of the scenery under it (it disappears with the Castle, as before);
///   4. applies the camera and the atmosphere of the approved composition.
/// The old visual roots of the scene stay disabled (CampaignSceneryBuilder). Runs before EnemyWaveSpawner.Awake.
/// </summary>
[DefaultExecutionOrder(-1000)]
public class CampaignScenery : MonoBehaviour
{
    [Serializable]
    public class Entry
    {
        public int[] levelIds = new int[0];
        public CampaignSceneryData data;
    }

    public Entry[] entries = new Entry[0];
    [Tooltip("Scenery when no campaign level is running (Nueva partida, tutorial) or no entry matches.")]
    public CampaignSceneryData sceneDefault;
    [Tooltip("Visual roots of the scene before phase 19 (kept disabled).")]
    public GameObject[] legacyRoots = new GameObject[0];

    public static CampaignScenery Current { get; private set; }

    // Phase 20.1: the runic Sacred Book (LibroSagradoRunico.prefab, model units: pedestal 1.90 m wide, book top 2.11 m
    // with its float) is shown with a pedestal BookDiameter metres wide on the altar of every scenery.
    public const float BookDiameter = 2.2f;
    public const float BookModelDiameter = 1.902f;
    public const float BookModelHeight = 2.11f;
    public CampaignSceneryData Active { get; private set; }
    public GameObject Environment { get; private set; }
    public IReadOnlyList<Path> Routes => routes;

    private readonly List<Path> routes = new List<Path>();

    private void Awake()
    {
        Current = this;
        int level = LevelSession.HasActiveLevel ? LevelSession.CurrentLevelId : 0;
        Apply(Pick(level));
        if (Active != null) StartCoroutine(FitWhenHudReady());
    }

    // The HUD is laid out (and scaled for phones by MobileHudAdapter) during the first frame.
    private System.Collections.IEnumerator FitWhenHudReady()
    {
        yield return null;
        Canvas.ForceUpdateCanvases();
        Camera cam = Camera.main;
        if (cam != null) FitCameraToHud(cam, HudViewportRects(cam));
        CameraFitted = true;
    }

    /// <summary>True once the HUD-aware camera framing of this scene has been applied (one frame after Awake).</summary>
    public bool CameraFitted { get; private set; }

    /// <summary>Viewport rects of the corner HUD (guardian cards, top pills, pause) for the given camera.</summary>
    public static List<Rect> HudViewportRects(Camera cam)
    {
        var rects = new List<Rect>();
        UIController ui = UIController.instance != null ? UIController.instance : FindAnyObjectByType<UIController>();
        if (ui == null) return rects;
        var parts = new List<RectTransform>();
        if (ui.towerButtons != null) parts.Add(ui.towerButtons.transform as RectTransform);
        foreach (string n in new[] { "Wave Status", "Book Integrity", "Gold Panel", "Pause Button" })
        {
            Transform t = ui.transform.Find(n);
            if (t is RectTransform r && t.gameObject.activeInHierarchy) parts.Add(r);
        }
        var corners = new Vector3[4];
        foreach (RectTransform r in parts)
        {
            if (r == null) continue;
            Canvas canvas = r.GetComponentInParent<Canvas>();
            if (canvas == null) continue;
            canvas = canvas.rootCanvas;
            r.GetWorldCorners(corners);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            foreach (Vector3 c in corners)
            {
                Vector2 v = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? new Vector2(c.x / Mathf.Max(1, Screen.width), c.y / Mathf.Max(1, Screen.height))
                    : (Vector2)cam.WorldToViewportPoint(c);
                min = Vector2.Min(min, v);
                max = Vector2.Max(max, v);
            }
            rects.Add(Rect.MinMaxRect(min.x, min.y, max.x, max.y));
        }
        return rects;
    }

    /// <summary>
    /// Keeps the approved composition but makes sure the corner HUD never hides the Sacred Book nor the roads: tries
    /// small pans (±20 % of the view) and zooms (0.75x-1.2x of the package size) and keeps the closest framing (bigger
    /// characters) with the fewest road samples under the HUD or off screen. The first metres of each route may stay
    /// off screen (enemies walk in from outside the view).
    /// </summary>
    public void FitCameraToHud(Camera cam, List<Rect> avoid)
    {
        CampaignSceneryData data = Active;
        if (data == null || cam == null || !data.orthographic) return;
        var samples = new List<Vector3>();
        foreach (CampaignSceneryData.Route r in data.routes)
        {
            if (r == null || r.points == null) continue;
            float walked = 0f;
            for (int i = 1; i < r.points.Length; i++)
            {
                Vector3 a = r.points[i - 1], b = r.points[i];
                float len = Vector3.Distance(a, b);
                for (float t = 0f; t < len; t += 1f)
                {
                    if (walked + t > 6f) samples.Add(Vector3.Lerp(a, b, t / len) + Vector3.up * 0.4f);
                }
                walked += len;
            }
        }
        // The Book and its altar (a 2.5 m ring around it) must be fully on screen and clear of the HUD.
        var bookRing = new List<Vector3> { data.castlePosition + Vector3.up * 0.8f };
        for (int a = 0; a < 8; a++)
        {
            bookRing.Add(data.castlePosition + Quaternion.Euler(0f, a * 45f, 0f) * Vector3.forward * 2.5f);
        }
        Vector3 basePos = data.cameraPosition;
        float baseSize = data.orthographicSize;
        Vector3 right = data.cameraRotation * Vector3.right, up = data.cameraRotation * Vector3.up;
        float aspect = cam.aspect;

        float bestScore = float.MaxValue, bestZoom = 1f;
        Vector2 bestPan = Vector2.zero;
        for (float zoom = 0.75f; zoom <= 1.201f; zoom += 0.05f)
        {
            for (float px = -0.2f; px <= 0.201f; px += 0.04f)
            {
                for (float py = -0.2f; py <= 0.201f; py += 0.04f)
                {
                    float size = baseSize * zoom;
                    cam.orthographicSize = size;
                    cam.transform.position = basePos + right * (px * size * 2f * aspect) + up * (py * size * 2f);
                    int bad = 0;
                    foreach (Vector3 s in samples) if (Hidden(cam, s, avoid, 0.01f)) bad++;
                    int bookHidden = 0;
                    foreach (Vector3 b in bookRing) if (Hidden(cam, b, avoid, 0.03f)) bookHidden++;
                    float score = bad * 10f + bookHidden * 1000f + zoom * 25f + (Mathf.Abs(px) + Mathf.Abs(py)) * 20f;
                    if (score < bestScore) { bestScore = score; bestZoom = zoom; bestPan = new Vector2(px, py); }
                }
            }
        }
        float finalSize = baseSize * bestZoom;
        cam.orthographicSize = finalSize;
        cam.transform.position = basePos + right * (bestPan.x * finalSize * 2f * aspect) + up * (bestPan.y * finalSize * 2f);
        LastFit = "zoom " + bestZoom.ToString("0.00") + ", pan " + bestPan.ToString("0.00") + ", score " + bestScore.ToString("0");
    }

    public string LastFit { get; private set; }

    private static bool Hidden(Camera cam, Vector3 world, List<Rect> avoid, float margin)
    {
        Vector3 v = cam.WorldToViewportPoint(world);
        if (v.x < margin || v.x > 1f - margin || v.y < margin || v.y > 1f - margin) return true;
        if (avoid != null)
        {
            foreach (Rect r in avoid)
            {
                Rect grown = new Rect(r.xMin - margin, r.yMin - margin, r.width + 2f * margin, r.height + 2f * margin);
                if (grown.Contains(new Vector2(v.x, v.y))) return true;
            }
        }
        return false;
    }

    private void OnDestroy()
    {
        if (Current == this) Current = null;
    }

    /// <summary>True in phone / tablet browsers: the PC compositions (800-900k triangles) give way to their Móvil variant.</summary>
    public static bool UseMobileVariants => Application.isMobilePlatform;

    public CampaignSceneryData Pick(int levelId)
    {
        CampaignSceneryData chosen = sceneDefault;
        if (levelId > 0 && entries != null)
        {
            foreach (Entry e in entries)
            {
                if (e != null && e.data != null && e.levelIds != null && Array.IndexOf(e.levelIds, levelId) >= 0) { chosen = e.data; break; }
            }
        }
        if (chosen != null && UseMobileVariants && chosen.mobileVariant != null) chosen = chosen.mobileVariant;
        return chosen;
    }

    /// <summary>Puts the scenery in place (also used by the editor tools for captures; nothing is saved).</summary>
    public void Apply(CampaignSceneryData data)
    {
        if (data == null) return;
        Active = data;
        foreach (GameObject legacy in legacyRoots) if (legacy != null) legacy.SetActive(false);

        // 1. Environment.
        GameObject prefab = Resources.Load<GameObject>(data.prefabResource);
        if (prefab == null)
        {
            Debug.LogError("[CampaignScenery] Missing environment prefab Resources/" + data.prefabResource);
            return;
        }
        Environment = Instantiate(prefab, Vector3.zero, Quaternion.identity);
        Environment.name = prefab.name;

        // 2. Routes.
        routes.Clear();
        var routeRoot = new GameObject("Rutas del escenario").transform;
        for (int r = 0; r < data.routes.Length; r++)
        {
            CampaignSceneryData.Route route = data.routes[r];
            if (route == null || route.points == null || route.points.Length < 2) continue;
            var go = new GameObject(string.IsNullOrEmpty(route.name) ? "Ruta " + r : route.name);
            go.transform.SetParent(routeRoot, false);
            Path path = go.AddComponent<Path>();
            path.points = new Transform[route.points.Length];
            for (int i = 0; i < route.points.Length; i++)
            {
                var p = new GameObject("Punto " + i.ToString("000")).transform;
                p.SetParent(go.transform, false);
                p.position = route.points[i];
                path.points[i] = p;
            }
            routes.Add(path);
        }

        Castle castle = FindAnyObjectByType<Castle>(FindObjectsInactive.Include);
        if (routes.Count > 0)
        {
            Path main = routes[0];
            var extra = routes.GetRange(1, routes.Count - 1).ToArray();
            foreach (EnemyWaveSpawner s in FindObjectsByType<EnemyWaveSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                s.thePath = main;
                s.extraPaths = extra;
                if (s.spawnPoint != null) s.spawnPoint.position = main.points[0].position;
                if (castle != null) s.theCastle = castle;
            }
            foreach (SimpleEnemySpawner s in FindObjectsByType<SimpleEnemySpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                s.thePath = main;
                if (s.spawnPoint != null) s.spawnPoint.position = main.points[0].position;
                if (castle != null) s.theCastle = castle;
            }
        }

        // 3. Sacred Book.
        if (castle != null)
        {
            castle.transform.SetPositionAndRotation(data.castlePosition, data.castleRotation);
            Transform sacredBook = null;
            foreach (Transform child in castle.transform)
            {
                // Phase 20.1: the Castle's "Libro Sagrado" visual (the runic book on its pedestal) is THE book of every
                // scenery; the old course model stays hidden.
                if (child.name == "Model") child.gameObject.SetActive(false);
                if (child.name == "Libro Sagrado") sacredBook = child;
            }
            if (data.attackPoints != null && data.attackPoints.Length > 0)
            {
                var holder = new GameObject("Attack Points (escenario)").transform;
                holder.SetParent(castle.transform, false);
                var points = new Transform[data.attackPoints.Length];
                for (int i = 0; i < points.Length; i++)
                {
                    points[i] = new GameObject("Attack Point " + i).transform;
                    points[i].SetParent(holder, false);
                    points[i].position = data.attackPoints[i];
                }
                castle.attackPoints = points;
            }
            // The package's own book and lectern give way to the runic book: it stands where the lectern stood.
            float groundY = data.castlePosition.y;
            bool foundGround = false;
            if (data.bookObjects != null)
            {
                foreach (string n in data.bookObjects)
                {
                    Transform book = FindDeep(Environment.transform, n);
                    if (book == null) continue;
                    foreach (Renderer r in book.GetComponentsInChildren<Renderer>())
                    {
                        groundY = foundGround ? Mathf.Min(groundY, r.bounds.min.y) : r.bounds.min.y;
                        foundGround = true;
                    }
                    book.SetParent(castle.transform, true);
                    book.gameObject.SetActive(false);
                }
            }
            if (sacredBook != null)
            {
                sacredBook.gameObject.SetActive(true);
                Vector3 p = castle.transform.position;
                sacredBook.position = new Vector3(p.x, groundY, p.z);
                // The cover (+Z of the model) faces the level camera.
                Vector3 toCam = Vector3.ProjectOnPlane(data.cameraRotation * Vector3.back, Vector3.up);
                sacredBook.rotation = toCam.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(toCam.normalized, Vector3.up) : castle.transform.rotation;
                float bookScale = BookDiameter / BookModelDiameter;
                sacredBook.localScale = Vector3.one * (bookScale / Mathf.Max(0.0001f, castle.transform.lossyScale.x));
                // The integrity bar floats over the book.
                if (castle.healthSlider != null)
                {
                    Canvas barCanvas = castle.healthSlider.GetComponentInParent<Canvas>();
                    if (barCanvas != null && barCanvas.transform != castle.transform)
                    {
                        Vector3 bp = barCanvas.transform.position;
                        barCanvas.transform.position = new Vector3(bp.x, groundY + bookScale * BookModelHeight + 0.3f, bp.z);
                    }
                }
            }
        }

        // Static batching of the environment (the book, now under the Castle, stays a separate object).
        if (Application.isPlaying) StaticBatchingUtility.Combine(Environment);

        // 4. Camera and atmosphere.
        Camera cam = Camera.main;
        Debug.Log("[CampaignScenery] " + data.id + ": environment " + Environment.name + ", " + routes.Count + " route(s), castle "
            + (castle != null ? castle.transform.position.ToString("0.0") : "none") + ", camera " + (cam != null ? cam.name : "none"));
        if (cam != null)
        {
            cam.transform.SetPositionAndRotation(data.cameraPosition, data.cameraRotation);
            cam.orthographic = data.orthographic;
            cam.orthographicSize = data.orthographicSize;
            cam.fieldOfView = data.fieldOfView;
            cam.nearClipPlane = data.nearClip;
            cam.farClipPlane = data.farClip;
            cam.clearFlags = data.clearFlags;
            cam.backgroundColor = data.background;
        }
        RenderSettings.ambientMode = data.ambientMode;
        RenderSettings.ambientSkyColor = data.ambientSky;
        RenderSettings.ambientEquatorColor = data.ambientEquator;
        RenderSettings.ambientGroundColor = data.ambientGround;
        RenderSettings.ambientLight = data.ambientLight;
        RenderSettings.ambientIntensity = data.ambientIntensity;
        RenderSettings.fog = data.fog;
        RenderSettings.fogMode = data.fogMode;
        RenderSettings.fogColor = data.fogColor;
        RenderSettings.fogDensity = data.fogDensity;
        RenderSettings.fogStartDistance = data.fogStart;
        RenderSettings.fogEndDistance = data.fogEnd;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == name) return t;
        }
        return null;
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using static UiKit;

namespace GameDevAgent.UiTools
{
    /// <summary>
    /// Applies the "v2 · Bosque" design (Figma "Towers of Defencopolis — UI/UX", page 11:172, frame P4 "HUD en partida")
    /// to the in-game HUD of the 'UI Canvas' prefab: gold pill, wave indicator, pause button, wave banner, tower tray
    /// with Normal / Elegido / Sin oro cards and the contextual placement hint.
    /// It also generates the 9-slice panel sprites (procedural, from the Figma tokens) and the TextMeshPro font assets
    /// (Fredoka, MedievalSharp; SIL OFL 1.1). Icons and ornaments are PNG exports of the Figma file (Assets/UI).
    /// Idempotent: running it again reuses what it created. Other screens (pause, end screens, upgrade panel) are not
    /// touched. Batch:
    /// Unity -batchmode -projectPath Game -executeMethod GameDevAgent.UiTools.HudSkinBuilder.ApplyFromCommandLine -quit
    /// </summary>
    public static class HudSkinBuilder
    {
        private const string Tag = "[HudSkin] ";
        private const string PrefabPath = "Assets/Course/Prefabs/Level Essentials/UI Canvas.prefab";
        private const string SpriteDir = "Assets/UI/Sprites";
        private const string FontDir = "Assets/UI/Fonts";
        private const string IconDir = "Assets/UI/Icons";
        private const string OrnamentDir = "Assets/UI/Ornaments";

        private const float U = HudTheme.UnitsPerFigmaPx; // canvas units per Figma px
        private const int S = 4;                          // texture pixels per Figma px (all PNGs are 4x)
        private const float SpritePpu = S / U * 100f;     // 200: 1 Figma px of texture = 2 canvas units

        private const string FontChars =
            " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~" +
            "¡¿·áéíóúüñÁÉÍÓÚÜÑ«»—–…“”‘’ºª°";

        private const string AncestralesDir = "Assets/UI/Ancestrales";

        private static TMP_FontAsset fontBold, fontSemi, fontTitle, fontBody, fontLogo;
        private static readonly List<UiSkin.ShadowMaterial> shadowMaterials = new List<UiSkin.ShadowMaterial>();
        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

        /// <summary>
        /// Read-only capture of the screens built at runtime (tutorial hub, first-run prompt, lesson panel). Nothing is
        /// saved. Unity (no -nographics) -executeMethod GameDevAgent.UiTools.HudSkinBuilder.CaptureTutorialFromCommandLine
        ///   -gdaOutDir "C:/tmp/shots"
        /// </summary>
        public static void CaptureTutorialFromCommandLine()
        {
            int code = 1;
            try
            {
                string outDir = null;
                string[] args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-gdaOutDir") outDir = args[i + 1];
                if (string.IsNullOrEmpty(outDir)) throw new System.ArgumentException("-gdaOutDir is required");
                Directory.CreateDirectory(outDir);

                foreach (string what in new[] { "hub", "prompt", "lesson" })
                {
                    string scene = what == "lesson" ? "Assets/Course/Scenes/Test 4.unity" : MainMenuScenePath;
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scene, UnityEditor.SceneManagement.OpenSceneMode.Single);
                    if (what == "lesson")
                    {
                        TutorialUI ui = new GameObject("TutorialUI").AddComponent<TutorialUI>();
                        ui.BuildForPreview();
                        ui.SetLesson("Tutorial inicial", 1, 6);
                        List<TutorialStep> steps = TutorialLessons.Build(TutorialLessonId.Intro);
                        TutorialStep step = steps[Mathf.Min(1, steps.Count - 1)];
                        ui.SetStep(step.title, step.body, 1, steps.Count);
                        ui.SetObjective(step.objective, false);
                        ui.SetContinue(true, "Saltar paso");
                    }
                    else
                    {
                        TutorialMenu menu = new GameObject("TutorialMenu").AddComponent<TutorialMenu>();
                        menu.BuildForPreview(what == "hub");
                    }
                    RenderOverlays(System.IO.Path.Combine(outDir, "tutorial_" + what + ".png"), 1688, 780);
                }
                code = 0;
            }
            catch (System.Exception e)
            {
                Debug.LogError(Tag + "Capture failed: " + e);
            }
            EditorApplication.Exit(code);
        }

        /// <summary>Phase 12.1: HUD of Test 4 with the placement hint and "cancelar" shown, at the five target resolutions.</summary>
        public static void CapturePlacementHintFromCommandLine()
        {
            int code = 1;
            try
            {
                string outDir = null;
                string[] args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-gdaOutDir") outDir = args[i + 1];
                if (string.IsNullOrEmpty(outDir)) throw new System.ArgumentException("-gdaOutDir is required");
                Directory.CreateDirectory(outDir);
                foreach (Vector2Int size in new[] { new Vector2Int(1920, 1080), new Vector2Int(1280, 720), new Vector2Int(960, 540), new Vector2Int(844, 390), new Vector2Int(800, 360) })
                {
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Course/Scenes/Test 4.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
                    PlacementHintView view = Object.FindAnyObjectByType<PlacementHintView>(FindObjectsInactive.Include);
                    if (view != null)
                    {
                        view.panel.SetActive(true);
                        view.message.text = "toca una casilla para invocar a la madremonte";
                        if (view.cancelButton != null) view.cancelButton.SetActive(true);
                    }
                    RenderOverlays(System.IO.Path.Combine(outDir, "hint_" + size.x + "x" + size.y + ".png"), size.x, size.y);
                }
                code = 0;
            }
            catch (System.Exception e)
            {
                Debug.LogError(Tag + "Capture failed: " + e);
            }
            EditorApplication.Exit(code);
        }

        /// <summary>HUD of each scene in -gdaScenes "Test 1,Test 2" (edit mode, as saved) at -gdaSize WxH (default 1920x1080).</summary>
        public static void CaptureScenesHudFromCommandLine()
        {
            int code = 1;
            try
            {
                string outDir = null, scenes = "Test 1,Test 2,Test 3,Test 4", size = "1920x1080";
                string[] args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "-gdaOutDir") outDir = args[i + 1];
                    if (args[i] == "-gdaScenes") scenes = args[i + 1];
                    if (args[i] == "-gdaSize") size = args[i + 1];
                }
                if (string.IsNullOrEmpty(outDir)) throw new System.ArgumentException("-gdaOutDir is required");
                Directory.CreateDirectory(outDir);
                foreach (string sz in size.Split(','))
                {
                    string[] wh = sz.Split('x');
                    int w = int.Parse(wh[0]), h = int.Parse(wh[1]);
                    foreach (string scene in scenes.Split(','))
                    {
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Course/Scenes/" + scene.Trim() + ".unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
                        RenderOverlays(System.IO.Path.Combine(outDir, "hud_" + scene.Trim().Replace(" ", "") + "_" + w + "x" + h + ".png"), w, h);
                    }
                }
                code = 0;
            }
            catch (System.Exception e)
            {
                Debug.LogError(Tag + "Capture failed: " + e);
            }
            EditorApplication.Exit(code);
        }

        /// <summary>
        /// Phase 18: read-only captures of every migrated screen (nothing is saved): Main Menu, Collection, Level Select
        /// and, on Test 4, the HUD, placement, pause, pause confirm, upgrade panel, victory and defeat. -gdaOutDir dir
        /// [-gdaSize 1688x780,1920x1080,...]. Edit mode: the screens are shown by activating their objects.
        /// </summary>
        public static void CaptureAncestralesFromCommandLine()
        {
            int code = 1;
            try
            {
                string outDir = null, size = "1688x780";
                string[] args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "-gdaOutDir") outDir = args[i + 1];
                    if (args[i] == "-gdaSize") size = args[i + 1];
                }
                if (string.IsNullOrEmpty(outDir)) throw new System.ArgumentException("-gdaOutDir is required");
                Directory.CreateDirectory(outDir);
                var shots = new (string name, string scene, System.Action setup)[]
                {
                    ("menu", MainMenuScenePath, null),
                    ("coleccion", MainMenuScenePath, () =>
                    {
                        CollectionController c = Object.FindAnyObjectByType<CollectionController>(FindObjectsInactive.Include);
                        foreach (GameObject go in c.hideWhileOpen) if (go != null) go.SetActive(false);
                        c.gameObject.SetActive(true);
                        c.BuildPreview();
                    }),
                    ("niveles", LevelSelectScenePath, null),
                    ("hud", "Assets/Course/Scenes/Test 4.unity", () =>
                    {
                        Transform canvas = Object.FindAnyObjectByType<UIController>(FindObjectsInactive.Include).transform;
                        Transform banner = FindDeep(canvas, "Wave Banner");
                        if (banner != null) banner.gameObject.SetActive(true);
                        Transform wave = FindDeep(canvas, "Wave Text");
                        if (wave != null) wave.gameObject.SetActive(true);
                    }),
                    ("colocacion", "Assets/Course/Scenes/Test 4.unity", () =>
                    {
                        PlacementHintView v = Object.FindAnyObjectByType<PlacementHintView>(FindObjectsInactive.Include);
                        v.panel.SetActive(true);
                        v.message.text = "toca una casilla para invocar a la madremonte";
                        v.cancelButton.SetActive(true);
                        TowerCardView card = Object.FindObjectsByType<TowerCardView>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                            .FirstOrDefault(cv => cv.displayName.Contains("madre"));
                        if (card != null && card.selectedFrame != null) card.selectedFrame.SetActive(true);
                    }),
                    ("pausa", "Assets/Course/Scenes/Test 4.unity", () => ShowScreen("Pause Screen", null)),
                    ("pausa_confirmar", "Assets/Course/Scenes/Test 4.unity", () => ShowScreen("Pause Screen", "Confirm")),
                    ("mejora", "Assets/Course/Scenes/Test 4.unity", () => ShowScreen("Tower Upgrade Panel", null)),
                    ("victoria", "Assets/Course/Scenes/Test 4.unity", () => ShowScreen("Level Complete", null)),
                    ("derrota", "Assets/Course/Scenes/Test 4.unity", () => ShowScreen("Level Failed", null)),
                };
                foreach (string sz in size.Split(','))
                {
                    string[] wh = sz.Split('x');
                    int w = int.Parse(wh[0]), h = int.Parse(wh[1]);
                    foreach (var shot in shots)
                    {
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(shot.scene, UnityEditor.SceneManagement.OpenSceneMode.Single);
                        shot.setup?.Invoke();
                        RenderOverlays(System.IO.Path.Combine(outDir, shot.name + "_" + w + "x" + h + ".png"), w, h);
                    }
                }
                code = 0;
            }
            catch (System.Exception e)
            {
                Debug.LogError(Tag + "Capture failed: " + e);
            }
            EditorApplication.Exit(code);
        }

        private static void ShowScreen(string name, string extra)
        {
            Transform canvas = Object.FindAnyObjectByType<UIController>(FindObjectsInactive.Include).transform;
            Transform t = FindDeep(canvas, name);
            if (t == null) return;
            t.gameObject.SetActive(true);
            for (Transform p = t.parent; p != null && p != canvas; p = p.parent) p.gameObject.SetActive(true);
            if (extra != null)
            {
                Transform x = FindDeep(t, extra);
                if (x != null) x.gameObject.SetActive(true);
                Transform footer = FindDeep(t, "Footer");
                if (footer != null) footer.gameObject.SetActive(false);
            }
        }

        private static void RenderOverlays(string path, int width, int height)
        {
            Camera cam = Camera.main;
            foreach (Canvas canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude))
            {
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 1f;
            }
            var rt = new RenderTexture(width, height, 24);
            cam.targetTexture = rt;
            // Canvas scalers read the screen size: render twice so layout settles at the target size.
            for (int pass = 0; pass < 2; pass++)
            {
                Canvas.ForceUpdateCanvases();
                cam.Render();
            }
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Debug.Log(Tag + "Wrote " + path);
        }

        [MenuItem("GameDevAgent/UI/Apply v2 Bosque HUD skin (UI Canvas prefab)")]
        public static void ApplyFromMenu() => Apply();

        public static void ApplyFromCommandLine() => EditorApplication.Exit(Apply() ? 0 : 1);

        public static bool Apply()
        {
            try
            {
                CreateFonts();
                CreatePanelSprites();
                ImportPngSprites(IconDir);
                ImportPngSprites(OrnamentDir);
                ImportAncestrales();
                CreateSkin();
                CreateCollectionAssets();
                AssetDatabase.SaveAssets();
                return SkinPrefab() && RevertHudLayoutOverrides() && SkinMainMenu() && SkinLevelSelect();
            }
            catch (System.Exception e)
            {
                Debug.LogError(Tag + "FAILED: " + e);
                return false;
            }
        }

        // ------------------------------------------------------------------ fonts

        // Phase 18 (Figma "Ancestrales", Tipografía): Oswald SemiBold = buttons and data, Alegreya Sans Bold = labels,
        // Alegreya SC ExtraBold = titles and ribbons, Alegreya Sans Medium = body, Cinzel Decorative Black = brand only.
        private static void CreateFonts()
        {
            fontBold = FontAsset("Oswald-SemiBold");
            fontSemi = FontAsset("AlegreyaSans-Bold");
            fontTitle = FontAsset("AlegreyaSC-ExtraBold");
            fontBody = FontAsset("AlegreyaSans-Medium");
            fontLogo = FontAsset("CinzelDecorative-Black");
            shadowMaterials.Clear();
            foreach (var f in new[] { (fontBold, "Oswald-SemiBold"), (fontSemi, "AlegreyaSans-Bold"), (fontTitle, "AlegreyaSC-ExtraBold"), (fontLogo, "CinzelDecorative-Black") })
            {
                shadowMaterials.Add(new UiSkin.ShadowMaterial { font = f.Item1, material = ShadowMaterial(f.Item1, f.Item2) });
            }
        }

        // "text-shadow: 0 2px 0 tinta" of the design: same atlas, TMP underlay.
        private static Material ShadowMaterial(TMP_FontAsset font, string file)
        {
            string path = FontDir + "/" + file + " Shadow.mat";
            Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(font.material);
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = font.material.shader;
            m.CopyPropertiesFromMaterial(font.material);
            m.name = file + " Shadow";
            m.EnableKeyword("UNDERLAY_ON");
            m.SetColor("_UnderlayColor", new Color(0.08f, 0.10f, 0.09f, 0.9f));
            m.SetFloat("_UnderlayOffsetX", 0f);
            m.SetFloat("_UnderlayOffsetY", -0.35f); // stays inside the 5 px SDF padding of the atlas (no clipped glyphs)
            m.SetFloat("_UnderlayDilate", 0f);
            m.SetFloat("_UnderlaySoftness", 0f);
            EditorUtility.SetDirty(m);
            return m;
        }

        private static TMP_FontAsset FontAsset(string file)
        {
            string assetPath = FontDir + "/" + file + " SDF.asset";
            TMP_FontAsset existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
            if (existing != null && existing.atlasPopulationMode == AtlasPopulationMode.Static
                && existing.characterTable.Count > 0)
            {
                // Glyphs added to FontChars later ("…" inserted by TMP ellipsis, "º"...) are baked into the static atlas;
                // a missing glyph would otherwise make TMP fall back to LiberationSans in an extra sub-mesh.
                if (!existing.HasCharacters(FontChars, out List<char> missingChars) && missingChars != null && missingChars.Count > 0)
                {
                    existing.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                    existing.TryAddCharacters(new string(missingChars.ToArray()), out string notInFont);
                    existing.atlasPopulationMode = AtlasPopulationMode.Static;
                    EditorUtility.SetDirty(existing);
                    if (existing.atlasTextures.Length > 0) EditorUtility.SetDirty(existing.atlasTextures[0]);
                    Debug.Log(Tag + file + ": added " + (missingChars.Count - (notInFont ?? "").Length) + " glyph(s); not in the font: '" + notInFont + "'");
                }
                return existing;
            }
            if (existing != null)
            {
                // Earlier dynamic version: TMP clears dynamic atlases on editor events, so it is rebuilt as static.
                AssetDatabase.DeleteAsset(assetPath);
            }

            Font font = AssetDatabase.LoadAssetAtPath<Font>(FontDir + "/" + file + ".ttf");
            if (font == null)
            {
                throw new System.Exception("Missing font file " + FontDir + "/" + file + ".ttf");
            }

            // 48 pt sampling on a 512x1024 atlas: every glyph of FontChars fits, and the WebGL build stays light.
            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(font, 48, 5, GlyphRenderMode.SDFAA, 512, 1024,
                AtlasPopulationMode.Dynamic, false);
            asset.name = file + " SDF";
            AssetDatabase.CreateAsset(asset, assetPath);
            asset.atlasTextures[0].name = file + " Atlas";
            AssetDatabase.AddObjectToAsset(asset.atlasTextures[0], asset);
            asset.material.name = file + " Material";
            AssetDatabase.AddObjectToAsset(asset.material, asset);

            if (!asset.TryAddCharacters(FontChars, out string missing) && !string.IsNullOrEmpty(missing))
            {
                Debug.LogWarning(Tag + file + " has no glyph for: " + missing);
            }
            // Static atlas: the glyphs baked here are all the HUD needs; nothing is generated at runtime (WebGL) and
            // TMP never clears it.
            asset.atlasPopulationMode = AtlasPopulationMode.Static;
            EditorUtility.SetDirty(asset.atlasTextures[0]);
            EditorUtility.SetDirty(asset);
            Debug.Log(Tag + file + ": " + asset.characterTable.Count + " characters baked");
            AssetDatabase.SaveAssets();
            Debug.Log(Tag + "Created font asset " + assetPath);
            return asset;
        }

        // ------------------------------------------------------------------ procedural 9-slice sprites

        private struct PanelSpec
        {
            public string name;
            public Color fill;
            public float radius, border, pad;
            public bool drop;            // 4 px contour drop shadow below
            public float hiPx, hiAlpha;  // inner white highlight at the top
            public float loPx, loAlpha;  // inner contour shade at the bottom
            public Color? loColor;       // shade colour (default: contour)
            public bool glow;            // "Brillo elegido": 4 px gold ring
            public float softY, softBlur, softAlpha; // soft drop shadow ("Panel hueso")
            public bool ringOnly;        // only the 4 px gold ring, transparent body (selection overlay)
            public float exactW, exactH; // fixed body size (circles, badges): used as Simple sprites, no 9-slice
            public Color? borderColor;   // default: tinta
        }

        private static PanelSpec Exact(string name, Color fill, float size, float border, bool glow = false, float pad = 0f)
        {
            return new PanelSpec { name = name, fill = fill, radius = size * 0.5f, border = border, exactW = size, exactH = size,
                glow = glow, pad = pad };
        }

        // Phase 18: flat panel with a coloured border (HUD pills, cost tag, El Olvido notice).
        private static PanelSpec Bordered(string name, Color fill, float radius, float border, Color borderColor)
        {
            return new PanelSpec { name = name, fill = fill, radius = radius, border = border, borderColor = borderColor };
        }

        // "Panel flotante" (drop shadow 0 10 24) for the parchment window and the dark carved panel.
        private static PanelSpec Floating(string name, Color fill, float radius, float softY, float softBlur, float softAlpha)
        {
            return new PanelSpec { name = name, fill = fill, radius = radius, border = 3f, pad = 20f,
                softY = softY, softBlur = softBlur, softAlpha = softAlpha };
        }

        // "Nodo de nivel" stone (60 px, "Relieve tallado": 4 px ink drop + 2 px inner light).
        private static PanelSpec Stone(string name, Color fill, float border, Color borderColor, bool glow)
        {
            return new PanelSpec { name = name, fill = fill, radius = 30f, border = border, borderColor = borderColor,
                exactW = 60f, exactH = 60f, drop = !glow, glow = glow, hiPx = glow ? 0f : 2f, hiAlpha = 0.25f, pad = 4f };
        }

        private static PanelSpec Relief(string name, Color fill, float radius, float pad = 0f, bool glow = false)
        {
            return new PanelSpec { name = name, fill = fill, radius = radius, border = 3f, pad = pad, drop = true,
                hiPx = 3f, hiAlpha = 0.35f, loPx = 4f, loAlpha = 0.35f, glow = glow };
        }

        private static PanelSpec Flat(string name, Color fill, float radius, float border)
        {
            return new PanelSpec { name = name, fill = fill, radius = radius, border = border };
        }

        private static void CreatePanelSprites()
        {
            Directory.CreateDirectory(SpriteDir);
            // Phase 18: the same sprite names as the "v2 · Bosque" system (runtime screens look them up by name), redrawn
            // with the "Ancestrales" tokens and component specs of the Figma file.
            Color dark90 = HudTheme.HudPill;
            var specs = new List<PanelSpec>
            {
                // HUD (E · Partida).
                Bordered("hud_pill", dark90, 20f, 2f, HudTheme.Stone),            // Vida del libro, Ecos, oleada
                Bordered("notice_dark", HudTheme.OblivionBlack, 14f, 2f, HudTheme.Muted), // Aviso de oleada
                Flat("instr_pill", new Color(dark90.r, dark90.g, dark90.b, 0.85f), 14f, 0f), // Instrucción
                Bordered("cost_tag", new Color(dark90.r, dark90.g, dark90.b, 0.92f), 10f, 1.5f, HudTheme.Gold), // Costo
                new PanelSpec { name = "sel_frame", fill = HudTheme.Gold, radius = 8f, pad = 4f, ringOnly = true }, // Guardián seleccionado
                Bordered("name_plate", HudTheme.Surface, 6f, 2f, HudTheme.Contour), // Placa of the guardian card
                Bordered("info_circle", HudTheme.Muted, 18f, 1.75f, HudTheme.Contour),
                // Collection cards ("Tarjeta coleccionable": Desbloqueada / Seleccionada / Bloqueada) and the art mask.
                Bordered("coll_card", HudTheme.Contour, 12f, 3f, HudTheme.Gold),
                new PanelSpec { name = "coll_card_viewed", fill = HudTheme.Contour, radius = 12f, border = 3f, borderColor = HudTheme.Gold, glow = true, pad = 4f },
                Bordered("coll_card_locked", HudTheme.OblivionBlack, 12f, 3f, HudTheme.Muted),
                Flat("coll_art", HudTheme.Contour, 9f, 0f),
                // Panels and windows.
                Floating("panel_bone", HudTheme.Surface, 22f, 8f, 14f, 0.7f),     // Ventana emergente / Pergamino
                Floating("panel_teal", HudTheme.PanelDeep, 22f, 10f, 24f, 0.55f), // Panel / Tallado oscuro
                Relief("icon_frame", HudTheme.Contour, 10f),                      // Botón ícono
                // Level nodes ("Nodo de nivel": Completado / Actual / Bloqueado) and the selection ring.
                Stone("level_card", HudTheme.Primary, 3f, HudTheme.Contour, false),
                Stone("level_next", HudTheme.Gold, 4f, HudTheme.Gold, true),
                Stone("level_locked", HudTheme.Muted, 3f, HudTheme.Contour, false),
                new PanelSpec { name = "level_ring", fill = HudTheme.Gold, radius = 30f, pad = 4f, ringOnly = true, exactW = 60f, exactH = 60f },
                // Earlier names, restyled.
                Bordered("panel_surface", HudTheme.Surface, 14f, 2f, HudTheme.Contour),
                Bordered("panel_deep", dark90, 20f, 2f, HudTheme.Stone),
                Flat("tray_deep", HudTheme.PanelDeep, 20f, 3f),
                Flat("pill_deep", dark90, 14f, 0f),
                Relief("button_primary", HudTheme.Primary, 10f),
                Relief("plate_surface", HudTheme.Surface, 8f),
                Relief("card_normal", HudTheme.Surface, 12f, 4f),
                Relief("card_selected", HudTheme.Primary, 12f, 4f, true),
                Relief("card_nogold", HudTheme.Muted, 12f, 4f),
                Bordered("bar_track", HudTheme.Hex(0x17201E), 7f, 2f, HudTheme.Stone),
                Flat("bar_fill", HudTheme.Hex(0x5DAE84), 5f, 0f),                 // book green of the art sheet
                Flat("shine", HudTheme.SurfaceLight, 2f, 0f),
                Relief("plate_primary", HudTheme.Primary, 8f),
                Flat("shine_primary", HudTheme.PrimaryLight, 2f, 0f),
                Flat("brand_deep", HudTheme.PanelDeep, 22f, 3f),                  // used without outsets: no shadow pad
                Flat("detail_deep", HudTheme.PanelDeep, 22f, 3f),
                Relief("button_panel", HudTheme.Panel, 10f),
                Relief("card_light", HudTheme.Surface, 12f, 4f),
                Exact("circle_surface", HudTheme.Surface, 42f, 2.5f),
                Exact("circle_dark", HudTheme.Stone, 42f, 2.5f),
                Exact("circle_deep", HudTheme.PanelDeep, 42f, 2.5f),
                Bordered("portrait_big", HudTheme.Contour, 12f, 3f, HudTheme.Gold), // art frame (gold edge, ink inside)
                Exact("badge_done", HudTheme.Primary, 28f, 2f),
                Exact("badge_todo", HudTheme.Muted, 28f, 2f),
                Flat("tab_active", HudTheme.Primary, 12f, 2.5f),                  // Pestaña marcador · Activa
                Flat("tab_inactive", HudTheme.Surface, 12f, 2.5f),                // Inactiva
                Flat("page", HudTheme.Surface, 12f, 2.5f),
                Bordered("chip", HudTheme.River, 10f, 2f, HudTheme.Contour),      // Insignia de rareza · Rara
                Flat("lesson_chip", HudTheme.SurfaceLight, 12f, 2.5f),
                Flat("card_deep16", HudTheme.PanelDeep, 16f, 3f),
                Flat("book_deep", HudTheme.PanelDeep, 22f, 3f),
            };
            foreach (PanelSpec spec in specs)
            {
                sprites[spec.name] = PanelSprite(spec);
            }
        }

        private static Sprite PanelSprite(PanelSpec p)
        {
            float drop = Mathf.Max(p.drop ? 4f : 0f, p.softY);
            float side = Mathf.Max(p.radius, p.border) + 1f;
            float top = Mathf.Max(p.radius, p.border + p.hiPx) + 1f;
            float bottom = Mathf.Max(p.radius, p.border + p.loPx) + 1f;
            Color shade = p.loColor ?? HudTheme.Contour;
            Color softColor = HudTheme.Hex(0x1E2B2A);
            float bw = 2f * side + 4f, bh = top + bottom + 4f;            // body (Figma px)
            if (p.exactW > 0f) { bw = p.exactW; bh = p.exactH; }
            float tw = bw + 2f * p.pad, th = bh + 2f * p.pad + drop;      // texture (Figma px)
            int w = Mathf.RoundToInt(tw * S), h = Mathf.RoundToInt(th * S);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color[w * h];
            float l = p.pad, t = p.pad;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float fx = (x + 0.5f) / S, fy = (h - y - 0.5f) / S; // Figma coords, y down
                    Color c = Color.clear;
                    if (p.softBlur > 0f)
                    {
                        float sds = SdRound(fx, fy - p.softY, l, t, bw, bh, p.radius);
                        float k = Mathf.Clamp01(1f - Mathf.Max(0f, sds) / p.softBlur);
                        c = Over(c, softColor, p.softAlpha * k * k);
                    }
                    if (p.drop)
                    {
                        float g = p.glow ? 4f : 0f;
                        c = Over(c, HudTheme.Contour, Cov(SdRound(fx, fy - 4f, l - g, t - g, bw + 2 * g, bh + 2 * g, p.radius + g)));
                    }
                    if (p.glow)
                    {
                        c = Over(c, HudTheme.Gold, Cov(SdRound(fx, fy, l - 4f, t - 4f, bw + 8f, bh + 8f, p.radius + 4f)));
                    }
                    float sd = SdRound(fx, fy, l, t, bw, bh, p.radius);
                    if (p.ringOnly)
                    {
                        float ring = Mathf.Clamp01(Cov(SdRound(fx, fy, l - 4f, t - 4f, bw + 8f, bh + 8f, p.radius + 4f)) - Cov(sd));
                        px[y * w + x] = Over(Color.clear, HudTheme.Gold, ring);
                        continue;
                    }
                    float bodyCov = Cov(sd);
                    if (bodyCov > 0f)
                    {
                        float inner = p.border > 0f ? Cov(sd + p.border) : 1f;
                        Color body = Color.Lerp(p.borderColor ?? HudTheme.Contour, p.fill, inner);
                        if (p.hiPx > 0f)
                        {
                            float band = inner * (1f - Cov(SdRound(fx, fy - p.hiPx, l, t, bw, bh, p.radius) + p.border));
                            body = Color.Lerp(body, Color.white, p.hiAlpha * band);
                        }
                        if (p.loPx > 0f)
                        {
                            float band = inner * (1f - Cov(SdRound(fx, fy + p.loPx, l, t, bw, bh, p.radius) + p.border));
                            body = Color.Lerp(body, shade, p.loAlpha * band);
                        }
                        c = Over(c, body, bodyCov);
                    }
                    px[y * w + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply();

            string path = SpriteDir + "/" + p.name + ".png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var border = new Vector4((p.pad + side) * S, (p.pad + drop + bottom) * S, (p.pad + side) * S, (p.pad + top) * S);
            return ConfigureSprite(path, p.exactW > 0f ? Vector4.zero : border);
        }

        private static float SdRound(float px, float py, float l, float t, float w, float h, float r)
        {
            float qx = Mathf.Abs(px - (l + w * 0.5f)) - (w * 0.5f - r);
            float qy = Mathf.Abs(py - (t + h * 0.5f)) - (h * 0.5f - r);
            float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
            return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        private static float Cov(float sdFigma) => Mathf.Clamp01(0.5f - sdFigma * S);

        private static Color Over(Color dst, Color src, float a)
        {
            float sa = src.a * a;
            float oa = sa + dst.a * (1f - sa);
            if (oa <= 0f) return Color.clear;
            Color rgb = (src * sa + dst * dst.a * (1f - sa)) / oa;
            rgb.a = oa;
            return rgb;
        }

        private static Sprite ConfigureSprite(string path, Vector4 border)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = SpritePpu;
            importer.spriteBorder = border;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        /// <summary>
        /// Phase 18: art of the Figma "Ancestrales" file (Assets/UI/Ancestrales, see its README): crops of the art sheet
        /// at 1 texel per Figma px, vector layers rendered at 3x or 4x, and the 3-slice carved buttons. Pixels per unit
        /// follow that density, so 1 Figma px = U canvas units everywhere. Large art is compressed and capped.
        /// </summary>
        private static void ImportAncestrales()
        {
            var density = new Dictionary<string, float>
            {
                { "libro_altar", 3f }, { "rama", 3f }, { "luciernagas", 3f }, { "olvido_humo", 3f }, { "luz_libro", 3f },
                { "vineta", 3f }, { "fondo_selva", 3f }, { "esquinero", 4f }, { "cinta_punta", 4f },
            };
            var borders = new Dictionary<string, Vector4>
            {
                { "tallado_musgo", new Vector4(15f, 0f, 15f, 0f) }, { "tallado_madera", new Vector4(13f, 0f, 13f, 0f) },
                { "tallado_pergamino", new Vector4(11f, 0f, 11f, 0f) }, { "cinta_banda", new Vector4(6f, 6f, 6f, 6f) },
                { "barra_verde", new Vector4(6f, 0f, 6f, 0f) }, { "barra_oro", new Vector4(6f, 0f, 6f, 0f) },
                { "barra_pista", new Vector4(12f, 0f, 14f, 0f) },
            };
            var maxSize = new Dictionary<string, int> { { "fondo_selva", 2048 }, { "vineta", 512 }, { "luz_libro", 512 } };
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { AncestralesDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("/Editor/")) continue; // the source art sheet never ships
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                float d = density.TryGetValue(name, out float v) ? v : 1f;
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = d / U * 100f;
                importer.spriteBorder = borders.TryGetValue(name, out Vector4 b) ? b : Vector4.zero;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                bool big = maxSize.ContainsKey(name) || name == "libro_altar" || name == "olvido_humo";
                importer.maxTextureSize = maxSize.TryGetValue(name, out int m) ? m : 1024;
                importer.textureCompression = big ? TextureImporterCompression.Compressed : TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
                sprites[name] = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            }
            // The book bar of the art sheet replaces the flat green fill wherever a progress bar is drawn.
            if (sprites.TryGetValue("barra_verde", out Sprite green) && green != null) sprites["bar_fill"] = green;
            if (sprites.TryGetValue("res_moneda", out Sprite coin) && coin != null) sprites["icon_coin"] = coin;
        }

        private static void ImportPngSprites(string dir)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { dir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // System.IO.Path written in full: the course defines a global class named 'Path'.
                sprites[System.IO.Path.GetFileNameWithoutExtension(path)] = ConfigureSprite(path, Vector4.zero);
            }
        }

        // ------------------------------------------------------------------ runtime skin + collection content

        private const string SkinPath = "Assets/UI/Resources/UiSkin.asset";
        private const string CollectionDir = "Assets/UI/Collection";
        private const string CatalogPath = CollectionDir + "/CollectionCatalog.asset";

        /// <summary>Copies every sprite and the three fonts to Resources/UiSkin so screens built at runtime use them.</summary>
        private static void CreateSkin()
        {
            Directory.CreateDirectory("Assets/UI/Resources");
            UiSkin skin = AssetDatabase.LoadAssetAtPath<UiSkin>(SkinPath);
            if (skin == null)
            {
                skin = ScriptableObject.CreateInstance<UiSkin>();
                AssetDatabase.CreateAsset(skin, SkinPath);
            }
            foreach (KeyValuePair<string, Sprite> entry in sprites)
            {
                skin.Set(entry.Key, entry.Value);
            }
            skin.fontBold = fontBold;
            skin.fontSemi = fontSemi;
            skin.fontTitle = fontTitle;
            skin.fontBody = fontBody;
            skin.fontLogo = fontLogo;
            skin.shadowMaterials = new List<UiSkin.ShadowMaterial>(shadowMaterials);
            EditorUtility.SetDirty(skin);
            UiKit.Skin = skin;
        }

        /// <summary>
        /// Initial Collection content (created only when missing, so later edits of the assets are kept): the four
        /// legends (locked: Manual rule, they arrive with their own content), the Sacred Book (unlocked from the start)
        /// and no visual variants yet. Icons are the symbols of the Figma file, marked as provisional art.
        /// </summary>
        private static void CreateCollectionAssets()
        {
            Directory.CreateDirectory(CollectionDir + "/Items");
            CollectionCatalog catalog = AssetDatabase.LoadAssetAtPath<CollectionCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<CollectionCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            const string Soon = "llegará en una próxima actualización";
            var defs = new[]
            {
                ("leyenda_mohan", "el mohán", CollectionCategory.Leyendas, "guardián de las aguas",
                    "Viejo sabio de los ríos. En la tradición del Tolima y el Huila vive en cuevas junto al agua y cuida de los peces y las corrientes.",
                    "icon_water", CollectionUnlockRule.Manual, Soon),
                ("leyenda_patasola", "la patasola", CollectionCategory.Leyendas, "espíritu de la selva",
                    "Mujer de una sola pierna que recorre las selvas y los montes. Su grito se oye entre los árboles cuando alguien se adentra en la espesura.",
                    "icon_claw", CollectionUnlockRule.Manual, Soon),
                ("leyenda_madremonte", "la madremonte", CollectionCategory.Leyendas, "señora de los montes",
                    "Guardiana de los bosques y las montañas, vestida de musgo y hojas. Castiga a quienes talan el monte o ensucian los ríos.",
                    "icon_mountain", CollectionUnlockRule.Manual, Soon),
                ("leyenda_hojarasquin", "el hojarasquín", CollectionCategory.Leyendas, "guardián del monte",
                    "Ser del bosque cubierto de hojarasca y ramas. Protege a los animales y desorienta a los cazadores.",
                    "icon_leaf", CollectionUnlockRule.Manual, Soon),
                ("libro_sagrado", "el libro sagrado", CollectionCategory.LibroSagrado, "objetivo de cada nivel",
                    "Guarda la memoria de las leyendas. Las fuerzas de El Olvido quieren apoderarse de él; los defensores lo protegen en cada nivel.",
                    "icon_question", CollectionUnlockRule.AlwaysUnlocked, "disponible desde el inicio"),
            };

            foreach (var d in defs)
            {
                string path = CollectionDir + "/Items/" + d.Item1 + ".asset";
                CollectionItemDefinition item = AssetDatabase.LoadAssetAtPath<CollectionItemDefinition>(path);
                if (item == null)
                {
                    item = ScriptableObject.CreateInstance<CollectionItemDefinition>();
                    item.id = d.Item1;
                    item.displayName = d.Item2;
                    item.category = d.Item3;
                    item.subtitle = d.Item4;
                    item.lore = d.Item5;
                    item.icon = Sp(d.Item6);
                    item.placeholderArt = true;
                    item.unlockRule = d.Item7;
                    item.unlockHint = d.Item8;
                    AssetDatabase.CreateAsset(item, path);
                }
                if (!catalog.items.Contains(item)) catalog.items.Add(item);
            }
            EditorUtility.SetDirty(catalog);
            List<string> invalid = catalog.InvalidIds();
            if (invalid.Count > 0) throw new System.Exception("Collection ids must be unique and non-empty: " + string.Join(", ", invalid));
        }

        private static Sprite Sp(string name)
        {
            if (!sprites.TryGetValue(name, out Sprite sprite) || sprite == null)
            {
                throw new System.Exception("Missing sprite " + name);
            }
            return sprite;
        }

        // ------------------------------------------------------------------ prefab

        private static bool SkinPrefab()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Transform canvas = root.transform;
                SkinGold(Child(canvas, "Gold Panel", true));
                BuildWaveStatus(canvas);
                SkinPause(Child(canvas, "Pause Button", true));
                SkinWaveBanner(canvas, Child(canvas, "Wave Text", true));
                RectTransform tray = SkinTray(Child(canvas, "Placement Buttons", true));
                BuildPlacementHint(canvas, tray);
                SkinPauseScreen(canvas);
                BuildBookIntegrity(canvas);
                SkinUpgradePanel(canvas);
                SkinEndScreen(canvas, "Level Complete", "Complete Text", "¡victoria!", "el libro sagrado resiste a El Olvido",
                    new[] { ("Next Level", "siguiente nivel", true), ("Level Select", "elegir nivel", false), ("Main Menu", "menú", false) });
                SkinEndScreen(canvas, "Level Failed", "Failed Text", "derrota", "El Olvido alcanzó el libro sagrado",
                    new[] { ("Try Again", "reintentar", true), ("Level Select", "elegir nivel", false), ("Main Menu", "menú", false) });
                SkinNotEnoughWarning(canvas);
                CleanTextSubMeshes(canvas);

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
                Debug.Log(Tag + (saved ? "UI Canvas prefab skinned and saved." : "Could not save the UI Canvas prefab."));
                return saved;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ------------------------------------------------------------------ HUD (Figma "Ancestrales" · E · Partida)

        // Top-left pills, left to right (Figma px from the top-left corner of the 844x390 frame). The oleada pill also
        // carries the enemies bar of the wave (not in the Figma frame, kept from the game), so the others move right.
        private const float PillTop = 14f, PillHeight = 40f;
        private const float WavePillX = 14f, WavePillW = 164f, BookPillX = 186f, BookPillW = 190f, GoldPillX = 384f;

        private static void RemoveLayout(GameObject go)
        {
            foreach (LayoutGroup lg in go.GetComponents<LayoutGroup>()) Object.DestroyImmediate(lg);
            ContentSizeFitter fit = go.GetComponent<ContentSizeFitter>();
            if (fit != null) Object.DestroyImmediate(fit);
        }

        private static void Hide(Transform parent, params string[] names)
        {
            foreach (string n in names)
            {
                Transform t = Child(parent, n, false);
                if (t != null) t.gameObject.SetActive(false);
            }
        }

        /// <summary>"Ecos" pill: coin of the art sheet + amount (Oswald 16, ink shadow).</summary>
        /// <summary>
        /// Phase 18: the level scenes kept, as prefab-instance overrides, layout values that the old layout groups used to
        /// drive (anchors / positions / sizes at 0) and old text colours. They would pin the HUD to the old layout. Only
        /// those overrides (RectTransforms and TMP colour / style inside the UI Canvas instance) are reverted; every other
        /// per-scene override (unlocked legends, references, active objects) is kept.
        /// </summary>
        private static bool RevertHudLayoutOverrides()
        {
            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Transform sourceRoot = prefabAsset.transform;
            int scenes = 0, reverted = 0;
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            {
                if (!s.enabled || s.path == MainMenuScenePath || s.path == LevelSelectScenePath) continue;
                var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(s.path, UnityEditor.SceneManagement.OpenSceneMode.Single);
                bool dirty = false;
                foreach (UIController ui in Object.FindObjectsByType<UIController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    GameObject root = PrefabUtility.GetOutermostPrefabInstanceRoot(ui.gameObject);
                    if (root == null || PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root) != PrefabPath) continue;
                    PropertyModification[] mods = PrefabUtility.GetPropertyModifications(root);
                    if (mods == null) continue;
                    var keep = new List<PropertyModification>();
                    foreach (PropertyModification m in mods)
                    {
                        bool layout = m.target is RectTransform rt && rt != sourceRoot;
                        bool textLook = m.target is TMP_Text && (m.propertyPath.StartsWith("m_fontColor") || m.propertyPath == "m_TextStyleHashCode");
                        if (layout || textLook) reverted++; else keep.Add(m);
                    }
                    if (keep.Count != mods.Length)
                    {
                        PrefabUtility.SetPropertyModifications(root, keep.ToArray());
                        dirty = true;
                    }
                }
                if (dirty)
                {
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                    scenes++;
                }
            }
            Debug.Log(Tag + "HUD layout overrides reverted: " + reverted + " in " + scenes + " scene(s).");
            return true;
        }

        private static void SkinGold(Transform panel)
        {
            Place(panel, new Vector2(0f, 1f), new Vector2(GoldPillX, -PillTop) * U, new Vector2(118f, PillHeight) * U);
            Pill(panel.gameObject).raycastTarget = false;
            var row = Layout<HorizontalLayoutGroup>(panel.gameObject);
            row.padding = new RectOffset(Mathf.RoundToInt(1f * U), Mathf.RoundToInt(18f * U), 0, 0);
            row.spacing = 8f * U;
            row.childAlignment = TextAnchor.MiddleLeft;
            Fit(panel.gameObject, true, false);

            Image coin = Icon(panel, "Coin Icon", Sp("res_moneda"), 38f * U, Color.white);
            coin.transform.SetSiblingIndex(0);
            TMP_Text value = Child(panel, "Gold Value", true).GetComponent<TMP_Text>();
            Style(value, fontBold, 16f * U, HudTheme.TextLight, TextAlignmentOptions.Left);
            Shadowed(value);
            value.rectTransform.sizeDelta = new Vector2(value.rectTransform.sizeDelta.x, 24f * U);
            value.transform.SetSiblingIndex(1);
            TMP_Text label = Child(panel, "Gold Label", true).GetComponent<TMP_Text>();
            label.text = "oro";
            label.gameObject.SetActive(false); // the coin says it: the Figma pill shows only the amount
        }

        /// <summary>Oleada pill: skull of the art sheet, "N/T" (wave of total) and the enemies bar of the wave.</summary>
        private static void BuildWaveStatus(Transform canvas)
        {
            Transform rootT = Child(canvas, "Wave Status", false) ?? NewChild(canvas, "Wave Status").transform;
            Place(rootT, new Vector2(0f, 1f), new Vector2(WavePillX, -PillTop) * U, new Vector2(WavePillW, PillHeight) * U);
            var view = GetOrAdd<WaveStatusView>(rootT.gameObject);

            Transform pill = Child(rootT, "Pill", false) ?? NewChild(rootT, "Pill").transform;
            RemoveLayout(pill.gameObject);
            Place(pill, new Vector2(0f, 1f), Vector2.zero, new Vector2(WavePillW, PillHeight) * U);
            Pill(pill.gameObject).raycastTarget = false;

            Image skull = Icon(pill, "Skull Icon", Sp("res_calavera"), 36f * U, Color.white);
            Corner(skull.rectTransform, new Vector2(0f, 0.5f), new Vector2(2f * U, 0f), new Vector2(36f, 36f) * U, new Vector2(0f, 0.5f));
            TMP_Text label = Text(pill, "Wave Label", "1/3", fontBold, 17f * U, HudTheme.TextLight);
            Shadowed(label);
            Corner(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(42f * U, 1f * U), new Vector2(46f, 24f) * U, new Vector2(0f, 0.5f));

            Transform track = Child(pill, "Bar", false) ?? NewChild(pill, "Bar").transform;
            SetImage(track.gameObject, Sp("bar_track")).raycastTarget = false;
            Corner((RectTransform)track, new Vector2(0f, 0.5f), new Vector2(90f * U, 0f), new Vector2(42f, 12f) * U, new Vector2(0f, 0.5f));
            Transform fill = Child(track, "Fill", false) ?? NewChild(track, "Fill").transform;
            var fillRect = (RectTransform)fill;
            fillRect.anchorMin = fillRect.anchorMax = new Vector2(0f, 0.5f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.anchoredPosition = new Vector2(2f * U, 0f);
            fillRect.sizeDelta = new Vector2(0f, 8f * U);
            Image fillImg = SetImage(fill.gameObject, Sp("barra_oro"));
            fillImg.pixelsPerUnitMultiplier = 17f / 8f;
            fillImg.raycastTarget = false;

            TMP_Text count = Text(pill, "Wave Count", "0/0", fontBold, 11f * U, HudTheme.Surface); // data: Oswald (lining digits)
            Corner(count.rectTransform, new Vector2(0f, 0.5f), new Vector2(136f * U, 0f), new Vector2(26f, 16f) * U, new Vector2(0f, 0.5f));
            rootT.SetSiblingIndex(Child(canvas, "Gold Panel", true).GetSiblingIndex() + 1);

            view.content = pill.gameObject;
            view.waveLabel = label;
            view.barFill = fillRect;
            view.countLabel = count;
        }

        /// <summary>Pause: the framed pause icon of the art sheet (Figma "Botón de ícono · Pausa"), top-right corner.</summary>
        private static void SkinPause(Transform button)
        {
            Place(button, new Vector2(1f, 1f), new Vector2(-14f, -10f) * U, new Vector2(58f, 53f) * U);
            Baked(button, "ico_pausa", 58f, 53f, "pausa");
        }

        /// <summary>
        /// "Aviso de oleada" (Figma Notificación · Olvido): olvido-black card with a grey info circle, the course wave
        /// text as its title ("¡oleada N!", written by EnemyWaveSpawner) and "El Olvido se acerca · N enemigos" below.
        /// Shown at the start of each wave, top-right, left of the pause button. Never receives raycasts.
        /// </summary>
        private static void SkinWaveBanner(Transform canvas, Transform waveText)
        {
            const float right = 84f, top = 10f, w = 236f, h = 59f;
            Transform banner = Child(canvas, "Wave Banner", false) ?? NewChild(canvas, "Wave Banner").transform;
            Place(banner, new Vector2(1f, 1f), new Vector2(-right, -top) * U, new Vector2(w, h) * U);
            Hide(banner, "Tail Left", "Tail Right", "Band");
            Image card = Pill(banner.gameObject, "notice_dark");
            card.raycastTarget = false;

            Image circle = Img(banner, "Info", Sp("info_circle"));
            circle.type = Image.Type.Sliced;
            Corner(circle.rectTransform, new Vector2(0f, 0.5f), new Vector2(9f * U, 0f), new Vector2(37f, 37f) * U, new Vector2(0f, 0.5f));
            TMP_Text glyph = Text(circle.transform, "Glyph", "i", fontTitle, 20f * U, HudTheme.Contour);
            glyph.alignment = TextAlignmentOptions.Center;
            Stretch(glyph.rectTransform, new Vector4(0f, 2f * U, 0f, 0f));

            Transform sub = Child(banner, "Subtitle", false) ?? NewChild(banner, "Subtitle").transform;
            RemoveLayout(sub.gameObject);
            Image oldPill = sub.GetComponent<Image>();
            if (oldPill != null) Object.DestroyImmediate(oldPill);
            Corner((RectTransform)sub, new Vector2(0f, 0f), new Vector2(56f * U, 10f * U), new Vector2(w - 66f, 16f) * U, new Vector2(0f, 0f));
            TMP_Text subText = Text(sub, "Subtitle Text", "El Olvido se acerca · 12 enemigos", fontBody, 11f * U, HudTheme.TextLight);
            subText.alpha = 0.85f;
            subText.enableAutoSizing = true;
            subText.fontSizeMin = 8f * U;
            subText.fontSizeMax = 11f * U;
            Stretch(subText.rectTransform, Vector4.zero);

            banner.SetSiblingIndex(waveText.GetSiblingIndex());
            waveText.SetSiblingIndex(banner.GetSiblingIndex() + 1); // the text is always drawn over the card (also on reruns)
            banner.gameObject.SetActive(false);

            TMP_Text text = waveText.GetComponent<TMP_Text>();
            Place(waveText, new Vector2(1f, 1f), new Vector2(-(right + 10f), -(top + 10f)) * U, new Vector2(w - 66f, 20f) * U);
            Style(text, fontTitle, 14f * U, HudTheme.Muted, TextAlignmentOptions.Left);
            text.characterSpacing = 2f;
            text.text = "¡oleada 1!";

            var view = GetOrAdd<WaveBannerView>(waveText.gameObject);
            view.banner = banner.gameObject;
            view.subtitle = sub.gameObject;
            view.subtitleText = subText;
        }

        /// <summary>
        /// Guardian tray (Figma E: four portrait cards in the bottom-left corner, costs on gold-edged tags, a gold frame on
        /// the chosen one). No panel behind the cards: the space between them belongs to the map.
        /// </summary>
        private static RectTransform SkinTray(Transform tray)
        {
            Place(tray, new Vector2(0f, 0f), new Vector2(12f, 2f) * U, new Vector2(384f, 132f) * U);
            Image trayImage = GetOrAdd<Image>(tray.gameObject);
            trayImage.sprite = null;
            trayImage.color = new Color(1f, 1f, 1f, 0f);
            trayImage.raycastTarget = false; // taps between the cards reach the map
            var row = Layout<HorizontalLayoutGroup>(tray.gameObject);
            row.padding = new RectOffset(0, 0, 0, 0);
            row.spacing = 6f * U;
            row.childAlignment = TextAnchor.LowerLeft;
            row.childControlWidth = false;
            row.childControlHeight = false;
            Fit(tray.gameObject, true, false); // levels that unlock fewer legends get a narrower tray

            foreach (TowerButton towerButton in tray.GetComponentsInChildren<TowerButton>(true))
            {
                SkinCard(towerButton);
            }
            Hide(tray, "Vine", "Corner TL", "Corner TR", "Corner BR", "Corner BL", "Mushrooms");
            return (RectTransform)tray;
        }

        private static string PortraitOf(LegendId legend)
        {
            switch (legend.ToString().ToLowerInvariant())
            {
                case "madremonte": return "carta_madremonte";
                case "hojarasquin": return "carta_hojarasquin";
                case "patasola": return "carta_patasola";
                default: return "carta_mohan";
            }
        }

        private static void SkinCard(TowerButton towerButton)
        {
            Transform card = towerButton.transform;
            var rect = (RectTransform)card;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(90f, 132f) * U; // 90x116 card + the cost tag that rises 16 px above it
            rect.localScale = Vector3.one;
            Image hit = GetOrAdd<Image>(card.gameObject);
            hit.sprite = null;
            hit.color = new Color(1f, 1f, 1f, 0f);
            hit.raycastTarget = true;
            DisableMeshEffects(card.gameObject);
            Hide(card, "Text (TMP)", "Icon");
            Transform oldCost = Child(card, "Cost", false);
            if (oldCost != null) Object.DestroyImmediate(oldCost.gameObject);

            string key = card.name.ToLowerInvariant();
            LegendId legend = LegendRoster.TryGet(towerButton.towerToPlace, out LegendId found)
                ? found : LegendRoster.FromTowerName(key);
            string display = LegendRoster.CardName(legend);
            Sprite art = Sp(PortraitOf(legend));

            Image portrait = Img(card, "Portrait", art);
            Corner(portrait.rectTransform, new Vector2(0.5f, 0f), Vector2.zero, new Vector2(90f, 116f) * U, new Vector2(0.5f, 0f));
            portrait.raycastTarget = false;
            DropShadow(portrait);
            Image frame = Img(card, "Selected Frame", Sp("sel_frame"));
            frame.type = Image.Type.Sliced;
            Corner(frame.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, -4f * U), new Vector2(98f, 124f) * U, new Vector2(0.5f, 0f));
            frame.gameObject.SetActive(false);

            Image plate = Img(card, "Name Plate", Sp("name_plate"));
            plate.type = Image.Type.Sliced;
            Corner(plate.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 5f * U), new Vector2(82f, 21f) * U, new Vector2(0.5f, 0f));
            TMP_Text name = Child(card, "Name", false) != null ? Child(card, "Name", false).GetComponent<TMP_Text>() : null;
            if (name == null) name = Text(plate.transform, "Name", display, fontSemi, 12f * U, HudTheme.Contour);
            name.transform.SetParent(plate.transform, false);
            name.text = display;
            Style(name, fontSemi, 12f * U, HudTheme.Contour, TextAlignmentOptions.Center);
            name.enableAutoSizing = true; // "hojarasquín" / "madremonte"
            name.fontSizeMin = 8f * U;
            name.fontSizeMax = 12f * U;
            Stretch(name.rectTransform, new Vector4(3f * U, 1f * U, 3f * U, 0f));

            Image tag = Img(card, "Cost Tag", Sp("cost_tag"));
            tag.type = Image.Type.Sliced;
            Corner(tag.rectTransform, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(48f, 20f) * U, new Vector2(0.5f, 1f));
            string costValue = towerButton.towerToPlace != null ? towerButton.towerToPlace.cost.ToString() : "0";
            TMP_Text costText = Text(tag.transform, "Cost Text", costValue, fontBold, 12f * U, HudTheme.Gold);
            costText.alignment = TextAlignmentOptions.Center;
            Stretch(costText.rectTransform, Vector4.zero);

            Button button = card.GetComponent<Button>();
            button.targetGraphic = portrait;
            NeutralTint(button);
            UiPressFeedback fb = GetOrAdd<UiPressFeedback>(card.gameObject);
            fb.visuals = new[] { portrait.rectTransform, frame.rectTransform, plate.rectTransform, tag.rectTransform };
            fb.pressDepth = 3f * U;

            var view = GetOrAdd<TowerCardView>(card.gameObject);
            view.displayName = display;
            view.background = portrait;
            view.icon = null;
            view.coin = null;
            view.nameText = name;
            view.costText = costText;
            view.normalSprite = art;
            view.selectedSprite = art;
            view.noGoldSprite = art;
            view.selectedFrame = frame.gameObject;
        }

        private static void DropShadow(Graphic g)
        {
            Shadow shadow = GetOrAdd<Shadow>(g.gameObject);
            shadow.effectColor = new Color(0.055f, 0.078f, 0.075f, 0.7f);
            shadow.effectDistance = new Vector2(0f, -5f * U);
        }

        private static void BuildPlacementHint(Transform canvas, RectTransform tray)
        {
            // Figma "Instrucción": a small translucent ink pill at the bottom, right of the guardian tray. It never
            // receives raycasts, so TowerManager.IsPointerOverUi ignores it and the ground behind it stays buildable.
            Transform rootT = Child(canvas, "Placement Hint", false) ?? NewChild(canvas, "Placement Hint").transform;
            Vector2 hintSize = new Vector2(300f, 28f) * U;
            Place(rootT, new Vector2(0.5f, 0f), new Vector2(90f * U, 10f * U), hintSize);
            var view = GetOrAdd<PlacementHintView>(rootT.gameObject);
            CanvasGroup group = GetOrAdd<CanvasGroup>(rootT.gameObject);
            group.alpha = 1f;
            group.blocksRaycasts = false;
            group.interactable = false;

            Transform panel = Child(rootT, "Panel", false) ?? NewChild(rootT, "Panel").transform;
            Transform oldCancel = Child(panel, "Cancel Button", false);
            if (oldCancel != null) Object.DestroyImmediate(oldCancel.gameObject);
            RemoveLayout(panel.gameObject);
            Place(panel, new Vector2(0.5f, 0f), Vector2.zero, hintSize);
            Image bg = SetImage(panel.gameObject, Sp("instr_pill"));
            bg.color = Color.white;

            TMP_Text message = Text(panel, "Message", "toca una casilla para invocar al mohán", fontSemi, 13f * U, HudTheme.TextLight);
            message.alignment = TextAlignmentOptions.Center;
            message.textWrappingMode = TextWrappingModes.NoWrap;
            message.enableAutoSizing = true; // "toca una casilla para invocar al hojarasquín" fits at every resolution
            message.fontSizeMin = 9f * U;
            message.fontSizeMax = 13f * U;
            RectTransform mr = message.rectTransform;
            mr.anchorMin = Vector2.zero;
            mr.anchorMax = Vector2.one;
            mr.pivot = new Vector2(0.5f, 0.5f);
            mr.offsetMin = new Vector2(12f * U, 2f * U);
            mr.offsetMax = new Vector2(-12f * U, -2f * U);
            foreach (Graphic g in rootT.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;

            // "cancelar": a real button (it must keep blocking the build behind it), bottom-right corner (the Figma
            // skills slot, free in this game), shown only while a legend is being placed.
            Transform oldTrayCancel = Child(tray, "Placement Cancel", false);
            Transform cancel = oldTrayCancel ?? Child(canvas, "Placement Cancel", false) ?? NewChild(canvas, "Placement Cancel").transform;
            cancel.SetParent(canvas, false);
            Button button = Link(cancel, "cancelar", HudTheme.Contour, 124f);
            var cr = (RectTransform)cancel;
            cr.anchorMin = cr.anchorMax = new Vector2(1f, 0f);
            cr.pivot = new Vector2(1f, 0f);
            cr.anchoredPosition = new Vector2(-14f * U, 8f * U);
            Ignore(cancel.gameObject);
            SetOnClick(button, view.Cancel);

            rootT.SetSiblingIndex(tray.GetSiblingIndex() + 1);
            cancel.SetSiblingIndex(rootT.GetSiblingIndex() + 1);
            view.panel = panel.gameObject;
            view.message = message;
            view.cancelButton = cancel.gameObject;
            view.preferredWidth = hintSize.x;
            view.sideMargin = 12f * U;
            view.avoid = new[] { tray, (RectTransform)cancel };
            panel.gameObject.SetActive(false);
            cancel.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ shared components

        private static void SetOnClick(Button button, UnityEngine.Events.UnityAction action)
        {
            for (int i = button.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
            {
                UnityEventTools.RemovePersistentListener(button.onClick, i);
            }
            UnityEventTools.AddPersistentListener(button.onClick, action);
        }

        /// <summary>"Vida del libro" pill (Figma E): book of the art sheet, carved track with the green fill, value on the bar.</summary>
        private static void BuildBookIntegrity(Transform canvas)
        {
            Transform rootT = Child(canvas, "Book Integrity", false) ?? NewChild(canvas, "Book Integrity").transform;
            Place(rootT, new Vector2(0f, 1f), new Vector2(BookPillX, -PillTop) * U, new Vector2(BookPillW, PillHeight) * U);
            var view = GetOrAdd<BookIntegrityView>(rootT.gameObject);

            Transform pill = Child(rootT, "Pill", false) ?? NewChild(rootT, "Pill").transform;
            RemoveLayout(pill.gameObject);
            Place(pill, new Vector2(0f, 1f), Vector2.zero, new Vector2(BookPillW, PillHeight) * U);
            Pill(pill.gameObject).raycastTarget = false;
            Hide(pill, "Label");

            Image book = Img(pill, "Book Icon", Sp("res_libro"));
            Corner(book.rectTransform, new Vector2(0f, 0.5f), new Vector2(4f * U, 0f), new Vector2(32f, 32f) * U, new Vector2(0f, 0.5f));
            Transform track = Child(pill, "Bar", false) ?? NewChild(pill, "Bar").transform;
            Image trackImg = SetImage(track.gameObject, Sp("barra_pista"));
            trackImg.pixelsPerUnitMultiplier = 34f / 24f;
            trackImg.raycastTarget = false;
            Corner((RectTransform)track, new Vector2(0f, 0.5f), new Vector2(38f * U, 0f), new Vector2(144f, 24f) * U, new Vector2(0f, 0.5f));
            Transform fill = Child(track, "Fill", false) ?? NewChild(track, "Fill").transform;
            var fillRect = (RectTransform)fill;
            fillRect.anchorMin = fillRect.anchorMax = new Vector2(0f, 0.5f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.anchoredPosition = new Vector2(4f * U, 0f);
            fillRect.sizeDelta = new Vector2(132f * U, 13f * U);
            Image fillImg = SetImage(fill.gameObject, Sp("barra_verde"));
            fillImg.pixelsPerUnitMultiplier = 18f / 13f;
            fillImg.raycastTarget = false;
            Transform oldValue = Child(pill, "Value", false);
            TMP_Text value = oldValue != null ? oldValue.GetComponent<TMP_Text>() : null;
            if (value == null) value = Text(track, "Value", "100%", fontBold, 13f * U, HudTheme.TextLight);
            value.transform.SetParent(track, false);
            Style(value, fontBold, 13f * U, HudTheme.TextLight, TextAlignmentOptions.Center);
            Shadowed(value);
            Stretch(value.rectTransform, new Vector4(0f, 1f * U, 0f, 0f));
            value.transform.SetAsLastSibling();

            rootT.SetSiblingIndex(Child(canvas, "Gold Panel", true).GetSiblingIndex() + 1);
            view.content = pill.gameObject;
            view.barFill = fillRect;
            view.valueText = value;
        }

        /// <summary>Defender panel (upgrade / retire): dark carved panel, gold label, carved buttons; actions unchanged.</summary>
        private static void SkinUpgradePanel(Transform canvas)
        {
            Transform panel = Child(canvas, "Tower Upgrade Panel", true);
            Place(panel, new Vector2(0.5f, 1f), new Vector2(0f, -68f * U), new Vector2(700f, 100f) * U);
            Image bg = SetImage(panel.gameObject, Sp("detail_deep"));
            bg.raycastTarget = true; // taps on the panel never reach the map
            var row = Layout<HorizontalLayoutGroup>(panel.gameObject);
            row.padding = new RectOffset(Mathf.RoundToInt(16f * U), Mathf.RoundToInt(14f * U), Mathf.RoundToInt(30f * U), Mathf.RoundToInt(10f * U));
            row.spacing = 8f * U;
            row.childControlWidth = false;
            row.childAlignment = TextAnchor.LowerLeft;
            Fit(panel.gameObject, true, true);
            Corners(panel, 24f, 3f);

            TMP_Text label = Text(panel, "Label", "MEJORAR DEFENSOR", fontTitle, 13f * U, HudTheme.Gold);
            label.characterSpacing = 6f;
            Ignore(label.gameObject);
            Corner(label.rectTransform, new Vector2(0f, 1f), new Vector2(22f, -9f) * U, new Vector2(400f, 17f) * U);
            TowerUpgradePanel upgradePanel = panel.GetComponentInParent<TowerUpgradePanel>(true)
                ?? canvas.GetComponentInChildren<TowerUpgradePanel>(true);
            if (upgradePanel != null) upgradePanel.titleText = label; // "MEJORAR · EL MOHÁN" at runtime

            Transform range = Child(panel, "Range Upgrade Button", true);
            Transform firerate = Child(panel, "Firerate Upgrade Button", true);
            Transform remove = Child(panel, "Remove Tower Button", true);
            Transform close = Child(panel, "Close Button", true);
            Plate(range, false, false, "alcance", 176f);
            Plate(firerate, false, false, "cadencia", 176f);
            Carved(remove, Carving.Pergamino, "retirar · +50 oro", 176f, 46f);
            Transform oldClose = Child(close, "Text (TMP)", false);
            if (oldClose != null) oldClose.gameObject.SetActive(false);
            SquareButton(close, "icon_frame", Sp("icon_close"));
            DisableMeshEffects(panel.gameObject);
            // Reading order: upgrades, retire, close.
            range.SetAsLastSibling();
            firerate.SetAsLastSibling();
            remove.SetAsLastSibling();
            close.SetAsLastSibling();
        }

        /// <summary>
        /// Victory / defeat: the window of the Figma pause ("Ventana emergente"): parchment, gold corners, title ribbon,
        /// message, the main action in musgo and the others in madera / pergamino.
        /// </summary>
        private static void SkinEndScreen(Transform canvas, string screenName, string titleName, string title, string message,
            (string button, string label, bool primary)[] buttons)
        {
            Transform screen = Child(canvas, screenName, true);
            Place(screen, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(420f, 222f) * U);
            Image rootImage = screen.GetComponent<Image>();
            if (rootImage != null)
            {
                rootImage.sprite = null;
                rootImage.color = new Color(1f, 1f, 1f, 0f);
                rootImage.raycastTarget = true;
            }
            DisableMeshEffects(screen.gameObject);

            Image dim = Img(screen, "Dim", null);
            dim.color = new Color(0.07f, 0.09f, 0.09f, 0.6f);
            dim.raycastTarget = true;
            Center(dim.rectTransform, Vector2.zero, new Vector2(6000f, 6000f));
            dim.transform.SetSiblingIndex(0);
            Image bg = BonePanelBackground(screen);
            bg.transform.SetSiblingIndex(1);
            Corners(screen, 24f, 3f);

            Transform ribbon = TitleRibbon(screen, titleName, title);
            Corner((RectTransform)ribbon, new Vector2(0.5f, 1f), new Vector2(0f, 29f * U), new Vector2(280f, 54f) * U, new Vector2(0.5f, 1f));
            TMP_Text msg = Text(screen, "Message", message, fontSemi, 15f * U, HudTheme.Stone);
            msg.alignment = TextAlignmentOptions.Center;
            Corner(msg.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -38f * U), new Vector2(380f, 20f) * U, new Vector2(0.5f, 1f));

            (string button, string label, bool primary) main = buttons[0];
            Transform mainT = Child(screen, main.button, true);
            Carved(mainT, Carving.Musgo, main.label, 230f, 56f);
            Corner((RectTransform)mainT, new Vector2(0.5f, 1f), new Vector2(0f, -66f * U), new Vector2(230f, 63f) * U, new Vector2(0.5f, 1f));
            Transform row = Child(screen, "Row", false) ?? NewChild(screen, "Row").transform;
            Corner((RectTransform)row, new Vector2(0.5f, 1f), new Vector2(0f, -146f * U), new Vector2(380f, 54f) * U, new Vector2(0.5f, 1f));
            var rowLayout = Layout<HorizontalLayoutGroup>(row.gameObject);
            rowLayout.childControlWidth = false;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.spacing = 12f * U;
            for (int i = 1; i < buttons.Length; i++)
            {
                Transform b = Child(screen, buttons[i].button, false) ?? Child(row, buttons[i].button, true);
                b.SetParent(row, false);
                bool last = i == buttons.Length - 1;
                Carved(b, last ? Carving.Pergamino : Carving.Madera, buttons[i].label, 168f, 46f);
            }
            ribbon.SetAsLastSibling();
        }

        /// <summary>
        /// Title ribbon that reuses the course title text (tests read it). The course text moved inside the ribbon on an
        /// earlier run, so it is searched in depth; a second "Label" left by such a run is removed (it drew over it).
        /// </summary>
        private static Transform TitleRibbon(Transform parent, string courseTitleName, string title, Transform searchRoot = null)
        {
            TMP_Text course = FindTextDeep(searchRoot ?? parent, courseTitleName);
            Transform existing = Child(parent, "Title Ribbon", false);
            if (course != null && existing != null)
            {
                Transform stray = Child(existing, "Label", false);
                if (stray != null && stray != course.transform) Object.DestroyImmediate(stray.gameObject);
            }
            return Ribbon(parent, "Title Ribbon", course, title);
        }

        private static void SkinNotEnoughWarning(Transform canvas)
        {
            Transform warning = Child(canvas, "Not Enough Money", false);
            if (warning == null) return;
            TMP_Text text = warning.GetComponentInChildren<TMP_Text>(true);
            if (text == null) return;
            text.text = "oro insuficiente";
            Style(text, fontBold, 24f * U, HudTheme.Gold, TextAlignmentOptions.Center);
            text.fontStyle = FontStyles.UpperCase;
            Shadowed(text);
        }

        // ------------------------------------------------------------------ main menu (Figma "Ancestrales" · A · Inicio)

        private const string MainMenuScenePath = "Assets/Course/Scenes/Main Menu.unity";

        // Places an element of the 844x390 Figma frame relative to one corner of the screen (x, y = Figma top-left
        // position; the corner decides how it moves on wider or taller screens).
        private static void FigmaPlace(RectTransform r, Vector2 corner, float x, float y, float w, float h)
        {
            const float W = 844f, H = 390f;
            float ax = corner.x < 0.5f ? x : corner.x > 0.5f ? x + w - W : x + w * 0.5f - W * 0.5f;
            float ay = corner.y > 0.5f ? -y : corner.y < 0.5f ? H - (y + h) : H * 0.5f - (y + h * 0.5f);
            r.anchorMin = r.anchorMax = corner;
            r.pivot = new Vector2(corner.x, corner.y);
            if (corner.x == 0.5f) r.pivot = new Vector2(0.5f, r.pivot.y);
            r.anchoredPosition = new Vector2(ax, ay) * U;
            r.sizeDelta = new Vector2(w, h) * U;
            r.localScale = Vector3.one;
            r.localEulerAngles = Vector3.zero;
        }

        private static Image Art(Transform parent, string name, string sprite, Vector2 corner, float x, float y, float w, float h, float alpha = 1f)
        {
            Image img = Img(parent, name, Sp(sprite));
            img.preserveAspect = false;
            img.color = new Color(1f, 1f, 1f, alpha);
            FigmaPlace(img.rectTransform, corner, x, y, w, h);
            return img;
        }

        private static TMP_Text Caption(Transform parent, string name, string text, float size)
        {
            TMP_Text t = Text(parent, name, text, fontSemi, size * U, HudTheme.TextLight);
            t.alignment = TextAlignmentOptions.Center;
            Shadowed(t);
            return t;
        }

        /// <summary>
        /// "A · Inicio": the night forest illustration, the ANCESTRALES brand on the left, the Sacred Book on its altar
        /// with light, magic, butterflies and fireflies on the right, and the actions of the game (unchanged): JUGAR
        /// (nueva partida) in the centre, COLECCIÓN bottom-left, ELEGIR NIVEL bottom-right (the Figma "Escanear" slot:
        /// this game has no AR scanning) and the tutorial and exit as icon buttons with their word, top-right (the Figma
        /// profile / notifications / settings slots: those screens do not exist in the game).
        /// </summary>
        private static bool SkinMainMenu()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(MainMenuScenePath,
                UnityEditor.SceneManagement.OpenSceneMode.Single);
            Canvas canvas = null;
            foreach (Canvas c in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c.renderMode == RenderMode.ScreenSpaceOverlay
                    && (c.transform.Find("Title") != null || c.transform.Find("Brand Block") != null || c.transform.Find("Inicio") != null)) canvas = c;
            }
            if (canvas == null) throw new System.Exception("Main Menu overlay canvas not found.");
            Transform root = canvas.transform;

            Transform home = Child(root, "Inicio", false) ?? NewChild(root, "Inicio").transform;
            Stretch((RectTransform)home, Vector4.zero);
            home.SetSiblingIndex(0);

            // Background: night forest + vignette (no raycasts: they never cover a button).
            Image bg = Img(home, "Fondo", Sp("fondo_selva"));
            Stretch(bg.rectTransform, Vector4.zero);
            var fitter = GetOrAdd<AspectRatioFitter>(bg.gameObject);
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fitter.aspectRatio = 2532f / 1170f;
            bg.transform.SetSiblingIndex(0);
            Image vignette = Img(home, "Viñeta", Sp("vineta"));
            Stretch(vignette.rectTransform, Vector4.zero);
            vignette.transform.SetSiblingIndex(1);

            // Right: the Sacred Book on the altar (the menu emblem), its light and the magic around it.
            Vector2 TR = new Vector2(1f, 1f), TL = new Vector2(0f, 1f), BL = new Vector2(0f, 0f), BR = new Vector2(1f, 0f);
            Art(home, "Luz del libro", "luz_libro", TR, 400f, 40f, 420f, 300f);
            Art(home, "Olvido acecha", "olvido_humo", BL, -90f, 250f, 420f, 200f, 0.9f);
            Art(home, "Olvido acecha 2", "olvido_humo", BR, 640f, 270f, 336f, 160f, 0.9f);
            Image emblem = Art(home, "Emblem", "libro_altar", TR, 450f, 74f, 341.6f, 256.2f);
            emblem.preserveAspect = true;
            Image magic = Art(home, "Magia", "fx_magia", TR, 535f, 40f, 143f, 114.4f);
            GetOrAdd<FireflyTwinkle>(magic.gameObject);
            Art(home, "Mariposas", "fx_mariposas", TR, 700f, 120f, 57.6f, 75.2f);
            Image ff1 = Art(home, "Luciérnagas", "luciernagas", TR, 380f, 30f, 240f, 120f);
            GetOrAdd<FireflyTwinkle>(ff1.gameObject);
            Image ff2 = Art(home, "Luciérnagas 2", "luciernagas", TL, 20f, 190f, 240f, 120f);
            GetOrAdd<FireflyTwinkle>(ff2.gameObject);

            // Left: brand.
            TMP_Text logo = Child(root, "Title", false) != null ? Child(root, "Title", false).GetComponent<TMP_Text>() : null;
            if (logo == null) logo = FindTextDeep(root, "Title");
            if (logo == null) logo = Text(home, "Title", "ANCESTRALES", fontLogo, 48f * U, HudTheme.SurfaceLight);
            logo.transform.SetParent(home, false);
            logo.gameObject.SetActive(true);
            logo.text = "ANCESTRALES";
            Style(logo, fontLogo, 48f * U, HudTheme.SurfaceLight, TextAlignmentOptions.Left);
            logo.characterSpacing = 2f;
            Shadowed(logo);
            FigmaPlace(logo.rectTransform, TL, 36f, 64f, 420f, 65f);
            TMP_Text subtitle = Text(home, "Subtitulo", "El último recuerdo", fontTitle, 24f * U, HudTheme.Gold);
            subtitle.fontStyle = FontStyles.UpperCase;
            subtitle.characterSpacing = 2f;
            Shadowed(subtitle);
            FigmaPlace(subtitle.rectTransform, TL, 40f, 124f, 300f, 33f);
            TMP_Text tagline = Text(home, "Lema", "«La memoria mantiene vivas las historias»", fontBody, 15f * U, HudTheme.SurfaceLight);
            Shadowed(tagline);
            FigmaPlace(tagline.rectTransform, TL, 42f, 160f, 320f, 20f);
            TMP_Text game = Text(home, "Juego", "Defensa de las Leyendas · 6 niveles", fontSemi, 11f * U, HudTheme.SurfaceLight);
            game.alpha = 0.7f;
            FigmaPlace(game.rectTransform, TL, 42f, 190f, 260f, 14f);
            Art(home, "Rama", "rama", TL, 0f, 0f, 105f, 105f);

            // Actions.
            MainMenuView view = GetOrAdd<MainMenuView>(root.gameObject);
            Button newGame = FindButtonDeep(root, "New Game");
            Button tutorial = FindButtonDeep(root, "Tutorial");
            Button quit = FindButtonDeep(root, "Quit Game");
            Transform levelsT = FindDeep(root, "Level Select Button") ?? NewChild(home, "Level Select Button").transform;
            Transform collectionT = FindDeep(root, "Collection Button") ?? NewChild(home, "Collection Button").transform;
            foreach (Transform t in new[] { newGame.transform, tutorial.transform, quit.transform, levelsT, collectionT })
            {
                t.SetParent(home, false);
                Ignore(t.gameObject);
            }

            Baked(newGame.transform, "btn_jugar", 236f, 77f, "jugar");
            FigmaPlace((RectTransform)newGame.transform, new Vector2(0.5f, 0f), 304f, 291f, 236f, 77f);

            Button collection = Baked(collectionT, "btn_coleccion", 164f, 59.4f, "colección");
            FigmaPlace((RectTransform)collectionT, BL, 25.2f, 307.2f, 164f, 59.4f);
            SetOnClick(collection, view.OpenCollection);
            Image badge = Img(collectionT, "Contador", Sp("cost_tag"));
            badge.type = Image.Type.Sliced;
            Corner(badge.rectTransform, new Vector2(1f, 1f), new Vector2(6f, 8f) * U, new Vector2(40f, 20f) * U, new Vector2(1f, 1f));
            TMP_Text collectionCount = Text(badge.transform, "Text", "0/0", fontBold, 12f * U, HudTheme.Gold);
            collectionCount.alignment = TextAlignmentOptions.Center;
            Stretch(collectionCount.rectTransform, Vector4.zero);

            Button levels = Carved(levelsT, Carving.Madera, "elegir nivel", 172f, 52f);
            FigmaPlace((RectTransform)levelsT, BR, 647f, 305f, 172f, 59f);
            SetOnClick(levels, view.OpenLevelSelect);

            // Top-right icon buttons, each with its word under it (UX principle: "el ícono siempre acompaña a la palabra").
            Baked(tutorial.transform, "ico_ayuda", 53f, 53f, "tutorial");
            FigmaPlace((RectTransform)tutorial.transform, TR, 697f, 12f, 53f, 53f);
            TMP_Text tutorialCaption = Caption(tutorial.transform, "Caption", "tutorial · 0/6", 11f);
            Corner(tutorialCaption.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, -15f * U), new Vector2(100f, 14f) * U, new Vector2(0.5f, 0f));
            Baked(quit.transform, "est_cerrar", 53f, 53f, "salir del juego");
            FigmaPlace((RectTransform)quit.transform, TR, 772f, 12f, 53f, 53f);
            TMP_Text quitCaption = Caption(quit.transform, "Caption", "salir", 11f);
            Corner(quitCaption.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, -15f * U), new Vector2(80f, 14f) * U, new Vector2(0.5f, 0f));

            view.tutorialLabel = tutorialCaption;
            view.collectionLabel = collectionCount;

            // Builder leftovers of the "v2 · Bosque" menu.
            foreach (string n in new[] { "Brand Block", "Menu", "Branch Top", "Branch Bottom" })
            {
                Transform old = Child(root, n, false);
                if (old != null) Object.DestroyImmediate(old.gameObject);
            }

            // Collection screen: frame built once here, cards rebuilt from the catalog every time it opens.
            Transform collectionScreen = Child(root, "Collection Screen", false) ?? NewChild(root, "Collection Screen").transform;
            collectionScreen.gameObject.SetActive(true);
            var collectionView = GetOrAdd<CollectionController>(collectionScreen.gameObject);
            collectionView.catalog = AssetDatabase.LoadAssetAtPath<CollectionCatalog>(CatalogPath);
            collectionView.hideWhileOpen = new[] { home.gameObject };
            collectionView.BuildFrame((button, method) => SetOnClick(button,
                (UnityEngine.Events.UnityAction)System.Delegate.CreateDelegate(typeof(UnityEngine.Events.UnityAction), collectionView, method)));
            collectionView.BuildPreview();
            collectionScreen.SetAsLastSibling();
            collectionScreen.gameObject.SetActive(false);
            view.collection = collectionView;

            CleanTextSubMeshes(root);
            // In a scene, script changes to prefab instances (the course buttons) are only kept when recorded.
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c != null && PrefabUtility.IsPartOfPrefabInstance(c))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(c);
                }
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            bool saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            Debug.Log(Tag + (saved ? "Main Menu (Ancestrales · Inicio) built and saved." : "Could not save the Main Menu scene."));
            return saved;
        }

        /// <summary>
        /// TextMeshPro keeps one "TMP SubMeshUI" child per extra font / material a text ever used. After the font change
        /// of phase 18 the old ones (Fredoka, MedievalSharp) stayed in the assets and drew stale glyphs over the new
        /// text. They are regenerated by TMP when needed, so every saved one is removed.
        /// </summary>
        private static void CleanTextSubMeshes(Transform root)
        {
            int n = 0;
            foreach (TMP_SubMeshUI sub in root.GetComponentsInChildren<TMP_SubMeshUI>(true))
            {
                if (sub == null) continue;
                Object.DestroyImmediate(sub.gameObject);
                n++;
            }
            if (n > 0) Debug.Log(Tag + "Removed " + n + " stale TMP sub-meshes under " + root.name);
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

        private static TMP_Text FindTextDeep(Transform root, string name)
        {
            Transform t = FindDeep(root, name);
            return t != null ? t.GetComponent<TMP_Text>() : null;
        }

        private static Button FindButtonDeep(Transform root, string name)
        {
            foreach (Button b in root.GetComponentsInChildren<Button>(true))
            {
                if (b.name == name) return b;
            }
            throw new System.Exception("Button '" + name + "' not found under '" + root.name + "'");
        }

        // ------------------------------------------------------------------ level select (no frame in the Figma file:
        // built from its components — Botón tallado Volver, Cinta de título, Panel · Pergamino, Nodo de nivel, Panel /
        // Tallado oscuro, Botón tallado Jugar — over the 3D map of the game)

        private const string LevelSelectScenePath = "Assets/Course/Scenes/Level Select.unity";

        private static bool SkinLevelSelect()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(LevelSelectScenePath,
                UnityEditor.SceneManagement.OpenSceneMode.Single);

            GameObject hudGo = GameObject.Find("Level Select HUD");
            if (hudGo == null)
            {
                hudGo = new GameObject("Level Select HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                hudGo.layer = 5;
            }
            Canvas canvas = hudGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            CanvasScaler scaler = hudGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            var hud = GetOrAdd<LevelSelectHud>(hudGo);
            var view = GetOrAdd<LevelSelectView>(hudGo);
            Transform root = hudGo.transform;
            Vector2 TL = new Vector2(0f, 1f);

            // Back: the carved "VOLVER" of the art sheet, top-left (as in every Figma screen).
            Transform backT = Child(root, "Back Button", false) ?? NewChild(root, "Back Button").transform;
            Button back = Baked(backT, "btn_volver", 112f, 52f, "volver");
            Place(backT, TL, new Vector2(16f, -12f) * U, new Vector2(112f, 52f) * U);
            SetOnClick(back, hud.GoToMainMenu);

            Transform ribbon = Ribbon(root, "Title Ribbon", null, "elige un nivel");
            Corner((RectTransform)ribbon, TL, new Vector2(150f, -12f) * U, new Vector2(260f, 54f) * U);

            // "N de 6 completados" on an ink pill (no text directly over the 3D scene).
            Transform counter = Child(root, "Completed Counter", false) ?? NewChild(root, "Completed Counter").transform;
            Place(counter, TL, new Vector2(426f, -23f) * U, new Vector2(173f, 30f) * U);
            Image counterBg = Pill(counter.gameObject);
            counterBg.raycastTarget = false;
            var counterRow = Layout<HorizontalLayoutGroup>(counter.gameObject);
            counterRow.padding = new RectOffset(Mathf.RoundToInt(14f * U), Mathf.RoundToInt(14f * U), Mathf.RoundToInt(6f * U), Mathf.RoundToInt(6f * U));
            counterRow.childAlignment = TextAnchor.MiddleCenter;
            Fit(counter.gameObject, true, true);
            TMP_Text completed = Text(counter, "Text", "0 de 6 completados", fontSemi, 14f * U, HudTheme.SurfaceLight);
            Shadowed(completed);

            var levels = new SortedDictionary<int, string>();
            foreach (LevelSelectButton b in Object.FindObjectsByType<LevelSelectButton>(FindObjectsInactive.Include))
            {
                if (b.levelId > 0) levels[b.levelId] = b.levelToLoad;
            }
            if (levels.Count == 0) throw new System.Exception("No LevelSelectButton found in Level Select.");

            // Left page: parchment with the six level stones (3 x 2), centred composition of the 844 px frame.
            Transform page = Child(root, "Level Page", false) ?? NewChild(root, "Level Page").transform;
            Place(page, new Vector2(0.5f, 1f), new Vector2(24f - 422f, -84f) * U, new Vector2(488f, 284f) * U);
            ((RectTransform)page).pivot = new Vector2(0f, 1f);
            BonePanelBackground(page);
            Corners(page, 24f, 3f);
            TMP_Text pageTitle = Text(page, "Page Title", "camino de las leyendas", fontTitle, 17f * U, HudTheme.Contour);
            pageTitle.fontStyle = FontStyles.UpperCase;
            pageTitle.characterSpacing = 3f;
            Corner(pageTitle.rectTransform, TL, new Vector2(22f, -14f) * U, new Vector2(380f, 22f) * U);

            // The grid lives inside the page since phase 18; earlier reruns left copies (one per run): keep one.
            Transform grid = Child(page, "Level Grid", false) ?? Child(root, "Level Grid", false) ?? NewChild(root, "Level Grid").transform;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Level Grid" && t != grid).ToList())
            {
                Object.DestroyImmediate(t.gameObject);
            }
            grid.SetParent(page, false);
            Place(grid, TL, new Vector2(20f, -44f) * U, new Vector2(448f, 228f) * U);
            foreach (LayoutGroup lg in grid.GetComponents<LayoutGroup>()) if (!(lg is GridLayoutGroup)) Object.DestroyImmediate(lg);
            var gridLayout = GetOrAdd<GridLayoutGroup>(grid.gameObject);
            gridLayout.cellSize = new Vector2(144f, 110f) * U;
            gridLayout.spacing = new Vector2(8f, 8f) * U;
            gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayout.constraintCount = 3;
            gridLayout.childAlignment = TextAnchor.UpperCenter;

            var cards = new List<LevelCardView>();
            foreach (KeyValuePair<int, string> level in levels)
            {
                cards.Add(BuildLevelCard(grid, level.Key, level.Value, view));
            }

            // Remove the old world-space buttons of the map (the cards now carry the LevelSelectButtons).
            foreach (LevelSelectButton b in Object.FindObjectsByType<LevelSelectButton>(FindObjectsInactive.Include))
            {
                if (b.GetComponent<LevelCardView>() != null) continue;
                GameObject go = b.gameObject;
                GameObject instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                Object.DestroyImmediate(instanceRoot != null ? instanceRoot : go);
            }

            // Right: detail on the dark carved panel, JUGAR of the art sheet (the one main action, right side).
            Transform detail = Child(root, "Detail", false) ?? NewChild(root, "Detail").transform;
            Place(detail, new Vector2(0.5f, 1f), new Vector2(822f - 422f, -84f) * U, new Vector2(286f, 284f) * U);
            ((RectTransform)detail).pivot = new Vector2(1f, 1f);
            Image oldDetail = detail.GetComponent<Image>();
            if (oldDetail != null) { oldDetail.sprite = null; oldDetail.color = new Color(1f, 1f, 1f, 0f); oldDetail.raycastTarget = false; }
            Transform oldClip = Child(detail, "Clip", false);
            if (oldClip != null) Object.DestroyImmediate(oldClip.gameObject);
            TealPanelBackground(detail);
            Corners(detail, 32f, 3f);
            Image emblem = Img(detail, "Libro", Sp("libro_altar"));
            emblem.preserveAspect = true;
            emblem.color = new Color(1f, 1f, 1f, 0.9f);
            Corner(emblem.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -86f * U), new Vector2(150f, 112f) * U, new Vector2(0.5f, 1f));

            TMP_Text label = Text(detail, "Label", "SIGUIENTE NIVEL", fontTitle, 12f * U, HudTheme.Gold);
            label.characterSpacing = 8f;
            Corner(label.rectTransform, TL, new Vector2(24f, -20f) * U, new Vector2(238f, 16f) * U);
            TMP_Text title = Text(detail, "Title", "nivel 1", fontTitle, 28f * U, HudTheme.TextLight);
            title.fontStyle = FontStyles.UpperCase;
            Shadowed(title);
            Corner(title.rectTransform, TL, new Vector2(24f, -38f) * U, new Vector2(238f, 34f) * U);
            TMP_Text waves = Text(detail, "Waves", "oleadas: 3", fontSemi, 14f * U, HudTheme.Surface);
            Corner(waves.rectTransform, TL, new Vector2(24f, -70f) * U, new Vector2(238f, 18f) * U);
            Transform playT = Child(detail, "Play Button", false) ?? NewChild(detail, "Play Button").transform;
            Button play = Baked(playT, "btn_jugar", 206f, 67f, "jugar");
            Corner((RectTransform)playT, new Vector2(0.5f, 0f), new Vector2(0f, 16f * U), new Vector2(206f, 67f) * U, new Vector2(0.5f, 0f));
            SetOnClick(play, view.Play);

            // Ornaments (decorative, no raycasts).
            foreach (string n in new[] { "Fireflies", "Branch Right", "Mushrooms" })
            {
                Transform old = Child(root, n, false);
                if (old != null) Object.DestroyImmediate(old.gameObject);
            }
            Image fireflies = Img(root, "Luciérnagas", Sp("luciernagas"));
            Corner(fireflies.rectTransform, new Vector2(1f, 1f), new Vector2(-40f, -4f) * U, new Vector2(240f, 120f) * U);
            GetOrAdd<FireflyTwinkle>(fireflies.gameObject);
            Image branch = Img(root, "Rama", Sp("rama"));
            Corner(branch.rectTransform, new Vector2(1f, 1f), Vector2.zero, new Vector2(105f, 105f) * U);
            branch.rectTransform.localScale = new Vector3(-1f, 1f, 1f);
            branch.rectTransform.pivot = new Vector2(0f, 1f);

            CleanTextSubMeshes(root);
            view.cards = cards.ToArray();
            view.completedText = completed;
            view.detailLabel = label;
            view.detailTitle = title;
            view.detailWaves = waves;

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            bool saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            Debug.Log(Tag + (saved ? "Level Select skinned and saved (" + cards.Count + " cards)." : "Could not save the Level Select scene."));
            return saved;
        }

        /// <summary>
        /// One level: the "Nodo de nivel" stone (Completado musgo + check, Actual gold and glowing, Bloqueado grey with a
        /// padlock) and its name under it. The whole cell is the button, so a tap only ever selects this level.
        /// </summary>
        private static LevelCardView BuildLevelCard(Transform grid, int levelId, string levelToLoad, LevelSelectView view)
        {
            string name = "Level " + levelId;
            Transform card = Child(grid, name, false) ?? NewChild(grid, name).transform;
            card.SetSiblingIndex(levelId - 1);
            var level = GetOrAdd<LevelSelectButton>(card.gameObject);
            level.levelId = levelId;
            level.levelToLoad = levelToLoad;
            Image hit = GetOrAdd<Image>(card.gameObject);
            hit.sprite = null;
            hit.type = Image.Type.Simple;
            hit.color = new Color(1f, 1f, 1f, 0f);
            hit.raycastTarget = true;
            Button button = GetOrAdd<Button>(card.gameObject);
            NeutralTint(button);
            ColorBlock colors = button.colors;
            colors.disabledColor = Color.white; // locked stones stay opaque: grey stone + padlock show the state
            button.colors = colors;

            Image stone = Img(card, "Stone", Sp("level_next"));
            stone.preserveAspect = true;
            Corner(stone.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -4f * U), new Vector2(72f, 72f) * U, new Vector2(0.5f, 1f));
            stone.transform.SetSiblingIndex(0);
            button.targetGraphic = stone;
            // The number is the first TMP text of the card (LevelFlowTestRunner reads its digits).
            TMP_Text number = Text(card, "Number", levelId.ToString(), fontBold, 28f * U, HudTheme.Contour);
            number.alignment = TextAlignmentOptions.Center;
            Corner(number.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -4f * U), new Vector2(72f, 66f) * U, new Vector2(0.5f, 1f));
            number.transform.SetSiblingIndex(0);
            stone.transform.SetSiblingIndex(0);

            Image ring = Img(card, "Ring", Sp("level_ring"));
            ring.preserveAspect = true;
            Corner(ring.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, 0f), new Vector2(80f, 80f) * U, new Vector2(0.5f, 1f));
            Image check = Img(card, "Check", Sp("est_check"));
            Corner(check.rectTransform, new Vector2(0.5f, 1f), new Vector2(24f * U, -54f * U), new Vector2(24f, 24f) * U, new Vector2(0.5f, 1f));
            Image padlock = Img(card, "Padlock", Sp("est_candado"));
            Corner(padlock.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -20f * U), new Vector2(34f, 34f) * U, new Vector2(0.5f, 1f));
            TMP_Text next = Text(card, "Next Label", "¡siguiente!", fontBold, 12f * U, HudTheme.Contour);
            next.alignment = TextAlignmentOptions.Center;
            next.fontStyle = FontStyles.UpperCase;
            next.characterSpacing = 6f;
            Corner(next.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 4f * U), new Vector2(140f, 16f) * U, new Vector2(0.5f, 0f));
            TMP_Text caption = Text(card, "Caption", "nivel " + levelId, fontSemi, 13f * U, HudTheme.Stone);
            caption.alignment = TextAlignmentOptions.Center;
            Corner(caption.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 18f * U), new Vector2(140f, 16f) * U, new Vector2(0.5f, 0f));
            UiPressFeedback fb = GetOrAdd<UiPressFeedback>(card.gameObject);
            fb.visuals = new[] { stone.rectTransform, number.rectTransform, ring.rectTransform, check.rectTransform, padlock.rectTransform };
            fb.pressDepth = 3f * U;

            var cardView = GetOrAdd<LevelCardView>(card.gameObject);
            cardView.background = stone;
            cardView.number = number;
            cardView.check = check;
            cardView.nextLabel = next;
            cardView.padlock = padlock;
            cardView.ring = ring.gameObject;
            cardView.button = button;
            cardView.completedSprite = Sp("level_card");
            cardView.nextSprite = Sp("level_next");
            cardView.lockedSprite = Sp("level_locked");
            SetOnClick(button, cardView.OnTap);

            // Default look in the editor: level 1 next, the rest locked (the real state is applied at runtime).
            cardView.Apply(levelId == 1 ? LevelCardView.CardState.Next : LevelCardView.CardState.Locked, levelId == 1);
            return cardView;
        }

        // ------------------------------------------------------------------ pause (Figma "Ancestrales" · Pausa (ventana))

        /// <summary>
        /// 420x280 parchment window: "PAUSA" ribbon, status line, CONTINUAR (musgo), REINICIAR + ELEGIR NIVEL (madera; the
        /// Figma "Ajustes" slot: the game has no settings screen) and SALIR AL MENÚ (pergamino, the Figma "Salir al
        /// mapa"), which asks for confirmation inside the same window.
        /// </summary>
        private static void SkinPauseScreen(Transform canvas)
        {
            Transform screen = Child(canvas, "Pause Screen", true);
            Transform panel = Child(screen, "Pause Panel", false) ?? NewChild(screen, "Pause Panel").transform;
            Place(panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(420f, 280f) * U);
            var column = Layout<VerticalLayoutGroup>(panel.gameObject);
            column.childAlignment = TextAnchor.UpperCenter;
            column.childControlWidth = false;
            column.padding = new RectOffset(0, 0, Mathf.RoundToInt(36f * U), Mathf.RoundToInt(18f * U));
            column.spacing = 8f * U;
            Fit(panel.gameObject, false, true);

            Transform oldRoots = Child(panel, "Roots", false);
            if (oldRoots != null) Object.DestroyImmediate(oldRoots.gameObject);
            Image bg = BonePanelBackground(panel);
            Corners(panel, 24f, 3f);
            Transform title = TitleRibbon(panel, "Paused", "pausa", screen);
            Ignore(title.gameObject);
            Corner((RectTransform)title, new Vector2(0.5f, 1f), new Vector2(-3f * U, 29f * U), new Vector2(260f, 54f) * U, new Vector2(0.5f, 1f));

            TMP_Text status = Text(panel, "Status", "nivel 1 · oleada 1 de 3", fontSemi, 14f * U, HudTheme.Stone);
            status.alignment = TextAlignmentOptions.Center;
            Element(status.gameObject, 380f * U, 18f * U);

            Button resume = FindButtonDeep(screen, "Resume");
            resume.transform.SetParent(panel, false);
            Carved(resume.transform, Carving.Musgo, "continuar", 150f, 55f);

            Transform row = Child(panel, "Row", false) ?? NewChild(panel, "Row").transform;
            var rowLayout = Layout<HorizontalLayoutGroup>(row.gameObject);
            rowLayout.childControlWidth = false;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.spacing = 14f * U;
            Element(row.gameObject, 380f * U, 52f * U);
            Transform restartT = Child(row, "Restart Button", false) ?? NewChild(row, "Restart Button").transform;
            Button restart = Carved(restartT, Carving.Madera, "reiniciar", 130f, 45f);
            UIController ui = canvas.GetComponentInChildren<UIController>(true) ?? canvas.GetComponentInParent<UIController>();
            if (ui == null) throw new System.Exception("UIController not found in the UI Canvas prefab.");
            SetOnClick(restart, ui.TryAgain);
            Button levelSelect = FindButtonDeep(screen, "Level Select");
            levelSelect.transform.SetParent(row, false);
            Carved(levelSelect.transform, Carving.Madera, "elegir nivel", 150f, 45f);
            restartT.SetSiblingIndex(0);

            var view = GetOrAdd<PauseView>(screen.gameObject);

            Transform footer = Child(panel, "Footer", false) ?? NewChild(panel, "Footer").transform;
            var footerLayout = Layout<HorizontalLayoutGroup>(footer.gameObject);
            footerLayout.childAlignment = TextAnchor.MiddleCenter;
            footerLayout.childControlWidth = false;
            Element(footer.gameObject, 380f * U, 52f * U);
            Transform exitT = Child(footer, "Exit Link", false) ?? NewChild(footer, "Exit Link").transform;
            Button exit = Carved(exitT, Carving.Pergamino, "salir al menú", 150f, 45f);
            SetOnClick(exit, view.ShowConfirm);

            Transform confirm = Child(panel, "Confirm", false) ?? NewChild(panel, "Confirm").transform;
            var confirmLayout = Layout<VerticalLayoutGroup>(confirm.gameObject);
            confirmLayout.childAlignment = TextAnchor.UpperCenter;
            confirmLayout.childControlWidth = false;
            confirmLayout.spacing = 6f * U;
            Fit(confirm.gameObject, false, true);
            ((RectTransform)confirm).sizeDelta = new Vector2(380f * U, 90f * U);
            Transform divider = Child(confirm, "Divider", false) ?? NewChild(confirm, "Divider").transform;
            Image dividerImage = GetOrAdd<Image>(divider.gameObject);
            dividerImage.sprite = null;
            dividerImage.color = HudTheme.BoneShade;
            dividerImage.raycastTarget = false;
            Element(divider.gameObject, 340f * U, 2f * U);
            Transform warning = Child(confirm, "Warning", false) ?? NewChild(confirm, "Warning").transform;
            var warningLayout = Layout<HorizontalLayoutGroup>(warning.gameObject);
            warningLayout.childAlignment = TextAnchor.MiddleCenter;
            warningLayout.spacing = 8f * U;
            Element(warning.gameObject, 380f * U, 24f * U);
            Transform oldSkull = Child(warning, "Skull", false);
            if (oldSkull != null) Object.DestroyImmediate(oldSkull.gameObject);
            Image icon = Icon(warning, "Aviso", Sp("est_aviso"), 22f * U, Color.white);
            icon.transform.SetSiblingIndex(0);
            TMP_Text warningText = Text(warning, "Warning Text", "¿salir? se pierde esta partida", fontSemi, 15f * U, HudTheme.Contour);
            Style(warningText, fontSemi, 15f * U, HudTheme.Contour, TextAlignmentOptions.Left);
            Transform answers = Child(confirm, "Answers", false) ?? NewChild(confirm, "Answers").transform;
            var answersLayout = Layout<HorizontalLayoutGroup>(answers.gameObject);
            answersLayout.childControlWidth = false;
            answersLayout.childAlignment = TextAnchor.MiddleCenter;
            answersLayout.spacing = 14f * U;
            Element(answers.gameObject, 380f * U, 52f * U);
            Transform stayT = Child(answers, "Stay Button", false) ?? NewChild(answers, "Stay Button").transform;
            Button stay = Carved(stayT, Carving.Madera, "no, me quedo", 150f, 45f);
            SetOnClick(stay, view.HideConfirm);
            Button mainMenu = FindButtonDeep(screen, "Main Menu");
            mainMenu.transform.SetParent(answers, false);
            Carved(mainMenu.transform, Carving.Musgo, "sí, salir", 150f, 45f);
            stayT.SetSiblingIndex(0);
            confirm.gameObject.SetActive(false);

            // Order: decorations first (behind), then the content column.
            bg.transform.SetSiblingIndex(0);
            status.transform.SetSiblingIndex(1);
            resume.transform.SetSiblingIndex(2);
            row.SetSiblingIndex(3);
            footer.SetSiblingIndex(4);
            confirm.SetSiblingIndex(5);
            title.SetAsLastSibling();

            view.status = status;
            view.footer = footer.gameObject;
            view.confirm = confirm.gameObject;
        }


        // ------------------------------------------------------------------ helpers

    }
}

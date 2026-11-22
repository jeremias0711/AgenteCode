using System.Collections.Generic;
using System.IO;
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
            "¡¿·áéíóúüñÁÉÍÓÚÜÑ";

        private static TMP_FontAsset fontBold, fontSemi, fontTitle;
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
                CreateSkin();
                CreateCollectionAssets();
                AssetDatabase.SaveAssets();
                return SkinPrefab() && SkinMainMenu() && SkinLevelSelect();
            }
            catch (System.Exception e)
            {
                Debug.LogError(Tag + "FAILED: " + e);
                return false;
            }
        }

        // ------------------------------------------------------------------ fonts

        private static void CreateFonts()
        {
            fontBold = FontAsset("Fredoka-Bold");
            fontSemi = FontAsset("Fredoka-SemiBold");
            fontTitle = FontAsset("MedievalSharp-Regular");
        }

        private static TMP_FontAsset FontAsset(string file)
        {
            string assetPath = FontDir + "/" + file + " SDF.asset";
            TMP_FontAsset existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
            if (existing != null && existing.atlasPopulationMode == AtlasPopulationMode.Static
                && existing.characterTable.Count > 0)
            {
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

            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(font, 64, 6, GlyphRenderMode.SDFAA, 1024, 1024,
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
        }

        private static PanelSpec Exact(string name, Color fill, float size, float border, bool glow = false, float pad = 0f)
        {
            return new PanelSpec { name = name, fill = fill, radius = size * 0.5f, border = border, exactW = size, exactH = size,
                glow = glow, pad = pad };
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
            var specs = new List<PanelSpec>
            {
                Relief("panel_surface", HudTheme.Surface, 14f),           // gold pill, placement hint
                Flat("panel_deep", HudTheme.PanelDeep, 14f, 3f),          // wave indicator
                Flat("tray_deep", HudTheme.PanelDeep, 20f, 3f),           // tower tray
                Flat("pill_deep", HudTheme.PanelDeep, 12f, 0f),           // wave banner subtitle
                Relief("button_primary", HudTheme.Primary, 10f),          // square button (pause)
                Relief("plate_surface", HudTheme.Surface, 8f),            // plate body ("cancelar")
                Relief("card_normal", HudTheme.Surface, 12f, 4f),
                Relief("card_selected", HudTheme.Primary, 12f, 4f, true),
                Relief("card_nogold", HudTheme.Panel, 12f, 4f),
                Flat("bar_track", HudTheme.Panel, 7f, 2f),
                Flat("bar_fill", HudTheme.Primary, 5f, 0f),
                Flat("shine", HudTheme.SurfaceLight, 2f, 0f),
                new PanelSpec { name = "ribbon_band", fill = HudTheme.Primary, radius = 6f, border = 3f,
                    hiPx = 4f, hiAlpha = 0.30f, loPx = 5f, loAlpha = 0.40f },
                Relief("plate_primary", HudTheme.Primary, 8f),            // main plate ("nueva partida", "continuar")
                Flat("shine_primary", HudTheme.PrimaryLight, 2f, 0f),
                Flat("brand_deep", HudTheme.PanelDeep, 24f, 3f),          // main menu brand block
                Relief("level_card", HudTheme.Surface, 14f),              // level select cards ("Nivel v2"): completado
                Relief("level_next", HudTheme.Primary, 14f),              // siguiente
                Relief("level_locked", HudTheme.Panel, 14f),              // bloqueado
                new PanelSpec { name = "level_ring", fill = HudTheme.Gold, radius = 14f, pad = 4f, ringOnly = true },
                Flat("detail_deep", HudTheme.PanelDeep, 22f, 3f),         // level detail panel
                Relief("button_panel", HudTheme.Panel, 10f),              // square button, panel colour (back)
                new PanelSpec { name = "panel_bone", fill = HudTheme.Surface, radius = 22f, border = 4f, pad = 20f,
                    loPx = 6f, loAlpha = 1f, loColor = HudTheme.BoneShade,
                    softY = 8f, softBlur = 18f, softAlpha = 0.55f },     // pause overlay
                // Collection (P6) and tutorial (P2).
                Relief("card_light", HudTheme.SurfaceLight, 12f, 4f),     // collectible: conseguido
                Exact("circle_surface", HudTheme.Surface, 42f, 2.5f),     // collectible portrait
                Exact("circle_dark", HudTheme.Hex(0x5C544C), 42f, 2.5f),  // portrait of the viewed card (acento oscuro)
                Exact("circle_deep", HudTheme.PanelDeep, 42f, 2.5f),      // portrait of a locked card
                Exact("portrait_big", HudTheme.Primary, 78f, 4f, true, 4f), // sheet portrait with gold ring
                Exact("badge_done", HudTheme.Primary, 28f, 2f),
                Exact("badge_todo", HudTheme.Panel, 28f, 2f),
                Flat("tab_active", HudTheme.Primary, 10f, 3f),
                Flat("tab_inactive", HudTheme.BoneShade, 10f, 3f),
                Flat("page", HudTheme.Surface, 12f, 2.5f),
                Flat("chip", HudTheme.Hex(0x638084), 10f, 2f),
                Flat("lesson_chip", HudTheme.SurfaceLight, 12f, 2.5f),
                Flat("card_deep16", HudTheme.PanelDeep, 16f, 3f),
                new PanelSpec { name = "book_deep", fill = HudTheme.PanelDeep, radius = 22f, border = 4f, pad = 20f,
                    loPx = 6f, loAlpha = 1f, loColor = HudTheme.BoneShade,
                    softY = 8f, softBlur = 18f, softAlpha = 0.55f },
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
                        Color body = Color.Lerp(HudTheme.Contour, p.fill, inner);
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

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
                Debug.Log(Tag + (saved ? "UI Canvas prefab skinned and saved." : "Could not save the UI Canvas prefab."));
                return saved;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void SkinGold(Transform panel)
        {
            Place(panel, new Vector2(0f, 1f), new Vector2(32f, -20f) * U, new Vector2(138f * U, 52f * U));
            SetImage(panel.gameObject, Sp("panel_surface"));
            var row = Layout<HorizontalLayoutGroup>(panel.gameObject);
            row.padding = new RectOffset(22, 38, 0, 8);
            row.spacing = 8f * U;
            Fit(panel.gameObject, true, false);

            Image coin = Icon(panel, "Coin Icon", Sp("icon_coin"), 30f * U, Color.white);
            coin.transform.SetSiblingIndex(0);
            TMP_Text value = Child(panel, "Gold Value", true).GetComponent<TMP_Text>();
            Style(value, fontBold, 24f * U, HudTheme.Contour, TextAlignmentOptions.Left);
            value.rectTransform.sizeDelta = new Vector2(value.rectTransform.sizeDelta.x, 26f * U);
            value.transform.SetSiblingIndex(1);
            TMP_Text label = Child(panel, "Gold Label", true).GetComponent<TMP_Text>();
            Style(label, fontSemi, 17f * U, HudTheme.Contour, TextAlignmentOptions.Left);
            label.rectTransform.sizeDelta = new Vector2(label.rectTransform.sizeDelta.x, 22f * U);
            label.text = "oro";
            label.transform.SetSiblingIndex(2);
        }

        private static void BuildWaveStatus(Transform canvas)
        {
            Transform rootT = Child(canvas, "Wave Status", false) ?? NewChild(canvas, "Wave Status").transform;
            Place(rootT, new Vector2(0.5f, 1f), new Vector2(0f, -20f * U), new Vector2(266f * U, 48f * U));
            var view = GetOrAdd<WaveStatusView>(rootT.gameObject);

            Transform pill = Child(rootT, "Pill", false) ?? NewChild(rootT, "Pill").transform;
            Place(pill, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(266f * U, 48f * U));
            SetImage(pill.gameObject, Sp("panel_deep"));
            var row = Layout<HorizontalLayoutGroup>(pill.gameObject);
            row.padding = new RectOffset(30, 38, 0, 0);
            row.spacing = 10f * U;
            Fit(pill.gameObject, true, false);

            Icon(pill, "Skull Icon", Sp("icon_skull"), 24f * U, HudTheme.TextLight);
            TMP_Text label = Text(pill, "Wave Label", "oleada 1", fontBold, 18f * U, HudTheme.TextLight);

            Transform track = Child(pill, "Bar", false) ?? NewChild(pill, "Bar").transform;
            SetImage(track.gameObject, Sp("bar_track"));
            Element(track.gameObject, 84f * U, 14f * U);
            Transform fill = Child(track, "Fill", false) ?? NewChild(track, "Fill").transform;
            var fillRect = (RectTransform)fill;
            fillRect.anchorMin = new Vector2(0f, 0.5f);
            fillRect.anchorMax = new Vector2(0f, 0.5f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.anchoredPosition = new Vector2(2f * U, 0f);
            fillRect.sizeDelta = new Vector2(0f, 10f * U);
            SetImage(fill.gameObject, Sp("bar_fill"));

            TMP_Text count = Text(pill, "Wave Count", "0/0", fontSemi, 13f * U, HudTheme.Surface);
            rootT.SetSiblingIndex(Child(canvas, "Gold Panel", true).GetSiblingIndex() + 1);

            view.content = pill.gameObject;
            view.waveLabel = label;
            view.barFill = fillRect;
            view.countLabel = count;
        }

        private static void SkinPause(Transform button)
        {
            Place(button, new Vector2(1f, 1f), new Vector2(-32f, -20f) * U, new Vector2(48f * U, 52f * U));
            SetImage(button.gameObject, Sp("button_primary"));
            NeutralTint(button.GetComponent<Button>());
            Transform oldLabel = Child(button, "Text (TMP)", false);
            if (oldLabel != null) oldLabel.gameObject.SetActive(false);
            Image icon = Icon(button, "Pause Icon", Sp("icon_pause"), 24f * U, HudTheme.TextLight);
            Center(icon.rectTransform, new Vector2(0f, 2f * U));
        }

        private static void SkinWaveBanner(Transform canvas, Transform waveText)
        {
            const float top = -96f * U;
            Transform banner = Child(canvas, "Wave Banner", false) ?? NewChild(canvas, "Wave Banner").transform;
            Place(banner, new Vector2(0.5f, 1f), new Vector2(0f, top), new Vector2(260f * U, 56f * U));

            Image tailL = Img(banner, "Tail Left", Sp("ribbon_tail_left"));
            Corner(tailL.rectTransform, new Vector2(0f, 1f), new Vector2(-26f * U, -14f * U), new Vector2(44f, 40f) * U);
            Image tailR = Img(banner, "Tail Right", Sp("ribbon_tail_right"));
            Corner(tailR.rectTransform, new Vector2(1f, 1f), new Vector2(26f * U, -14f * U), new Vector2(44f, 40f) * U);
            Image band = Img(banner, "Band", Sp("ribbon_band"));
            band.type = Image.Type.Sliced;
            var bandRect = band.rectTransform;
            bandRect.anchorMin = new Vector2(0f, 1f);
            bandRect.anchorMax = new Vector2(1f, 1f);
            bandRect.pivot = new Vector2(0.5f, 1f);
            bandRect.anchoredPosition = new Vector2(0f, -3f * U);
            bandRect.sizeDelta = new Vector2(0f, 46f * U);

            Transform sub = Child(banner, "Subtitle", false) ?? NewChild(banner, "Subtitle").transform;
            Place(sub, new Vector2(0.5f, 1f), new Vector2(0f, -62f * U), new Vector2(200f * U, 34f * U));
            SetImage(sub.gameObject, Sp("pill_deep"));
            var subRow = Layout<HorizontalLayoutGroup>(sub.gameObject);
            subRow.padding = new RectOffset(28, 28, 12, 12);
            Fit(sub.gameObject, true, true);
            TMP_Text subText = Text(sub, "Subtitle Text", "12 enemigos", fontSemi, 17f * U, HudTheme.Gold);

            banner.SetSiblingIndex(waveText.GetSiblingIndex()); // right before the text: drawn behind it
            banner.gameObject.SetActive(false);

            TMP_Text text = waveText.GetComponent<TMP_Text>();
            Place(waveText, new Vector2(0.5f, 1f), new Vector2(0f, top - 3f * U), new Vector2(260f * U, 46f * U));
            Style(text, fontTitle, 26f * U, HudTheme.TextLight, TextAlignmentOptions.Center);
            text.margin = new Vector4(0f, 0f, 0f, 4f * U);
            text.text = "¡oleada 1!";

            var view = GetOrAdd<WaveBannerView>(waveText.gameObject);
            view.banner = banner.gameObject;
            view.subtitle = sub.gameObject;
            view.subtitleText = subText;
        }

        private static RectTransform SkinTray(Transform tray)
        {
            Place(tray, new Vector2(0.5f, 0f), new Vector2(0f, 14f * U), new Vector2(408f * U, 108f * U));
            SetImage(tray.gameObject, Sp("tray_deep"));
            var row = Layout<HorizontalLayoutGroup>(tray.gameObject);
            row.padding = new RectOffset(18, 18, 18, 14);
            row.spacing = 2f * U;
            row.childAlignment = TextAnchor.UpperCenter;
            row.childControlWidth = false;
            row.childControlHeight = false;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            Fit(tray.gameObject, true, false); // levels that unlock fewer towers get a narrower tray

            foreach (TowerButton towerButton in tray.GetComponentsInChildren<TowerButton>(true))
            {
                SkinCard(towerButton);
            }

            Image vine = Img(tray, "Vine", Sp("vine"));
            Ignore(vine.gameObject);
            Corner(vine.rectTransform, new Vector2(0.5f, 1f), new Vector2(-24f * U, 14f * U), new Vector2(320f, 30f) * U, new Vector2(0.5f, 1f));
            CornerOrnament(tray, "Corner TL", new Vector2(0f, 1f), 0f);
            CornerOrnament(tray, "Corner TR", new Vector2(1f, 1f), -90f);
            CornerOrnament(tray, "Corner BR", new Vector2(1f, 0f), 180f);
            CornerOrnament(tray, "Corner BL", new Vector2(0f, 0f), 90f);
            Image mushrooms = Img(tray, "Mushrooms", Sp("mushrooms"));
            Ignore(mushrooms.gameObject);
            Corner(mushrooms.rectTransform, new Vector2(0f, 0f), new Vector2(-50f * U, 3f * U), new Vector2(64.8f, 43.2f) * U, new Vector2(0f, 0f));
            mushrooms.transform.SetAsLastSibling();
            return (RectTransform)tray;
        }

        private static void SkinCard(TowerButton towerButton)
        {
            Transform card = towerButton.transform;
            var rect = (RectTransform)card;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(96f, 92f) * U; // 88x80 card + 4 px glow padding + 4 px drop shadow
            rect.localScale = Vector3.one;
            Image bg = SetImage(card.gameObject, Sp("card_normal"));
            NeutralTint(card.GetComponent<Button>());

            Transform oldLabel = Child(card, "Text (TMP)", false);
            if (oldLabel != null) oldLabel.gameObject.SetActive(false);

            string key = card.name.ToLowerInvariant();
            string iconName = key.Contains("slow") ? "icon_slow" : key.Contains("bomb") ? "icon_bomb"
                : key.Contains("triple") ? "icon_triple" : "icon_cannon";
            // Phase 11: the card shows the legend that represents the defender ("mohán", "madremonte"...).
            LegendId legend = LegendRoster.TryGet(towerButton.towerToPlace, out LegendId found)
                ? found : LegendRoster.FromTowerName(key);
            string display = LegendRoster.CardName(legend);

            Image icon = Icon(card, "Icon", Sp(iconName), 28f * U, HudTheme.Contour);
            Center(icon.rectTransform, new Vector2(0f, 21f * U));
            TMP_Text name = Text(card, "Name", display, fontBold, 15f * U, HudTheme.Contour);
            name.alignment = TextAlignmentOptions.Center;
            name.enableAutoSizing = true; // "hojarasquín" / "madremonte" fit the 88 px card
            name.fontSizeMin = 10f * U;
            name.fontSizeMax = 15f * U;
            name.textWrappingMode = TextWrappingModes.NoWrap;
            Center(name.rectTransform, new Vector2(0f, -3.5f * U), new Vector2(88f * U, 17f * U));

            Transform cost = Child(card, "Cost", false) ?? NewChild(card, "Cost").transform;
            Center((RectTransform)cost, new Vector2(0f, -22.5f * U), new Vector2(60f * U, 17f * U));
            var costRow = Layout<HorizontalLayoutGroup>(cost.gameObject);
            costRow.spacing = 3f * U;
            costRow.childAlignment = TextAnchor.MiddleCenter;
            Fit(cost.gameObject, true, false);
            Image coin = Icon(cost, "Coin", Sp("icon_coin"), 13f * U, Color.white);
            string costValue = towerButton.towerToPlace != null ? towerButton.towerToPlace.cost.ToString() : "0";
            TMP_Text costText = Text(cost, "Cost Text", costValue, fontSemi, 13f * U, HudTheme.Contour);

            var view = GetOrAdd<TowerCardView>(card.gameObject);
            view.displayName = display;
            view.background = bg;
            view.icon = icon;
            view.coin = coin;
            view.nameText = name;
            view.costText = costText;
            view.normalSprite = Sp("card_normal");
            view.selectedSprite = Sp("card_selected");
            view.noGoldSprite = Sp("card_nogold");
        }

        private static void BuildPlacementHint(Transform canvas, RectTransform tray)
        {
            Transform rootT = Child(canvas, "Placement Hint", false) ?? NewChild(canvas, "Placement Hint").transform;
            Place(rootT, new Vector2(0.5f, 0f), new Vector2(0f, 132f * U), new Vector2(559f * U, 66f * U));
            var view = GetOrAdd<PlacementHintView>(rootT.gameObject);

            Transform panel = Child(rootT, "Panel", false) ?? NewChild(rootT, "Panel").transform;
            Place(panel, new Vector2(0.5f, 0f), Vector2.zero, new Vector2(559f * U, 66f * U));
            ((RectTransform)panel).pivot = new Vector2(0.5f, 0f);
            SetImage(panel.gameObject, Sp("panel_surface"));
            var row = Layout<HorizontalLayoutGroup>(panel.gameObject);
            row.padding = new RectOffset(38, 22, 18, 18);
            row.spacing = 12f * U;
            Fit(panel.gameObject, true, true);

            TMP_Text message = Text(panel, "Message", "toca una casilla para construir la torre cañón", fontBold, 18f * U, HudTheme.Contour);

            Transform plate = Child(panel, "Cancel Button", false) ?? NewChild(panel, "Cancel Button").transform;
            Button button = Plate(plate, false, false, "cancelar", 140f);
            SetOnClick(button, view.Cancel);

            rootT.SetSiblingIndex(tray.GetSiblingIndex() + 1);
            view.panel = panel.gameObject;
            view.message = message;
            panel.gameObject.SetActive(false);
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

        // ------------------------------------------------------------------ HUD: book, upgrade panel, end screens

        /// <summary>"libro sagrado" integrity pill below the gold pill (real castle health, BookIntegrityView).</summary>
        private static void BuildBookIntegrity(Transform canvas)
        {
            Transform rootT = Child(canvas, "Book Integrity", false) ?? NewChild(canvas, "Book Integrity").transform;
            Place(rootT, new Vector2(0f, 1f), new Vector2(32f, -80f) * U, new Vector2(170f, 34f) * U);
            var view = GetOrAdd<BookIntegrityView>(rootT.gameObject);

            Transform pill = Child(rootT, "Pill", false) ?? NewChild(rootT, "Pill").transform;
            Place(pill, new Vector2(0f, 1f), Vector2.zero, new Vector2(170f, 34f) * U);
            SetImage(pill.gameObject, Sp("panel_deep"));
            var row = Layout<HorizontalLayoutGroup>(pill.gameObject);
            row.padding = new RectOffset(26, 28, 0, 0);
            row.spacing = 8f * U;
            Fit(pill.gameObject, true, false);
            Text(pill, "Label", "libro sagrado", fontSemi, 13f * U, HudTheme.TextLight);
            Transform track = Child(pill, "Bar", false) ?? NewChild(pill, "Bar").transform;
            SetImage(track.gameObject, Sp("bar_track"));
            Element(track.gameObject, 60f * U, 14f * U);
            Transform fill = Child(track, "Fill", false) ?? NewChild(track, "Fill").transform;
            var fillRect = (RectTransform)fill;
            fillRect.anchorMin = fillRect.anchorMax = new Vector2(0f, 0.5f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.anchoredPosition = new Vector2(2f * U, 0f);
            fillRect.sizeDelta = new Vector2(56f * U, 10f * U);
            SetImage(fill.gameObject, Sp("bar_fill"));
            TMP_Text value = Text(pill, "Value", "100%", fontSemi, 13f * U, HudTheme.Surface);

            rootT.SetSiblingIndex(Child(canvas, "Gold Panel", true).GetSiblingIndex() + 1);
            view.content = pill.gameObject;
            view.barFill = fillRect;
            view.valueText = value;
        }

        /// <summary>Defender panel (upgrade / retire): deep panel, gold label and plates; actions stay in TowerUpgradePanel.</summary>
        private static void SkinUpgradePanel(Transform canvas)
        {
            Transform panel = Child(canvas, "Tower Upgrade Panel", true);
            Place(panel, new Vector2(0.5f, 1f), new Vector2(0f, -84f * U), new Vector2(700f, 100f) * U);
            Image bg = SetImage(panel.gameObject, Sp("detail_deep"));
            bg.raycastTarget = true; // taps on the panel never reach the map
            var row = Layout<HorizontalLayoutGroup>(panel.gameObject);
            row.padding = new RectOffset(Mathf.RoundToInt(18f * U), Mathf.RoundToInt(14f * U), Mathf.RoundToInt(32f * U), Mathf.RoundToInt(14f * U));
            row.spacing = 10f * U;
            row.childControlWidth = false;
            row.childAlignment = TextAnchor.LowerLeft;
            Fit(panel.gameObject, true, true);

            TMP_Text label = Text(panel, "Label", "MEJORAR DEFENSOR", fontSemi, 12f * U, HudTheme.Gold);
            label.characterSpacing = 10f;
            Ignore(label.gameObject);
            Corner(label.rectTransform, new Vector2(0f, 1f), new Vector2(20f, -11f) * U, new Vector2(400f, 15f) * U);
            TowerUpgradePanel upgradePanel = panel.GetComponentInParent<TowerUpgradePanel>(true)
                ?? canvas.GetComponentInChildren<TowerUpgradePanel>(true);
            if (upgradePanel != null) upgradePanel.titleText = label; // "MEJORAR · EL MOHÁN" at runtime

            Transform range = Child(panel, "Range Upgrade Button", true);
            Transform firerate = Child(panel, "Firerate Upgrade Button", true);
            Transform remove = Child(panel, "Remove Tower Button", true);
            Transform close = Child(panel, "Close Button", true);
            Plate(range, false, false, "alcance", 190f);
            Plate(firerate, false, false, "cadencia", 190f);
            Plate(remove, false, false, "retirar · +50 oro", 190f);
            Transform oldClose = Child(close, "Text (TMP)", false);
            if (oldClose != null) oldClose.gameObject.SetActive(false);
            SquareButton(close, "button_panel", Sp("icon_close"));
            DisableMeshEffects(panel.gameObject);
            // Reading order: upgrades, retire, close.
            range.SetAsLastSibling();
            firerate.SetAsLastSibling();
            remove.SetAsLastSibling();
            close.SetAsLastSibling();
        }

        /// <summary>Victory / defeat panels with the pause components: bone panel, ribbon title, message, plates.</summary>
        private static void SkinEndScreen(Transform canvas, string screenName, string titleName, string title, string message,
            (string button, string label, bool primary)[] buttons)
        {
            Transform screen = Child(canvas, screenName, true);
            Place(screen, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(400f, 240f) * U);
            Image rootImage = screen.GetComponent<Image>();
            if (rootImage != null)
            {
                rootImage.sprite = null;
                rootImage.color = new Color(1f, 1f, 1f, 0f);
                rootImage.raycastTarget = true;
            }
            DisableMeshEffects(screen.gameObject);

            Image dim = Img(screen, "Dim", null);
            dim.color = new Color(0.14f, 0.19f, 0.18f, 0.55f);
            dim.raycastTarget = true;
            Center(dim.rectTransform, Vector2.zero, new Vector2(6000f, 6000f));
            dim.transform.SetSiblingIndex(0);
            Image bg = BonePanelBackground(screen);
            bg.transform.SetSiblingIndex(1);
            Corners(screen, 40f, 4f);

            Transform ribbon = Ribbon(screen, "Title Ribbon", Child(screen, titleName, false)?.GetComponent<TMP_Text>(), title);
            Corner((RectTransform)ribbon, new Vector2(0.5f, 1f), new Vector2(0f, 30f * U), new Vector2(260f, 56f) * U, new Vector2(0.5f, 1f));
            TMP_Text msg = Text(screen, "Message", message, fontSemi, 17f * U, HudTheme.Contour);
            msg.alignment = TextAlignmentOptions.Center;
            Corner(msg.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -50f * U), new Vector2(336f, 22f) * U, new Vector2(0.5f, 1f));

            (string button, string label, bool primary) main = buttons[0];
            Transform mainT = Child(screen, main.button, true);
            Plate(mainT, main.primary, true, main.label, 336f);
            Corner((RectTransform)mainT, new Vector2(0.5f, 1f), new Vector2(0f, -86f * U), new Vector2(336f, 62f) * U, new Vector2(0.5f, 1f));
            Transform row = Child(screen, "Row", false) ?? NewChild(screen, "Row").transform;
            Corner((RectTransform)row, new Vector2(0.5f, 1f), new Vector2(0f, -158f * U), new Vector2(336f, 52f) * U, new Vector2(0.5f, 1f));
            var rowLayout = Layout<HorizontalLayoutGroup>(row.gameObject);
            rowLayout.childControlWidth = false;
            rowLayout.spacing = 12f * U;
            for (int i = 1; i < buttons.Length; i++)
            {
                Transform b = Child(screen, buttons[i].button, false) ?? Child(row, buttons[i].button, true);
                b.SetParent(row, false);
                Plate(b, buttons[i].primary, false, buttons[i].label, 162f);
            }
            ribbon.SetAsLastSibling();
        }

        private static void SkinNotEnoughWarning(Transform canvas)
        {
            Transform warning = Child(canvas, "Not Enough Money", false);
            if (warning == null) return;
            TMP_Text text = warning.GetComponentInChildren<TMP_Text>(true);
            if (text == null) return;
            text.text = "oro insuficiente";
            Style(text, fontBold, 24f * U, HudTheme.Gold, TextAlignmentOptions.Center);
        }

        // ------------------------------------------------------------------ main menu (P1)

        private const string MainMenuScenePath = "Assets/Course/Scenes/Main Menu.unity";

        private static bool SkinMainMenu()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(MainMenuScenePath,
                UnityEditor.SceneManagement.OpenSceneMode.Single);
            Canvas canvas = null;
            foreach (Canvas c in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c.renderMode == RenderMode.ScreenSpaceOverlay
                    && (c.transform.Find("Title") != null || c.transform.Find("Brand Block") != null)) canvas = c;
            }
            if (canvas == null) throw new System.Exception("Main Menu overlay canvas with 'Title' not found.");
            Transform root = canvas.transform;

            // Brand block (left).
            Transform brand = Child(root, "Brand Block", false) ?? NewChild(root, "Brand Block").transform;
            Place(brand, new Vector2(0f, 0.5f), new Vector2(32f * U, 7f * U), new Vector2(388f, 328f) * U);
            SetImage(brand.gameObject, Sp("brand_deep"));

            Transform clip = Child(brand, "Clip", false) ?? NewChild(brand, "Clip").transform;
            Stretch((RectTransform)clip, Vector4.zero);
            Image clipImage = SetImage(clip.gameObject, Sp("brand_deep"));
            clipImage.raycastTarget = false;
            GetOrAdd<Mask>(clip.gameObject).showMaskGraphic = false;
            Image horizon = Img(clip, "Forest Horizon", Sp("forest_horizon"));
            Corner(horizon.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 3f * U), new Vector2(388f, 120f) * U, new Vector2(0.5f, 0f));
            horizon.color = new Color(1f, 1f, 1f, 0.6f);

            Transform content = Child(brand, "Content", false) ?? NewChild(brand, "Content").transform;
            Stretch((RectTransform)content, Vector4.zero);
            var column = Layout<VerticalLayoutGroup>(content.gameObject);
            column.childAlignment = TextAnchor.UpperCenter;
            column.childControlWidth = false;
            column.padding = new RectOffset(0, 0, Mathf.RoundToInt(21f * U), 0);
            column.spacing = 6f * U;

            Image emblem = Img(content, "Emblem", Sp("emblem"));
            emblem.preserveAspect = true;
            Element(emblem.gameObject, 150f * U, 120f * U);
            TMP_Text title = Child(root, "Title", false) != null ? Child(root, "Title", false).GetComponent<TMP_Text>()
                : Child(content, "Title", true).GetComponent<TMP_Text>();
            title.transform.SetParent(content, false);
            title.text = "Towers of Defencopolis";
            Style(title, fontTitle, 50f * U, HudTheme.TextLight, TextAlignmentOptions.Center);
            title.textWrappingMode = TextWrappingModes.Normal;
            title.lineSpacing = -8f;
            Element(title.gameObject, 334f * U, 104f * U);
            Image runes = Img(content, "Runes", Sp("runes"));
            Element(runes.gameObject, 200f * U, 18f * U);
            TMP_Text tagline = Text(content, "Tagline", "defiende el libro sagrado · 6 niveles", fontSemi, 17f * U, HudTheme.Surface);
            tagline.alignment = TextAlignmentOptions.Center;
            Element(tagline.gameObject, 334f * U, 22f * U);
            emblem.transform.SetSiblingIndex(0);
            title.transform.SetSiblingIndex(1);
            runes.transform.SetSiblingIndex(2);
            tagline.transform.SetSiblingIndex(3);

            Image fireflies = Img(brand, "Fireflies", Sp("fireflies"));
            Corner(fireflies.rectTransform, new Vector2(0f, 1f), new Vector2(67f * U, -7f * U), new Vector2(240f, 120f) * U);
            fireflies.color = new Color(1f, 1f, 1f, 0.9f);
            GetOrAdd<FireflyTwinkle>(fireflies.gameObject);
            Corners(brand, 40f, 5f);
            Image mushrooms = Img(brand, "Mushrooms", Sp("mushrooms"));
            Corner(mushrooms.rectTransform, new Vector2(1f, 0f), new Vector2(-64f * U, -14f * U), new Vector2(72f, 48f) * U, new Vector2(0f, 0f));
            clip.SetSiblingIndex(0);
            content.SetSiblingIndex(1);

            // Menu column (right).
            Transform menu = Child(root, "Menu", false) ?? NewChild(root, "Menu").transform;
            Place(menu, new Vector2(1f, 0.5f), new Vector2(-42f * U, -14f * U), new Vector2(300f, 320f) * U);
            var stack = Layout<VerticalLayoutGroup>(menu.gameObject);
            stack.childAlignment = TextAnchor.UpperCenter;
            stack.childControlWidth = false;
            stack.spacing = 10f * U - 8f; // the 4 px plate shadow is part of each element
            Button newGame = FindButtonDeep(root, "New Game");
            Button tutorial = FindButtonDeep(root, "Tutorial");
            Button quit = FindButtonDeep(root, "Quit Game");
            newGame.transform.SetParent(menu, false);
            tutorial.transform.SetParent(menu, false);
            quit.transform.SetParent(menu, false);
            Plate(newGame.transform, true, true, "nueva partida", 300f);
            Image vine = Img(newGame.transform, "Vine", Sp("vine"));
            Corner(vine.rectTransform, new Vector2(0f, 1f), new Vector2(6f * U, 14f * U), new Vector2(288f, 30f) * U, new Vector2(0f, 1f));
            vine.transform.SetSiblingIndex(newGame.transform.childCount - 2);
            Button tutorialPlate = Plate(tutorial.transform, false, false, "tutorial", 280f);
            Link(quit.transform, "salir del juego", HudTheme.SurfaceLight, 280f);
            var view = GetOrAdd<MainMenuView>(root.gameObject);

            // Phase 10 accesses: level select and collection.
            Transform levelsT = Child(menu, "Level Select Button", false) ?? NewChild(menu, "Level Select Button").transform;
            Button levels = Plate(levelsT, false, false, "elegir nivel", 280f);
            SetOnClick(levels, view.OpenLevelSelect);
            Transform collectionT = Child(menu, "Collection Button", false) ?? NewChild(menu, "Collection Button").transform;
            Button collectionPlate = Plate(collectionT, false, false, "colección", 280f);
            SetOnClick(collectionPlate, view.OpenCollection);

            newGame.transform.SetSiblingIndex(0);
            levelsT.SetSiblingIndex(1);
            tutorial.transform.SetSiblingIndex(2);
            collectionT.SetSiblingIndex(3);
            quit.transform.SetSiblingIndex(4);

            view.tutorialLabel = tutorialPlate.GetComponentInChildren<TMP_Text>(true);
            view.collectionLabel = collectionPlate.GetComponentInChildren<TMP_Text>(true);

            // Collection screen (P6): frame built once here, cards rebuilt from the catalog every time it opens.
            Transform collectionScreen = Child(root, "Collection Screen", false) ?? NewChild(root, "Collection Screen").transform;
            collectionScreen.gameObject.SetActive(true);
            var collection = GetOrAdd<CollectionController>(collectionScreen.gameObject);
            collection.catalog = AssetDatabase.LoadAssetAtPath<CollectionCatalog>(CatalogPath);
            collection.hideWhileOpen = new[] { brand.gameObject, menu.gameObject };
            collection.BuildFrame((button, method) => SetOnClick(button,
                (UnityEngine.Events.UnityAction)System.Delegate.CreateDelegate(typeof(UnityEngine.Events.UnityAction), collection, method)));
            collection.BuildPreview();
            collectionScreen.SetAsLastSibling();
            collectionScreen.gameObject.SetActive(false);
            view.collection = collection;

            // Corner branches over the 3D scene.
            Image branchTop = Img(root, "Branch Top", Sp("branch_corner"));
            Corner(branchTop.rectTransform, new Vector2(0f, 1f), Vector2.zero, new Vector2(133f, 133f) * U);
            Image branchBottom = Img(root, "Branch Bottom", Sp("branch_corner_bottom"));
            Corner(branchBottom.rectTransform, new Vector2(0f, 0f), Vector2.zero, new Vector2(112f, 112f) * U);
            brand.SetSiblingIndex(0);
            menu.SetSiblingIndex(1);
            collectionScreen.SetAsLastSibling(); // above the corner branches

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
            Debug.Log(Tag + (saved ? "Main Menu skinned and saved." : "Could not save the Main Menu scene."));
            return saved;
        }

        private static Button FindButtonDeep(Transform root, string name)
        {
            foreach (Button b in root.GetComponentsInChildren<Button>(true))
            {
                if (b.name == name) return b;
            }
            throw new System.Exception("Button '" + name + "' not found under '" + root.name + "'");
        }

        // ------------------------------------------------------------------ level select (P3)

        private const string LevelSelectScenePath = "Assets/Course/Scenes/Level Select.unity";

        /// <summary>
        /// "v2 · P3 Selección de nivel" without stars: grid of "Nivel v2" cards (Completado / Siguiente / Bloqueado from
        /// LevelProgress), "N de 6 completados", detail panel with "jugar", back button, ribbon and ornaments. The old
        /// world-space buttons of the 3D map are removed (their LevelSelectButton data moves to the cards) and the map
        /// stays as a still background.
        /// </summary>
        private static bool SkinLevelSelect()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(LevelSelectScenePath,
                UnityEditor.SceneManagement.OpenSceneMode.Single);

            // Screen-space layer (back button, ribbon, counter, grid, detail, ornaments). The 3D map stays as a still
            // background (LevelSelectController stops framing/scrolling when a LevelSelectView exists).
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

            Transform backT = Child(root, "Back Button", false) ?? NewChild(root, "Back Button").transform;
            Place(backT, new Vector2(0f, 1f), new Vector2(32f, -20f) * U, new Vector2(48f, 52f) * U);
            SetImage(backT.gameObject, Sp("button_panel"));
            Button back = GetOrAdd<Button>(backT.gameObject);
            NeutralTint(back);
            back.targetGraphic = backT.GetComponent<Image>();
            SetOnClick(back, hud.GoToMainMenu);
            Image backIcon = Icon(backT, "Icon", Sp("icon_back"), 24f * U, HudTheme.TextLight);
            Center(backIcon.rectTransform, new Vector2(0f, 2f * U));

            Transform ribbon = Ribbon(root, "Title Ribbon", null, "elige un nivel");
            Corner((RectTransform)ribbon, new Vector2(0f, 1f), new Vector2(120f, -16f) * U, new Vector2(260f, 56f) * U);

            // "N de 6 completados" on a dark pill (no text directly over the 3D scene).
            Transform counter = Child(root, "Completed Counter", false) ?? NewChild(root, "Completed Counter").transform;
            Place(counter, new Vector2(0f, 1f), new Vector2(414f, -27f) * U, new Vector2(173f, 34f) * U);
            SetImage(counter.gameObject, Sp("pill_deep"));
            var counterRow = Layout<HorizontalLayoutGroup>(counter.gameObject);
            counterRow.padding = new RectOffset(24, 24, 12, 12);
            Fit(counter.gameObject, true, true);
            TMP_Text completed = Text(counter, "Text", "0 de 6 completados", fontSemi, 17f * U, HudTheme.SurfaceLight);

            // Level data: from the old buttons on the 3D map (first run) or from the cards already built.
            var levels = new SortedDictionary<int, string>();
            foreach (LevelSelectButton b in Object.FindObjectsByType<LevelSelectButton>(FindObjectsInactive.Include))
            {
                if (b.levelId > 0) levels[b.levelId] = b.levelToLoad;
            }
            if (levels.Count == 0) throw new System.Exception("No LevelSelectButton found in Level Select.");

            const float Gap = 16f;
            Transform grid = Child(root, "Level Grid", false) ?? NewChild(root, "Level Grid").transform;
            // Grid and detail keep the Figma composition centred on screen (844 px frame: centre at x = 422).
            Place(grid, new Vector2(0.5f, 1f), new Vector2(32f - 422f, -96f) * U, new Vector2(482f, 256f) * U);
            ((RectTransform)grid).pivot = new Vector2(0f, 1f);
            foreach (LayoutGroup lg in grid.GetComponents<LayoutGroup>()) if (!(lg is GridLayoutGroup)) Object.DestroyImmediate(lg);
            var gridLayout = GetOrAdd<GridLayoutGroup>(grid.gameObject);
            gridLayout.cellSize = new Vector2(150f, 122f) * U;               // card + 4 px drop shadow
            gridLayout.spacing = new Vector2(Gap, Gap - 4f) * U;
            gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayout.constraintCount = 3;
            gridLayout.childAlignment = TextAnchor.UpperLeft;

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

            // Detail panel.
            Transform detail = Child(root, "Detail", false) ?? NewChild(root, "Detail").transform;
            Place(detail, new Vector2(0.5f, 1f), new Vector2(812f - 422f, -96f) * U, new Vector2(264f, 268f) * U);
            ((RectTransform)detail).pivot = new Vector2(1f, 1f);
            SetImage(detail.gameObject, Sp("detail_deep"));
            Transform clip = Child(detail, "Clip", false) ?? NewChild(detail, "Clip").transform;
            Stretch((RectTransform)clip, Vector4.zero);
            Image clipImage = SetImage(clip.gameObject, Sp("detail_deep"));
            clipImage.raycastTarget = false;
            GetOrAdd<Mask>(clip.gameObject).showMaskGraphic = false;
            Image horizon = Img(clip, "Forest Horizon", Sp("forest_horizon"));
            Corner(horizon.rectTransform, new Vector2(0f, 0f), new Vector2(0f, -17f * U), new Vector2(388f, 120f) * U, new Vector2(0f, 0f));
            horizon.color = new Color(1f, 1f, 1f, 0.5f);
            Corners(detail, 22f, 3f);

            TMP_Text label = Text(detail, "Label", "SIGUIENTE NIVEL", fontSemi, 12f * U, HudTheme.Gold);
            label.characterSpacing = 10f;
            Corner(label.rectTransform, new Vector2(0f, 1f), new Vector2(23f, -21f) * U, new Vector2(218f, 15f) * U);
            TMP_Text title = Text(detail, "Title", "nivel 1", fontTitle, 28f * U, HudTheme.TextLight);
            Corner(title.rectTransform, new Vector2(0f, 1f), new Vector2(23f, -42f) * U, new Vector2(218f, 34f) * U);
            TMP_Text waves = Text(detail, "Waves", "oleadas: 3", fontSemi, 15f * U, HudTheme.Surface);
            Corner(waves.rectTransform, new Vector2(0f, 1f), new Vector2(23f, -84f) * U, new Vector2(218f, 20f) * U);
            Transform playT = Child(detail, "Play Button", false) ?? NewChild(detail, "Play Button").transform;
            Button play = Plate(playT, true, true, "jugar", 218f);
            Corner((RectTransform)playT, new Vector2(0.5f, 0f), new Vector2(0f, 17f * U), new Vector2(218f, 62f) * U, new Vector2(0.5f, 0f));
            SetOnClick(play, view.Play);
            clip.SetSiblingIndex(0);

            // Ornaments.
            Image fireflies = Img(root, "Fireflies", Sp("fireflies"));
            Corner(fireflies.rectTransform, new Vector2(1f, 1f), new Vector2(-104f, -8f) * U, new Vector2(140f, 80f) * U);
            fireflies.color = new Color(1f, 1f, 1f, 0.9f);
            GetOrAdd<FireflyTwinkle>(fireflies.gameObject);
            Image branch = Img(root, "Branch Right", Sp("branch_corner_right"));
            Corner(branch.rectTransform, new Vector2(1f, 1f), Vector2.zero, new Vector2(112f, 112f) * U);
            Image mushrooms = Img(root, "Mushrooms", Sp("mushrooms"));
            Corner(mushrooms.rectTransform, new Vector2(0f, 0f), new Vector2(470f, 0f) * U, new Vector2(57.6f, 38.4f) * U, new Vector2(0f, 0f));

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

        private static LevelCardView BuildLevelCard(Transform grid, int levelId, string levelToLoad, LevelSelectView view)
        {
            string name = "Level " + levelId;
            Transform card = Child(grid, name, false) ?? NewChild(grid, name).transform;
            card.SetSiblingIndex(levelId - 1);
            var level = GetOrAdd<LevelSelectButton>(card.gameObject);
            level.levelId = levelId;
            level.levelToLoad = levelToLoad;
            Image bg = SetImage(card.gameObject, Sp("level_card"));
            Button button = GetOrAdd<Button>(card.gameObject);
            NeutralTint(button);
            ColorBlock colors = button.colors;
            colors.disabledColor = Color.white; // locked cards stay opaque: grey sprite + padlock show the state
            button.colors = colors;
            button.targetGraphic = bg;

            // The number is the first TMP text of the card (LevelFlowTestRunner reads its digits).
            TMP_Text number = Text(card, "Number", levelId.ToString(), fontTitle, 46f * U, HudTheme.Contour);
            number.alignment = TextAlignmentOptions.Center;
            Center(number.rectTransform, new Vector2(0f, 16f * U), new Vector2(150f, 50f) * U);
            number.transform.SetSiblingIndex(0);

            Image ring = Img(card, "Ring", Sp("level_ring"));
            ring.type = Image.Type.Sliced;
            Stretch(ring.rectTransform, new Vector4(-4f * U, 0f, -4f * U, -4f * U));
            Image check = Icon(card, "Check", Sp("icon_check"), 20f * U, HudTheme.Contour);
            Center(check.rectTransform, new Vector2(0f, -24f * U));
            TMP_Text next = Text(card, "Next Label", "¡siguiente!", fontBold, 18f * U, HudTheme.TextLight);
            next.alignment = TextAlignmentOptions.Center;
            Center(next.rectTransform, new Vector2(0f, -24f * U), new Vector2(150f, 22f) * U);
            Image padlock = Icon(card, "Padlock", Sp("icon_lock"), 20f * U, HudTheme.Muted);
            Center(padlock.rectTransform, new Vector2(0f, -24f * U));

            var cardView = GetOrAdd<LevelCardView>(card.gameObject);
            cardView.background = bg;
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

        // ------------------------------------------------------------------ pause (P5 / P5b)

        private static void SkinPauseScreen(Transform canvas)
        {
            Transform screen = Child(canvas, "Pause Screen", true);
            Transform panel = Child(screen, "Pause Panel", false) ?? NewChild(screen, "Pause Panel").transform;
            Place(panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(400f, 284f) * U);
            var column = Layout<VerticalLayoutGroup>(panel.gameObject);
            column.childAlignment = TextAnchor.UpperCenter;
            column.childControlWidth = false;
            column.padding = new RectOffset(Mathf.RoundToInt(32f * U), Mathf.RoundToInt(32f * U), Mathf.RoundToInt(46f * U), Mathf.RoundToInt(26f * U) - 8);
            column.spacing = 12f * U - 8f;
            Fit(panel.gameObject, false, true);

            Image roots = Img(panel, "Roots", Sp("roots"));
            Ignore(roots.gameObject);
            Corner(roots.rectTransform, new Vector2(0.5f, 0f), new Vector2(-4f * U, 8f * U), new Vector2(388f, 46f) * U, new Vector2(0.5f, 1f));
            roots.color = new Color(1f, 1f, 1f, 0.9f);
            Image bg = Img(panel, "Background", Sp("panel_bone"));
            bg.type = Image.Type.Sliced;
            Ignore(bg.gameObject);
            Stretch(bg.rectTransform, new Vector4(-20f * U, -28f * U, -20f * U, -20f * U));
            Corners(panel, 40f, 4f);
            Transform title = Ribbon(panel, "Title Ribbon", Child(screen, "Paused", false)?.GetComponent<TMP_Text>(), "pausa");
            Ignore(title.gameObject);
            Corner((RectTransform)title, new Vector2(0.5f, 1f), new Vector2(0f, 30f * U), new Vector2(260f, 56f) * U, new Vector2(0.5f, 1f));

            TMP_Text status = Text(panel, "Status", "nivel 1 · oleada 1 de 3", fontSemi, 17f * U, HudTheme.Contour);
            status.alignment = TextAlignmentOptions.Center;
            Element(status.gameObject, 336f * U, 22f * U);

            Button resume = FindButtonDeep(screen, "Resume");
            resume.transform.SetParent(panel, false);
            Plate(resume.transform, true, true, "continuar", 336f);

            Transform row = Child(panel, "Row", false) ?? NewChild(panel, "Row").transform;
            var rowLayout = Layout<HorizontalLayoutGroup>(row.gameObject);
            rowLayout.childControlWidth = false;
            rowLayout.spacing = 12f * U;
            Element(row.gameObject, 336f * U, 52f * U);
            Transform restartT = Child(row, "Restart Button", false) ?? NewChild(row, "Restart Button").transform;
            Button restart = Plate(restartT, false, false, "reiniciar", 162f);
            UIController ui = canvas.GetComponentInChildren<UIController>(true) ?? canvas.GetComponentInParent<UIController>();
            if (ui == null) throw new System.Exception("UIController not found in the UI Canvas prefab.");
            SetOnClick(restart, ui.TryAgain);
            Button levelSelect = FindButtonDeep(screen, "Level Select");
            levelSelect.transform.SetParent(row, false);
            Plate(levelSelect.transform, false, false, "elegir nivel", 162f);
            restartT.SetSiblingIndex(0);

            var view = GetOrAdd<PauseView>(screen.gameObject);

            Transform footer = Child(panel, "Footer", false) ?? NewChild(panel, "Footer").transform;
            var footerLayout = Layout<HorizontalLayoutGroup>(footer.gameObject);
            footerLayout.childAlignment = TextAnchor.MiddleRight;
            footerLayout.childControlWidth = false;
            Element(footer.gameObject, 336f * U, 48f * U);
            Transform exitT = Child(footer, "Exit Link", false) ?? NewChild(footer, "Exit Link").transform;
            Button exit = Link(exitT, "salir al menú", HudTheme.Contour, 120f);
            SetOnClick(exit, view.ShowConfirm);

            Transform confirm = Child(panel, "Confirm", false) ?? NewChild(panel, "Confirm").transform;
            var confirmLayout = Layout<VerticalLayoutGroup>(confirm.gameObject);
            confirmLayout.childAlignment = TextAnchor.UpperCenter;
            confirmLayout.childControlWidth = false;
            confirmLayout.spacing = 10f * U;
            Fit(confirm.gameObject, false, true);
            ((RectTransform)confirm).sizeDelta = new Vector2(336f * U, 100f * U);
            Transform divider = Child(confirm, "Divider", false) ?? NewChild(confirm, "Divider").transform;
            Image dividerImage = GetOrAdd<Image>(divider.gameObject);
            dividerImage.sprite = null;
            dividerImage.color = HudTheme.BoneShade;
            dividerImage.raycastTarget = false;
            Element(divider.gameObject, 336f * U, 2f * U);
            Transform warning = Child(confirm, "Warning", false) ?? NewChild(confirm, "Warning").transform;
            var warningLayout = Layout<HorizontalLayoutGroup>(warning.gameObject);
            warningLayout.childAlignment = TextAnchor.MiddleCenter;
            warningLayout.spacing = 8f * U;
            Element(warning.gameObject, 336f * U, 22f * U);
            Icon(warning, "Skull", Sp("icon_skull"), 22f * U, HudTheme.Contour);
            Text(warning, "Warning Text", "¿salir? se pierde esta partida", fontSemi, 17f * U, HudTheme.Contour);
            Transform answers = Child(confirm, "Answers", false) ?? NewChild(confirm, "Answers").transform;
            var answersLayout = Layout<HorizontalLayoutGroup>(answers.gameObject);
            answersLayout.childControlWidth = false;
            answersLayout.spacing = 12f * U;
            Element(answers.gameObject, 336f * U, 52f * U);
            Transform stayT = Child(answers, "Stay Button", false) ?? NewChild(answers, "Stay Button").transform;
            Button stay = Plate(stayT, false, false, "no, me quedo", 162f);
            SetOnClick(stay, view.HideConfirm);
            Button mainMenu = FindButtonDeep(screen, "Main Menu");
            mainMenu.transform.SetParent(answers, false);
            Plate(mainMenu.transform, true, false, "sí, salir", 162f);
            stayT.SetSiblingIndex(0);
            confirm.gameObject.SetActive(false);

            // Order: decorations first (behind), then the content column.
            roots.transform.SetSiblingIndex(0);
            bg.transform.SetSiblingIndex(1);
            status.transform.SetSiblingIndex(2);
            resume.transform.SetSiblingIndex(3);
            row.SetSiblingIndex(4);
            footer.SetSiblingIndex(5);
            confirm.SetSiblingIndex(6);
            title.SetAsLastSibling();

            view.status = status;
            view.footer = footer.gameObject;
            view.confirm = confirm.gameObject;
        }

        // ------------------------------------------------------------------ helpers

    }
}

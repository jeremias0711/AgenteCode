using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GameDevAgent.ContentTools
{
    /// <summary>
    /// Sound design of Towers of Defencopolis. The clips in GameDevAgentContent/Audio were edited with ffmpeg from the
    /// user's reference folder "Sonido" (originals untouched) or synthesised (UI, victory, defeat); see README.
    ///  1. Import settings for WebGL / mobile: music = Vorbis, compressed in memory, loaded in background; effects =
    ///     mono, decompressed on load (short), ADPCM-free Vorbis on WebGL (the browser decodes it).
    ///  2. SoundLibrary asset (every clip, level, voice limit and pitch range; editable in the Inspector).
    ///  3. GameAudio on the course Audio Manager prefab (the single persistent audio object).
    ///  4. SlowAuraAudio on the Slow Tower (La Madremonte's wind). Projectile sounds are set by ProjectileContentBuilder.
    /// Run: Unity -batchmode -projectPath Game -executeMethod GameDevAgent.ContentTools.AudioContentBuilder.BuildFromCommandLine
    /// </summary>
    public static class AudioContentBuilder
    {
        private const string Tag = "[Audio] ";
        public const string Dir = LegendContentBuilder.Root + "/Audio";
        public const string LibraryPath = Dir + "/SoundLibrary.asset";
        private const string AudioManagerPrefab = "Assets/Course/Prefabs/Audio Manager.prefab";
        private const string SlowTowerPrefab = "Assets/Course/Prefabs/Towers/Slow Tower.prefab";

        public static void BuildFromCommandLine() => EditorApplication.Exit(Build() ? 0 : 1);

        [MenuItem("Tools/GameDevAgent/Sound design")]
        public static void BuildFromMenu() => Build();

        private static AudioClip Clip(string file) => AssetDatabase.LoadAssetAtPath<AudioClip>(Dir + "/" + file);

        public static bool Build()
        {
            var report = new List<string>();
            try
            {
                foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { Dir }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    bool music = path.Contains("/Music/");
                    var ai = (AudioImporter)AssetImporter.GetAtPath(path);
                    ai.forceToMono = !music;
                    ai.loadInBackground = music;
                    var s = ai.defaultSampleSettings;
                    s.loadType = music ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad;
                    s.compressionFormat = AudioCompressionFormat.Vorbis;
                    s.quality = music ? 0.6f : 0.75f;
                    s.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
                    s.preloadAudioData = !music;
                    ai.defaultSampleSettings = s;
                    ai.SaveAndReimport();
                }
                report.Add("import: music Vorbis q0.6 compressed in memory + background load; effects mono Vorbis q0.75 decompressed on load");

                SoundLibrary lib = AssetDatabase.LoadAssetAtPath<SoundLibrary>(LibraryPath);
                if (lib == null) { lib = ScriptableObject.CreateInstance<SoundLibrary>(); AssetDatabase.CreateAsset(lib, LibraryPath); }
                lib.menuMusic = Clip("Music/GDA_Music_MisticaSelva_Loop.ogg");
                lib.gameplayMusic = Clip("Music/GDA_Music_SelvaMistica_Loop.ogg");
                lib.windLoop = Clip("Sfx/GDA_Madremonte_WindLoop.wav");
                SoundEntry E(SoundId id, float vol, float p0, float p1, int voices, float interval, params string[] files) =>
                    new SoundEntry { id = id, volume = vol, pitch = new Vector2(p0, p1), maxVoices = voices, minInterval = interval, clips = files.Select(f => Clip("Sfx/" + f)).ToArray() };
                lib.sounds = new[]
                {
                    E(SoundId.UiTap, 0.7f, 0.96f, 1.04f, 2, 0.04f, "GDA_UI_WoodTap.wav"),
                    E(SoundId.UiConfirm, 0.75f, 1f, 1f, 1, 0.08f, "GDA_UI_Confirm.wav"),
                    E(SoundId.SelectLegend, 0.75f, 0.97f, 1.03f, 1, 0.06f, "GDA_UI_SelectLegend.wav"),
                    E(SoundId.Invoke, 0.85f, 0.98f, 1.02f, 2, 0.1f, "GDA_UI_Invoke.wav"),
                    E(SoundId.Upgrade, 0.85f, 1f, 1f, 1, 0.1f, "GDA_UI_Upgrade.wav"),
                    E(SoundId.Retire, 0.8f, 1f, 1f, 1, 0.1f, "GDA_UI_Retire.wav"),
                    E(SoundId.CollectionOpen, 0.75f, 1f, 1f, 1, 0.3f, "GDA_UI_CollectionOpen.wav"),
                    E(SoundId.AppearancePiedra, 0.8f, 0.98f, 1.02f, 1, 0.05f, "GDA_UI_Appearance_Piedra.wav"),
                    E(SoundId.AppearanceBronce, 0.7f, 0.98f, 1.02f, 1, 0.05f, "GDA_UI_Appearance_Bronce.wav"),
                    E(SoundId.AppearanceOro, 0.7f, 0.98f, 1.02f, 1, 0.05f, "GDA_UI_Appearance_Oro.wav"),
                    E(SoundId.Victory, 0.9f, 1f, 1f, 1, 1f, "GDA_Victory.wav"),
                    E(SoundId.Defeat, 0.9f, 1f, 1f, 1, 1f, "GDA_Defeat.wav"),
                    E(SoundId.EmberLaunch, 0.55f, 0.92f, 1.08f, 3, 0.05f, "GDA_Mohan_EmberLaunch.wav"),
                    E(SoundId.EmberImpact, 0.5f, 0.9f, 1.1f, 3, 0.05f, "GDA_Mohan_EmberImpact.wav"),
                    E(SoundId.RockLaunch, 0.55f, 0.92f, 1.05f, 2, 0.1f, "GDA_Hojarasquin_RockLaunch.wav"),
                    E(SoundId.RockImpact, 0.75f, 0.9f, 1.05f, 2, 0.1f, "GDA_Hojarasquin_RockImpact_A.wav", "GDA_Hojarasquin_RockImpact_B.wav"),
                    E(SoundId.BoomerangLaunch, 0.55f, 0.9f, 1.12f, 2, 0.1f, "GDA_Patasola_BoomerangLaunch.wav"),
                    E(SoundId.LegHit, 0.5f, 0.88f, 1.12f, 2, 0.06f, "GDA_Patasola_LegHit.wav"),
                    E(SoundId.WindGust, 0.7f, 0.95f, 1.05f, 1, 1f, "GDA_Madremonte_WindGust.wav"),
                };
                lib.legacyOverrides = new[]
                {
                    new LegacySfxOverride { index = 8, sound = SoundId.Invoke },   // tower place
                    new LegacySfxOverride { index = 9, sound = SoundId.Retire },   // tower remove / destroyed
                    new LegacySfxOverride { index = 10, sound = SoundId.Upgrade }, // tower upgrade
                };
                int missing = lib.sounds.Sum(e => e.clips.Count(c => c == null)) + (lib.menuMusic == null ? 1 : 0) + (lib.gameplayMusic == null ? 1 : 0) + (lib.windLoop == null ? 1 : 0);
                if (missing > 0) throw new System.Exception(missing + " clip reference(s) missing");
                EditorUtility.SetDirty(lib);
                report.Add("SoundLibrary: " + lib.sounds.Length + " sounds, 2 music loops, wind loop, 3 course sound overrides");

                GameObject am = PrefabUtility.LoadPrefabContents(AudioManagerPrefab);
                try
                {
                    GameAudio ga = am.GetComponent<GameAudio>() ?? am.AddComponent<GameAudio>();
                    ga.library = lib;
                    // GameAudio owns the music: the course tracks are never played. Unassigning them keeps ~24 MB of
                    // course music out of the builds (the audio files themselves stay in the project).
                    AudioManager course = am.GetComponent<AudioManager>();
                    int unassigned = 0;
                    if (course != null)
                    {
                        var musicSources = new List<AudioSource> { course.menuMusic, course.levelSelectMusic };
                        if (course.bgm != null) musicSources.AddRange(course.bgm);
                        foreach (AudioSource s in musicSources)
                            if (s != null && s.clip != null) { s.clip = null; unassigned++; }
                    }
                    report.Add("course music clips unassigned (never played, out of the build): " + unassigned);
                    PrefabUtility.SaveAsPrefabAsset(am, AudioManagerPrefab);
                }
                finally { PrefabUtility.UnloadPrefabContents(am); }
                report.Add("Audio Manager.prefab: GameAudio (same persistent object as the course AudioManager)");

                GameObject slow = PrefabUtility.LoadPrefabContents(SlowTowerPrefab);
                try
                {
                    if (slow.GetComponent<SlowAuraAudio>() == null) slow.AddComponent<SlowAuraAudio>();
                    PrefabUtility.SaveAsPrefabAsset(slow, SlowTowerPrefab);
                }
                finally { PrefabUtility.UnloadPrefabContents(slow); }
                report.Add("Slow Tower.prefab: SlowAuraAudio (La Madremonte wind; stats untouched)");

                AssetDatabase.SaveAssets();
                foreach (string line in report) Debug.Log(Tag + line);
                Debug.Log(Tag + "DONE ok");
                return true;
            }
            catch (System.Exception e)
            {
                foreach (string line in report) Debug.Log(Tag + line);
                Debug.LogError(Tag + "FAILED: " + e);
                return false;
            }
        }
    }
}

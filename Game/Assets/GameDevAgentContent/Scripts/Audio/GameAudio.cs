using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Sound design of Towers of Defencopolis. Lives on the course AudioManager object (one persistent audio object,
/// no competing managers) and owns:
///  - Music: two sources crossfading between the menu and gameplay loops. Returning to a screen that uses the
///    playing track never restarts it; the collection and the tutorial only change its filter and level.
///  - Effects: a fixed pool of AudioSources (no create/destroy during combat), per-sound voice limits, minimum
///    intervals and small pitch variations, so simultaneous shots do not saturate.
///  - La Madremonte: one shared wind loop driven by the slowing towers that are active (no stacking).
///  - Volumes: master (AudioListener), music and effects, persisted by AudioSettingsStore.
///  - The course sounds of placing / upgrading / removing a defender are replaced by the themed clips.
/// </summary>
[DisallowMultipleComponent]
public class GameAudio : MonoBehaviour
{
    public static GameAudio Instance { get; private set; }

    public SoundLibrary library;
    [Tooltip("Pool of effect voices.")]
    public int voices = 14;

    private class Voice { public AudioSource source; public SoundId id; public float startTime; }

    private AudioSource musicA, musicB, wind;
    private AudioLowPassFilter lowA, lowB;
    private AudioSource currentMusic;
    private MusicId currentMusicId;
    private MusicMood mood;
    private float duck = 1f;
    private float musicBase;
    private readonly List<Voice> pool = new List<Voice>();
    private readonly Dictionary<SoundId, float> lastStart = new Dictionary<SoundId, float>();
    private readonly HashSet<Object> auraOwners = new HashSet<Object>();
    private float windLevel, lastGust = -100f;
    private float[] legacyBase;
    private AudioManager course;
    private readonly System.Random random = new System.Random(1234); // never touches UnityEngine.Random (gameplay)
    private bool waitingForInteraction = true;

    /// <summary>For the automated tests.</summary>
    public MusicId CurrentMusic => currentMusicId;
    public MusicMood Mood => mood;
    public AudioClip MusicClip => currentMusic != null ? currentMusic.clip : null;
    public bool MusicPlaying => currentMusic != null && currentMusic.isPlaying;
    public float MusicTime => currentMusic != null ? currentMusic.time : 0f;
    public int MusicSourcesPlaying => (musicA != null && musicA.isPlaying ? 1 : 0) + (musicB != null && musicB.isPlaying ? 1 : 0);
    public float WindLevel => windLevel;
    public bool WindPlaying => wind != null && wind.isPlaying;
    public int AuraOwners => auraOwners.Count;
    public int VoicesPlaying(SoundId id) { int n = 0; foreach (Voice v in pool) if (v.id == id && v.source.isPlaying) n++; return n; }
    public int TotalVoicesPlaying { get { int n = 0; foreach (Voice v in pool) if (v.source.isPlaying) n++; return n; } }
    public int PlayCount(SoundId id) => counts.TryGetValue(id, out int n) ? n : 0;
    public int SkippedCount { get; private set; }
    private readonly Dictionary<SoundId, int> counts = new Dictionary<SoundId, int>();

    void Awake()
    {
        if (Instance != null && Instance != this) { enabled = false; return; }
        Instance = this;
        musicA = NewSource("Music A", true, out lowA);
        musicB = NewSource("Music B", true, out lowB);
        wind = NewSource("Madremonte Wind", true, out _);
        for (int i = 0; i < voices; i++) pool.Add(new Voice { source = NewSource("Voice " + i, false, out _) });
        SceneManager.sceneLoaded += OnSceneLoaded;
        AudioSettingsStore.Changed += ApplyVolumes;
    }

    void OnDestroy()
    {
        if (Instance != this) return;
        Instance = null;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        AudioSettingsStore.Changed -= ApplyVolumes;
    }

    void Start()
    {
        if (Instance != this) return;
        course = GetComponent<AudioManager>();
        if (course != null && course.sfx != null)
        {
            legacyBase = new float[course.sfx.Length];
            for (int i = 0; i < course.sfx.Length; i++) legacyBase[i] = course.sfx[i] != null ? course.sfx[i].volume : 1f;
            if (library != null && library.legacyOverrides != null)
                foreach (LegacySfxOverride o in library.legacyOverrides)
                {
                    SoundEntry e = library.Get(o.sound);
                    if (o.index < 0 || o.index >= course.sfx.Length || course.sfx[o.index] == null || e == null || e.clips == null || e.clips.Length == 0) continue;
                    course.sfx[o.index].clip = e.clips[0];
                    legacyBase[o.index] = e.volume;
                }
        }
        ApplyVolumes();
        OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
    }

    private AudioSource NewSource(string name, bool loop, out AudioLowPassFilter low)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        AudioSource s = go.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 0f; // top-down camera: 2D sound
        s.priority = loop ? 32 : 128;
        low = name.StartsWith("Music") ? go.AddComponent<AudioLowPassFilter>() : null;
        if (low != null) low.cutoffFrequency = 22000f;
        return s;
    }

    // ------------------------------------------------------------------ scenes and music

    public static MusicId MusicForScene(string scene)
    {
        if (scene == "Main Menu" || scene == "Level Select" || scene == "Tutorial") return MusicId.Menu;
        return MusicId.Gameplay;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single || Instance != this) return;
        duck = 1f;
        mood = scene.name == "Tutorial" ? MusicMood.Tutorial : MusicMood.Normal;
        auraOwners.Clear();
        RequestMusic(MusicForScene(scene.name));
        HookButtons();
    }

    /// <summary>Called by the course AudioManager (menu, level select, level start): picks the track of the scene.</summary>
    public void RequestSceneMusic() => RequestMusic(MusicForScene(SceneManager.GetActiveScene().name));

    public void RequestMusic(MusicId id)
    {
        if (library == null) return;
        AudioClip clip = id == MusicId.Menu ? library.menuMusic : id == MusicId.Gameplay ? library.gameplayMusic : null;
        if (id == currentMusicId && currentMusic != null && currentMusic.clip == clip && currentMusic.isPlaying) return; // no restart
        AudioSource next = currentMusic == musicA ? musicB : musicA;
        next.clip = clip;
        next.volume = 0f;
        if (clip != null) next.Play();
        currentMusic = next;
        currentMusicId = id;
        musicBase = id == MusicId.Menu ? library.menuMusicVolume : library.gameplayMusicVolume;
    }

    public void SetMood(MusicMood value) => mood = value;

    /// <summary>End of a level: themed jingle once; the music lowers (victory) or fades out (defeat).</summary>
    public void LevelEnded(bool victory)
    {
        Play(victory ? SoundId.Victory : SoundId.Defeat);
        duck = victory && library != null ? library.victoryMusicLevel : 0f;
    }

    // ------------------------------------------------------------------ effects

    public static void PlayIfReady(SoundId id, float volumeScale = 1f)
    {
        if (Instance != null) Instance.Play(id, volumeScale);
    }

    public bool Play(SoundId id, float volumeScale = 1f)
    {
        if (library == null || id == SoundId.None) return false;
        SoundEntry e = library.Get(id);
        if (e == null || e.clips == null || e.clips.Length == 0) return false;
        float now = Time.unscaledTime;
        if (lastStart.TryGetValue(id, out float last) && now - last < e.minInterval) { SkippedCount++; return false; }
        Voice voice = null;
        if (VoicesPlaying(id) >= Mathf.Max(1, e.maxVoices))
        {
            // At the cap: the new event restarts the oldest voice of the same sound (heard, never stacked).
            foreach (Voice v in pool) if (v.id == id && v.source.isPlaying && (voice == null || v.startTime < voice.startTime)) voice = v;
        }
        if (voice == null) foreach (Voice v in pool) if (!v.source.isPlaying) { voice = v; break; }
        if (voice == null)
        {
            foreach (Voice v in pool) if (voice == null || v.startTime < voice.startTime) voice = v; // steal the oldest
        }
        AudioClip clip = e.clips[random.Next(e.clips.Length)];
        voice.id = id;
        voice.startTime = now;
        voice.source.clip = clip;
        voice.source.volume = Mathf.Clamp01(e.volume * volumeScale) * AudioSettingsStore.Sfx;
        voice.source.pitch = Mathf.Lerp(e.pitch.x, e.pitch.y, (float)random.NextDouble());
        voice.source.Play();
        lastStart[id] = now;
        counts[id] = PlayCount(id) + 1;
        return true;
    }

    // ------------------------------------------------------------------ La Madremonte

    /// <summary>A slowing tower reports whether it is slowing enemies. One shared loop for all of them.</summary>
    public void SetAura(Object owner, bool active)
    {
        if (owner == null) return;
        if (active)
        {
            bool first = auraOwners.Count == 0;
            if (auraOwners.Add(owner) && first && library != null && Time.unscaledTime - lastGust > library.windGustCooldown)
            {
                lastGust = Time.unscaledTime;
                Play(SoundId.WindGust);
            }
        }
        else auraOwners.Remove(owner);
    }

    // ------------------------------------------------------------------ volumes

    private void ApplyVolumes()
    {
        AudioListener.volume = AudioSettingsStore.Master;
        if (course != null && course.sfx != null && legacyBase != null)
        {
            float level = library != null ? library.legacySfxLevel : 1f;
            for (int i = 0; i < course.sfx.Length && i < legacyBase.Length; i++)
                if (course.sfx[i] != null) course.sfx[i].volume = legacyBase[i] * level * AudioSettingsStore.Sfx;
        }
    }

    void Update()
    {
        if (Instance != this || library == null) return;
        float dt = Time.unscaledDeltaTime;
        // Music: crossfade, mood filter and level.
        float moodLevel = mood == MusicMood.Collection ? library.collectionLevel : mood == MusicMood.Tutorial ? library.tutorialLevel : 1f;
        float cutoff = mood == MusicMood.Collection ? library.collectionLowPass : mood == MusicMood.Tutorial ? library.tutorialLowPass : 22000f;
        float target = musicBase * moodLevel * duck * AudioSettingsStore.Music;
        float step = dt / Mathf.Max(0.05f, library.crossfadeSeconds);
        foreach (AudioSource s in new[] { musicA, musicB })
        {
            bool isCurrent = s == currentMusic;
            float goal = isCurrent ? target : 0f;
            s.volume = Mathf.MoveTowards(s.volume, goal, step * Mathf.Max(0.05f, Mathf.Max(target, musicBase)));
            if (!isCurrent && s.isPlaying && s.volume <= 0.001f) s.Stop();
        }
        AudioLowPassFilter low = currentMusic == musicA ? lowA : lowB;
        if (low != null) low.cutoffFrequency = Mathf.Lerp(low.cutoffFrequency, cutoff, 1f - Mathf.Exp(-dt * 3f));
        // Browsers start audio only after the first user gesture: make sure the music is running after it.
        if (waitingForInteraction && (Input.anyKeyDown || Input.GetMouseButtonDown(0) || Input.touchCount > 0))
        {
            waitingForInteraction = false;
            if (currentMusic != null && currentMusic.clip != null && !currentMusic.isPlaying) currentMusic.Play();
        }
        // La Madremonte: fade the shared loop in while any slowing tower is active, out (and stop) otherwise.
        auraOwners.RemoveWhere(o => o == null);
        bool windOn = auraOwners.Count > 0;
        float windGoal = windOn ? library.windLoopVolume : 0f;
        float windStep = dt * library.windLoopVolume / Mathf.Max(0.05f, windOn ? library.windFadeIn : library.windFadeOut);
        windLevel = Mathf.MoveTowards(windLevel, windGoal, windStep);
        if (windLevel > 0.0001f)
        {
            if (!wind.isPlaying && library.windLoop != null) { wind.clip = library.windLoop; wind.Play(); }
            wind.volume = windLevel * AudioSettingsStore.Sfx;
        }
        else if (wind.isPlaying) wind.Stop();
    }

    // ------------------------------------------------------------------ interface

    /// <summary>
    /// Discreet wooden tap on the buttons of the loaded scene (once per scene load, never during combat).
    /// Buttons with their own sound (upgrade / remove panel, appearance swatches) are left alone; play / start
    /// buttons get the confirm sound.
    /// </summary>
    private void HookButtons()
    {
        foreach (Button b in FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (b == null || b.GetComponent<ButtonSoundHook>() != null) continue;
            if (b.GetComponentInParent<TowerUpgradePanel>(true) != null || b.name.StartsWith("Appearance ")) continue;
            ButtonSoundHook hook = b.gameObject.AddComponent<ButtonSoundHook>();
            string n = b.name.ToLowerInvariant();
            hook.sound = n.Contains("play") || n.Contains("start") || n.Contains("jugar") || n.Contains("next") || n.Contains("retry") || n.Contains("again")
                ? SoundId.UiConfirm : SoundId.UiTap;
            b.onClick.AddListener(hook.PlaySound);
        }
    }
}

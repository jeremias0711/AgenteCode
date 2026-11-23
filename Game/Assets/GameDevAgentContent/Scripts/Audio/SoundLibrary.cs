using System;
using UnityEngine;

/// <summary>Sound events of Towers of Defencopolis (phase 15 sound design).</summary>
public enum SoundId
{
    None = 0,
    UiTap, UiConfirm, SelectLegend, Invoke, Upgrade, Retire, CollectionOpen,
    AppearancePiedra, AppearanceBronce, AppearanceOro,
    Victory, Defeat,
    EmberLaunch, EmberImpact, RockLaunch, RockImpact, BoomerangLaunch, LegHit,
    WindGust,
}

public enum MusicId { None = 0, Menu, Gameplay }

/// <summary>Music mood: the same track keeps playing, only its filter and level change (no restart).</summary>
public enum MusicMood { Normal = 0, Collection, Tutorial }

[Serializable]
public class SoundEntry
{
    public SoundId id;
    [Tooltip("Variations: one is picked at random.")]
    public AudioClip[] clips;
    [Range(0f, 1f)] public float volume = 1f;
    [Tooltip("Random pitch range (x = min, y = max).")]
    public Vector2 pitch = new Vector2(1f, 1f);
    [Tooltip("Maximum simultaneous voices of this sound (avoids piling up).")]
    public int maxVoices = 2;
    [Tooltip("Minimum seconds between two starts of this sound.")]
    public float minInterval = 0.03f;
}

[Serializable]
public class LegacySfxOverride
{
    [Tooltip("Index in the course AudioManager.sfx array.")]
    public int index;
    public SoundId sound;
}

/// <summary>
/// Every clip and level of the game's audio, editable in the Inspector (one asset, referenced by GameAudio).
/// </summary>
[CreateAssetMenu(menuName = "GameDevAgent/Sound Library", fileName = "SoundLibrary")]
public class SoundLibrary : ScriptableObject
{
    [Header("Music (seamless loops)")]
    public AudioClip menuMusic;
    public AudioClip gameplayMusic;
    [Range(0f, 1f)] public float menuMusicVolume = 0.5f;
    [Range(0f, 1f)] public float gameplayMusicVolume = 0.38f;
    public float crossfadeSeconds = 1.6f;

    [Header("Moods (same track, no restart)")]
    public float collectionLowPass = 1500f;
    [Range(0f, 1f)] public float collectionLevel = 0.8f;
    public float tutorialLowPass = 2800f;
    [Range(0f, 1f)] public float tutorialLevel = 0.7f;
    [Range(0f, 1f)] public float victoryMusicLevel = 0.35f;

    [Header("La Madremonte wind (one shared loop)")]
    public AudioClip windLoop;
    [Range(0f, 1f)] public float windLoopVolume = 0.45f;
    public float windFadeIn = 0.5f, windFadeOut = 1.2f;
    public float windGustCooldown = 2.5f;

    [Header("Effects")]
    public SoundEntry[] sounds = new SoundEntry[0];
    [Tooltip("Course sounds replaced by the themed ones (place / upgrade / remove a defender).")]
    public LegacySfxOverride[] legacyOverrides = new LegacySfxOverride[0];
    [Range(0f, 1f)] public float legacySfxLevel = 0.8f;

    public SoundEntry Get(SoundId id)
    {
        if (sounds == null) return null;
        foreach (SoundEntry e in sounds) if (e != null && e.id == id) return e;
        return null;
    }
}

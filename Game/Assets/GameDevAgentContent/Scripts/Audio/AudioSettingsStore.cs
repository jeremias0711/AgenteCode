using System;
using UnityEngine;

/// <summary>
/// Master / music / effects volumes (0..1), saved in PlayerPrefs (the persistence used by the rest of the game).
/// Ready to be bound to an Options menu: set a value and GameAudio applies it at once.
/// </summary>
public static class AudioSettingsStore
{
    public const string MasterKey = "gda_audio_master";
    public const string MusicKey = "gda_audio_music";
    public const string SfxKey = "gda_audio_sfx";

    public static event Action Changed;

    public static float Master { get => Get(MasterKey); set => Set(MasterKey, value); }
    public static float Music { get => Get(MusicKey); set => Set(MusicKey, value); }
    public static float Sfx { get => Get(SfxKey); set => Set(SfxKey, value); }

    private static float Get(string key) => Mathf.Clamp01(PlayerPrefs.GetFloat(key, 1f));

    private static void Set(string key, float value)
    {
        value = Mathf.Clamp01(value);
        if (Mathf.Approximately(Get(key), value) && PlayerPrefs.HasKey(key)) return;
        PlayerPrefs.SetFloat(key, value);
        PlayerPrefs.Save();
        Changed?.Invoke();
    }

    /// <summary>Back to 100 % for the three channels.</summary>
    public static void ResetDefaults()
    {
        PlayerPrefs.DeleteKey(MasterKey);
        PlayerPrefs.DeleteKey(MusicKey);
        PlayerPrefs.DeleteKey(SfxKey);
        PlayerPrefs.Save();
        Changed?.Invoke();
    }
}

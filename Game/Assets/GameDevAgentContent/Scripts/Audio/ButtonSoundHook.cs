using UnityEngine;

/// <summary>Marks a button already hooked by GameAudio and remembers its sound.</summary>
public class ButtonSoundHook : MonoBehaviour
{
    public SoundId sound = SoundId.UiTap;
    public void PlaySound() => GameAudio.PlayIfReady(sound);
}

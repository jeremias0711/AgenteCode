using UnityEngine;

/// <summary>
/// Sounds of a legend projectile, driven by real events (presentation only, no gameplay effect):
///  - Launch: when the projectile appears (Awake).
///  - Impact, projectile (ember, leg): only when it touches an enemy, the same trigger that deals the damage;
///    a projectile that leaves or expires without hitting is silent.
///  - Impact, rock (Bomb): once, when the rock reaches its target point (the area damage moment), not once per
///    enemy hit.
/// When this component is present the course projectile scripts skip their generic sounds.
/// </summary>
[DisallowMultipleComponent]
public class ProjectileAudio : MonoBehaviour
{
    public SoundId launch = SoundId.None;
    public SoundId impact = SoundId.None;
    [Range(0f, 1f)] public float launchVolume = 1f;
    [Range(0f, 1f)] public float impactVolume = 1f;

    private Bomb bomb;
    private bool impacted;

    // Awake runs in the very frame the tower instantiates the projectile, even when it hits an enemy in its first
    // physics step and is destroyed before Start.
    void Awake()
    {
        bomb = GetComponent<Bomb>();
        if (launch != SoundId.None) GameAudio.PlayIfReady(launch, launchVolume);
    }

    void OnTriggerEnter(Collider other)
    {
        if (impacted || bomb != null || impact == SoundId.None || !other.CompareTag("Enemy")) return;
        impacted = true;
        GameAudio.PlayIfReady(impact, impactVolume);
    }

    void OnDestroy()
    {
        // The Bomb destroys itself right after its area damage, when its model reached the target point.
        if (impacted || bomb == null || bomb.model == null || impact == SoundId.None) return;
        if (Vector3.Distance(bomb.model.position, bomb.targetPoint) > 0.2f) return; // scene unload or other removal
        impacted = true;
        GameAudio.PlayIfReady(impact, impactVolume);
    }
}

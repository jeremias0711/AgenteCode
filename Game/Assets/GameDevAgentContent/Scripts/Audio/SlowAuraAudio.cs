using UnityEngine;

/// <summary>
/// La Madremonte's magic wind: reports to GameAudio whether this slowing tower is slowing enemies (enemies inside
/// its range, the list the tower already keeps). GameAudio plays one gust when the first tower activates and a
/// single shared wind loop while any is active, fading out when none is. Removing, selling or destroying the tower
/// releases it at once. Presentation only: slow amount, range and stats are untouched.
/// </summary>
[DisallowMultipleComponent]
public class SlowAuraAudio : MonoBehaviour
{
    private Tower tower;
    private bool active;

    /// <summary>For the automated tests.</summary>
    public bool Active => active;

    void Awake()
    {
        tower = GetComponent<Tower>();
    }

    void Update()
    {
        // The placement ghost has its Tower disabled: never active.
        bool now = tower != null && tower.enabled && HasEnemy();
        if (now == active) return;
        active = now;
        if (GameAudio.Instance != null) GameAudio.Instance.SetAura(this, active);
    }

    private bool HasEnemy()
    {
        foreach (EnemyController e in tower.enemiesInRange) if (e != null) return true;
        return false;
    }

    void OnDisable()
    {
        if (active && GameAudio.Instance != null) GameAudio.Instance.SetAura(this, false);
        active = false;
    }
}

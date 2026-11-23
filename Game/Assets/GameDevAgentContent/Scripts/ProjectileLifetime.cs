using UnityEngine;

/// <summary>
/// Phase 14: removes a projectile that missed every enemy once it is far beyond any map. The course Projectile relies
/// on OnBecameInvisible, which never fires because its renderer is on a child object, so missed shots used to fly
/// forever. The default 6 s at the projectile speed (10 m/s) is 60 m, far outside the playable area: no hit that
/// could happen in the original game is removed. Impacts are still handled (and destroyed) by Projectile.
/// </summary>
[DisallowMultipleComponent]
public class ProjectileLifetime : MonoBehaviour
{
    public float seconds = 6f;

    void Start()
    {
        Destroy(gameObject, seconds);
    }
}

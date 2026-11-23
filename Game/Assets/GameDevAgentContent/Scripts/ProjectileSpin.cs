using UnityEngine;

/// <summary>
/// Phase 14: visual spin of a projectile model (El Mohán's ember, El Hojarasquín's rock, La Patasola's boomerang leg).
/// Lives on the "Visual" child, which has no collider and no rigidbody: the projectile root keeps its trajectory,
/// speed, trigger and damage exactly as before. Presentation only.
/// </summary>
[DisallowMultipleComponent]
public class ProjectileSpin : MonoBehaviour
{
    [Tooltip("Local axis of the spin (La Patasola's leg: Y, a flat boomerang spin).")]
    public Vector3 axis = Vector3.up;
    public float degreesPerSecond = 720f;
    [Tooltip("Start at a varied angle so simultaneous projectiles do not look identical.")]
    public bool variedStart = true;

    /// <summary>Total degrees turned since Start (read by the automated tests).</summary>
    public float SpunDegrees { get; private set; }

    void Start()
    {
        // Deterministic variation (UnityEngine.Random is shared with gameplay code: not touched here).
        if (variedStart) transform.Rotate(axis, (GetHashCode() * 37 + Time.frameCount * 53) % 360, Space.Self);
    }

    void Update()
    {
        float step = degreesPerSecond * Time.deltaTime;
        transform.Rotate(axis, step, Space.Self);
        SpunDegrees += Mathf.Abs(step);
    }
}

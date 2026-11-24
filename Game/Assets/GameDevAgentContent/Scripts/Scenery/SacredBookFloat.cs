using UnityEngine;

/// <summary>
/// Phase 20.1: the float of the runic Sacred Book over its pedestal. It reproduces the armature action of the source
/// ("Libro y base animacion.blend", ArmatureAction: the book rises 0.158 m and falls back, 22 frames at 24 fps) as a
/// smooth local offset, so the model ships as two static meshes (no Animator, no skinning). Visual only: the Castle,
/// its collider, health and attack points never move. Uses unscaled time (it also floats in the paused collection).
/// </summary>
public sealed class SacredBookFloat : MonoBehaviour
{
    [Tooltip("Rise of the book in local units of the model (metres of the source).")]
    public float amplitude = 0.158f;

    [Tooltip("Seconds of one up-and-down cycle (22 frames at 24 fps in the source action).")]
    public float period = 0.92f;

    [Tooltip("Slow turn of the book, degrees per second (0 = none).")]
    public float spin = 0f;

    private Vector3 rest;
    private float phase;

    private void Awake()
    {
        rest = transform.localPosition;
        phase = Random.value; // several books (menus, collection) never float in lockstep
    }

    private void LateUpdate()
    {
        float t = (Time.unscaledTime / Mathf.Max(0.05f, period) + phase) * Mathf.PI * 2f;
        transform.localPosition = rest + Vector3.up * (amplitude * 0.5f * (1f - Mathf.Cos(t)));
        if (spin != 0f) transform.localRotation = Quaternion.Euler(0f, Time.unscaledTime * spin, 0f);
    }
}

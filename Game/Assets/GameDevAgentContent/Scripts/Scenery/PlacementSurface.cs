using UnityEngine;

/// <summary>
/// Phase 19: marks a placement collider whose surface is not at y = 0 (terrain of the campaign sceneries, with hills
/// and the raised plaza of the Sacred Book). TowerManager keeps the defender on that surface instead of forcing y = 0,
/// so defenders never sink into the relief. Colliders without this marker keep the original rule (y = 0).
/// </summary>
public class PlacementSurface : MonoBehaviour
{
}

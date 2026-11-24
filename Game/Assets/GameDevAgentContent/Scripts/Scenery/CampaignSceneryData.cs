using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Phase 19: one campaign scenery of the "Niveles armados campaña" package (composition, routes, Sacred Book, camera and
/// atmosphere), extracted by CampaignSceneryBuilder. The environment itself is a prefab loaded from Resources only when
/// the level that uses it starts, so a scene that hosts two levels never keeps two sceneries in memory.
/// </summary>
[CreateAssetMenu(fileName = "Escenario", menuName = "Tower Defense/Campaign Scenery")]
public class CampaignSceneryData : ScriptableObject
{
    [Serializable]
    public class Route
    {
        public string name;
        public Vector3[] points = new Vector3[0];
    }

    public string id;
    public string displayName;
    [Tooltip("Resources path of the environment prefab (without extension).")]
    public string prefabResource;
    [Tooltip("Lighter composition of the same scenery (the package Móvil variant) used in mobile browsers. Optional.")]
    public CampaignSceneryData mobileVariant;

    [Header("Gameplay")]
    public Route[] routes = new Route[0];
    public Vector3 castlePosition;
    public Quaternion castleRotation = Quaternion.identity;
    public Vector3[] attackPoints = new Vector3[0];
    [Tooltip("Objects of the environment that are the Sacred Book itself (book and lectern): they move under the Castle so they disappear with it.")]
    public string[] bookObjects = new string[0];

    [Header("Camera (composition of the package)")]
    public Vector3 cameraPosition;
    public Quaternion cameraRotation = Quaternion.identity;
    public bool orthographic = true;
    public float orthographicSize = 12f;
    public float fieldOfView = 60f;
    public float nearClip = 0.3f;
    public float farClip = 300f;
    public CameraClearFlags clearFlags = CameraClearFlags.SolidColor;
    public Color background = Color.black;

    [Header("Atmosphere")]
    public AmbientMode ambientMode = AmbientMode.Trilight;
    public Color ambientSky = Color.gray, ambientEquator = Color.gray, ambientGround = Color.gray, ambientLight = Color.gray;
    public float ambientIntensity = 1f;
    public bool fog;
    public FogMode fogMode = FogMode.Linear;
    public Color fogColor = Color.gray;
    public float fogDensity = 0.01f, fogStart = 30f, fogEnd = 120f;

    [Header("Measures (report)")]
    public float mainRouteLength;
    public int triangles;
}

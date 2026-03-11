using UnityEngine;

// Manages the orbiting sun, ambient lighting color, and sky background color.
//
// Scene setup:
//   1. Create a GameObject "SunController", add this script.
//   2. Create a child "Sun" with a SpriteRenderer and a LightSource (isDirectional = true).
//      - Also add a Light2D (Point, intensity 0, Use Normal Map ON, off-screen) as a normals activator.
//   3. Wire all References fields in the Inspector.
//   4. Background Camera → Background Type: Solid Color.
//   5. Remove any DayNightController GameObject from the scene.
//
// Timing reference (fraction of a day):
//   0.00 = midnight
//   0.25 = sunrise (sun on east horizon)
//   0.50 = noon
//   0.75 = sunset  (sun on west horizon)
public class SunController : MonoBehaviour {
    public static SunController instance { get; private set; }

    [Header("References")]
    [SerializeField] Transform      sunTransform;
    [SerializeField] SpriteRenderer sunSR;
    [SerializeField] LightSource    sunSource;

    [Header("Orbit")]
    [SerializeField] float orbitCenterX;
    [SerializeField] float orbitCenterY;
    [SerializeField] float orbitRadius;

    [Header("Timing")]
    [Tooltip("Duration of twilight centred on sunrise/sunset, in days  (1.2 h ≈ 0.05)")]
    [SerializeField] float twilightLength;
    [Tooltip("How long the sun spotlight fades in/out around the horizon, in days  (20 min ≈ 0.014)")]
    [SerializeField] float sunsetLength;

    [Header("Sky Colors")]
    [SerializeField] Color skyDay;
    [SerializeField] Color skyTwilight1;
    [SerializeField] Color skyTwilight2;
    [SerializeField] Color skyTwilight3;
    [SerializeField] Color skyNight;

    [Header("Sun Light Colors")]
    [SerializeField] Color sunColorDay;
    [SerializeField] Color sunColorDusk;

    [Header("Sun Light Intensity")]
    [SerializeField] float sunIntensityNoon;
    [SerializeField] float sunIntensityHorizon;

    [Header("Ambient Colors")]
    [SerializeField] Color ambientDay;
    [SerializeField] Color ambientNight;

    Camera bgCamera;

    void Awake() {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
    }

    void Start() {
        bgCamera = null;
        float lowestDepth = float.MaxValue;
        foreach (Camera cam in Camera.allCameras)
            if (cam.depth < lowestDepth) { lowestDepth = cam.depth; bgCamera = cam; }
    }

    void Update() {
        if (World.instance == null) return;
        float phase = GetDayPhase();
        nightT = NightT(phase);
        UpdateSun(phase);
        if (bgCamera != null)
            bgCamera.backgroundColor = SkyColor(phase);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    // 0 = full day, 1 = full night. Updated every frame.
    public static float nightT { get; private set; }

    public static float GetDayPhase() {
        if (World.instance == null) return 0f;
        return World.instance.timer % Db.ticksInDay / Db.ticksInDay;
    }

    // Ambient light color for the current time of day.
    // LightFeature uses this as the light RT clear color (the minimum light level).
    public static Color GetAmbientColor() {
        if (instance == null) return Color.black;
        return Color.Lerp(instance.ambientDay, instance.ambientNight, nightT);
    }

    // Normalized direction from scene toward the sun (used as _SunDir in LightSun shader).
    public static Vector3 GetSunDirection() {
        if (instance == null) return Vector3.up;
        Vector3 toSun = instance.sunTransform.position - new Vector3(instance.orbitCenterX, instance.orbitCenterY, 0f);
        // sinElev = how far above the horizon the sun is (0 at horizon, 1 at noon).
        // Use it to set a Z (depth) component scaled to orbitRadius, so the normalized
        // direction has a comparable XY/Z ratio. Without this, toSun.z ≈ 1 vs orbitRadius ≈ 20,
        // giving near-zero NdotL on flat camera-facing sprites (normal = (0,0,-1)).
        float sinElev = instance.orbitRadius > 0f ? toSun.y / instance.orbitRadius : 0f;
        toSun.z = -Mathf.Max(0f, sinElev) * instance.orbitRadius;
        return toSun == Vector3.zero ? Vector3.up : toSun.normalized;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    // Returns 0 = full day, 1 = full night, with smooth transitions over twilightLength.
    // Twilight is centred on sunrise (0.25) and sunset (0.75).
    float NightT(float phase) {
        float half    = twilightLength * 0.5f;
        float srStart = 0.25f - half;  float srEnd = 0.25f + half;
        float ssStart = 0.75f - half;  float ssEnd = 0.75f + half;

        if (phase >= srEnd   && phase <= ssStart) return 0f;  // full day
        if (phase >= ssEnd   || phase <  srStart) return 1f;  // full night
        if (phase >= ssStart)
            return Mathf.Clamp01((phase - ssStart) / twilightLength);  // sunset  0→1
        return 1f - Mathf.Clamp01((phase - srStart) / twilightLength); // sunrise 1→0
    }

    // Returns 1 = sun fully shining, 0 = off.  Fades over sunsetLength around the horizon.
    float SunFadeT(float phase) {
        float srStart = 0.25f - sunsetLength;  float srEnd = 0.25f;
        float ssStart = 0.75f;                 float ssEnd = 0.75f + sunsetLength;

        if (phase >= srEnd   && phase <= ssStart) return 1f;
        if (phase <= srStart || phase >= ssEnd)   return 0f;
        if (phase >= ssStart)
            return 1f - Mathf.Clamp01((phase - ssStart) / sunsetLength);  // fade out
        return Mathf.Clamp01((phase - srStart) / sunsetLength);            // fade in
    }

    void UpdateSun(float phase) {
        float angle = (phase - 0.25f) * Mathf.PI * 2f;
        sunTransform.position = new Vector3(
            orbitCenterX + orbitRadius * Mathf.Cos(angle),
            orbitCenterY + orbitRadius * Mathf.Sin(angle),
            1);

        float sinElev = Mathf.Sin(angle);
        float elevT   = Mathf.Clamp01(sinElev);
        float sunFade = SunFadeT(phase);

        // Sun sprite
        sunSR.enabled = sinElev > 0f;
        sunSR.color   = Color.Lerp(sunColorDusk, sunColorDay, elevT);

        // Sun LightSource (feeds into LightFeature custom pass)
        float intensity    = Mathf.Lerp(sunIntensityHorizon, sunIntensityNoon, elevT) * sunFade;
        sunSource.lightColor = Color.Lerp(sunColorDusk, sunColorDay, elevT);
        sunSource.intensity  = intensity;
    }

    // 5-stop gradient: skyDay → skyTwilight1 → skyTwilight2 → skyTwilight3 → skyNight
    Color SkyColor(float phase) {
        float t      = NightT(phase) * 4f;
        Color[] stops = { skyDay, skyTwilight1, skyTwilight2, skyTwilight3, skyNight };
        int   i = Mathf.Clamp(Mathf.FloorToInt(t), 0, stops.Length - 2);
        return Color.Lerp(stops[i], stops[i + 1], t - i);
    }
}

using UnityEngine;
using UnityEngine.Rendering.Universal;

// Manages the orbiting sun, URP 2D lighting, and sky background color.
//
// Scene setup:
//   1. Create a GameObject "SunController", add this script.
//   2. Create a child "Sun" with a SpriteRenderer and a Light2D (Point, Additive blend).
//      - Light2D: Outer Radius ~130, Shadows On, Shadow Intensity ~0.75
//   3. Create a child "GlobalLight" with a Light2D (Global, Additive blend).
//   4. Wire all four fields below in the Inspector.
//   5. Background Camera → Background Type: Solid Color (already done).
public class SunController : MonoBehaviour {
    public static SunController instance { get; private set; }

    [Header("References")]
    [SerializeField] Transform       sunTransform;
    [SerializeField] SpriteRenderer  sunSR;
    [SerializeField] Light2D         sunLight;
    [SerializeField] Light2D         globalLight;

    [Header("Orbit")]
    [SerializeField] float orbitCenterX = 50f;
    [SerializeField] float orbitCenterY = 10f;
    [SerializeField] float orbitRadius  = 58f;
    [SerializeField] float sunZ         = 0f; // more negative -> more frontal

    [Header("Sky Color")]
    [SerializeField] Color skyDay   = new Color(0.38f, 0.68f, 1.00f);

    [Header("Sun Light Colors")]
    [SerializeField] Color sunColorDay  = new Color(1.00f, 0.96f, 0.82f);
    [SerializeField] Color sunColorDusk = new Color(1.00f, 0.52f, 0.14f);

    [Header("Sun Light Intensity")]
    [SerializeField] float sunIntensityNoon    = 0.35f;
    [SerializeField] float sunIntensityHorizon = 0.05f;
    // Fraction of elevation (sin scale 0–1) over which the light fades in/out at sunrise/sunset
    [SerializeField] float horizonFadeZone     = 0.12f;

    // Global light uses Multiply blend — white = no change, dark = night tint.
    // Set its Blend Style to Multiply (index 0) in the Inspector.
    [Header("Ambient (Multiply) Colors")]
    [SerializeField] Color ambientDay   = new Color(1.00f, 1.00f, 1.00f); // white = no tint
    [SerializeField] Color ambientNight = new Color(0.10f, 0.10f, 0.25f); // dark blue

    [Header("Ambient (Multiply) Intensity")]
    [SerializeField] float ambientIntensityDay   = 1.0f;
    [SerializeField] float ambientIntensityNight = 1.0f; // keep at 1 — color does the darkening

    Camera bgCamera;

    void Awake() {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
    }

    void Start() {
        // Find the background camera (lowest depth)
        bgCamera = null;
        float lowestDepth = float.MaxValue;
        foreach (Camera cam in Camera.allCameras) {
            if (cam.depth < lowestDepth) { lowestDepth = cam.depth; bgCamera = cam; }
        }
    }

    void Update() {
        if (World.instance == null) return;
        float phase = DayNightController.GetDayPhase();
        UpdateSun(phase);
        UpdateSkyColor(phase);
    }

    void UpdateSun(float phase) {
        // phase 0=midnight, 0.25=east horizon, 0.5=noon, 0.75=west horizon
        float angle = (phase - 0.25f) * Mathf.PI * 2f;
        float x = orbitCenterX + orbitRadius * Mathf.Cos(angle);
        float y = orbitCenterY + orbitRadius * Mathf.Sin(angle);
        sunTransform.position = new Vector3(x, y, sunZ);

        float sinElev = Mathf.Sin(angle);               // 1 at noon, 0 at horizon, negative underground
        float elevT   = Mathf.Clamp01(sinElev);         // 0..1 for color lerp
        // Fade light in/out smoothly over horizonFadeZone instead of hard on/off
        float fadeT   = Mathf.Clamp01(sinElev / Mathf.Max(horizonFadeZone, 0.001f));

        sunSR.enabled = sinElev > 0f;
        sunSR.color   = Color.Lerp(sunColorDusk, sunColorDay, elevT);

        float intensity = Mathf.Lerp(sunIntensityHorizon, sunIntensityNoon, elevT) * fadeT;
        sunLight.color     = Color.Lerp(sunColorDusk, sunColorDay, elevT);
        sunLight.intensity = intensity;
        sunLight.enabled   = intensity > 0.001f;

        // Global ambient tracks overall day brightness
        float dayT = Mathf.Clamp01(1f - Mathf.Abs(phase * 2f - 1f));
        globalLight.color     = Color.Lerp(ambientNight, ambientDay, dayT);
        globalLight.intensity = Mathf.Lerp(ambientIntensityNight, ambientIntensityDay, dayT);
    }

    void UpdateSkyColor(float phase) {
        if (bgCamera == null) return;
        // Camera background bypasses URP lighting, so manually apply the same
        // multiply-ambient the global light applies to sprites.
        float dayT = Mathf.Clamp01(1f - Mathf.Abs(phase * 2f - 1f));
        Color ambient = Color.Lerp(ambientNight, ambientDay, dayT);
        bgCamera.backgroundColor = skyDay * ambient;
    }
}

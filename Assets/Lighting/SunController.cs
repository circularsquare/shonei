using UnityEngine;

// Manages the orbiting sun, ambient lighting color, and sky background color.
//
// Scene setup:
//   1. Create a GameObject "SunController", add this script.
//   2. Create a child "Sun" with a SpriteRenderer and a LightSource (isDirectional = true).
//   3. Wire all References fields in the Inspector.
//   4. Background Camera → Background Type: Solid Color.
//
// Timing reference (fraction of a day):
//   0.00 = midnight
//   0.25 = sunrise (sun on east horizon)
//   0.50 = noon
//   0.75 = sunset  (sun on west horizon)
//
// twilightFraction: 1 = full day, 0 = full night.
// Brightness: sin^3 of sun elevation, 0 at horizon/night, 1 at noon.
//
// ── Sky color stops ────────────────────────────────────────────────────────
// Two parallel 4-stop gradients describe the sky as a vertical band:
//   skyDay/Twilight1/Twilight3/Night     → the **zenith** (top) color of the sky.
//   horizonDay/Twilight1/Twilight3/Night → the **horizon** (bottom) color.
// SkyGradient.cs blends between them to fill the SkyCamera frustum. Author
// horizon stops with warmer/lighter values at twilight so the offscreen sun
// reads as a horizon glow.
public class SunController : MonoBehaviour {
    public static SunController instance { get; private set; }

    [Header("References")]
    [SerializeField] LightSource sunSource;

    // Cached from sunSource at Awake — the sun GO carries the orbiting
    // transform and the (currently off-screen) sprite renderer. One
    // inspector wire instead of three keeps them all in lockstep.
    Transform      _sunTransform;
    SpriteRenderer _sunSR;

    [Header("Orbit")]
    [SerializeField] float orbitCenterX;
    [SerializeField] float orbitCenterY;
    [SerializeField] float orbitRadius;

    [Header("Timing")]
    [Tooltip("Duration of twilight centred on sunrise/sunset, in days  (1.2 h ≈ 0.05)")]
    [SerializeField] float twilightLength;

    [Header("Sky Colors (Zenith — top of sky)")]
    [SerializeField] Color skyDay;
    [SerializeField] Color skyTwilight1;
    [SerializeField] Color skyTwilight3;
    [SerializeField] Color skyNight;

    [Header("Horizon Sky Colors (bottom of sky)")]
    [SerializeField] Color horizonDay;
    [SerializeField] Color horizonTwilight1;
    [SerializeField] Color horizonTwilight3;
    [SerializeField] Color horizonNight;

    [Tooltip("Viewport V at which the horizon→zenith blend completes. Below this is the transition zone, above is full zenith. 1.0 = gradient spans the entire viewport (no solid-zenith band). Read by SkyGradient.")]
    [Range(0f, 1f)] [SerializeField] float _horizonY01 = 0.4f;

    [Header("Sun Light Colors")]
    [SerializeField] Color sunColorDay;
    [SerializeField] Color sunColorEarlyDusk;
    [SerializeField] Color sunColorDusk;
    [SerializeField] Color sunColorNight;

    // Note: sun-baseline-at-noon intensity lives on sunSource.baseIntensity
    // (the LightSource on the Sun GO) — same field torches use, so the
    // concept lines up across light kinds.

    [Header("Ambient Colors")]
    [SerializeField] Color ambientDay;
    [SerializeField] Color ambientNight;
    [Tooltip("Ambient brightness at night (0 = fully dark, 1 = full brightness). The floor of the ambient ramp.")]
    [SerializeField] [Range(0f, 1f)] float ambientBrightnessMin = 0.6f;
    [Tooltip("Ambient brightness at noon (the ceiling of the ramp). Should be ≥ Min.")]
    [SerializeField] [Range(0f, 1f)] float ambientBrightnessMax = 1.0f;

    [Header("Debug (read-only in play mode)")]
    [SerializeField] float _twilightFraction;
    [SerializeField] float _brightness;

    void Awake() {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        if (sunSource != null) {
            _sunTransform = sunSource.transform;
            _sunSR        = sunSource.GetComponent<SpriteRenderer>();
        } else {
            Debug.LogError("SunController: sunSource not assigned in inspector.");
        }
    }

    void Update() {
        if (World.instance == null) return;
        float phase = GetDayPhase();
        twilightFraction = _twilightFraction = CalcTwilightFraction(phase);
        brightness       = _brightness       = Brightness(phase);
        UpdateSun();
        skyColor     = SkyColor();
        horizonColor = HorizonColor();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    // 1 = full day, 0 = full night. Smooth transitions over twilightLength.
    public static float twilightFraction { get; private set; }

    // 0 = full day (torch off), 1 = full night (torch fully on). Partial values during twilight.
    // Used by LightSource to gate fuel consumption — no burn when torchFactor == 0.
    public static float torchFactor { get; private set; }

    // 0 at night, 1 during day; linear transition over twilightLength/2 around sunrise/sunset.
    public static float brightness { get; private set; }

    // 4-stop sky gradient color for the current time of day — **zenith** (top of sky).
    public static Color skyColor { get; private set; }

    // 4-stop sky gradient color for the current time of day — **horizon** (bottom of sky).
    public static Color horizonColor { get; private set; }

    // Viewport V at which the horizon→zenith blend completes (authored once,
    // not time-varying). Lives here so all sky-look knobs sit together.
    public static float horizonY01 => instance != null ? instance._horizonY01 : 0.4f;

    public static float GetDayPhase() {
        if (World.instance == null) return 0f;
        return World.instance.timer % World.ticksInDay / World.ticksInDay;
    }

    // Returns true if the current hour falls within [startHour, endHour).
    // endHour < startHour means the window wraps midnight (e.g. 16→6 = 4pm–6am).
    // startHour < 0 means no gate — always returns true.
    public static bool IsHourInRange(float startHour, float endHour) {
        if (startHour < 0f) return true;
        float hour = GetDayPhase() * 24f;
        return startHour < endHour
            ? hour >= startHour && hour < endHour   // normal window
            : hour >= startHour || hour < endHour;  // wraps midnight
    }

    // Ambient light color for the current time of day.
    // Color tint: lerp(ambientNight, ambientDay, twilightFraction).
    // Brightness: ramps from ambientBrightnessMin (night) to
    // ambientBrightnessMax (noon) by sun elevation, then scaled by weather.
    public static Color GetAmbientColor() {
        if (instance == null) return Color.white;
        Color tint   = Color.Lerp(instance.ambientNight, instance.ambientDay, twilightFraction);
        float bright = Mathf.Lerp(instance.ambientBrightnessMin, instance.ambientBrightnessMax, brightness);
        return tint * bright * WeatherSystem.GetAmbientMultiplier();
    }

    // Normalized direction from scene toward the sun (used as _SunDir in LightSun shader).
    // XY direction toward the sun in world space. Z is omitted — lightHeight on the
    // LightSource component controls the sun's apparent elevation in the shader.
    public static Vector3 GetSunDirection() {
        if (instance == null) return Vector3.up;
        Vector3 toSun = instance._sunTransform.position - new Vector3(instance.orbitCenterX, instance.orbitCenterY, 0f);
        toSun.z = 0f;
        return toSun == Vector3.zero ? Vector3.up : toSun.normalized;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    // Clamped linear ramp: 0 at `start`, 1 at `end`. The three day-cycle
    // ramp methods below all express their sunrise/sunset transitions as
    // calls to this helper — same shape, different windows.
    static float Ramp(float phase, float start, float end) {
        return Mathf.Clamp01((phase - start) / (end - start));
    }

    // Returns 1 = full day, 0 = full night, with smooth transitions over twilightLength.
    // Twilight is centred on sunrise (0.25) and sunset (0.75).
    float CalcTwilightFraction(float phase) {
        float half    = twilightLength * 0.5f;
        float srStart = 0.25f - half;  float srEnd = 0.25f + half;
        float ssStart = 0.75f - half;  float ssEnd = 0.75f + half;

        if (phase >= srEnd   && phase <= ssStart) return 1f;  // full day
        if (phase >= ssEnd   || phase <  srStart) return 0f;  // full night
        if (phase >= ssStart) return 1f - Ramp(phase, ssStart, ssEnd);  // sunset  1→0
        return Ramp(phase, srStart, srEnd);                              // sunrise 0→1
    }

    // Linear 0→1 over first half of sunrise (srStart→0.25), full day, linear 1→0 over second half of sunset (0.75→ssEnd).
    float Brightness(float phase) {
        float half    = twilightLength * 0.5f;
        float srStart = 0.25f - half;
        float ssEnd   = 0.75f + half;

        if (phase >= 0.25f && phase <= 0.75f)  return 1f;                  // full day
        if (phase >= ssEnd || phase < srStart) return 0f;                  // full night
        if (phase >= 0.75f) return 1f - Ramp(phase, 0.75f, ssEnd);         // sunset  1→0
        return Ramp(phase, srStart, 0.25f);                                // sunrise 0→1
    }

    // Like Brightness but transitions over the first half of twilight only:
    // sunset 1→0 over (0.75-half → 0.75), sunrise 0→1 over (0.25 → 0.25+half).
    // Torches use (1 - this) so they're fully lit by mid-sunset.
    float TorchBrightness(float phase) {
        float half = twilightLength * 0.5f;
        if (phase >= 0.75f || phase < 0.25f)                return 0f;  // night
        if (phase >= 0.25f + half && phase <= 0.75f - half) return 1f;  // full day
        if (phase >= 0.75f - half) return 1f - Ramp(phase, 0.75f - half, 0.75f);  // sunset 1→0
        return Ramp(phase, 0.25f, 0.25f + half);                                  // sunrise 0→1
    }

    // Sunset:  day → earlyDusk (0.75-half) → dusk (0.75) → night (0.75+half)
    // Sunrise: symmetric reverse — night (0.25-half) → dusk (0.25) → earlyDusk (0.25+half) → day
    Color SunColor(float phase) {
        float half = twilightLength * 0.5f;

        if (phase > 0.25f + twilightLength && phase < 0.75f - twilightLength) return sunColorDay;   // full day
        if (phase >= 0.75f + half || phase < 0.25f - half)                    return sunColorNight; // full night

        // sunset
        if (phase >= 0.75f - twilightLength && phase < 0.75f - half)
            return Color.Lerp(sunColorDay,       sunColorEarlyDusk, (phase - (0.75f - twilightLength)) / half);
        if (phase >= 0.75f - half && phase < 0.75f)
            return Color.Lerp(sunColorEarlyDusk, sunColorDusk,      (phase - (0.75f - half)) / half);
        if (phase >= 0.75f && phase < 0.75f + half)
            return Color.Lerp(sunColorDusk,      sunColorNight,     (phase - 0.75f) / half);

        // sunrise (symmetric)
        if (phase >= 0.25f - half && phase < 0.25f)
            return Color.Lerp(sunColorNight,     sunColorDusk,      (phase - (0.25f - half)) / half);
        if (phase >= 0.25f && phase < 0.25f + half)
            return Color.Lerp(sunColorDusk,      sunColorEarlyDusk, (phase - 0.25f) / half);
        return     Color.Lerp(sunColorEarlyDusk, sunColorDay,       (phase - (0.25f + half)) / half);
    }

    void UpdateSun() {
        float phase = GetDayPhase();
        float angle = (phase - 0.25f) * Mathf.PI * 2f;
        _sunTransform.position = new Vector3(
            orbitCenterX + orbitRadius * Mathf.Cos(angle),
            orbitCenterY + orbitRadius * Mathf.Sin(angle),
            1);

        bool aboveHorizon = Mathf.Sin(angle) > 0f;
        _sunSR.enabled = aboveHorizon;
        _sunSR.color   = SunColor(phase);

        sunSource.lightColor = SunColor(phase);
        sunSource.intensity  = brightness * sunSource.baseIntensity * WeatherSystem.GetSunMultiplier();

        // Torches ramp to full over the first half of sunset/sunrise,
        // ahead of the sun — so they're already bright by deep dusk.
        // Each sun-modulated LightSource pulls this value in its own
        // Update() to scale its intensity; we just publish it.
        torchFactor = 1f - TorchBrightness(GetDayPhase());
    }

    // 4-stop gradient: day → twilight1 → twilight3 → night
    // driven by twilightFraction (1=day, 0=night). Both sky (zenith) and horizon
    // share the same time-of-day phase; SkyGradient blends between them vertically.
    Color SkyColor()     => LerpStops(skyDay,     skyTwilight1,     skyTwilight3,     skyNight);
    Color HorizonColor() => LerpStops(horizonDay, horizonTwilight1, horizonTwilight3, horizonNight);

    Color LerpStops(Color s0, Color s1, Color s2, Color s3) {
        float t = (1f - twilightFraction) * 3f;
        Color[] stops = { s0, s1, s2, s3 };
        int i = Mathf.Clamp(Mathf.FloorToInt(t), 0, stops.Length - 2);
        return Color.Lerp(stops[i], stops[i + 1], t - i);
    }
}

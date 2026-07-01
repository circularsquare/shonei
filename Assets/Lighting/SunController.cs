using UnityEngine;

// Manages the orbiting sun, ambient lighting color, and sky background color.
//
// Scene setup:
//   1. Create a GameObject "SunController", add this script.
//   2. Create a child "Sun" with a SpriteRenderer and a LightSource (isDirectional = true).
//   3. Wire all References fields in the Inspector.
//   4. Background Camera → Background Type: Solid Color.
//
// Timing reference. Two phases are in play:
//   • Clock phase (GetDayPhase) — real time-of-day, 0=midnight … 0.5=solar noon.
//     Solar noon is ALWAYS 0.5, so clock-anchored schedules (mouse routines,
//     building active-hour windows, the clock hand) never shift with the season.
//   • Solar phase (SolarPhase) — clock phase remapped so the season-varying
//     sunrise/noon/sunset land on the fixed canonical anchors below. All the
//     sun-look ramps (twilight, brightness, sun color, orbit angle) are authored
//     against these and read solar phase, so they track the real sunrise/sunset.
//       0.25 = sunrise (sun on east horizon)
//       0.50 = noon
//       0.75 = sunset  (sun on west horizon)
//
// Day length varies with the season at latitude 35°N (see DaylightFraction):
// ~0.5 of the day is lit at the equinoxes, ~0.6 at the summer solstice, ~0.4 at
// the winter solstice. The longest day is aligned to the yearly temperature peak.
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

    [Tooltip("How strongly night ambient dims Unlit selection / harvest overlays. " +
             "0 = always full bright (old behaviour), 1 = full ambient, 0.5 = half strength. " +
             "Broadcast as the _OverlayAmbient shader global; see UnlitOverlayAmbient.shader.")]
    [SerializeField] [Range(0f, 1f)] float overlayAmbientStrength = 0.5f;

    static readonly int OverlayAmbientId = Shader.PropertyToID("_OverlayAmbient");

    [Header("Standalone clock (menu / no-World scenes)")]
    [Tooltip("When true and there is no World, advance the day cycle from real time so the sun animates in scenes without the game clock (the menu). In-game leave false — World drives time.")]
    [SerializeField] bool useStandaloneClock = false;
    [Tooltip("Real seconds for one full day/night cycle when useStandaloneClock is on.")]
    [SerializeField] float standaloneSecondsPerDay = 120f;
    [Tooltip("Hour of day (0-24) the standalone clock starts at on scene load.")]
    [SerializeField] float standaloneStartHour = 9f;
    float _standaloneClock;

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
        // Standalone (no-World) scenes start the day cycle at this hour.
        _standaloneClock = Mathf.Repeat(standaloneStartHour / 24f, 1f) * Mathf.Max(1f, standaloneSecondsPerDay);
    }

    void Update() {
        if (World.instance == null) {
            if (!useStandaloneClock) return;
            _standaloneClock += Time.deltaTime;  // no World (menu): drive the cycle from real time
        }
        // Remap real clock time onto solar phase so every ramp below tracks the
        // season's actual sunrise/sunset while solar noon stays pinned at 0.5.
        float phase = SolarPhase(GetDayPhase());
        twilightFraction = _twilightFraction = CalcTwilightFraction(phase);
        brightness       = _brightness       = Brightness(phase);
        UpdateSun(phase);
        skyColor     = SkyColor();
        horizonColor = HorizonColor();

        // Broadcast the half-strength ambient tint for Unlit-layer overlays
        // (selection / harvest highlights). They render after the lighting
        // composite at full bright; this lets their bright colors settle toward
        // the night ambient so they stop glaring in the dark, while staying
        // full-bright by day (where ambient ≈ white). UnlitOverlayAmbient.shader
        // multiplies RGB by this global.
        Shader.SetGlobalColor(OverlayAmbientId,
            Color.Lerp(Color.white, GetAmbientColor(), overlayAmbientStrength));
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
        if (World.instance != null) return World.instance.timer % World.ticksInDay / World.ticksInDay;
        // No World (e.g. the menu): advance from a real-time standalone clock if enabled.
        if (instance != null && instance.useStandaloneClock)
            return Mathf.Repeat(instance._standaloneClock / Mathf.Max(1f, instance.standaloneSecondsPerDay), 1f);
        return 0f;
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

    // Vertical component the normalized sun direction WOULD have `inGameMinutes`
    // after sunset (negative = below the horizon). Season-accurate: the night
    // half (sunset→midnight) spans clock-phase 0.5·(1−daylight), and that maps
    // linearly onto a fixed quarter of solar phase, so a real clock offset
    // corresponds to a larger solar-angle step on short winter nights than on
    // long summer ones. Mirrors before sunrise by symmetry. A negative
    // `inGameMinutes` therefore gives the value that many minutes BEFORE sunset.
    // Used by CloudLayer's night-band flatten window so its timing stays put
    // across seasons. Falls back to the equinox curve when there's no World.
    public static float SunYAfterSunset(float inGameMinutes) {
        float daylight  = instance != null ? instance.DaylightFraction() : 0.5f;
        float nightSpan = 1f - daylight;                  // clock fraction the night occupies
        if (nightSpan < 1e-4f) return -1f;                // polar guard (unreachable at 35°)
        return -Mathf.Sin(Mathf.PI * (inGameMinutes / 1440f) / nightSpan);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    // ── Season-varying day length ──────────────────────────────────────────────
    // Latitude and axial tilt fix how strongly day length swings across the year.
    // 35°N is a fixed design choice, so these are consts (no scene-serialized
    // value to drift out of sync with the code).
    const float LatitudeDeg  = 35f;
    const float AxialTiltDeg = 23.44f;  // Earth's obliquity → max solar declination

    // Fraction of the day the sun is above the horizon at the current day-of-year.
    // 0.5 at the equinoxes, ~0.6 at the summer solstice, ~0.4 at the winter
    // solstice for latitude 35°N. No World (the menu) → a flat equinox so the
    // standalone sun stays symmetric.
    float DaylightFraction() {
        if (World.instance == null) return 0.5f;

        float yearLen  = World.ticksInDay * World.daysInYear;
        float yearFrac = (World.instance.timer % yearLen) / yearLen;

        // Solar declination: peaks at +tilt on the summer solstice, troughs at
        // −tilt half a year later. The solstice sits at the start of summer
        // (yearFrac 0.25), which lands all four astronomical events on the season
        // boundaries (equinox at start of spring/fall, solstice at start of
        // summer/winter). Temperature peaks later, at yearFrac 0.375 (mid-summer)
        // — the longest day leads the hottest day, i.e. seasonal thermal lag.
        const float SummerSolsticeFrac = 0.25f;
        float decl = AxialTiltDeg * Mathf.Deg2Rad
                   * Mathf.Cos(2f * Mathf.PI * (yearFrac - SummerSolsticeFrac));

        // Sunrise hour angle H0: cos(H0) = −tan(lat)·tan(decl). Daylight spans
        // 2·H0 of the 2π day, so the lit fraction is H0/π. The clamp guards the
        // polar day/night case — unreachable at 35°, but keeps Acos finite.
        float cosH0 = -Mathf.Tan(LatitudeDeg * Mathf.Deg2Rad) * Mathf.Tan(decl);
        return Mathf.Acos(Mathf.Clamp(cosH0, -1f, 1f)) / Mathf.PI;
    }

    // Remaps real clock phase (0=midnight, 0.5=solar noon) onto solar phase, where
    // sunrise/noon/sunset sit at the canonical 0.25/0.5/0.75 the ramp curves are
    // authored against. Each of the four clock segments (pre-dawn, morning,
    // afternoon, night) is stretched linearly onto its canonical quarter, so the
    // daytime ramps fill the actual daylight window and the night ramps fill the
    // rest. Solar noon and midnight are fixed points, so clock-anchored systems
    // are untouched. A side effect: twilight/color durations scale with day
    // length (longer twilight in summer) — a fair approximation of the real thing.
    float SolarPhase(float clockPhase) {
        float daylight = DaylightFraction();
        float sunrise  = 0.5f - daylight * 0.5f;
        float sunset   = 0.5f + daylight * 0.5f;

        if (clockPhase < sunrise) return 0.25f * Mathf.InverseLerp(0f, sunrise, clockPhase);
        if (clockPhase < 0.5f)    return 0.25f + 0.25f * Mathf.InverseLerp(sunrise, 0.5f, clockPhase);
        if (clockPhase < sunset)  return 0.50f + 0.25f * Mathf.InverseLerp(0.5f, sunset, clockPhase);
        return 0.75f + 0.25f * Mathf.InverseLerp(sunset, 1f, clockPhase);
    }

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

    // `phase` is solar phase (sunrise/noon/sunset pinned to 0.25/0.5/0.75), so the
    // orbit maps east-horizon→zenith→west-horizon onto the actual daylight window.
    void UpdateSun(float phase) {
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
        torchFactor = 1f - TorchBrightness(phase);
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

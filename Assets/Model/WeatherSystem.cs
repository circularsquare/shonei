using UnityEngine;

// Tracks the current weather state and drives hourly transitions.
//
// Initialised by World.Awake(). Per-frame tick driven by World.Update().
// SunController queries GetSunMultiplier() / GetAmbientMultiplier() every frame.
//
// Rain probabilities (per in-game hour):
//   Clear → Rain:  4%
//   Rain  → Clear: 12%
//
// Precipitation type splits on temperature: when isRaining is true, the active
// channel is rainAmount if temperature ≥ snowThresholdC, else snowAmount. Both
// channels lerp smoothly via the same MoveTowards step, so transitions across
// the threshold (which only shift slowly via the daily/yearly sine waves) cross-
// fade rather than snap. Hard-switch design — no smoothing band today.
//
// Light multipliers at full intensity (max(rainAmount, snowAmount) = 1):
//   Sun intensity: 0.25
//   Ambient:       0.90
//
// Transitions lerp over lerpDuration seconds (default 5s).
//
// Wind: Ornstein-Uhlenbeck random walk, updated each hour.
//   step = -0.02 * wind  +  Uniform(-0.15, 0.15)
//   The -0.02*wind term pulls back toward zero (at wind=0.5, mean step = -0.01).
//   Stationary std ≈ 0.43, so |wind| typically lives in [0, 0.9] with occasional
//   excursions past ±1. Both readers (Windmill output / blades, RainParticles
//   horizontal velocity) handle the full range — Windmill clamps the magnitude
//   for output via Mathf.Min(1, w), so excursions saturate at MaxOutput.
//   Positive = blowing right. Used by RainParticles for sideways drop velocity.
public class WeatherSystem {
    public static WeatherSystem instance { get; private set; }

    public bool isRaining { get; private set; }

    // 0 = fully clear, 1 = fully raining (above snowThresholdC). Lerps smoothly.
    public float rainAmount { get; private set; }

    // 0 = no snow, 1 = full snow (raining at temperature < snowThresholdC).
    // Same lerp cadence as rainAmount; only one of the two ramps up at a time.
    public float snowAmount { get; private set; }

    // Below this temperature, "raining" produces snow instead of rain. Snow does
    // not water plants (MoistureSystem reads rainAmount only) and does not fill
    // tanks/puddles (RainReplenish gates on temperature too).
    public const float snowThresholdC = 2f;

    // Positive = wind blowing right. Updated each hour via random walk.
    public float wind { get; private set; }

    // Ambient temperature in Celsius, driven by yearly + daily sine waves.
    // Yearly: peaks midsummer (day 7.5) at ~30°C high, troughs midwinter (day 17.5) at ~5°C high.
    // Daily: peaks at 2pm, amplitude ±4°C around the daily mean.
    public float temperature { get; private set; }

    const float lerpDuration = 4f;

    // Called by World.Update() every frame.
    public void Tick(float dt) {
        UpdateTemperature();
        bool snowing  = isRaining && temperature <  snowThresholdC;
        bool rainingT = isRaining && temperature >= snowThresholdC;
        float step    = dt / lerpDuration;
        rainAmount = Mathf.MoveTowards(rainAmount, rainingT ? 1f : 0f, step);
        snowAmount = Mathf.MoveTowards(snowAmount, snowing  ? 1f : 0f, step);
    }

    // Called by World.Update() once per in-game hour.
    public void OnHourElapsed() {
        wind += -0.02f * wind + Rng.Range(-0.15f, 0.15f);

        if (!isRaining) {
            if (Rng.value < 0.04f) SetRain(true);
        } else {
            if (Rng.value < 0.12f) SetRain(false);
            // Snow doesn't fill tanks or top up puddles immediately — accumulation
            // and melt are a future feature; for now snowing skips this entirely.
            if (temperature >= snowThresholdC) ReplenishRainwater();
        }

        // Soil diffusion, evaporation, and plant passive draw run once per in-game hour.
        // Rain uptake runs on a separate per-second cadence in World.Update.
        MoistureSystem.instance?.HourlyUpdate();
    }

    void ReplenishRainwater() {
        if (WaterController.instance == null) return;
        WaterController.instance.RainReplenish();
        WaterController.instance.RainFillTanks();
    }

    // Called by SaveSystem when loading a save file — snaps immediately, no lerp.
    // Picks the rain or snow channel based on the temperature reconstructed from
    // the world timer, so a save loaded mid-winter resumes as snow not rain.
    public void RestoreState(bool rain) {
        isRaining = rain;
        UpdateTemperature();
        bool asSnow = rain && temperature < snowThresholdC;
        rainAmount  = (rain && !asSnow) ? 1f : 0f;
        snowAmount  = asSnow            ? 1f : 0f;
    }

    // Combined precipitation intensity — overcast is overcast regardless of type.
    static float Overcast() => Mathf.Max(instance?.rainAmount ?? 0f, instance?.snowAmount ?? 0f);

    // Multiplier applied to sunSource.intensity by SunController.
    public static float GetSunMultiplier()     => Mathf.Lerp(1.0f, 0.25f, Overcast());

    // Multiplier applied to the ambient color returned by SunController.GetAmbientColor().
    public static float GetAmbientMultiplier() => Mathf.Lerp(1.0f, 0.90f, Overcast());

    public static WeatherSystem Create() {
        instance = new WeatherSystem();
        return instance;
    }

    // Returns fractional day-of-year (0 to daysInYear) from the world timer.
    public float GetDayOfYear() {
        float totalDays = World.instance.timer / World.ticksInDay;
        return totalDays % World.daysInYear;
    }

    // Returns the current season name. Time 0 = start of spring.
    // Each season spans daysInYear/4 days.
    public string GetSeason() {
        float day = GetDayOfYear();
        float seasonLength = World.daysInYear / 4f;
        if (day < seasonLength)     return "Spring";
        if (day < seasonLength * 2) return "Summer";
        if (day < seasonLength * 3) return "Fall";
        return "Winter";
    }

    // Recalculates temperature from the current world timer.
    // temperature = 13.5 + 12.5*sin(yearly) + 4*sin(daily)
    //   yearly peaks at midsummer (day 7.5/20), daily peaks at 2pm (hour 14/24).
    void UpdateTemperature() {
        float timer = World.instance.timer;
        float yearLength = World.ticksInDay * World.daysInYear;
        float yearFrac = (timer % yearLength) / yearLength;
        float dayFrac  = (timer % World.ticksInDay) / World.ticksInDay;

        const float twoPi = 2f * Mathf.PI;
        float yearly = Mathf.Sin(twoPi * yearFrac - Mathf.PI / 4f);   // peaks at day 7.5
        float daily  = Mathf.Sin(twoPi * dayFrac  - 2f * Mathf.PI / 3f); // peaks at hour 14

        temperature = 13.5f + 12.5f * yearly + 4f * daily;
    }

    void SetRain(bool rain) {
        isRaining = rain;
    }

    // Debug-toggle entry point. Flips the raining state; Tick() handles the
    // visual fade. The next OnHourElapsed roll may revert it as usual.
    public void ToggleRain() {
        SetRain(!isRaining);
    }
}

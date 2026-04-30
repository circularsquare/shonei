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
// Light multipliers at full rain (rainAmount = 1):
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

    // 0 = fully clear, 1 = fully raining. Lerps smoothly on weather change.
    public float rainAmount { get; private set; }

    // Positive = wind blowing right. Updated each hour via random walk.
    public float wind { get; private set; }

    // Ambient temperature in Celsius, driven by yearly + daily sine waves.
    // Yearly: peaks midsummer (day 7.5) at ~30°C high, troughs midwinter (day 17.5) at ~5°C high.
    // Daily: peaks at 2pm, amplitude ±4°C around the daily mean.
    public float temperature { get; private set; }

    const float lerpDuration = 4f;

    // Called by World.Update() every frame.
    public void Tick(float dt) {
        float target = isRaining ? 1f : 0f;
        rainAmount = Mathf.MoveTowards(rainAmount, target, dt / lerpDuration);
        UpdateTemperature();
    }

    // Called by World.Update() once per in-game hour.
    public void OnHourElapsed() {
        wind += -0.02f * wind + Rng.Range(-0.15f, 0.15f);

        if (!isRaining) {
            if (Rng.value < 0.04f) SetRain(true);
        } else {
            if (Rng.value < 0.12f) SetRain(false);
            ReplenishRainwater();
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
    public void RestoreState(bool rain) {
        isRaining   = rain;
        rainAmount  = rain ? 1f : 0f;
        UpdateTemperature();
    }

    // Multiplier applied to sunSource.intensity by SunController.
    public static float GetSunMultiplier()     => Mathf.Lerp(1.0f, 0.25f, instance?.rainAmount ?? 0f);

    // Multiplier applied to the ambient color returned by SunController.GetAmbientColor().
    public static float GetAmbientMultiplier() => Mathf.Lerp(1.0f, 0.90f, instance?.rainAmount ?? 0f);

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
}

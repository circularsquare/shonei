using UnityEngine;

// Tracks the current weather state and drives hourly transitions.
//
// Initialised by World.Awake(). Per-frame tick driven by World.Update().
// SunController queries GetSunMultiplier() / GetAmbientMultiplier() every frame.
//
// Rain is driven by a continuous `humidity` field: rain happens when humidity
// crosses `rainThreshold` from below, and stops when it crosses back. Humidity
// itself is a slow Ornstein-Uhlenbeck walk reverting toward `humidityMean`,
// stepped once per in-game hour and smoothed continuously toward the target.
// The walk is calibrated so humidity spends roughly a quarter of its time
// above the rain threshold, matching the old hardcoded 4% / 12% transition
// rates. CloudLayer also reads `humidity` to drive cloud count, size, altitude
// and tint, so the sky visually builds up before rain and clears after.
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
// Wind: Ornstein-Uhlenbeck random walk targets a new value each in-game
// hour; the public `wind` field exponentially eases toward that target each
// frame so all readers (Windmill output / blades, RainParticles horizontal
// velocity, CloudLayer drift, plant sway shader) see continuous motion
// rather than hourly steps.
//   target step = -reversion * targetWind  +  Uniform(-shock, +shock)
//   wind <- Lerp(wind, targetWind, 1 - exp(-dt * windSmoothingRate))
// Mean reversion is slow — wind drifts for hours before pulling back, so
// direction flips happen on average ~once per in-game day. Stationary
// amplitude is set by the ratio shock² / (6·reversion): with both knobs
// halved relative to a brisker config, jitter halves while amplitude is
// preserved. Windmill clamps the magnitude for output via Mathf.Min(1, w),
// so any excursions past ±1 saturate at MaxOutput. Positive = blowing right.
public class WeatherSystem {
    public static WeatherSystem instance { get; private set; }

    // Reload-Domain-off support — see MaintenanceSystem.ResetStatics for the why.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; }

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

    // Positive = wind blowing right. Eased toward `targetWind` continuously
    // in Tick; targetWind itself takes a fresh OU step each in-game hour.
    public float wind { get; private set; }

    // Hourly OU random-walk target; `wind` lerps toward it each frame.
    // Not persisted — on load both reset to 0 and the walk warms back up.
    float targetWind;

    // Exponential smoothing rate (per real second). 0.15 → ~95% of the way
    // to a new target after ~20 s of real time, comfortably faster than the
    // hourly step interval (≈25 s for a 10-min IRL day) so wind isn't
    // perpetually mid-transition, but slow enough to feel weather-y.
    const float windSmoothingRate = 0.15f;

    // Ambient temperature in Celsius, driven by yearly + daily sine waves.
    // Yearly: peaks midsummer (day 7.5) at ~30°C high, troughs midwinter (day 17.5) at ~5°C high.
    // Daily: peaks at 2pm, amplitude ±4°C around the daily mean.
    public float temperature { get; private set; }

    // ── Humidity (rain driver) ─────────────────────────────────────────────
    // Smoothed atmospheric humidity in [0, 1]. `targetHumidity` is the OU
    // random-walk state stepped each in-game hour; `humidity` eases toward it
    // each frame so external readers (CloudLayer, rain trigger) see continuous
    // motion. Distinct from the soil-side MoistureSystem.
    public float humidity { get; private set; }
    float targetHumidity;

    // Central tendency the OU walk reverts toward. Sits below the rain
    // threshold so clear weather is the default and rain is an excursion.
    public const float humidityMean = 0.5f;

    // Above this, isRaining flips true. With humidityMean=0.5 and the shock
    // params below, the walk's stationary std is ~0.22, putting the threshold
    // about 0.7σ above mean — humidity sits above ~25% of the time, matching
    // the old 4%/12% rate's long-run rain fraction.
    public const float rainThreshold = 0.65f;

    // Reversion ≈ 0.12/hr → autocorrelation timescale ~8 hours, so a rain
    // episode (humidity sitting above the threshold) lasts on the order of
    // single-digit in-game hours — matches the average rain duration the
    // old 4% / 12% Bernoulli model produced.
    const float humidityReversion = 0.12f;
    // Shock half-width scales with √reversion to preserve stationary
    // variance V = h²/(6λ). Keeping std ≈ 0.22 puts the 0.65 threshold at
    // ~0.7σ above mean, so the long-run rain fraction stays around 25%.
    const float humidityShock     = 0.18f;

    // Continuous smoothing rate (per real second). Slower than wind because
    // weather inertia should feel like hours not seconds — visible cloud
    // build-up should outlast a single hourly step.
    const float humiditySmoothingRate = 0.05f;

    const float lerpDuration = 4f;

    // Called by World.Update() every frame.
    public void Tick(float dt) {
        UpdateTemperature();
        bool snowing  = isRaining && temperature <  snowThresholdC;
        bool rainingT = isRaining && temperature >= snowThresholdC;
        float step    = dt / lerpDuration;
        rainAmount = Mathf.MoveTowards(rainAmount, rainingT ? 1f : 0f, step);
        snowAmount = Mathf.MoveTowards(snowAmount, snowing  ? 1f : 0f, step);

        // Frame-rate-independent exponential ease toward the hourly targets.
        wind     = Mathf.Lerp(wind,     targetWind,     1f - Mathf.Exp(-dt * windSmoothingRate));
        humidity = Mathf.Lerp(humidity, targetHumidity, 1f - Mathf.Exp(-dt * humiditySmoothingRate));
    }

    // Called by World.Update() once per in-game hour.
    public void OnHourElapsed() {
        targetWind += -0.01f * targetWind + Rng.Range(-0.10f, 0.10f);

        // Humidity OU step. Mean-revert toward humidityMean, then shock.
        // Clamp keeps the walk inside [0, 1] so cloud-cover math stays sane;
        // the clamp's reflection bias is small because the mean sits well
        // inside the interval.
        targetHumidity += -humidityReversion * (targetHumidity - humidityMean)
                          + Rng.Range(-humidityShock, humidityShock);
        targetHumidity = Mathf.Clamp01(targetHumidity);

        // Rain flips on the smoothed humidity (not the target) so the
        // transition matches what the player sees on the cloud field, which
        // also reads the smoothed value.
        bool nowRaining = humidity > rainThreshold;
        if (nowRaining != isRaining) SetRain(nowRaining);

        if (isRaining) {
            // Snow doesn't fill tanks or top up puddles immediately — accumulation
            // and melt are a future feature; for now snowing skips this entirely.
            if (temperature >= snowThresholdC) {
                ReplenishRainwater();
                SoakExposedFloorInventories();
            }
        }

        // Soil diffusion, evaporation, and plant passive draw run once per in-game hour.
        // Rain uptake runs on a separate per-second cadence in World.Update.
        MoistureSystem.instance?.HourlyUpdate();
    }

    // Refreshes the wet timer on every floor inventory exposed to the open sky while
    // rain (not snow) is falling. Wet floor inventories decay at 2× the normal rate
    // for the duration; sweep is hourly so a freshly-dropped pile waits at most one
    // in-game hour before catching its first soak. Storage and animal inventories
    // are never wet by construction (MarkWet gates on InvType.Floor).
    void SoakExposedFloorInventories() {
        World w = World.instance;
        if (w == null) return;
        float duration = World.ticksInDay / 4f;
        for (int x = 0; x < w.nx; x++) {
            for (int y = 0; y < w.ny; y++) {
                Tile t = w.GetTileAt(x, y);
                if (t?.inv == null || t.inv.invType != Inventory.InvType.Floor) continue;
                if (!w.IsExposedAbove(x, y)) continue;
                t.inv.MarkWet(duration);
            }
        }
    }

    void ReplenishRainwater() {
        if (WaterController.instance == null) return;
        WaterController.instance.RainReplenish();
        WaterController.instance.RainFillTanks();
    }

    // Called by SaveSystem when loading a save file — snaps immediately, no lerp.
    // Picks the rain or snow channel based on the temperature reconstructed from
    // the world timer, so a save loaded mid-winter resumes as snow not rain.
    //
    // Humidity restores from save when available. On old saves (humidity=0) we
    // synthesize a plausible value from the rain flag so the cloud field
    // doesn't disagree with the rain state on the first frame: above-threshold
    // if it was raining, mean otherwise. The walk will take over from there.
    public void RestoreState(bool rain, float savedHumidity) {
        isRaining = rain;
        UpdateTemperature();
        bool asSnow = rain && temperature < snowThresholdC;
        rainAmount  = (rain && !asSnow) ? 1f : 0f;
        snowAmount  = asSnow            ? 1f : 0f;

        if (savedHumidity > 0f) {
            humidity = savedHumidity;
        } else {
            humidity = rain ? rainThreshold + 0.05f : humidityMean;
        }
        targetHumidity = humidity;
    }

    // Combined precipitation intensity — overcast is overcast regardless of type.
    static float Overcast() => Mathf.Max(instance?.rainAmount ?? 0f, instance?.snowAmount ?? 0f);

    // Multiplier applied to sunSource.intensity by SunController.
    public static float GetSunMultiplier()     => Mathf.Lerp(1.0f, 0.25f, Overcast());

    // Multiplier applied to the ambient color returned by SunController.GetAmbientColor().
    public static float GetAmbientMultiplier() => Mathf.Lerp(1.0f, 0.90f, Overcast());

    public static WeatherSystem Create() {
        instance = new WeatherSystem();
        // Seed humidity at the mean so a fresh world doesn't start with an
        // empty cloudless sky that takes hours to build up via the OU walk.
        instance.humidity = humidityMean;
        instance.targetHumidity = humidityMean;
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

        temperature = 13.5f + 12.5f * yearly + 3f * daily;
    }

    void SetRain(bool rain) {
        isRaining = rain;
    }

    // Debug-toggle entry point. Flips the raining state AND nudges humidity
    // across the threshold so the next OnHourElapsed doesn't immediately
    // revert. Tick() handles the visual fade. The OU walk takes over from
    // here, so the toggled state lasts as long as humidity stays on its side.
    public void ToggleRain() {
        bool nowRaining = !isRaining;
        SetRain(nowRaining);
        humidity       = nowRaining ? rainThreshold + 0.05f : rainThreshold - 0.05f;
        targetHumidity = humidity;
    }

    // Debug entry point. Snaps both the smoothed `wind` and the OU random-walk
    // `targetWind` to the same value — otherwise the Tick lerp would pull
    // `wind` back toward the unchanged target within a few seconds. The walk
    // resumes from this value on the next hourly step.
    public void SetWind(float w) {
        wind = w;
        targetWind = w;
    }

    // Debug entry point. Snaps humidity (and its OU target) to a value in
    // [0, 1]. Also flips isRaining to match the new threshold state so the
    // visual fade in Tick begins immediately rather than waiting for the
    // next OnHourElapsed roll to notice.
    public void SetHumidity(float h) {
        humidity = Mathf.Clamp01(h);
        targetHumidity = humidity;
        bool nowRaining = humidity > rainThreshold;
        if (nowRaining != isRaining) SetRain(nowRaining);
    }
}

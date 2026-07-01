using UnityEngine;

// Tracks the current weather state and drives hourly transitions.
//
// Initialised by World.Awake(). Per-frame tick driven by World.Update().
// SunController queries GetSunMultiplier() / GetAmbientMultiplier() every frame.
//
// Rain is driven by a continuous `humidity` field: rain happens when humidity
// crosses `rainThreshold` from below, and stops when it crosses back. Humidity
// itself is a slow Ornstein-Uhlenbeck walk reverting toward `humidityMean`,
// stepped 3× per in-game hour and smoothed continuously toward the target.
// The walk is calibrated so the smoothed humidity sits above the rain
// threshold ≈11% of the time (≈1 rain episode/in-game-day, ~3 hr each; estimate
// after lowering the threshold to 0.67 from the Monte-Carlo-verified ≈9% at
// 0.7). CloudLayer also reads `humidity` to drive cloud count, size, altitude
// and tint, so the sky visually builds up before rain and clears after.
//
// Precipitation intensity is a smooth function of humidity (see ComputeWetness):
// 0 below (rainThreshold − rainLerpHalfRange), ramping linearly to 1 above
// (rainThreshold + rainLerpHalfRange). Particles fade in before the discrete
// `isRaining` flag flips and fade out after it clears. Temperature gates which
// channel carries the intensity — rainAmount if ≥ snowThresholdC, else
// snowAmount; both channels lerp smoothly via the same MoveTowards step so
// temperature transitions cross-fade rather than snap.
//
// Light multipliers at full intensity (max(rainAmount, snowAmount) = 1):
//   Sun intensity: 0.25
//   Ambient:       0.90
//
// Transitions lerp over lerpDuration seconds (default 5s).
//
// Wind: Ornstein-Uhlenbeck random walk steps targetWind 3× per in-game hour;
// the public `wind` field exponentially eases toward that target each frame
// so all readers (Windmill output / blades, RainParticles horizontal
// velocity, CloudLayer drift, plant sway shader) see continuous motion
// rather than sub-hourly steps.
//   target step = -reversion * targetWind  +  Uniform(-shock, +shock)
//   wind <- Lerp(wind, targetWind, 1 - exp(-dt * windSmoothingRate))
// Mean reversion is slow — wind drifts for hours before pulling back, so
// direction flips happen on average ~once per in-game day. Stationary
// amplitude is set by the ratio shock² / (6·reversion); constants are sized
// so the typical drift sits near ±0.4. Windmill clamps the magnitude for
// output via Mathf.Min(1, w), so any excursions past ±1 saturate at
// MaxOutput. Positive = blowing right.
public class WeatherSystem {
    public static WeatherSystem instance { get; private set; }

    // Reload-Domain-off support — see MaintenanceSystem.ResetStatics for the why.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void ResetStatics() { instance = null; }

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

    // Per-sub-step OU constants. Sized so 3 sub-steps per in-game hour
    // reproduce the same stationary std (≈0.41) and autocorrelation timescale
    // (~100 in-game hours) as the previous hourly 0.01 / 0.10 step. Shock
    // half-width scales with √reversion to preserve stationary variance
    // V = h²/(6λ).
    const float windReversion = 0.00333f;
    const float windShock     = 0.0577f;

    // Exponential smoothing rate (per real second). Halved 0.45→0.225 when
    // ticksInDay doubled (240→480): the rate is per real second but the OU
    // step interval scales with the day, so halving keeps wind chasing its
    // target by the same ~78%/step as the old 240-day — gently continuous,
    // never plateauing, and not perpetually lagging either.
    const float windSmoothingRate = 0.225f;

    // ── Temperature-noise OU constants ─────────────────────────────────────
    // Stepped twice per in-game day (World.Tick). reversion 0.3/step → an
    // autocorrelation timescale ~1/0.3 ≈ 3 steps ≈ 1.5 days, so warm/cold spells
    // linger rather than flickering. tempShock is the Uniform(±) half-width; with
    // reversion it gives stationary variance V = shock²/(6·reversion) ≈ 4 →
    // std ≈ 2°C. The gentle smoothing below leaves that variance essentially
    // unchanged (sim-verified), so no shock inflation is needed.
    const float tempReversion     = 0.3f;
    const float tempShock         = 2.67f;
    const float tempSmoothingRate = 0.01f;  // per timer-second; ~0.2-day ease toward target

    // Ambient temperature in Celsius = yearly sine + daily sine + a random
    // anomaly (see below). Yearly: peaks midsummer (~28°C high), troughs
    // midwinter (~4°C high). Daily: peaks at 2pm, amplitude ±2.5°C around the mean.
    public float temperature { get; private set; }

    // ── Temperature noise (day-to-day weather variability) ──────────────────
    // A mean-reverting OU random walk layered on top of the deterministic sines
    // so otherwise-identical days differ by a few degrees — cold snaps and warm
    // spells. `targetTempAnomaly` takes an OU step twice per in-game day (driven
    // from World.Tick); `tempAnomaly` eases toward it each frame so the applied
    // offset moves continuously rather than jumping at the step. Calibrated for
    // ~2°C stationary std (typical ±2–4°C, occasional ±6°C). Mean is 0, so this
    // adds variability without shifting the climate's average. Same two-layer
    // shape as wind/humidity. The smoothed offset is persisted, so a save/reload
    // mid cold-snap resumes continuously; on load targetTempAnomaly is re-seeded
    // to it and the walk carries on. Old saves load 0 (a valid neutral state).
    public float tempAnomaly { get; private set; }
    float targetTempAnomaly;

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

    // Midpoint of the precipitation lerp band — also where the discrete
    // `isRaining` boolean flips. With humidityMean=0.5 and the shock params
    // below, the walk's stationary std is ~0.21, putting the threshold
    // about 0.8σ above mean. Light drizzle starts below this (see lerp band).
    public const float rainThreshold = 0.67f;

    // Half-width of the humidity band over which rainAmount/snowAmount lerp
    // from 0 → 1. Below (rainThreshold − rainLerpHalfRange = 0.57) no particles;
    // above (rainThreshold + rainLerpHalfRange = 0.77) full intensity. Most
    // rain-driven effects (drain, replenish, tank fill, floor soak) scale
    // their magnitude by the resulting wetness so light drizzle has light
    // effects and heavy rain has heavy effects — no on/off cliff at the
    // discrete threshold.
    const float rainLerpHalfRange = 0.1f;

    // Reversion ≈ 0.05 per sub-step × 3 sub-steps/hr ≈ 0.15/hr →
    // autocorrelation timescale ~6.5 in-game hours. Bumped above the bare
    // OU calibration so clear↔rain transitions are less autocorrelated and
    // feel closer to the old per-hour Bernoulli model's independent flips.
    const float humidityReversion = 0.05f;
    // Shock half-width sets the stationary variance V = h²/(6λ) of the OU
    // target. Set slightly above the bare √reversion calibration (≈0.1162)
    // to offset the variance that the humidity low-pass below removes — so
    // the *smoothed* humidity keeps std ≈ 0.22 and the 0.67 threshold stays
    // ~0.8σ above mean. Long-run rain fraction ≈ 11% of the time (estimated
    // from the σ shift after lowering the threshold from 0.7, where Monte-Carlo
    // gave ≈ 9% — not re-verified); raise this in lockstep if
    // humiditySmoothingRate is lowered further, or rain thins out.
    const float humidityShock     = 0.121f;

    // Subtracted from `targetHumidity` each sub-step, scaled by current
    // wetness. Physically: rain depletes atmospheric moisture, so episodes
    // don't linger above the threshold. At full rain (wetness=1) the drain
    // is the full value, lowering the effective equilibrium from
    // humidityMean=0.5 to 0.5 - rainHumidityDrain/humidityReversion = 0.2 —
    // well below rainThreshold so rain reliably terminates. At light
    // drizzle the drain is proportionally smaller.
    const float rainHumidityDrain = 0.015f;

    // Continuous smoothing rate (per timer-second). Slower than wind because
    // weather inertia should feel like hours not seconds — visible cloud
    // build-up should outlast several sub-hourly steps. Lowered from 0.15 to
    // 0.08 to make the cloud field thicken / clear more lazily (CloudLayer
    // reads `humidity` for its coverage threshold). Heavier smoothing damps
    // the signal's variance, so humidityShock is raised to hold the rain
    // fraction; the side effect is rain arrives in fewer, longer episodes
    // (≈0.8/day vs ≈1.1/day before). If you want lazier coverage WITHOUT
    // touching rain episode count, give CloudLayer its own separate low-pass
    // and leave this on the fast signal instead.
    // Halved 0.08→0.04 when ticksInDay doubled (240→480): this rate is per real
    // second, so halving keeps the smoothing window the same fraction of the
    // now-2x-longer day — preserving the damped variance and thus rain fraction.
    const float humiditySmoothingRate = 0.04f;

    const float lerpDuration = 4f;

    // Called by World.Update() every frame.
    public void Tick(float dt) {
        // Ease the temperature anomaly toward its OU target before recomputing
        // temperature, so this frame's reading reflects the smoothed offset.
        tempAnomaly = Mathf.Lerp(tempAnomaly, targetTempAnomaly, 1f - Mathf.Exp(-dt * tempSmoothingRate));
        UpdateTemperature();
        // Precipitation intensity rises smoothly with humidity across a band
        // centred on rainThreshold — particles fade in before isRaining flips
        // and fade out after it clears. Temperature gates which channel (rain
        // or snow) carries the wetness.
        float wetness = ComputeWetness();
        bool  isSnow  = temperature < snowThresholdC;
        float step    = dt / lerpDuration;
        rainAmount = Mathf.MoveTowards(rainAmount, isSnow ? 0f : wetness, step);
        snowAmount = Mathf.MoveTowards(snowAmount, isSnow ? wetness : 0f, step);

        // Frame-rate-independent exponential ease toward the sub-hourly targets.
        wind     = Mathf.Lerp(wind,     targetWind,     1f - Mathf.Exp(-dt * windSmoothingRate));
        humidity = Mathf.Lerp(humidity, targetHumidity, 1f - Mathf.Exp(-dt * humiditySmoothingRate));
    }

    // Called by World.Update() 3× per in-game hour. Steps the OU random-walk
    // targets for wind and humidity, then checks the rain transition against
    // the smoothed humidity. Constants are sized so the long-run statistics
    // match the previous once-per-hour walk (see windReversion / humidityReversion).
    public void StepWindHumidity() {
        targetWind += -windReversion * targetWind
                      + Rng.Range(-windShock, windShock);

        // Humidity OU step. Mean-revert toward humidityMean, then shock.
        // Clamp keeps the walk inside [0, 1] so cloud-cover math stays sane;
        // the clamp's reflection bias is small because the mean sits well
        // inside the interval.
        targetHumidity += -humidityReversion * (targetHumidity - humidityMean)
                          + Rng.Range(-humidityShock, humidityShock);
        // Rain depletes humidity, so episodes terminate instead of lingering.
        // Scaled by wetness so light drizzle drains weakly and heavy rain
        // drains hard — no on/off jump at the discrete threshold crossing.
        targetHumidity -= rainHumidityDrain * ComputeWetness();
        targetHumidity = Mathf.Clamp01(targetHumidity);

        // Rain flips on the smoothed humidity (not the target) so the
        // transition matches what the player sees on the cloud field, which
        // also reads the smoothed value. Checked on the finer sub-hourly
        // cadence so rain start/stop isn't snapped to hour boundaries.
        bool nowRaining = humidity > rainThreshold;
        if (nowRaining != isRaining) SetRain(nowRaining);
    }

    // Called by World.Tick twice per in-game day. One OU step of the temperature
    // anomaly: mean-revert toward 0, then add a uniform shock. The smoothed
    // `tempAnomaly` (eased each frame in Tick) is what actually offsets temperature.
    public void StepTemperatureAnomaly() {
        targetTempAnomaly += -tempReversion * targetTempAnomaly
                             + Rng.Range(-tempShock, tempShock);
    }

    // Called by World.Update() once per in-game hour. Hourly physical effects
    // of weather; the OU random walks live in StepWindHumidity instead.
    public void OnHourElapsed() {
        // Snow doesn't fill tanks or top up puddles immediately — accumulation
        // and melt are a future feature; for now snowing routes wetness to
        // snowAmount and leaves rainAmount at 0, so the calls below early-exit
        // without us needing an explicit temperature gate.
        if (rainAmount > 0f) {
            ReplenishRainwater(rainAmount);
            SoakExposedFloorInventories(rainAmount);
        }

        // Soil diffusion, evaporation, and plant passive draw run once per in-game hour.
        // Rain uptake runs on a separate per-second cadence in World.Update.
        MoistureSystem.instance?.HourlyUpdate();
    }

    // Refreshes the wet timer on every floor inventory exposed to the open sky.
    // Wet floor inventories decay at 2× the normal rate for the duration; sweep
    // is hourly so a freshly-dropped pile waits at most one in-game hour before
    // catching its first soak. Storage and animal inventories are never wet by
    // construction (MarkWet gates on InvType.Floor). Duration scales with
    // rainIntensity so a light drizzle only wets briefly while a downpour wets
    // for the full quarter-day baseline.
    void SoakExposedFloorInventories(float rainIntensity) {
        World w = World.instance;
        if (w == null) return;
        float duration = (World.ticksInDay / 4f) * rainIntensity;
        if (duration <= 0f) return;
        for (int x = 0; x < w.nx; x++) {
            for (int y = 0; y < w.ny; y++) {
                Tile t = w.GetTileAt(x, y);
                if (t?.inv == null || t.inv.invType != Inventory.InvType.Floor) continue;
                if (!w.IsExposedAbove(x, y)) continue;
                t.inv.MarkWet(duration);
            }
        }
    }

    void ReplenishRainwater(float rainIntensity) {
        if (WaterController.instance == null) return;
        WaterController.instance.RainReplenish(rainIntensity);
        WaterController.instance.RainFillTanks(rainIntensity);
    }

    // Called by SaveSystem when loading a save file — snaps immediately, no lerp.
    // Picks the rain or snow channel based on the temperature reconstructed from
    // the world timer, so a save loaded mid-winter resumes as snow not rain.
    //
    // Humidity restores from save when available. On old saves (humidity=0) we
    // synthesize a plausible value from the rain flag so the cloud field
    // doesn't disagree with the rain state on the first frame: above-threshold
    // if it was raining, mean otherwise. The walk will take over from there.
    public void RestoreState(bool rain, float savedHumidity, float savedTempAnomaly = 0f) {
        isRaining = rain;
        // Seed the noise offset (and its OU target) before recomputing temperature
        // so the restored reading includes the persisted anomaly. 0 on old saves.
        tempAnomaly       = savedTempAnomaly;
        targetTempAnomaly = savedTempAnomaly;
        UpdateTemperature();
        if (savedHumidity > 0f) {
            humidity = savedHumidity;
        } else {
            humidity = rain ? rainThreshold + 0.05f : humidityMean;
        }
        targetHumidity = humidity;

        // Snap rain/snow channels to the wetness implied by humidity so the
        // loaded scene shows the right intensity on the first frame instead
        // of fading in over lerpDuration.
        float wetness = ComputeWetness();
        bool  isSnow  = temperature < snowThresholdC;
        rainAmount = isSnow ? 0f : wetness;
        snowAmount = isSnow ? wetness : 0f;
    }

    // Smooth precipitation intensity from the current smoothed humidity:
    // 0 below (rainThreshold − rainLerpHalfRange), ramps linearly to 1 above
    // (rainThreshold + rainLerpHalfRange). Read by Tick() and RestoreState().
    float ComputeWetness() {
        return Mathf.InverseLerp(rainThreshold - rainLerpHalfRange,
                                 rainThreshold + rainLerpHalfRange,
                                 humidity);
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

    // Concise temperature label shared by the thermometer's readouts (top-bar date display +
    // info panel). Rounded to the nearest even degree with a "c" suffix — the m5x7 font has no
    // degree glyph — and "<0c" for anything below freezing rather than a negative number.
    public static string FormatTemp(float tempC) {
        if (tempC < 0f) return "<0c";
        return (2 * Mathf.RoundToInt(tempC / 2f)) + "c";
    }

    // Recalculates temperature from the current world timer plus the smoothed
    // random anomaly.
    // temperature = 13.5 + 12*sin(yearly) + 2.5*sin(daily) + tempAnomaly
    //   yearly peaks at midsummer, daily peaks at 2pm (hour 14/24).
    void UpdateTemperature() {
        float timer = World.instance.timer;
        float yearLength = World.ticksInDay * World.daysInYear;
        float yearFrac = (timer % yearLength) / yearLength;
        float dayFrac  = (timer % World.ticksInDay) / World.ticksInDay;

        const float twoPi = 2f * Mathf.PI;
        float yearly = Mathf.Sin(twoPi * yearFrac - Mathf.PI / 4f);   // peaks at day 7.5
        float daily  = Mathf.Sin(twoPi * dayFrac  - 2f * Mathf.PI / 3f); // peaks at hour 14

        temperature = 13.5f + 12f * yearly + 2.5f * daily + tempAnomaly;
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

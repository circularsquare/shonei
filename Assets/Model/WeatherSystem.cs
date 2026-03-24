using UnityEngine;

// Tracks the current weather state and drives hourly transitions.
//
// Initialised by World.Awake(). Per-frame tick driven by World.Update().
// SunController queries GetSunMultiplier() / GetAmbientMultiplier() every frame.
//
// Rain probabilities (per in-game hour):
//   Clear → Rain:  10%
//   Rain  → Clear: 25%
//
// Light multipliers at full rain (rainAmount = 1):
//   Sun intensity: 0.10  (–90%)
//   Ambient:       0.90  (–10%)
//
// Transitions lerp over lerpDuration seconds (default 5s).
public class WeatherSystem {
    public static WeatherSystem instance { get; private set; }

    public bool isRaining { get; private set; }

    // 0 = fully clear, 1 = fully raining. Lerps smoothly on weather change.
    public float rainAmount { get; private set; }

    const float lerpDuration = 5f;

    // Called by World.Update() every frame.
    public void Tick(float dt) {
        float target = isRaining ? 1f : 0f;
        rainAmount = Mathf.MoveTowards(rainAmount, target, dt / lerpDuration);
    }

    // Called by World.Update() once per in-game hour.
    public void OnHourElapsed() {
        if (!isRaining) {
            if (Random.value < 0.1f) SetRain(true);
        } else {
            if (Random.value < 0.2f) SetRain(false);
            ReplenishRainwater();
        }
    }

    void ReplenishRainwater() {
        if (WaterController.instance != null) WaterController.instance.RainReplenish();
    }

    // Called by SaveSystem when loading a save file — snaps immediately, no lerp.
    public void RestoreState(bool rain) {
        isRaining   = rain;
        rainAmount  = rain ? 1f : 0f;
    }

    // Multiplier applied to sunSource.intensity by SunController.
    public static float GetSunMultiplier()     => Mathf.Lerp(1.0f, 0.10f, instance?.rainAmount ?? 0f);

    // Multiplier applied to the ambient color returned by SunController.GetAmbientColor().
    public static float GetAmbientMultiplier() => Mathf.Lerp(1.0f, 0.90f, instance?.rainAmount ?? 0f);

    public static WeatherSystem Create() {
        instance = new WeatherSystem();
        return instance;
    }

    void SetRain(bool rain) {
        isRaining = rain;
        Debug.Log("Weather: " + (rain ? "Rain started" : "Rain stopped"));
    }
}

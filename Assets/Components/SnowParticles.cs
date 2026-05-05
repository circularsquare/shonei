using UnityEngine;

// Snow. Falls slowly, whitish, no splash.
//
// All the camera-tracking, viewport-fill, lifetime, and density logic lives in
// PrecipitationParticles. This subclass just reads the snow channel; particles
// are killed on impact (so they don't pile up below the world) but emit no
// follow-up effects today. Tile accumulation / persistent snow cover is a
// future feature — would extend OnParticleHit() to mark the tile.
//
// Tune in the inspector by lowering fallSpeed (default 7 → e.g. 1.5) and
// reducing densityPerUnitWidth so flakes feel airier than rain. The prefab's
// velocityOverLifetime.y must match fallSpeed (negative magnitude). Wind drift
// is more visible at low fall speeds — windSpeedScale stays useful.
public class SnowParticles : PrecipitationParticles {
    protected override float GetIntensity() => WeatherSystem.instance?.snowAmount ?? 0f;
}

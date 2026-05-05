using UnityEngine;

// Drives snow accumulation and melt on tiles. Pure C# singleton, mirrors
// MoistureSystem / OverlayGrowthSystem in shape — created from World.Awake()
// and ticked once per in-game second from World.Tick.
//
// ── Per-tile rules ────────────────────────────────────────────────────────
//
// Eligibility (a tile is exposed enough for snow to land or rest on):
//   • tile.type.solid                   — air doesn't hold snow
//   • World.IsExposedAbove(x, y)        — no solid / solidTop / blocksRain above
//
// Roads and buildings on the same tile aren't gated here — sortingOrder layering
// in the renderer means a road's sprite is below snow (snow-covered road = nice
// wintry look) and a building's sprite sits above snow on its anchor tile, so
// snow simply hides under the building visual without us having to filter.
//
// Accumulation (only fires when conditions hold):
//   • currently snowing (WeatherSystem.snowAmount > 0)
//   • temperature < AccumThresholdC (0 °C)
//   • Rng roll: AccumChancePerSecond × snowAmount  → at full snowfall, ~1/10 per
//     second per tile; light flurries proportionally slower
//
// On accumulation: tile.snow = true. If grass exists on the tile, kill it —
// overlayMask cleared and overlayState set to Dead, so a later melt reveals
// bare ground rather than the grass that was there. (Grass can regrow via
// OverlayGrowthSystem once temps and moisture allow.)
//
// Melt (only fires when tile.snow is already true):
//   • temperature > MeltStartC          → linear ramp
//   • chance = clamp01((temp - MeltStartC) / (MeltFullC - MeltStartC))
//   • at MeltFullC (5 °C) and above: 100% chance per second → ~1 second to clear
//   • at 2.5 °C: 50% chance per second → ~1.5 second mean time-to-clear
//
// ── Future extensions ─────────────────────────────────────────────────────
// • Snow depth (byte) instead of binary, so visuals can show light dusting
//   vs deep cover. Would replace the bool with a counter, with accumulation
//   incrementing and melt decrementing.
// • Hysteresis on melt to prevent rapid flicker around 0 °C if temperature
//   oscillates within a single in-game day cycle.
public class SnowAccumulationSystem {
    public static SnowAccumulationSystem instance { get; private set; }

    const float AccumThresholdC      = 0f;        // strictly less than → can accumulate
    const float MeltStartC           = 0f;        // strictly greater than → starts melting (linearly ramped from here)
    const float MeltFullC            = 5f;        // at and above → 100% melt chance per tick
    const float AccumChancePerSecond = 1f / 10f;  // base chance at snowAmount=1; scaled by current intensity

    public static SnowAccumulationSystem Create() {
        instance = new SnowAccumulationSystem();
        return instance;
    }

    public void Tick() {
        var weather = WeatherSystem.instance;
        if (weather == null) return;
        World world = World.instance;
        if (world == null) return;

        float temp        = weather.temperature;
        float snowAmount  = weather.snowAmount;
        bool  canAccum    = snowAmount > 0f && temp < AccumThresholdC;
        float meltChance  = temp > MeltStartC
            ? Mathf.Clamp01((temp - MeltStartC) / (MeltFullC - MeltStartC))
            : 0f;
        // Whole-grid early-exit only if neither direction is possible. Both can
        // be false simultaneously (e.g. exactly 0 °C with no snow falling) — in
        // which case there's nothing for the system to do this tick.
        if (!canAccum && meltChance <= 0f) return;

        float accumP = AccumChancePerSecond * snowAmount;
        int   nx     = world.nx, ny = world.ny;

        for (int x = 0; x < nx; x++) {
            for (int y = 0; y < ny; y++) {
                Tile t = world.GetTileAt(x, y);
                if (t == null) continue;

                if (t.snow) {
                    if (meltChance > 0f && Rng.value < meltChance) t.snow = false;
                    continue;
                }

                if (!canAccum) continue;
                if (!t.type.solid) continue;
                if (!world.IsExposedAbove(x, y)) continue;

                if (Rng.value < accumP) {
                    t.snow = true;
                    // Snow on grass kills the grass. Clearing overlayMask hides it
                    // immediately; setting Dead persists the "killed" state so it
                    // doesn't auto-regrow on the next OverlayGrowthSystem tick.
                    if (t.overlayMask != 0) {
                        t.overlayMask  = 0;
                        t.overlayState = OverlayState.Dead;
                    }
                }
            }
        }
    }
}

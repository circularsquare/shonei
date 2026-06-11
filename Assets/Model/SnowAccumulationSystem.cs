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
// Underlying grass is *preserved*, not destroyed: at accumulation we snapshot
// the live overlayMask/state into tile.preSnowOverlayMask/State and clear the
// live mask so the snow renders cleanly on top. OverlayGrowthSystem skips
// snowed tiles entirely (so the snapshot doesn't drift). On melt we restore
// the mask + state — the same grass that was there comes back unchanged, then
// resumes normal growth/wilt under current conditions.
//
// Accumulation (only fires when conditions hold):
//   • currently snowing (WeatherSystem.snowAmount > 0)
//   • temperature < AccumThresholdC (0 °C). Note this is colder than the 2 °C
//     snowfall threshold, so in the 0–2 °C band flakes fall but don't stick —
//     accumulation only happens around the coldest days of the year (≈ day 21).
//   • Rng roll: AccumChancePerSecond × snowAmount  → at full snowfall, ~1/10 per
//     second per tile; light flurries proportionally slower
//
// On accumulation: tile.snow = true. If grass exists on the tile, kill it —
// overlayMask cleared and overlayState set to Dead, so a later melt reveals
// bare ground rather than the grass that was there. (Grass can regrow via
// OverlayGrowthSystem once temps and moisture allow.)
//
// Melt (only fires when tile.snow is already true):
//   • temperature > MeltStartC          → quadratic ramp
//   • chance = clamp01( ((temp - MeltStartC) / (MeltFullC - MeltStartC))^2 )
//   • squaring keeps melt near-zero just above freezing and accelerates it as
//     the day warms, so snow can survive a cold winter day and only clears once
//     the afternoon climbs well above freezing. Mean time-to-clear = 1/chance:
//   • at MeltStartC (1 °C) and below: no melt — snow persists indefinitely
//   • at 2 °C:  0.25% chance per second → ~400 s mean time-to-clear (~20 in-game hr)
//   • at 3 °C:  1% per second           → ~100 s
//   • at 5 °C:  4% per second           → ~25 s
//   • at 11 °C: 25% per second          → ~4 s
//   • at MeltFullC (21 °C) and above:   100% per second → ~1 s to clear
//
// ── Future extensions ─────────────────────────────────────────────────────
// • Snow depth (byte) instead of binary, so visuals can show light dusting
//   vs deep cover. Would replace the bool with a counter, with accumulation
//   incrementing and melt decrementing.
// • Hysteresis on melt to prevent rapid flicker around 0 °C if temperature
//   oscillates within a single in-game day cycle.
public class SnowAccumulationSystem {
    public static SnowAccumulationSystem instance { get; private set; }

    // Reload-Domain-off support — see MaintenanceSystem.ResetStatics for the why.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void ResetStatics() { instance = null; }

    const float AccumThresholdC      = 0f;        // strictly less than → can accumulate
    const float MeltStartC           = 1f;        // strictly greater than → starts melting (quadratically ramped from here)
    const float MeltFullC            = 21f;       // at and above → 100% melt chance per tick
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
        // Quadratic ramp: square the normalized (MeltStartC→MeltFullC) fraction so
        // melt stays near-zero just above freezing and only ramps up as it warms.
        float meltRamp    = (temp - MeltStartC) / (MeltFullC - MeltStartC);
        float meltChance  = temp > MeltStartC
            ? Mathf.Clamp01(meltRamp * meltRamp)
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
                    if (meltChance > 0f && Rng.value < meltChance) {
                        // Restore the under-snow grass before flipping snow off
                        // so the overlay renderer redraws with the correct
                        // mask/state on the same callback wave.
                        if (t.preSnowOverlayMask != 0)
                            t.overlayMask = t.preSnowOverlayMask;
                        t.overlayState        = t.preSnowOverlayState;
                        t.preSnowOverlayMask  = 0;
                        t.preSnowOverlayState = OverlayState.Live;
                        t.snow = false;
                    }
                    continue;
                }

                if (!canAccum) continue;
                if (!t.type.solid) continue;
                if (!world.IsExposedAbove(x, y)) continue;

                if (Rng.value < accumP) {
                    // Snapshot under-snow grass so melt can restore it exactly.
                    // overlayState is captured even when mask is 0 — it's free
                    // and lets us round-trip Dying/Dead states cleanly. Clearing
                    // the live mask is what actually hides grass under the snow;
                    // OverlayGrowthSystem skips snowed tiles so neither the live
                    // values nor the snapshot drift while snow sits on top.
                    t.preSnowOverlayMask  = t.overlayMask;
                    t.preSnowOverlayState = t.overlayState;
                    if (t.overlayMask != 0) t.overlayMask = 0;
                    t.snow = true;
                }
            }
        }
    }
}

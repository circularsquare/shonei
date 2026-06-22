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
// Underlying grass is *preserved*, not destroyed — with no snapshot. The
// renderer skips the grass overlay quad while tile.snow is true (the snow mesh
// draws on top), and OverlayGrowthSystem skips snowed tiles entirely, so the
// live overlayMask/state sit frozen and untouched. On melt the same grass
// simply reappears and resumes normal growth/wilt under current conditions.
//
// Accumulation (only fires when conditions hold):
//   • currently snowing (WeatherSystem.snowAmount > 0)
//   • temperature < AccumThresholdC (0 °C). Note this is colder than the 2 °C
//     snowfall threshold, so in the 0–2 °C band flakes fall but don't stick —
//     accumulation only happens around the coldest days of the year (≈ day 21).
//   • Rng roll: AccumChancePerSecond × snowAmount  → at full snowfall, ~1/10 per
//     second per tile; light flurries proportionally slower
//   • On a hit: tile.snowAmount += AccumStep (capped at SnowMax), so a tile builds
//     depth over sustained snowfall and climbs through the snowlight→mid→deep textures.
//     The grass underneath is just hidden by the renderer — no snapshot.
//
// Depth → texture: SnowLevel() maps the continuous amount to a discrete level
// (0 none / 1 / 2 / 3); the chunked renderer draws one mesh per level. The mapping
// is deterministic (no flicker near a threshold) — the organic, non-uniform depth
// across the field comes from per-tile accumulation/melt timing, not from RNG here.
//
// Melt (only fires when tile.snowAmount > 0):
//   • temperature > MeltStartC          → quadratic per-second chance (ramp below)
//   • chance = clamp01( ((temp - MeltStartC) / (MeltFullC - MeltStartC))^2 )
//   • On a hit: tile.snowAmount -= MeltStep (gradual draw-down, not an instant
//     clear) — reaches 0 after ~SnowMax/MeltStep hits. Squaring keeps melt
//     near-zero just above freezing and accelerates as the day warms, so snow
//     survives a cold winter day and clears as the afternoon warms. Per-second
//     hit chance by temperature:
//   • at MeltStartC (1 °C) and below: no melt — snow persists indefinitely
//   • 2 °C: 0.25%/s    5 °C: 4%/s    11 °C: 25%/s    MeltFullC (21 °C)+: 100%/s
//
// ── Future extensions ─────────────────────────────────────────────────────
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
    const float AccumChancePerSecond = 0.12f;     // base chance at snowAmount=1; scaled by current intensity

    // ── Depth tuning ────────────────────────────────────────────────────
    // Continuous depth runs 0..SnowMax. Each accumulation hit adds AccumStep, each
    // melt hit removes MeltStep — so a tile climbs/draws-down over several rolls
    // rather than snapping on/off. Depth2/3Threshold split the range into the three
    // visual levels. All tunable to taste; no save format depends on the values.
    public const byte SnowMax  = 100;             // max tracked depth
    const int  AccumStep       = 8;               // depth added per accumulation hit (~12 hits to full)
    const int  MeltStep        = 6;               // depth removed per melt hit
    const byte Depth2Threshold = 34;              // amount ≥ here → snowmid texture
    const byte Depth3Threshold = 67;              // amount ≥ here → snowdeep texture

    // Maps a continuous depth to a discrete visual level: 0 (none) / 1 / 2 / 3. The
    // renderer keys one snow mesh per level off this. Deterministic (no RNG) so the
    // texture doesn't flicker as the depth wobbles near a threshold.
    public static int SnowLevel(byte amount) {
        if (amount == 0)              return 0;
        if (amount < Depth2Threshold) return 1;
        if (amount < Depth3Threshold) return 2;
        return 3;
    }

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

                // Accumulation and melt are temperature-exclusive (accum needs
                // temp < 0, melt needs temp > 1), so a tile does at most one per tick.

                // Accumulate: cold + snowing + exposed solid. Builds depth up to
                // SnowMax, including on tiles that already carry snow, so a tile
                // climbs through snowlight→mid→deep over sustained snowfall. The grass
                // underneath is preserved (renderer hides it while snowAmount > 0).
                if (canAccum && t.type.solid && world.IsExposedAbove(x, y)) {
                    if (Rng.value < accumP)
                        t.snowAmount = (byte)Mathf.Min(SnowMax, t.snowAmount + AccumStep);
                    continue;
                }

                // Melt: temperature-paced roll shaves MeltStep off the depth rather
                // than clearing in one go, so snow draws down smoothly through the
                // depth textures. The tile leaves the snow set only when it hits 0,
                // re-revealing the frozen grass automatically (it was never cleared).
                if (t.snowAmount > 0 && meltChance > 0f && Rng.value < meltChance)
                    t.snowAmount = (byte)Mathf.Max(0, t.snowAmount - MeltStep);
            }
        }
    }
}

using UnityEngine;

// Live growth and health-state evolution of tile overlay decoration (today: grass
// on dirt, future: moss on stone, etc.). Pure C# singleton, mirrors MoistureSystem
// in shape — created from World.Awake() and ticked from World.Tick at the
// per-second cadence.
//
// ── State machine (per tile) ──────────────────────────────────────────────
// Each overlay-bearing tile carries a Live / Dying / Dead state. Death is
// medium-fast (per-second chance ~ DeathChancePerSecond, ~10 s steady-state
// average), recovery is slow (per-second chance ~ GrowChancePerSecondPerSide,
// ~1 in-game day average — same scale as fresh-grass growth so a Dead tile
// takes about as long to revive as bare dirt takes to sprout).
//
//   • temp <  DeadTempMax (-1°C)        → Dead   (roll, from Live OR Dying)
//   • temp <  DyingTempMax (2°C)        → Dying  (roll, only from Live)
//   • moisture == 0                     → Dying  (roll, only from Live)
//   • Dying or Dead, temp > 5, moist>40 → Live   (roll, slower)
//
// Sudden deep freeze rolls Live → Dead direct (no Dying intermediate); gradual
// cooling can walk Live → Dying → Dead as conditions step down.
//
// Growable / dying / dead thresholds are stacked: anywhere temp ≥ 5 AND
// moisture > 40, the tile is healthy and can spread. Anywhere temp drops past
// 2 the grass starts wilting. Anywhere temp drops below -1, it's killed off
// outright. Recovery requires returning to genuinely growable conditions.
//
// ── Per-side growth (Live tiles only) ─────────────────────────────────────
//   • tile.type.overlay != null  (only overlay-bearing tile types grow)
//   • tile.overlayState == Live
//   • tile.moisture > MoistureMin
//   • temp > GrowableTempMin
//   • side is currently NOT grassy (mask bit clear)
//   • side is currently exposed (cardinal neighbour non-solid)
//   • neighbour on that side is non-flooded (water == 0)
//   • side is L, R, or U — never D (grass doesn't grow on undersides)
//
// Each eligible side rolls independently against GrowChancePerSecondPerSide.
// Tunable: at ~1/120 per side per second the expected wait per side is roughly
// half an in-game day (240 ticks/day, 1 tick/sec). Crank up while testing
// visuals, crank back down for production. Future seasonal/temperature variants
// and moss-on-stone slot in here as additional rule blocks keyed by overlay name.
public class OverlayGrowthSystem {
    public static OverlayGrowthSystem instance { get; private set; }

    // Tuning constants. Adjust to taste — there's no save format involved.
    const byte  MoistureMin                = 40;        // strictly greater than this
    const float GrowableTempMin            = 5f;        // strictly greater than this (°C) — growth + recovery gate
    const float DyingTempMax               = 2f;        // strictly less than this triggers Dying (or moisture==0)
    const float DeadTempMax                = -1f;       // strictly less than this triggers Dead (overrides Dying)
    const float GrowChancePerSecondPerSide = 1f / 120f; // ~½ in-game day expected wait per side
    const float DeathChancePerSecond       = 0.1f;      // per-tick chance to advance toward Dying or Dead while conditions warrant it

    // Bits we're allowed to flip ON: L, R, U (not D — no underside grass).
    // Layout matches Tile.overlayMask: 0=L 1=R 2=D 3=U.
    const byte GrowableSidesMask = 0b1011;

    public static OverlayGrowthSystem Create() {
        instance = new OverlayGrowthSystem();
        return instance;
    }

    // Called from World.Tick once per real-time second. Walks every tile in
    // the world; each candidate side is an independent Bernoulli trial driven
    // by Rng (deterministic gameplay RNG, save-reproducible).
    //
    // Note: no global cold early-exit. Death transitions need to fire below
    // the growth gate (down at sub-zero temps), so we always walk the grid.
    public void Tick() {
        var weather = WeatherSystem.instance;
        if (weather == null) return;
        float temp = weather.temperature;
        bool growable = temp > GrowableTempMin;

        World world = World.instance;
        int nx = world.nx, ny = world.ny;

        for (int x = 0; x < nx; x++) {
            for (int y = 0; y < ny; y++) {
                Tile t = world.GetTileAt(x, y);
                if (t.type.overlay == null) continue;

                // Cardinal-solidity bitmask: bit set = neighbour is solid (side buried).
                // Computed once at the top so we can short-circuit fully-buried tiles
                // before doing any other work. Same convention as
                // WorldController.OnTileOverlayChanged so the renderer and growth
                // agree on what "exposed" means.
                int cMask = 0;
                if (IsSolidAt(world, x - 1, y    )) cMask |= 1;
                if (IsSolidAt(world, x + 1, y    )) cMask |= 2;
                if (IsSolidAt(world, x,     y - 1)) cMask |= 4;
                if (IsSolidAt(world, x,     y + 1)) cMask |= 8;

                // Fully buried — skip both state evolution and growth. Buried
                // grass is treated as preserved-in-place: insulated from surface
                // weather, frozen at whatever state it carried when it got buried.
                // When a neighbour is mined and the tile becomes exposed again, the
                // next Tick re-evaluates normally. Saves work on deep-dirt seams
                // and on grass tiles the player has built over.
                if (cMask == 0xF) continue;

                // ── State transitions ─────────────────────────────────
                // Only meaningful where grass actually exists. Bare overlay
                // tiles stay Live (default) — there's no decoration to wilt.
                if (t.overlayMask != 0)
                    UpdateOverlayState(t, temp, growable);

                // Per-side growth runs only on healthy tiles in growable conditions.
                if (t.overlayState != OverlayState.Live) continue;
                if (!growable) continue;
                if (t.moisture <= MoistureMin) continue;

                // Eligible = growable side & exposed & not already grassy.
                byte mask = t.overlayMask;
                int eligible = GrowableSidesMask & ~cMask & ~mask & 0xF;
                if (eligible == 0) continue;

                byte newMask = mask;
                for (int d = 0; d < 4; d++) {
                    int bit = 1 << d;
                    if ((eligible & bit) == 0) continue;
                    if (NeighbourIsFlooded(world, x, y, d)) continue;
                    if (Rng.value < GrowChancePerSecondPerSide)
                        newMask |= (byte)bit;
                }
                if (newMask != mask) t.overlayMask = newMask;
            }
        }
    }

    // Function of (current state, temperature, moisture) with an Rng roll on
    // every transition — both death and recovery. Death rolls at the higher
    // DeathChancePerSecond rate (~10 s expected in steady cold); recovery rolls
    // at the slower GrowChancePerSecondPerSide rate (~1 in-game day expected,
    // matching fresh-grass growth so a Dead tile takes about as long to revive
    // as bare dirt takes to sprout). Single roll per tick — gradual cooling can
    // walk Live → Dying → Dead, but a sudden deep freeze rolls Live → Dead direct.
    static void UpdateOverlayState(Tile t, float temp, bool growable) {
        OverlayState state = t.overlayState;
        OverlayState newState = state;

        if (temp < DeadTempMax) {
            // Conditions warrant Dead. Live or Dying → Dead.
            if (state != OverlayState.Dead && Rng.value < DeathChancePerSecond)
                newState = OverlayState.Dead;
        } else if (temp < DyingTempMax || t.moisture == 0) {
            // Live → Dying on mild cold or full dryout. Dead stays Dead — only
            // a full recovery roll promotes back, never an intermediate Dying step.
            if (state == OverlayState.Live && Rng.value < DeathChancePerSecond)
                newState = OverlayState.Dying;
        } else if (state != OverlayState.Live && growable && t.moisture > MoistureMin) {
            // Conditions are growth-capable: chance per second to recover to Live.
            if (Rng.value < GrowChancePerSecondPerSide) newState = OverlayState.Live;
        }

        if (newState != state) t.overlayState = newState;
    }

    static bool IsSolidAt(World world, int x, int y) {
        Tile t = world.GetTileAt(x, y);
        return t != null && t.type.solid;
    }

    static bool NeighbourIsFlooded(World world, int x, int y, int dir) {
        int nx = x, ny = y;
        switch (dir) {
            case 0: nx -= 1; break; // L
            case 1: nx += 1; break; // R
            case 2: ny -= 1; break; // D
            case 3: ny += 1; break; // U
        }
        Tile n = world.GetTileAt(nx, ny);
        return n != null && n.water > 0;
    }
}

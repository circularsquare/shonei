using UnityEngine;

// Soil moisture simulation — pure C# singleton, created from World.Awake() alongside
// WeatherSystem / MaintenanceSystem. Owns Tile.moisture (byte 0..tile.type.moistureCapacity) on
// SOLID tiles only; air tiles stay 0. Capacity is per-material (earth 100, stone less); all clamps,
// the diffusion conductivity, and the well threshold read tile.type.moistureCapacity. Plants read
// moisture from the solid tile directly below them (see PlantType.IsMoistureComfortableAt). Water
// levels stay on WaterController; this system reads AND (during seep) decrements tile.water to
// account for liquid absorbed into soil.
//
// Dispatch cadences (all driven from World.Update + WeatherSystem.OnHourElapsed):
//
//   Per 1 s    (in-game s.) — RainUptakePerSecond() : adds a 1/TicksPerInGameHour slice
//                             of the hourly rain rate to non-capped soil.
//                           — SeepPerSecond() : water→soil neighbour absorption. Drains
//                             actual water from the source tile (1 water → MoisturePerWaterUnit
//                             moisture, billed via per-soil debt so water drains at sub-unit rate).
//   Per 10 s   (in-game hr) — HourlyUpdate() : single snapshot-and-sweep that does
//                             • soil-to-soil diffusion (capillary spread) on every solid tile
//                             • evaporation on non-capped soil only
//                             • per-plant passive draw from the soil tile below
//
// "Capped" = tile directly above the soil is solid ground OR carries any structure with
// solidTop=true (buildings, platforms, roads). Capped soil never evaporates and never
// receives rain, so cave farms / covered growhouses hold baseline without irrigation.
public class MoistureSystem {
    public static MoistureSystem instance { get; private set; }

    // Reload-Domain-off support — see MaintenanceSystem.ResetStatics for the why.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void ResetStatics() { instance = null; }

    public const byte  MoistureMax                = 100;   // full saturation / default + pool capacity; per-tile cap is tile.type.moistureCapacity
    public static int  TicksPerInGameHour => World.ticksInDay / 24;  // in-game-hour length in ticks; derived so it can't drift from ticksInDay
    private const int  MoistureRainGainPerHour    = 100;    // at rainAmount=1; subdivided into per-second slices
    private const int  MoistureEvaporationPerHour = 1;      // non-capped soil only
    // Diffusion: each in-game hour a solid tile pulls this percent of the moisture gap to its
    // wettest solid neighbour. Applied through a per-tile sub-unit debt accumulator (`_diffDebt`) so
    // fractional pulls below 1 moisture still accumulate and eventually move. Without it the integer
    // round-off floored small gaps to zero, permanently stalling diffusion a few moisture short of
    // the source (worse once per-material caps shrink the moisture range, e.g. a 20-max stone).
    private const int  MoistureDiffusionPctPerHour = 15;    // 15% of the gap per in-game hour (horizontal / base)

    // Gravity bias on VERTICAL diffusion: down-flow (source above) runs at (base + this), up-flow
    // (source below) at (base − this), horizontal at base. Slight — soil moisture wicks upward
    // (capillary) but percolates down faster. Must stay < MoistureDiffusionPctPerHour so the up-flow
    // rate stays positive. Applies to all soil (not just per-material caps): rain sinks quickly,
    // groundwater rises slowly.
    private const int  MoistureGravityBiasPct = 3;          // down 18%/h, up 12%/h, side 15%/h

    // Per-second seep budget:
    //   • MoistureSeepMoisturePerSec — soil absorption rate cap (moisture/tile/sec). This
    //     governs how fast a dry soil tile saturates from a wet neighbour (~10 s at 10/sec).
    //   • MoisturePerWaterUnit — moisture yield per 1 internal sim water unit drained. The seep
    //     drain is sim-unit-granular (tile.water is integer), so it's decoupled from the absorption
    //     rate via a per-tile debt accumulator (`_seepDebt`): each tick's absorbed moisture credits
    //     to the soil's debt, and only every whole MoisturePerWaterUnit of debt cashes out as 1
    //     water from the source. At MoisturePerWaterUnit == MoistureSeepMoisturePerSec the source
    //     drains ~1 water/sec/soil.
    private const int  MoistureSeepMoisturePerSec = 10;

    // 1 liang of water ≡ this many soil-moisture. THE meaningful moisture ⇄ water conversion: water
    // is measured in liang (the real player-facing unit); internal sim water units exist only for the
    // fluid display. A fully saturated soil tile (moisture 100) is therefore worth 2 liang. Public —
    // the well reservoir and farmer watering both read it.
    public const int   MoisturePerLiang = 50;

    // Moisture per 1 internal sim water unit drained — derived from MoisturePerLiang so liang stays
    // the only authored rate. Used by the sim-unit-granular seep/well drains (tile.water and
    // Well.storedWater are integer sim units). = 50 * 32 / 160 = 10.
    public const int   MoisturePerWaterUnit =
        MoisturePerLiang * WaterController.LiangPerFullTile / WaterController.WaterMax;

    private byte[] _moistureSnapshot;                       // reused per HourlyUpdate; lazy-init
    private int[]  _seepDebt;                               // per-tile absorbed-but-unpaid moisture; flushes to water in whole MoisturePerWaterUnit chunks
    private int[]  _diffDebt;                               // per-tile sub-unit diffusion intake, in 1/100-moisture; flushes to whole moisture units

    public static MoistureSystem Create() {
        instance = new MoistureSystem();
        return instance;
    }

    // ── Water-item ⇄ soil-moisture conversion ────────────────────────────────
    // Lets the farmer watering system (WaterPlantTask) hand-pour water onto soil at the SAME rate
    // the world physics uses, so a bottled "water" item is worth exactly the moisture it would have
    // produced had that water seeped in from a tile. Both sides speak liang (100 fen = 1 liang), and
    // 1 liang ≡ MoisturePerLiang moisture — so the conversion is direct, no sim-water-unit detour.
    // The seep economy yields the same per-liang rate by construction (MoisturePerWaterUnit derives
    // from MoisturePerLiang), so the watering and physics economies can't silently drift apart.

    // Moisture produced by pouring `fen` of the water item onto soil.
    public static int MoistureForWaterFen(int fen) => fen * MoisturePerLiang / 100;

    // Fen of the water item needed to add `moisture` to soil (rounded up — never under-fetch).
    public static int WaterFenForMoisture(int moisture) =>
        (moisture * 100 + MoisturePerLiang - 1) / MoisturePerLiang;

    // Per-second water→soil seep. Each solid tile with headroom pulls from its wettest
    // 4-orthogonal water neighbour and absorbs up to MoistureSeepMoisturePerSec moisture.
    // Payment is debt-amortised via _seepDebt: each tick's absorbed moisture credits to
    // the soil's debt; only every whole MoisturePerWaterUnit of accumulated debt
    // cashes out as 1 water drained from the source. The debt machinery lets water drain
    // at a sub-unit-per-second rate when yield > rate (yield == rate today → ~1 water per
    // second per soil) without giving the soil free moisture in the long run. If the source runs dry mid-cycle we cap
    // absorption so leftover debt stays below one whole water unit — no free overrun.
    // Sweep-direction bias is accepted (minor).
    public void SeepPerSecond() {
        World world = World.instance;
        int nx = world.nx, ny = world.ny;
        int cells = nx * ny;
        if (_seepDebt == null || _seepDebt.Length != cells) _seepDebt = new int[cells];

        for (int x = 0; x < nx; x++) {
            for (int y = 0; y < ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                if (!tile.type.solid) continue;
                int headroom = tile.type.moistureCapacity - tile.moisture;
                if (headroom <= 0) continue;

                Tile src = PickWettestWaterNeighbor(world, x, y);
                if (src == null) continue;

                int idx        = y * nx + x;
                int desiredAdd = headroom < MoistureSeepMoisturePerSec ? headroom : MoistureSeepMoisturePerSec;
                int newDebt    = _seepDebt[idx] + desiredAdd;
                int waterCost  = newDebt / MoisturePerWaterUnit;

                // Source can't fully cover. Drain what's available and cap absorption so
                // leftover debt stays in [0, MoisturePerWaterUnit). Anything we'd otherwise absorb
                // past that bound would be unpaid moisture.
                if (waterCost > src.water) {
                    waterCost = src.water;
                    int maxDebtAfter = (waterCost + 1) * MoisturePerWaterUnit - 1;
                    if (newDebt > maxDebtAfter) {
                        desiredAdd -= newDebt - maxDebtAfter;
                        newDebt     = maxDebtAfter;
                    }
                }
                if (desiredAdd <= 0) continue;

                if (waterCost > 0) src.water -= (ushort)waterCost;
                _seepDebt[idx] = newDebt - waterCost * MoisturePerWaterUnit;
                tile.moisture  = (byte)(tile.moisture + desiredAdd);
            }
        }
    }

    private static Tile PickWettestWaterNeighbor(World world, int x, int y) {
        Tile best = null;
        int bestW = 0;
        ConsiderWaterNeighbor(ref best, ref bestW, world.GetTileAt(x - 1, y));
        ConsiderWaterNeighbor(ref best, ref bestW, world.GetTileAt(x + 1, y));
        ConsiderWaterNeighbor(ref best, ref bestW, world.GetTileAt(x, y - 1));
        ConsiderWaterNeighbor(ref best, ref bestW, world.GetTileAt(x, y + 1));
        return best;
    }

    private static void ConsiderWaterNeighbor(ref Tile best, ref int bestW, Tile n) {
        if (n == null || n.water == 0) return;
        if (n.water > bestW) { best = n; bestW = n.water; }
    }

    // Per-in-game-second rain uptake. Only applies to soil whose immediate tile-above
    // isn't a ceiling. Gains 1/TicksPerInGameHour of the full-hour rate each call so
    // rainfall feels smooth instead of arriving as one hourly jump.
    public void RainUptakePerSecond() {
        World world = World.instance;
        WeatherSystem weather = WeatherSystem.instance;
        float rain = weather?.rainAmount ?? 0f;
        if (rain <= 0f) return;

        int perSecondGain = Mathf.RoundToInt(rain * MoistureRainGainPerHour / (float)TicksPerInGameHour);
        if (perSecondGain <= 0) return;

        int nx = world.nx, ny = world.ny;
        for (int x = 0; x < nx; x++) {
            for (int y = 0; y < ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                if (!tile.type.solid) continue;
                if (y + 1 >= ny) continue;
                if (CapsSoilFromAbove(world.GetTileAt(x, y + 1))) continue;

                int m = tile.moisture + perSecondGain;
                int cap = tile.type.moistureCapacity;
                if (m > cap) m = cap;
                tile.moisture = (byte)m;
            }
        }
    }

    // Dispatched once per in-game hour from WeatherSystem.OnHourElapsed.
    // Per solid tile:
    //   1. Soil-to-soil diffusion — pulls from the wettest solid neighbour (snapshot).
    //   2. Evaporation (non-capped soil only — i.e. no ceiling directly above).
    // After the tile sweep:
    //   3. Plant passive draw — each plant deducts plantType.moistureDrawPerHour
    //      from the soil tile below. Undersupplied plants simply take what's there.
    public void HourlyUpdate() {
        World world = World.instance;
        int nx = world.nx, ny = world.ny;
        int cells = nx * ny;

        // Lazy-allocate snapshot + diffusion-debt buffers; reused across calls.
        if (_moistureSnapshot == null || _moistureSnapshot.Length != cells)
            _moistureSnapshot = new byte[cells];
        if (_diffDebt == null || _diffDebt.Length != cells)
            _diffDebt = new int[cells];

        // Snapshot before any modification so diffusion pulls from a consistent state.
        for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
                _moistureSnapshot[y * nx + x] = world.GetTileAt(x, y).moisture;

        const int evaporation = MoistureEvaporationPerHour;

        for (int x = 0; x < nx; x++) {
            for (int y = 0; y < ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                if (!tile.type.solid) continue;

                int snapIdx = y * nx + x;
                int cur     = _moistureSnapshot[snapIdx];
                int m       = cur;

                // 1. Soil diffusion: pull toward the moisture this tile WOULD hold at its most-
                // saturated solid neighbour's saturation — i.e. the driving force is the saturation
                // gap, not the absolute one (a full low-cap stone pushes as hard as full dirt). The
                // `target = nbrMoisture × thisCap ÷ nbrCap` form bakes in the puller-cap conductivity
                // scaling, so a low-cap tile is a poor conductor (slow barrier). The rate is then
                // gravity-biased by the winning neighbour's vertical direction (down faster, up
                // slower). Sub-unit pulls accumulate in _diffDebt (1/100-moisture) and cash out as
                // whole moisture, so even a tiny gap eventually closes instead of rounding to zero.
                // Non-conservative — the source isn't debited (capillary spread, not a finite
                // reservoir). Equalizes SATURATION, so the far side of a low-cap barrier can fully
                // saturate, just slowly.
                int cap    = tile.type.moistureCapacity;
                int target = cur, srcDir = 0;   // srcDir: +1 source above (down-flow), -1 below (up-flow), 0 side
                if (x > 0)      EvalDiffusionNbr(world.GetTileAt(x - 1, y), snapIdx - 1,   0, cap, ref target, ref srcDir);
                if (x < nx - 1) EvalDiffusionNbr(world.GetTileAt(x + 1, y), snapIdx + 1,   0, cap, ref target, ref srcDir);
                if (y > 0)      EvalDiffusionNbr(world.GetTileAt(x, y - 1), snapIdx - nx, -1, cap, ref target, ref srcDir);
                if (y < ny - 1) EvalDiffusionNbr(world.GetTileAt(x, y + 1), snapIdx + nx, +1, cap, ref target, ref srcDir);
                int diff = target - cur;
                if (diff > 0) {
                    int pct = MoistureDiffusionPctPerHour + srcDir * MoistureGravityBiasPct;   // gravity bias
                    _diffDebt[snapIdx] += diff * pct;                            // credit pct% of the gap, in 1/100-moisture
                    int whole = _diffDebt[snapIdx] / 100;
                    if (whole > 0) { m += whole; _diffDebt[snapIdx] -= whole * 100; }
                }

                // 2. Evaporation on non-capped soil only. "Capped" = immediate tile above
                // is solid ground OR carries a solidTop structure (see CapsSoilFromAbove).
                if (y + 1 < ny && !CapsSoilFromAbove(world.GetTileAt(x, y + 1))) {
                    m -= evaporation;
                    if (m < 0) m = 0;
                }

                if (m > cap) m = cap; // clamp to this material's capacity (also self-heals over-cap old saves)
                if (m != cur) tile.moisture = (byte)m;
            }
        }

        // 3. Plant passive draw. Each plant pulls moistureDrawPerHour from its reservoir — the soil
        // tile below, or a self-contained greenhouse's pool — clamped ≥ 0, no penalty if undersupplied
        // (the advancement cost in Plant.Grow is where moisture actually gates growth).
        PlantController pc = PlantController.instance;
        if (pc != null) {
            foreach (Plant plant in pc.Plants) {
                // A greenhouse's controlled humidity cuts transpiration — scale the draw by the
                // frame's moisture multiplier (same factor trims the stage cost in Plant.Grow).
                float drawF = plant.plantType.moistureDrawPerHour;
                // Broken greenhouse: no transpiration savings until repaired (mirrors Plant.Grow).
                Structure ghStruct = plant.tile.greenhouse;
                StructType gh = (ghStruct != null && !ghStruct.IsBroken) ? ghStruct.structType : null;
                if (gh != null) drawF *= gh.greenhouseMoistureMult;
                int draw = Mathf.RoundToInt(drawF);
                if (draw <= 0) continue;
                // Routes to the self-contained pool or the soil below; no-op if the plant has neither.
                plant.AddReservoirMoisture(-draw);
            }
        }
    }

    // Evaluates one solid neighbour for the diffusion pull: the moisture the PULLER (cap pullerCap)
    // would hold at the neighbour's saturation (nbrMoisture × pullerCap ÷ nbrCap). If that beats the
    // running `best`, it becomes the new target and records the neighbour's vertical direction `dir`
    // (+1 above, −1 below, 0 side) so the caller can gravity-bias the rate. Non-solid neighbours
    // (moisture 0) never win. A neighbour can't exceed its own cap, so the target never exceeds
    // pullerCap (diffusion can't push a tile over capacity).
    private void EvalDiffusionNbr(Tile nbr, int idx, int dir, int pullerCap, ref int best, ref int bestDir) {
        if (nbr == null || !nbr.type.solid) return;
        int t = _moistureSnapshot[idx] * pullerCap / nbr.type.moistureCapacity;
        if (t > best) { best = t; bestDir = dir; }
    }

    // True when the tile directly above a soil tile prevents rain from reaching it:
    // it is either solid ground or carries any structure with solidTop=true OR
    // blocksRain=true (buildings, platforms, roads, tarps). Off-world (null) treated
    // as capped.
    private static bool CapsSoilFromAbove(Tile t) {
        if (t == null) return true;
        if (t.type.solid) return true;
        for (int d = 0; d < t.structs.Length; d++) {
            Structure s = t.structs[d];
            if (s != null && (s.structType.solidTop || s.structType.blocksRain)) return true;
        }
        return false;
    }

    // Zeros moisture on every tile. Called from WorldController.ClearWorld().
    public void Clear() {
        World world = World.instance;
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                world.GetTileAt(x, y).moisture = 0;
            }
        }
        if (_seepDebt != null) System.Array.Clear(_seepDebt, 0, _seepDebt.Length);
        if (_diffDebt != null) System.Array.Clear(_diffDebt, 0, _diffDebt.Length);
    }
}

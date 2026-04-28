using UnityEngine;

// Soil moisture simulation — pure C# singleton, created from World.Awake() alongside
// WeatherSystem / MaintenanceSystem. Owns Tile.moisture (byte 0–100) on SOLID tiles only;
// air tiles stay 0. Plants read moisture from the solid tile directly below them (see
// PlantType.IsComfortableAt). Water levels stay on WaterController; this system reads
// AND (during seep) decrements tile.water to account for liquid absorbed into soil.
//
// Dispatch cadences (all driven from World.Update + WeatherSystem.OnHourElapsed):
//
//   Per 1 s    (in-game s.) — RainUptakePerSecond() : adds a 1/TicksPerInGameHour slice
//                             of the hourly rain rate to non-capped soil.
//                           — SeepPerSecond() : water→soil neighbour absorption. Drains
//                             actual water from the source tile (1 water → 10 moisture).
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

    public const byte  MoistureMax                = 100;
    public const int   TicksPerInGameHour         = 10;     // 1 s ticks per in-game hour (from ticksInDay=240 / 24)
    private const int  MoistureRainGainPerHour    = 100;    // at rainAmount=1; subdivided into per-second slices
    private const int  MoistureEvaporationPerHour = 1;      // non-capped soil only
    private const float MoistureDiffusionPerHour  = 0.05f;  // fraction of neighbour gap pulled per in-game hour (5%)

    // Per-second seep budget: at most N water units drain from the wettest water neighbour
    // per soil tile per second, each buying GainPerWater moisture. A full WaterMax-filled
    // neighbour next to a dry soil tile therefore takes ~10 s to saturate that soil, and
    // a 1-tile pond next to 4 solid tiles drains in ~(WaterMax/4) seconds under load.
    private const int  MoistureSeepWaterPerSec    = 1;
    private const int  MoistureSeepGainPerWater   = 10;

    private byte[] _moistureSnapshot;                       // reused per HourlyUpdate; lazy-init

    public static MoistureSystem Create() {
        instance = new MoistureSystem();
        return instance;
    }

    // Per-second water→soil seep. Each solid tile with headroom pulls from its wettest
    // 4-orthogonal water neighbour, converting MoistureSeepWaterPerSec water units into
    // MoistureSeepGainPerWater moisture each. Partial fills (when soil is near
    // saturation) still pay the same water-per-moisture-added ratio — no free moisture.
    // Sweep-direction bias is accepted (minor: drain is 1 water/sec per pair).
    public void SeepPerSecond() {
        World world = World.instance;
        int nx = world.nx, ny = world.ny;
        int maxThisTick = MoistureSeepWaterPerSec * MoistureSeepGainPerWater;

        for (int x = 0; x < nx; x++) {
            for (int y = 0; y < ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                if (!tile.type.solid) continue;
                int headroom = MoistureMax - tile.moisture;
                if (headroom <= 0) continue;

                Tile src = PickWettestWaterNeighbor(world, x, y);
                if (src == null) continue;

                int desiredAdd = headroom < maxThisTick ? headroom : maxThisTick;
                // Ceil-divide so a partial-fill soil still pays the same rate.
                int waterCost = (desiredAdd + MoistureSeepGainPerWater - 1) / MoistureSeepGainPerWater;
                if (waterCost > src.water) waterCost = src.water;
                if (waterCost <= 0) continue;

                int actualAdd = waterCost * MoistureSeepGainPerWater;
                if (actualAdd > headroom) actualAdd = headroom;

                src.water     -= (ushort)waterCost;
                tile.moisture  = (byte)(tile.moisture + actualAdd);
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
                if (m > MoistureMax) m = MoistureMax;
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

        // Lazy-allocate snapshot buffer; reused across calls.
        if (_moistureSnapshot == null || _moistureSnapshot.Length != cells)
            _moistureSnapshot = new byte[cells];

        // Snapshot before any modification so diffusion pulls from a consistent state.
        for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
                _moistureSnapshot[y * nx + x] = world.GetTileAt(x, y).moisture;

        const int   evaporation = MoistureEvaporationPerHour;
        const float diffFrac    = MoistureDiffusionPerHour;

        for (int x = 0; x < nx; x++) {
            for (int y = 0; y < ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                if (!tile.type.solid) continue;

                int snapIdx = y * nx + x;
                int cur     = _moistureSnapshot[snapIdx];
                int m       = cur;

                // 1. Soil diffusion: pull from wettest solid neighbour (snapshot values).
                int maxN = cur;
                if (x > 0)      maxN = MaxSolidSnap(maxN, world.GetTileAt(x - 1, y), snapIdx - 1);
                if (x < nx - 1) maxN = MaxSolidSnap(maxN, world.GetTileAt(x + 1, y), snapIdx + 1);
                if (y > 0)      maxN = MaxSolidSnap(maxN, world.GetTileAt(x, y - 1), snapIdx - nx);
                if (y < ny - 1) maxN = MaxSolidSnap(maxN, world.GetTileAt(x, y + 1), snapIdx + nx);
                int diff = maxN - cur;
                if (diff > 0) {
                    int pull = Mathf.RoundToInt(diff * diffFrac);
                    if (pull > 0) m += pull;
                }

                // 2. Evaporation on non-capped soil only. "Capped" = immediate tile above
                // is solid ground OR carries a solidTop structure (see CapsSoilFromAbove).
                if (y + 1 < ny && !CapsSoilFromAbove(world.GetTileAt(x, y + 1))) {
                    m -= evaporation;
                    if (m < 0) m = 0;
                }

                if (m > MoistureMax) m = MoistureMax; // diffusion clamp
                if (m != cur) tile.moisture = (byte)m;
            }
        }

        // 3. Plant passive draw. Each plant pulls moistureDrawPerHour from the soil
        // tile below — clamped ≥ 0, no penalty if undersupplied (the advancement cost
        // in Plant.Grow is where moisture actually gates growth).
        PlantController pc = PlantController.instance;
        if (pc != null) {
            foreach (Plant plant in pc.Plants) {
                int py = plant.tile.y - 1;
                if (py < 0) continue;
                Tile soil = world.GetTileAt(plant.tile.x, py);
                if (soil == null || !soil.type.solid) continue;
                int draw = Mathf.RoundToInt(plant.plantType.moistureDrawPerHour);
                if (draw <= 0) continue;
                int left = soil.moisture - draw;
                soil.moisture = left > 0 ? (byte)left : (byte)0;
            }
        }
    }

    // Returns max(cur, snapshot[idx]) when nbr is a solid tile, else cur.
    // Non-solid neighbours carry moisture = 0 semantically, so they never dominate.
    private int MaxSolidSnap(int cur, Tile nbr, int idx) {
        if (nbr == null || !nbr.type.solid) return cur;
        int v = _moistureSnapshot[idx];
        return v > cur ? v : cur;
    }

    // True when the tile directly above a soil tile prevents rain from reaching it:
    // it is either solid ground or carries any structure with solidTop=true
    // (buildings, platforms, roads). Off-world (null) treated as capped.
    private static bool CapsSoilFromAbove(Tile t) {
        if (t == null) return true;
        if (t.type.solid) return true;
        for (int d = 0; d < t.structs.Length; d++) {
            Structure s = t.structs[d];
            if (s != null && s.structType.solidTop) return true;
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
    }
}

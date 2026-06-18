using System.Collections.Generic;
using UnityEngine;

// WildHerbSystem — owns the spawning and seasonal lifecycle of WILD herb plants
// (PlantTypes with maxWild > 0). Wild herbs are NOT crops: the world seeds them, they
// grow in, and when their season ends they die back. Foraging (harvest) removes them
// entirely (see Plant.Harvest's isWild branch) rather than auto-replanting, and this
// system refills the population over time — so an equilibrium emerges between the spawn
// trickle and the forage rate.
//
// State is implicit: the live Plant population IS the state (plants persist as Structures),
// so this system holds NO save data. Each in-game hour it:
//   1. Culls a fraction of out-of-season wild herbs — staggered die-back, not an instant
//      wipe the moment the season flips (reads more naturally).
//   2. Trickles in at most SpawnsPerHour new herbs toward each type's maxWild cap, choosing
//      among in-season, under-cap types weighted by genWeight.
//
// Worldgen seeds the initial in-season population via SeedWorld (replaces the herb side of
// WorldGen.ScatterPlants — PickPlantType now excludes wild types so they don't double-spawn).
//
// Pure C# singleton, created from World.Awake alongside MoistureSystem / WeatherSystem and
// reset in World.OnDestroy (Reload-Domain-off support — see MoistureSystem.ResetStatics).
public class WildHerbSystem {
    public static WildHerbSystem instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void ResetStatics() { instance = null; }

    // Per-hour chance each out-of-season wild herb dies back. ~0.15 thins a cohort over
    // roughly an in-game day rather than wiping it the instant the season flips. Tunable.
    private const float CullChancePerHour = 0.15f;

    // Max herbs introduced per in-game hour across all types. Keeps refill a trickle so a
    // freshly-foraged world recovers gradually and foraging always matters.
    private const int SpawnsPerHour = 1;

    // Random columns probed per spawn before giving up for this attempt (the map may be
    // mostly ineligible terrain for a given placement kind).
    private const int PlacementAttempts = 40;

    private System.Random rng;

    public static WildHerbSystem Create() {
        instance = new WildHerbSystem { rng = new System.Random() };
        return instance;
    }

    // Hooked from World.Tick on the hourly cadence (alongside WeatherSystem.OnHourElapsed).
    public void OnHourElapsed() {
        if (World.instance == null || PlantController.instance == null) return;
        CullOutOfSeason();
        TrickleSpawn(World.instance);
    }

    // Worldgen initial seeding — fills each in-season wild type toward its cap, matured to a
    // random growth stage so a fresh world reads as established. Out-of-season types start
    // empty and trickle in when their season arrives. Called from WorldController.GenerateDefault
    // after ScatterPlants (which skips wild types). Deterministic off the world seed.
    public void SeedWorld(World world, int seed) {
        if (world == null) return;
        var seedRng = new System.Random(seed + 4242);
        foreach (var type in Db.plantTypeByName.Values) {
            if (!Spawnable(type)) continue;
            for (int i = 0; i < type.maxWild; i++)
                if (!TrySpawn(world, type, seedRng, mature: true)) break;
        }
    }

    // ── Seasonal die-back ────────────────────────────────────────────────────
    // Snapshots victims first because Destroy() mutates PlantController.Plants.
    private void CullOutOfSeason() {
        var plants = PlantController.instance.Plants;
        List<Plant> doomed = null;
        for (int i = 0; i < plants.Count; i++) {
            Plant p = plants[i];
            if (!p.plantType.isWild) continue;
            if (p.plantType.IsSeasonComfortableAt(WeatherSystem.instance)) continue; // in season → lives
            if (rng.NextDouble() >= CullChancePerHour) continue;
            (doomed ??= new List<Plant>()).Add(p);
        }
        if (doomed == null) return;
        foreach (Plant p in doomed) p.Destroy();
    }

    // ── Trickle spawn toward per-type caps ───────────────────────────────────
    private void TrickleSpawn(World world) {
        for (int n = 0; n < SpawnsPerHour; n++) {
            PlantType type = PickUnderCapType();
            if (type == null) return; // every eligible type is at cap (or none are in season)
            TrySpawn(world, type, rng, mature: false); // young — grows in visibly
        }
    }

    // genWeight-weighted random pick among wild types that are in-season AND below their
    // maxWild cap. Null if none are eligible.
    private PlantType PickUnderCapType() {
        Dictionary<PlantType, int> counts = CountWildLive();
        float total = 0f;
        foreach (var pt in Db.plantTypeByName.Values)
            if (UnderCap(pt, counts)) total += pt.genWeight;
        if (total <= 0f) return null;

        double pick = rng.NextDouble() * total;
        float acc = 0f;
        foreach (var pt in Db.plantTypeByName.Values) {
            if (!UnderCap(pt, counts)) continue;
            acc += pt.genWeight;
            if (pick < acc) return pt;
        }
        return null; // FP edge — caller treats as "nothing to spawn"
    }

    // Spawnable = a wild type whose season is currently active (ignores cap). UnderCap adds
    // the live-count < maxWild test on top.
    private static bool Spawnable(PlantType pt) =>
        pt.isWild && pt.genWeight > 0f && pt.IsSeasonComfortableAt(WeatherSystem.instance);

    private static bool UnderCap(PlantType pt, Dictionary<PlantType, int> counts) {
        if (!Spawnable(pt)) return false;
        counts.TryGetValue(pt, out int live);
        return live < pt.maxWild;
    }

    private Dictionary<PlantType, int> CountWildLive() {
        Dictionary<PlantType, int> counts = new();
        var plants = PlantController.instance.Plants;
        for (int i = 0; i < plants.Count; i++) {
            PlantType pt = plants[i].plantType;
            if (!pt.isWild) continue;
            counts.TryGetValue(pt, out int c);
            counts[pt] = c + 1;
        }
        return counts;
    }

    // ── Placement ────────────────────────────────────────────────────────────
    // Probes random columns for an eligible tile of the type's placement kind, then spawns.
    // `mature` true = worldgen seed (random established stage); false = runtime trickle (young).
    private bool TrySpawn(World world, PlantType type, System.Random r, bool mature) {
        if (!TryFindTile(world, type, r, out int x, out int y)) return false;
        Plant p = new Plant(type, x, y);
        if (mature) p.Mature(r.Next(0, type.maxStage + 1));
        StructController.instance.Place(p);
        return true;
    }

    private bool TryFindTile(World world, PlantType type, System.Random r, out int x, out int y) {
        x = y = -1;
        bool wantsWater = type.placement == "water";
        TileType dirt = Db.tileTypeByName["dirt"];
        for (int a = 0; a < PlacementAttempts; a++) {
            int cx = r.Next(0, world.nx);
            int cy = wantsWater ? FindTopWater(world, cx) : FindMeadowAir(world, cx, dirt);
            if (cy < 0) continue;
            x = cx; y = cy; return true;
        }
        return false;
    }

    // The air tile directly above the column's topmost solid dirt, clear of water and
    // structures — where a meadow herb roots. Scans top-down; the first solid hit is the
    // ground line. Independent of World.surfaceY (whose convention differs between the
    // generate path and the RecomputeSurfaceY save fallback).
    private int FindMeadowAir(World world, int x, TileType dirt) {
        for (int gy = world.ny - 1; gy >= 1; gy--) {
            Tile ground = world.GetTileAt(x, gy);
            if (ground == null || ground.type == null || !ground.type.solid) continue;
            if (ground.type != dirt) return -1;          // herbs only root on dirt surfaces
            Tile air = world.GetTileAt(x, gy + 1);
            if (air == null || air.type.solid) return -1;
            if (air.water > 0) return -1;
            if (air.structs[0] != null) return -1;
            return gy + 1;
        }
        return -1;
    }

    // The topmost meaningfully-filled water tile in the column with a free depth-0 slot (where
    // a lily floats). Skips shallow puddle tiles (<¼ full) so lilies don't perch on a sheen of
    // water that's about to evaporate; returns -1 if that tile is already occupied.
    private const int MinLilyWater = WaterController.WaterMax / 4;
    private int FindTopWater(World world, int x) {
        for (int wy = world.ny - 1; wy >= 0; wy--) {
            Tile t = world.GetTileAt(x, wy);
            if (t == null || t.water < MinLilyWater) continue;
            if (t.structs[0] != null) return -1;
            // Surface ponds only — lilies need open sky above, like the decorative flowers.
            // Excludes cave pools (rock overhead). IsExposedAbove is the shared sun/rain gate.
            if (!world.IsExposedAbove(x, wy)) return -1;
            return wy;
        }
        return -1;
    }
}

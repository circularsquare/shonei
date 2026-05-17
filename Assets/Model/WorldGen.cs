using System;
using System.Collections.Generic;
using UnityEngine;

// Pure C# world generation — no MonoBehaviour, all static methods.
// Called from WorldController.GenerateDefault() to fill the tile grid
// with terrain, caves, and natural features before graph.Initialize().
//
// Tuning params live as `public static` (not const) so the editor-only
// WorldGenTuner window can drive them via sliders. EditorPrefs persistence
// is handled in WorldGenTuner.cs — runtime defaults below are the canonical
// values; tuner overrides are reapplied on every domain reload via
// [InitializeOnLoadMethod].
public static class WorldGen {

    // ── Terrain shape ─────────────────────────────────────────────────
    // Tuned for the 200x120 default world. Surface sits ~halfway through the
    // grid with ~10 tiles of variation either side. Raising BaseHeight raises
    // the horizon line across the map; the rest of the world (dirt depth,
    // veins, caves) is anchored to the surface dynamically so the shape of
    // the underground stays consistent — only the absolute Y of the surface
    // line changes. Keep WaterLine ≈ BaseHeight so basins fill at the
    // expected horizon.
    public static int BaseHeight = 60;       // nominal surface y
    public static int SurfaceMin = 50;       // surface height never below this
    public static int SurfaceMax = 72;       // surface height never above this
    public static int DirtDepth = 3;         // dirt tiles above stone
    public static int BedrockY = 0;          // lowest row is always solid
    public static float SurfaceFreq = 0.06f; // noise frequency (lower = broader hills)
    public static float SurfaceAmp = 5.0f;   // noise amplitude (height variation)
    public static int SurfaceOctaves = 3;    // noise detail layers

    // Domain warping: before sampling the surface FBM at column x, shift x by a
    // second noise. Vanilla FBM looks "wavy" because the noise is statistically
    // uniform along x — every wavelength looks like every other. Warping the
    // input coordinate breaks that regularity, producing twisted, organic-
    // looking surface shapes (peaks lean, valleys curl, features compress and
    // stretch unevenly).
    //
    // WarpFreq must be on the same order as SurfaceFreq — if the warp varies
    // much slower than the features it's trying to distort, it just uniformly
    // slides whole regions of the noise around, which still looks like vanilla
    // FBM. Matching the frequencies (and adding a second octave for inner
    // detail) is what actually twists shapes within their own scale.
    //
    // The warp signal is decorrelated from the surface signal via a separate
    // seed offset. WarpAmp is in tiles of x-shift; ~one surface-wavelength
    // (≈16 at freq 0.06) is the upper bound before features start doubling
    // back on themselves chaotically.
    public static float SurfaceWarpFreq = 0.06f;
    public static float SurfaceWarpAmp = 10.0f;
    public static int SurfaceWarpOctaves = 2;
    public static int SurfaceWarpSeedOffset = 31337;

    // Region-level amplitude variation: a very slow noise signal multiplies the
    // surface amplitude per column, so some stretches read as plains (small
    // bumps) and others as hills/mountains (taller features). Freq is low
    // enough that regions are dozens of tiles wide rather than column-flipping.
    // The output is a multiplier on SurfaceAmp; values < 1 dampen, > 1 amplify.
    // Decorrelated from the warp signal via a separate seed offset.
    public static float SurfaceAmpVarFreq = 0.012f;
    public static float SurfaceAmpVarMin = 0.6f;
    public static float SurfaceAmpVarMax = 1.4f;
    public static int SurfaceAmpVarSeedOffset = 7919;

    // ── Spawn zone ────────────────────────────────────────────────────
    public static int SpawnMinX = 25;        // flat starting zone x-range (inclusive)
    public static int SpawnMaxX = 37;        // >=12 tiles wide
    public static int SpawnBlend = 4;        // tiles over which terrain blends to flat

    // ── Stone veins ───────────────────────────────────────────────────
    // Per-stone vein pass tuning. Each pass samples FBM at its own frequency /
    // seed offset and converts limestone tiles to the vein tile where the noise
    // drops below `Threshold × depthBias`. depthBias is 1.0 at `DepthCenter`
    // (0=surface, 1=bedrock), tapering linearly to 0 at ±DepthWidth.
    // Thresholds are tuned for "veins" rather than "layers": FBM noise mean is
    // ~0.5, so threshold ~0.4 at peak bias converts roughly 30-40% of tiles at
    // the depth center. Bump up for more abundant veins, down for rarer ones.
    public static float GraniteFreq        = 0.08f;
    public static float GraniteThreshold   = 0.48f;
    public static float GraniteDepthCenter = 0.4f;   // (0=surface, 1=bedrock)
    public static float GraniteDepthWidth  = 0.3f;
    public static int   GraniteSeedOffset  = 1111;

    public static float SlateFreq        = 0.09f;
    public static float SlateThreshold   = 0.5f;
    public static float SlateDepthCenter = 0.7f;
    public static float SlateDepthWidth  = 0.25f;
    public static int   SlateSeedOffset  = 2222;

    // ── Caves ────────────────────────────────────────────────────────
    public static float CaveFreqX = 0.06f;          // lower = wider caves horizontally
    public static float CaveFreqY = 0.14f;          // higher = thinner caves vertically
    public static int CaveOctaves = 2;              // FBM detail layers for cave noise (more = rougher walls)
    public static float CavePersistence = 0.5f;     // amplitude falloff per octave
    public static float CaveLacunarity = 2.0f;        // frequency multiplier per octave
    public static float CaveThresholdSurface = 0.28f; // near surface: fewer caves
    public static float CaveThresholdDeep = 0.38f;  // deep underground: more caves
    public static int CaveExclusionBelow = 4;       // no caves within this many tiles below surface
    public static int CACycles = 1;                 // cellular automata smoothing iterations (low = preserve FBM detail)
    public static int MinCaveSize = 8;              // flood-fill removes voids smaller than this

    // ── Worm carvers ──────────────────────────────────────────────────
    // Each world rolls a worm count in [Min, Max] and picks that many distinct
    // random start columns from the eligible set, with WormMinSeparation enforced
    // so worms don't clump. The walk itself reflects off the spawn-zone boundary
    // (see BlendWormTunnels) so even after random direction reversals the worm
    // stays on its starting side — the player's starting area never gets tunneled
    // into.
    public static int WormCountMin = 2;
    public static int WormCountMax = 3;
    public static int WormMinSeparation = 25;       // min x-distance between worm starts; relaxed if no candidate fits
    public static int WormSpawnBuffer = 2;          // worm walk reflects off (spawn ± this many tiles)
    public static int WormMinSteps = 100;
    public static int WormMaxSteps = 150;
    public static int WormRadius = 1;               // carve radius (1 = 3x3 area)
    public static int WormFalloff = 1;              // extra radius for soft falloff (must be wide enough to survive CA)
    public static int ChamberRadius = 1;            // wider carve every N steps
    public static int ChamberInterval = 10000;
    public static float WormStrength = 0.4f;        // how much worm pushes noise toward cave (higher = guaranteed tunnel)
    public static float WormTurnChance = 0.1f;      // chance per step to reverse horizontal direction

    // ── Water ────────────────────────────────────────────────────────
    // Water budget is split into `WaterChunkCount` equal-width horizontal
    // chunks. Each chunk independently rolls a budget in
    // [WaterChunkBudgetMin, WaterChunkBudgetMax] and allocates it to basins
    // whose center falls in that chunk, deepest-floor-first. This stops the
    // whole map's water from clustering on one side of the world — every third
    // gets its own pool(s), as long as the heightmap offers a basin there.
    //
    // Chunks with no eligible basins simply waste their roll. The
    // "every world has water" guarantee runs per-chunk too: if a chunk has
    // basins but none meet MinPoolVolume, the deepest is filled anyway.
    public static int WaterLine = 60;        // depressions only fill below this y (≈ BaseHeight)
    public static int WaterChunkCount = 3;          // horizontal slices the budget is split across
    public static int WaterChunkBudgetMin = 25;     // per-chunk water tile budget, rolled per world
    public static int WaterChunkBudgetMax = 60;
    public static int MinPoolVolume = 3;            // basins smaller than this are skipped (unless they're the only option in a chunk)

    // ── Main entry point ─────────────────────────────────────────────────

    // Generates the full terrain: surface heightmap, dirt/stone fill, caves.
    // seed controls all randomness for reproducibility.
    // Returns the surface height array (surfaceY per column) for use by the caller.
    public static int[] Generate(World world, int seed) {
        int nx = world.nx;
        int ny = world.ny;

        int[] surfaceY = GenerateSurfaceHeights(nx, seed);
        FillTerrain(world, surfaceY);
        ApplyVeins(world, surfaceY, seed);

        // Cave generation: build a continuous noise field, blend in worm tunnels,
        // then threshold + CA smooth into the final boolean mask.
        float[,] caveNoise = BuildCaveNoiseField(surfaceY, nx, ny, seed);
        BlendWormTunnels(caveNoise, surfaceY, nx, ny, seed);
        bool[,] isCave = ThresholdCaveField(caveNoise, surfaceY, nx, ny);
        RefineCavesCA(isCave, nx, ny);
        RemoveSmallCaves(isCave, nx, ny);
        ApplyCaves(world, isCave, surfaceY);
        // TEMP: outcropping cleanup disabled while tuning surface warp/amp
        // variation — restore when re-enabling.
        // RemoveSurfaceOutcroppings(world, surfaceY);

        FillDepressions(world, surfaceY, seed);

        ApplyBeachSand(world, seed);

        PopulateOverlays(world);

        SeedMoisture(world);

        return surfaceY;
    }

    // ── Tile overlays ────────────────────────────────────────────────────
    // Seed overlayMask bits on every overlay-bearing tile (today: dirt → grass)
    // so each cardinal edge that's currently exposed to non-solid, non-flooded air
    // is decorated. Mining never auto-sets bits (handled by Tile.type setter and
    // by the absence of any code path that *adds* bits at runtime), so freshly
    // exposed dirt edges look like bare dirt, not insta-grass.
    //
    // Runs after FillDepressions so we can see which surface tiles are flooded —
    // submerged dirt shouldn't sprout grass on the under-water side. Underground
    // dirt exposed to caves is intentionally treated like the surface and gets
    // grass on the cave-facing edges.
    public static void PopulateOverlays(World world) {
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile t = world.GetTileAt(x, y);
                if (t.type.overlay == null) continue;

                byte mask = 0;
                // Bit layout matches cMask: 0=L, 1=R, 2=D, 3=U.
                if (IsExposedAndDry(world, x - 1, y    )) mask |= 1;
                if (IsExposedAndDry(world, x + 1, y    )) mask |= 2;
                if (IsExposedAndDry(world, x,     y - 1)) mask |= 4;
                if (IsExposedAndDry(world, x,     y + 1)) mask |= 8;
                t.overlayMask = mask;
            }
        }
    }

    static bool IsExposedAndDry(World world, int x, int y) {
        Tile n = world.GetTileAt(x, y);
        if (n == null) return false;
        if (n.type.solid) return false;
        if (n.water > 0) return false;
        return true;
    }

    // ── Beach sand ───────────────────────────────────────────────────────
    // Convert a clumpy fraction of dirt tiles touching water (orthogonal OR
    // diagonal) into sand. A perlin mask shapes which eligible tiles flip so
    // we get patchy beaches and dunes instead of a uniform sand ring around
    // every pool. Frequency picks the clump size; threshold tunes coverage.
    //
    // Runs AFTER FillDepressions (so water placement is final) and BEFORE
    // PopulateOverlays (so grass bits are seeded on dirt's final boundary,
    // not on tiles we're about to make sand). The Tile.type setter clears
    // overlayMask on a type change anyway — this ordering is just the simpler
    // invariant to reason about.
    public static float SandFreq = 0.18f;       // ~6-tile clumps
    public static float SandThreshold = 0.52f;  // Mathf.PerlinNoise concentrates around 0.5; ~0.52 ≈ 40% pass
    public static void ApplyBeachSand(World world, int seed) {
        TileType sand = Db.tileTypeByName["sand"];
        TileType dirt = Db.tileTypeByName["dirt"];
        float seedOffX = seed * 0.7f;
        float seedOffY = seed * 1.3f;

        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile t = world.GetTileAt(x, y);
                if (t.type != dirt) continue;
                if (!HasAdjacentWater(world, x, y)) continue;
                float n = Mathf.PerlinNoise(
                    (x + seedOffX) * SandFreq,
                    (y + seedOffY) * SandFreq);
                if (n > SandThreshold) t.type = sand;
            }
        }
    }

    static bool HasAdjacentWater(World world, int x, int y) {
        for (int dx = -1; dx <= 1; dx++) {
            for (int dy = -1; dy <= 1; dy++) {
                if (dx == 0 && dy == 0) continue;
                Tile n = world.GetTileAt(x + dx, y + dy);
                if (n != null && n.water > 0) return true;
            }
        }
        return false;
    }

    // ── Background walls ─────────────────────────────────────────────────
    // Place a wall behind every tile below the natural surface heightmap, with
    // a near-surface skylight relaxation: a non-solid tile within 2 of the
    // surface stays open so shallow caves visibly punch through to sky.
    // Wall type is positional (top DirtDepth rows = Dirt, deeper = Stone) and
    // saved per-tile — never changes after mining. Since FillTerrain seeds the
    // top DirtDepth rows as dirt and stone-vein passes only convert limestone,
    // the positional decision matches what each tile was at fill time.
    public static void SetBackgrounds(World world, int[] surfaceY) {
        int nx = world.nx;
        int ny = world.ny;

        // Pass 1: place walls following the surface contour, with the near-surface
        // skylight relaxation for shallow caves.
        for (int x = 0; x < nx; x++) {
            int sy = surfaceY[x];
            int yMax = Mathf.Min(sy, ny);
            for (int y = 0; y < yMax; y++) {
                Tile t = world.GetTileAt(x, y);
                if (!t.type.solid && y >= sy - 2) continue;
                t.backgroundType = (y >= sy - DirtDepth)
                    ? BackgroundType.Dirt
                    : BackgroundType.Stone;
            }
        }

        // Pass 2: 1-tile erosion at every sky/cave boundary. A wall tile that
        // is cardinally adjacent to a no-wall tile is cleared. The wall
        // participates in lighting (NormalsCaptureBackground), so a wall sat
        // directly behind the topmost solid row visibly dims the surface;
        // pulling the wall back one tile keeps surface and cave-edge tiles
        // reading at full sky/ambient brightness. Snapshot first, then clear,
        // so the erosion is a single 1-tile peel rather than a cascade.
        var toClear = new List<Tile>();
        for (int x = 0; x < nx; x++) {
            for (int y = 0; y < ny; y++) {
                Tile t = world.GetTileAt(x, y);
                if (t.backgroundType == BackgroundType.None) continue;
                if (HasNoneCardinalNeighbor(world, x, y, nx, ny))
                    toClear.Add(t);
            }
        }
        foreach (Tile t in toClear) t.backgroundType = BackgroundType.None;
    }

    static bool HasNoneCardinalNeighbor(World world, int x, int y, int nx, int ny) {
        if (x > 0      && world.GetTileAt(x - 1, y).backgroundType == BackgroundType.None) return true;
        if (x < nx - 1 && world.GetTileAt(x + 1, y).backgroundType == BackgroundType.None) return true;
        if (y > 0      && world.GetTileAt(x, y - 1).backgroundType == BackgroundType.None) return true;
        if (y < ny - 1 && world.GetTileAt(x, y + 1).backgroundType == BackgroundType.None) return true;
        return false;
    }

    // Baseline soil dampness so virgin worlds support plant growth from turn 1,
    // and sheltered soil (caves, deep stone) has something for seep/plants to read.
    // Surface soil dries from here via MoistureSystem.HourlyUpdate; underground holds.
    public static byte StartingMoisture = 50;
    static void SeedMoisture(World world) {
        for (int x = 0; x < world.nx; x++)
            for (int y = 0; y < world.ny; y++) {
                Tile t = world.GetTileAt(x, y);
                if (t.type.solid) t.moisture = StartingMoisture;
            }
    }

    // ── Surface terrain ──────────────────────────────────────────────────

    // Computes per-column surface height using layered Perlin noise (FBM).
    // The starting zone is forced flat with smooth blending at the edges.
    static int[] GenerateSurfaceHeights(int nx, int seed) {
        int[] heights = new int[nx];

        for (int x = 0; x < nx; x++) {
            // Domain-warp the sample coordinate before feeding it to the surface
            // FBM. Spawn-zone blending and the integer column index are untouched —
            // only the noise lookup sees a shifted x.
            float warp = FBM1D(x, seed + SurfaceWarpSeedOffset, SurfaceWarpOctaves, SurfaceWarpFreq, SurfaceWarpAmp);
            // Slow region multiplier: FBM1D with amp=1 returns roughly [-1, 1];
            // remap to [0, 1] then lerp to the configured min/max amplitude scale.
            float ampSignal = FBM1D(x, seed + SurfaceAmpVarSeedOffset, 1, SurfaceAmpVarFreq, 1f);
            float ampMult = Mathf.Lerp(SurfaceAmpVarMin, SurfaceAmpVarMax, Mathf.Clamp01((ampSignal + 1f) * 0.5f));
            float noiseH = BaseHeight + FBM1D(x + warp, seed, SurfaceOctaves, SurfaceFreq, SurfaceAmp * ampMult);
            float flat = BaseHeight;

            // Blend toward flat in the spawn zone
            float blend = SpawnBlendFactor(x);
            float finalH = Mathf.Lerp(noiseH, flat, blend);
            heights[x] = Mathf.Clamp(Mathf.RoundToInt(finalH), SurfaceMin, SurfaceMax);
        }

        // TEMP: nub flattener disabled while tuning surface warp/amp variation
        // — it was hiding small-scale shape detail. Restore when re-enabling.
        // Remove single-column nubs: if a column is higher or lower than both
        // neighbors, snap it to match so the surface stays smooth.
        // for (int x = 1; x < nx - 1; x++) {
        //     int l = heights[x - 1], h = heights[x], r = heights[x + 1];
        //     if ((h > l && h > r) || (h < l && h < r))
        //         heights[x] = (l + r) / 2;
        // }

        return heights;
    }

    // Returns 1.0 inside the spawn zone, 0.0 outside, with smoothstep blend at edges.
    static float SpawnBlendFactor(int x) {
        if (x >= SpawnMinX && x <= SpawnMaxX) return 1f;

        float dist;
        if (x < SpawnMinX) dist = SpawnMinX - x;
        else dist = x - SpawnMaxX;

        if (dist > SpawnBlend) return 0f;
        float t = 1f - (dist / SpawnBlend);
        return t * t * (3f - 2f * t); // smoothstep
    }

    // Fills the grid with dirt and stone based on surface heights.
    // All underground solid is seeded as limestone; vein passes (ApplyVeins) then
    // convert bands of it into granite / slate to create mining variety.
    static void FillTerrain(World world, int[] surfaceY) {
        TileType dirt = Db.tileTypeByName["dirt"];
        TileType limestone = Db.tileTypeByName["limestone"];

        for (int x = 0; x < world.nx; x++) {
            int surface = surfaceY[x];
            for (int y = 0; y < surface; y++) {
                if (y >= surface - DirtDepth)
                    world.GetTileAt(x, y).type = dirt;
                else
                    world.GetTileAt(x, y).type = limestone;
            }
            // y >= surface stays empty (default TileType)
        }
    }

    // ── Stone vein generation ────────────────────────────────────────────
    // Runs after FillTerrain, before caves. Each pass samples its own FBM and
    // converts limestone tiles to the target stone where the noise drops below
    // the (depth-biased) threshold. Later passes can overwrite earlier ones
    // only if they also start from limestone — since granite/slate pass checks
    // the current tile against limestone, veins don't overlap (first pass wins
    // per tile). Ordering: granite first, then slate.
    static void ApplyVeins(World world, int[] surfaceY, int seed) {
        ApplyVeinPass(world, surfaceY, "granite", GraniteFreq, GraniteThreshold,
                      GraniteDepthCenter, GraniteDepthWidth, seed + GraniteSeedOffset);
        ApplyVeinPass(world, surfaceY, "slate",   SlateFreq,   SlateThreshold,
                      SlateDepthCenter,   SlateDepthWidth,   seed + SlateSeedOffset);
    }

    static void ApplyVeinPass(World world, int[] surfaceY, string tileName,
                              float freq, float threshold,
                              float depthCenter, float depthWidth, int seedOffset) {
        if (!Db.tileTypeByName.TryGetValue(tileName, out TileType veinTile)) {
            Debug.LogError($"ApplyVeinPass: tileType '{tileName}' not found in tilesDb");
            return;
        }
        TileType limestone = Db.tileTypeByName["limestone"];
        float seedOffX = seedOffset * 0.7f;
        float seedOffY = seedOffset * 1.3f;
        for (int x = 0; x < world.nx; x++) {
            int surface = surfaceY[x];
            // depthSpan protects against divide-by-zero on degenerate columns.
            int depthSpan = Math.Max(1, surface - BedrockY);
            for (int y = BedrockY + 1; y < surface; y++) {
                Tile t = world.GetTileAt(x, y);
                // Only convert base limestone — leaves dirt, already-carved veins, and empties alone.
                if (t.type != limestone) continue;
                float depth = 1f - (float)(y - BedrockY) / depthSpan;
                float bias = Mathf.Clamp01(1f - Mathf.Abs(depth - depthCenter) / depthWidth);
                float noise = FBM2D((x + seedOffX) * freq, (y + seedOffY) * freq, 2, 0.5f, 2f);
                // Lower noise + stronger depth bias → vein tile. When bias=0 (far from center)
                // the threshold goes to 0 and no tiles convert, naturally gating by depth.
                if (noise < threshold * bias) t.type = veinTile;
            }
        }
    }

    // ── Cave generation — Pass A: continuous noise field ───────────────

    // Builds a continuous noise field for the underground. Values closer to 0
    // are more "cave-like". The field is kept as floats so worm tunnels can
    // blend in before the hard threshold step.
    // Spawn zone and near-surface tiles get a high value (1 = definitely solid).
    static float[,] BuildCaveNoiseField(int[] surfaceY, int nx, int ny, int seed) {
        float[,] field = new float[nx, ny];
        float seedOffX = seed * 0.7f;
        float seedOffY = seed * 1.3f;

        for (int x = 0; x < nx; x++) {
            int surface = surfaceY[x];
            int caveTop = surface - CaveExclusionBelow;
            bool inSpawn = x >= SpawnMinX - 2 && x <= SpawnMaxX + 2;

            for (int y = 0; y < ny; y++) {
                if (inSpawn || y <= BedrockY || y >= surface) {
                    field[x, y] = 1f;
                    continue;
                }
                // Exclusion zone: high enough to block Perlin caves,
                // low enough that worms can push through.
                if (y >= caveTop) {
                    field[x, y] = 0.4f;
                    continue;
                }

                field[x, y] = FBM2D(
                    (x + seedOffX) * CaveFreqX,
                    (y + seedOffY) * CaveFreqY,
                    CaveOctaves, CavePersistence, CaveLacunarity
                );
            }
        }
        return field;
    }

    // Thresholds the continuous noise field into a boolean cave mask.
    // Threshold varies with depth (more caves deeper underground).
    // The exclusion zone (near surface) has noise = 1.0 from BuildCaveNoiseField,
    // so Perlin caves can't form there — but worm soft-carving can still push
    // values low enough to break through, creating natural tunnel entrances.
    static bool[,] ThresholdCaveField(float[,] field, int[] surfaceY, int nx, int ny) {
        bool[,] isCave = new bool[nx, ny];
        for (int x = 0; x < nx; x++) {
            int caveTop = surfaceY[x] - CaveExclusionBelow;
            for (int y = BedrockY + 1; y < surfaceY[x]; y++) {
                float depthRatio = 1f - (float)(y - BedrockY) / (caveTop - BedrockY);
                float threshold = Mathf.Lerp(CaveThresholdSurface, CaveThresholdDeep, depthRatio);
                isCave[x, y] = field[x, y] < threshold;
            }
        }
        return isCave;
    }

    // ── Cave generation — Pass B: Cellular automata ──────────────────────

    // Smooths cave shapes using cellular automata rules.
    // A tile becomes solid (not cave) if >4 of its 8 neighbors are solid.
    // This transforms blobby Perlin output into craggy, natural-looking chambers.
    static void RefineCavesCA(bool[,] isCave, int nx, int ny) {
        bool[,] buffer = new bool[nx, ny];

        for (int iter = 0; iter < CACycles; iter++) {
            for (int x = 0; x < nx; x++) {
                for (int y = 0; y < ny; y++) {
                    int solidNeighbors = CountSolidNeighbors(isCave, x, y, nx, ny);
                    // If >4 neighbors are solid (not cave), this tile becomes solid too.
                    // Edge tiles (border of the grid) count as solid for boundary stability.
                    buffer[x, y] = solidNeighbors <= 4;
                }
            }

            // Swap
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    isCave[x, y] = buffer[x, y];
        }

    }

    static int CountSolidNeighbors(bool[,] isCave, int cx, int cy, int nx, int ny) {
        int solid = 0;
        for (int dx = -1; dx <= 1; dx++) {
            for (int dy = -1; dy <= 1; dy++) {
                if (dx == 0 && dy == 0) continue;
                int x = cx + dx, y = cy + dy;
                // Out-of-bounds counts as solid (keeps cave edges stable)
                if (x < 0 || x >= nx || y < 0 || y >= ny) { solid++; continue; }
                if (!isCave[x, y]) solid++;
            }
        }
        return solid;
    }

    // ── Cave generation — cleanup: remove tiny caves ─────────────────────

    // Flood-fill removes disconnected cave pockets smaller than MinCaveSize.
    static void RemoveSmallCaves(bool[,] isCave, int nx, int ny) {
        bool[,] visited = new bool[nx, ny];
        List<(int x, int y)> region = new();
        Queue<(int x, int y)> queue = new();

        for (int x = 0; x < nx; x++) {
            for (int y = 0; y < ny; y++) {
                if (!isCave[x, y] || visited[x, y]) continue;

                // BFS flood-fill this cave region
                region.Clear();
                queue.Clear();
                queue.Enqueue((x, y));
                visited[x, y] = true;

                while (queue.Count > 0) {
                    var (cx, cy) = queue.Dequeue();
                    region.Add((cx, cy));

                    for (int dx = -1; dx <= 1; dx++) {
                        for (int dy = -1; dy <= 1; dy++) {
                            if (dx == 0 && dy == 0) continue;
                            if (Math.Abs(dx) + Math.Abs(dy) > 1) continue; // 4-connected
                            int nx2 = cx + dx, ny2 = cy + dy;
                            if (nx2 < 0 || nx2 >= nx || ny2 < 0 || ny2 >= ny) continue;
                            if (!isCave[nx2, ny2] || visited[nx2, ny2]) continue;
                            visited[nx2, ny2] = true;
                            queue.Enqueue((nx2, ny2));
                        }
                    }
                }

                // Remove if too small
                if (region.Count < MinCaveSize) {
                    foreach (var (rx, ry) in region)
                        isCave[rx, ry] = false;
                }
            }
        }
    }

    // ── Cave generation — Worm tunnel blending ────────────────────────

    // Walks worm paths and lowers the noise field along them with soft falloff,
    // so tunnels merge naturally with surrounding Perlin caves.
    static void BlendWormTunnels(float[,] caveNoise, int[] surfaceY, int nx, int ny, int seed) {
        System.Random rng = new(seed + 777);

        // Eligible start columns: away from world edges and outside the spawn-zone
        // buffer. The walk below also reflects off the spawn boundary, so a worm
        // that wanders toward spawn turns back rather than tunneling in.
        List<int> candidates = new();
        int leftBound = SpawnMinX - WormSpawnBuffer;   // left walks must stay strictly below this
        int rightBound = SpawnMaxX + WormSpawnBuffer;  // right walks must stay strictly above this
        for (int x = 5; x < nx - 5; x++) {
            if (x >= leftBound && x <= rightBound) continue;
            candidates.Add(x);
        }

        // Pick worm count, then random distinct start columns with WormMinSeparation
        // between them. If the separation can't be met within a few tries (narrow
        // map, dense placement), accept whatever's left so the count target still hits.
        int wormCount = rng.Next(WormCountMin, WormCountMax + 1);
        List<int> starts = new();
        for (int w = 0; w < wormCount && candidates.Count > 0; w++) {
            int startX = -1;
            for (int attempt = 0; attempt < 12; attempt++) {
                int pick = candidates[rng.Next(candidates.Count)];
                bool farEnough = true;
                foreach (int s in starts) if (Math.Abs(s - pick) < WormMinSeparation) { farEnough = false; break; }
                if (farEnough) { startX = pick; break; }
            }
            if (startX < 0) startX = candidates[rng.Next(candidates.Count)]; // relaxed fallback
            starts.Add(startX);
            candidates.Remove(startX);
        }

        // Walk each worm. Initial xDir is forced outward if the start sits right at
        // the spawn buffer so the first step doesn't immediately bounce.
        foreach (int startX in starts) {
            int startY = surfaceY[startX] - 1; // start at surface, carves down through exclusion zone
            bool startsLeft = startX < SpawnMinX;
            int steps = rng.Next(WormMinSteps, WormMaxSteps + 1);
            int cx = startX, cy = startY;
            int xDir;
            if (startsLeft && cx >= leftBound - 1) xDir = -1;
            else if (!startsLeft && cx <= rightBound + 1) xDir = 1;
            else xDir = rng.NextDouble() < 0.5 ? -1 : 1;

            for (int s = 0; s < steps; s++) {
                int r = (s % ChamberInterval == 0 && s > 0) ? ChamberRadius : WormRadius;
                SoftCarve(caveNoise, cx, cy, r + WormFalloff, nx, ny);

                // Small chance to reverse horizontal direction each step
                if (rng.NextDouble() < WormTurnChance) xDir = -xDir;

                // Move: down or sideways in current direction
                if (rng.NextDouble() < 0.3f) cy--;
                else cx += xDir;

                // Spawn-zone reflection: if the step pushed us into the buffer,
                // step back out and flip direction. Keeps the worm strictly on
                // its starting side for the whole walk.
                if (startsLeft && cx >= leftBound) { cx = leftBound - 1; xDir = -1; }
                else if (!startsLeft && cx <= rightBound) { cx = rightBound + 1; xDir = 1; }

                cx = Math.Clamp(cx, 1, nx - 2);
                cy = Math.Clamp(cy, BedrockY + 1, ny - 2);
            }
        }
    }

    // Lowers noise values in a soft circle — full push at center, fading to zero at edge.
    // This makes the worm path "attract" cave space from the Perlin field.
    static void SoftCarve(float[,] field, int cx, int cy, int radius, int nx, int ny) {
        for (int dx = -radius; dx <= radius; dx++) {
            for (int dy = -radius; dy <= radius; dy++) {
                int x = cx + dx, y = cy + dy;
                if (x < 1 || x >= nx - 1 || y < 1 || y >= ny - 1) continue;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;
                // Smoothstep falloff: 1 at center, 0 at edge
                float t = 1f - dist / radius;
                float strength = t * t * (3f - 2f * t);
                field[x, y] -= strength * WormStrength;
            }
        }
    }

    // ── Apply cave mask to tile grid ─────────────────────────────────────

    // Sets cave tiles to empty. Only affects tiles that were solid (underground).
    static void ApplyCaves(World world, bool[,] isCave, int[] surfaceY) {
        TileType empty = Db.tileTypeByName["empty"];

        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < surfaceY[x]; y++) {
                if (isCave[x, y]) {
                    world.GetTileAt(x, y).type = empty;
                }
            }
        }
    }

    // ── Post-cave cleanup: remove hanging outcroppings near surface ────────

    // Removes 1-wide solid nubs left where worm tunnels exit to the surface.
    // Operates on actual world tiles (after ApplyCaves) so it sees the full
    // picture: surface air + cave openings + worm exits.
    // Scoped to near-surface to avoid eating deep cave walls.
    static void RemoveSurfaceOutcroppings(World world, int[] surfaceY) {
        TileType empty = Db.tileTypeByName["empty"];
        int nx = world.nx;

        // Check a band around the surface where worms exit
        bool changed = true;
        while (changed) {
            changed = false;
            for (int x = 1; x < nx - 1; x++) {
                int yMin = Math.Max(0, surfaceY[x] - CaveExclusionBelow - 2);
                int yMax = Math.Min(world.ny - 1, surfaceY[x] + 2);

                for (int y = yMin; y <= yMax; y++) {
                    Tile t = world.GetTileAt(x, y);
                    if (!t.type.solid) continue;

                    // Empty on both horizontal sides → hanging nub
                    bool emptyLeft = !world.GetTileAt(x - 1, y).type.solid;
                    bool emptyRight = !world.GetTileAt(x + 1, y).type.solid;
                    // Empty on both vertical sides → floating pixel
                    bool emptyBelow = y > 0 && !world.GetTileAt(x, y - 1).type.solid;
                    bool emptyAbove = y < world.ny - 1 && !world.GetTileAt(x, y + 1).type.solid;

                    if ((emptyLeft && emptyRight) || (emptyBelow && emptyAbove)) {
                        t.type = empty;
                        changed = true;
                    }
                }
            }
        }
    }

    // ── Surface water — depression filling ────────────────────────────────

    // Splits the world into `WaterChunkCount` equal-width horizontal chunks
    // and allocates a seed-rolled budget [WaterChunkBudgetMin, …Max] to each.
    // Inside a chunk, basins are sorted by floor elevation ascending and
    // filled deepest-first; the last filled basin partial-fills via the
    // binary-searched uniform-level helper if the chunk budget would overflow.
    //
    // Chunked allocation keeps water reasonably even across the map width
    // (a flat west side and lumpy east side used to dump all the water in the
    // east). Per-chunk randomized budgets keep total surface water varying
    // within a controlled band rather than the wide swing the old top-N-by-
    // volume scheme produced.
    //
    // Guarantee (per chunk): if a chunk has any non-draining basin, at least
    // one is filled even if all are below MinPoolVolume. Chunks with no
    // eligible basins waste their roll.
    static void FillDepressions(World world, int[] surfaceY, int seed) {
        int nx = world.nx;
        ushort waterMax = WaterController.WaterMax;
        System.Random rng = new(seed + 555);

        // Use the EFFECTIVE surface (top of actual solid terrain, post-carve) rather
        // than the original heightmap surfaceY — otherwise worm tunnels and cave
        // exits that break through to the surface create visible pits that this
        // algorithm can't see (since surfaceY still claims the column is solid up
        // to the original height).
        int[] effSurfaceY = ComputeEffectiveSurface(world, surfaceY);

        // Compute water level per column: min(maxLeft, maxRight)
        int[] waterLevel = new int[nx];
        int[] maxLeft = new int[nx];
        int[] maxRight = new int[nx];

        maxLeft[0] = effSurfaceY[0];
        for (int x = 1; x < nx; x++)
            maxLeft[x] = Math.Max(maxLeft[x - 1], effSurfaceY[x]);

        maxRight[nx - 1] = effSurfaceY[nx - 1];
        for (int x = nx - 2; x >= 0; x--)
            maxRight[x] = Math.Max(maxRight[x + 1], effSurfaceY[x]);

        for (int x = 0; x < nx; x++)
            waterLevel[x] = Math.Min(maxLeft[x], maxRight[x]);

        // Pass 1: collect all eligible basins (contiguous runs where
        // waterLevel > effSurfaceY, not draining into caves). MinPoolVolume
        // gating happens in Pass 2 so the guarantee can bypass it.
        List<(int x0, int x1, int volume, int waterLevel, int floor)> basins = new();
        int cursor = 0;
        while (cursor < nx) {
            if (waterLevel[cursor] <= effSurfaceY[cursor]) { cursor++; continue; }

            int x0 = cursor, x1 = cursor;
            int volume = 0;
            int floor = int.MaxValue;
            while (x1 < nx && waterLevel[x1] > effSurfaceY[x1]) {
                int cappedLevel = Math.Min(waterLevel[x1], WaterLine);
                volume += Math.Max(0, cappedLevel - effSurfaceY[x1]);
                if (effSurfaceY[x1] < floor) floor = effSurfaceY[x1];
                x1++;
            }

            // Skip basins that would drain into caves — any column with an
            // empty tile just below the actual (effective) floor lets water out.
            bool drains = false;
            for (int x = x0; x < x1 && !drains; x++) {
                for (int y = effSurfaceY[x] - 1; y >= Math.Max(0, effSurfaceY[x] - 3); y--) {
                    if (!world.GetTileAt(x, y).type.solid) { drains = true; break; }
                }
            }

            if (!drains && volume >= 1)
                basins.Add((x0, x1, volume, Math.Min(waterLevel[x0], WaterLine), floor));

            cursor = x1;
        }

        if (basins.Count == 0) {
            // Fallback: the WaterLine cap zeros out volume for basins whose floor
            // sits above it, so on maps with mostly-high terrain the primary scan
            // finds nothing. Re-collect using the natural water level (uncapped).
            // Drain check is preserved — we still don't want water falling into caves.
            basins = CollectBasinsUncapped(world, effSurfaceY, waterLevel, nx);
            if (basins.Count == 0) {
                Debug.LogWarning("FillDepressions: no eligible basins on this map even with fallback — no surface water will spawn.");
                return;
            }
        }

        // Pass 2: bucket basins into chunks by center column, then allocate
        // each chunk its own rolled budget. Inside a chunk, fill deepest-floor
        // first; tiny basins are skipped unless none qualify (per-chunk
        // fallback to the deepest available so the chunk doesn't go fully dry
        // when terrain offers nothing of MinPoolVolume).
        int chunkWidth = Math.Max(1, nx / WaterChunkCount);
        var byChunk = new List<(int x0, int x1, int volume, int waterLevel, int floor)>[WaterChunkCount];
        for (int i = 0; i < WaterChunkCount; i++) byChunk[i] = new();
        foreach (var b in basins) {
            int center = (b.x0 + b.x1 - 1) / 2;
            int c = Math.Min(WaterChunkCount - 1, center / chunkWidth);
            byChunk[c].Add(b);
        }

        for (int c = 0; c < WaterChunkCount; c++) {
            var chunkBasins = byChunk[c];
            if (chunkBasins.Count == 0) continue;
            chunkBasins.Sort((a, b) => a.floor.CompareTo(b.floor));
            int budget = rng.Next(WaterChunkBudgetMin, WaterChunkBudgetMax + 1);

            bool anyFilled = false;
            foreach (var b in chunkBasins) {
                if (budget <= 0) break;
                if (b.volume < MinPoolVolume) continue;
                budget -= FillBasinUpTo(world, effSurfaceY, b, Math.Min(b.volume, budget), waterMax);
                anyFilled = true;
            }
            if (!anyFilled && budget > 0) {
                var b = chunkBasins[0];
                FillBasinUpTo(world, effSurfaceY, b, Math.Min(b.volume, budget), waterMax);
            }
        }
    }

    // Fills basin `b` with up to `cap` tiles of water at the highest uniform
    // water level achievable. Returns the actual tile count placed. When `cap`
    // is below the basin's natural capacity, binary-searches for the highest
    // uniform level whose total volume ≤ cap — so partial fills stay flat
    // rather than pooling against one side of the basin.
    static int FillBasinUpTo(World world, int[] effSurfaceY,
                             (int x0, int x1, int volume, int waterLevel, int floor) b,
                             int cap, ushort waterMax) {
        int fillLevel = b.waterLevel;
        if (b.volume > cap) {
            int lo = b.floor, hi = fillLevel;
            while (lo < hi) {
                int mid = (lo + hi + 1) / 2;
                int vol = 0;
                for (int x = b.x0; x < b.x1; x++)
                    vol += Math.Max(0, mid - effSurfaceY[x]);
                if (vol <= cap) lo = mid;
                else hi = mid - 1;
            }
            fillLevel = lo;
        }

        int placed = 0;
        for (int x = b.x0; x < b.x1; x++) {
            for (int y = effSurfaceY[x]; y < fillLevel; y++) {
                Tile t = world.GetTileAt(x, y);
                if (!t.type.solid) {
                    t.water = waterMax;
                    placed++;
                }
            }
        }
        return placed;
    }

    // Actual topmost-solid-tile y per column, scanning down from the sky. Differs
    // from the heightmap surfaceY whenever caves/worms carve away the original top,
    // creating pits or cliffs FillDepressions would otherwise miss. Returned values
    // are the y-coordinate of the first EMPTY tile above the highest remaining solid
    // (matching surfaceY semantics: the "surface" is the air-tile just above ground).
    static int[] ComputeEffectiveSurface(World world, int[] surfaceY) {
        int nx = world.nx, ny = world.ny;
        int[] eff = new int[nx];
        for (int x = 0; x < nx; x++) {
            // Start one above the original heightmap top — if anything got carved,
            // we'll walk down past the empties until we find solid ground.
            int y = Math.Min(ny - 1, surfaceY[x]);
            while (y > 0 && !world.GetTileAt(x, y - 1).type.solid) y--;
            eff[x] = y;
        }
        return eff;
    }

    // Fallback basin collector: same geometry as the main scan in FillDepressions,
    // but the volume uses the natural waterLevel (no WaterLine cap) so high-altitude
    // basins count too. Drain-into-cave check is preserved — water on cave ceilings
    // would fall through. Used only when the primary below-WaterLine scan is empty.
    // Caller passes the effective (post-carve) surface so carved pits are visible.
    static List<(int x0, int x1, int volume, int waterLevel, int floor)> CollectBasinsUncapped(
        World world, int[] effSurfaceY, int[] waterLevel, int nx) {
        var basins = new List<(int x0, int x1, int volume, int waterLevel, int floor)>();
        int cursor = 0;
        while (cursor < nx) {
            if (waterLevel[cursor] <= effSurfaceY[cursor]) { cursor++; continue; }

            int x0 = cursor, x1 = cursor;
            int volume = 0;
            int floor = int.MaxValue;
            while (x1 < nx && waterLevel[x1] > effSurfaceY[x1]) {
                volume += waterLevel[x1] - effSurfaceY[x1]; // no WaterLine cap
                if (effSurfaceY[x1] < floor) floor = effSurfaceY[x1];
                x1++;
            }

            bool drains = false;
            for (int x = x0; x < x1 && !drains; x++) {
                for (int y = effSurfaceY[x] - 1; y >= Math.Max(0, effSurfaceY[x] - 3); y--) {
                    if (!world.GetTileAt(x, y).type.solid) { drains = true; break; }
                }
            }

            if (!drains && volume >= 1)
                basins.Add((x0, x1, volume, waterLevel[x0], floor));

            cursor = x1;
        }
        return basins;
    }

    // ── Plant scattering ──────────────────────────────────────────────────

    // Scatters random plant clusters across the surface. Each eligible column
    // has a small chance to seed a cluster of 1-3 plants (pine, apple, ramie).
    // Called from WorldController.GenerateDefault after terrain + caves + water.
    public static float PlantChance = 0.08f;   // per-column chance to start a cluster
    public static int ClusterMin = 1;
    public static int ClusterMax = 3;

    public static void ScatterPlants(World world, int[] surfaceY, int seed) {
        System.Random rng = new(seed + 999);
        TileType dirt = Db.tileTypeByName["dirt"];
        int nx = world.nx;

        // Track which columns already have a plant to avoid overlap
        bool[] occupied = new bool[nx];

        for (int x = 0; x < nx; x++) {
            if (occupied[x]) continue;
            // Skip spawn zone — leave room for the player
            if (x >= SpawnMinX - 2 && x <= SpawnMaxX + 2) continue;
            // Must have solid dirt at surface and no water above
            if (!IsPlantEligible(world, surfaceY, x, dirt)) continue;

            if (rng.NextDouble() >= PlantChance) continue;

            // Pick plant type: 50% pine, 25% apple, 25% ramie
            string plantName = PickPlantType(rng);
            int clusterSize = rng.Next(ClusterMin, ClusterMax + 1);

            for (int i = 0; i < clusterSize; i++) {
                // Place cluster members nearby (within +-2 columns)
                int px = x + i;
                if (px < 0 || px >= nx) continue;
                if (occupied[px]) continue;
                if (!IsPlantEligible(world, surfaceY, px, dirt)) continue;

                Plant p = new Plant(Db.plantTypeByName[plantName], px, surfaceY[px]);
                p.Mature();
                StructController.instance.Place(p);
                occupied[px] = true;
            }
        }
    }

    static bool IsPlantEligible(World world, int[] surfaceY, int x, TileType dirt) {
        int sy = surfaceY[x];
        if (sy <= 0 || sy >= world.ny) return false;
        // Surface tile must be dirt
        if (world.GetTileAt(x, sy - 1).type != dirt) return false;
        // No water at the surface
        if (world.GetTileAt(x, sy).water > 0) return false;
        // Space must be unoccupied (market, starter plants, etc. share depth 0)
        if (world.GetTileAt(x, sy).structs[0] != null) return false;
        return true;
    }

    static string PickPlantType(System.Random rng) {
        double roll = rng.NextDouble();
        if (roll < 0.40) return "pinetree";   // pine + pinecone (replaces legacy "tree" — old saves still load their existing "tree" plants by typeName)
        if (roll < 0.60) return "appletree";
        if (roll < 0.80) return "ramie";
        return "bamboo";
    }

    // ── Noise utilities ──────────────────────────────────────────────────

    // Fractal Brownian Motion (2D): layered Perlin noise, normalized to ~[0,1]
    // so the existing cave thresholds (CaveThresholdSurface/Deep) stay meaningful.
    // Each octave contributes amp * PerlinNoise; we divide by the total amp to renormalize.
    static float FBM2D(float x, float y, int octaves, float persistence, float lacunarity) {
        float value = 0f;
        float amp = 1f;
        float freq = 1f;
        float ampSum = 0f;
        for (int i = 0; i < octaves; i++) {
            value += Mathf.PerlinNoise(x * freq, y * freq) * amp;
            ampSum += amp;
            amp *= persistence;
            freq *= lacunarity;
        }
        return value / ampSum;
    }

    // Fractal Brownian Motion (1D): layered Perlin noise for natural terrain.
    // Each octave doubles frequency and halves amplitude.
    static float FBM1D(float x, int seed, int octaves, float frequency, float amplitude) {
        float value = 0f;
        float freq = frequency;
        float amp = amplitude;
        float seedOffset = seed * 0.31415f; // offset based on seed

        for (int i = 0; i < octaves; i++) {
            // PerlinNoise needs 2D input; fix y to a seed-derived constant
            value += (Mathf.PerlinNoise((x + seedOffset) * freq, seedOffset * 0.73f) - 0.5f) * 2f * amp;
            freq *= 2f;
            amp *= 0.5f;
        }
        return value;
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

// Pure C# world generation — no MonoBehaviour, all static methods. Called from
// WorldController.GenerateDefault() to fill the tile grid with terrain, caves,
// and natural features before graph.Initialize().
//
// All tunable parameters live on a WorldGenConfig ScriptableObject loaded
// lazily from Resources/WorldGenConfig.asset. Access via WorldGen.config.X.
// Inspector sliders + tooltips live on the asset itself — right-click in
// Project view → Create → Shonei → WorldGen Config to make one.
public static class WorldGen {

    public const string ConfigResourcePath = "WorldGenConfig";
    static WorldGenConfig _config;
    public static WorldGenConfig config {
        get {
            if (_config == null) {
                _config = Resources.Load<WorldGenConfig>(ConfigResourcePath);
                if (_config == null) {
                    Debug.LogError($"WorldGen: config asset not found at Resources/{ConfigResourcePath}.asset — falling back to runtime defaults. Create one via Assets → Create → Shonei → WorldGen Config and place it in Assets/Resources/.");
                    _config = ScriptableObject.CreateInstance<WorldGenConfig>();
                }
            }
            return _config;
        }
    }

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

        // Backgrounds run inside Generate (not just after, in WorldController)
        // so RemoveFloatingChunks below can read post-erosion backgroundType
        // for its connectivity graph. SetBackgrounds is pure on surfaceY +
        // current tile solidity, so the call site doesn't matter for its own
        // correctness — moved here purely to feed the chunk-removal pass.
        SetBackgrounds(world, surfaceY);

        // Remove solid clusters that aren't connected (orthogonally) to the
        // mainland via either solid tiles OR background walls. Catches the
        // floating-spire artefact that worm tunnels + sharp surfaceY steps
        // produce when SetBackgrounds' Pass-2 erosion strips a chunk's wall.
        RemoveFloatingChunks(world);

        // Cave water runs BEFORE FillDepressions so the cave-region flood-fill
        // sees the freshly-carved mask. Surface depression filling then proceeds
        // independently on the top-of-column geometry.
        FillCaveWater(world, isCave, nx, ny, seed);

        bool[] chunkFilled = FillDepressions(world, surfaceY, seed);
        CarveDryChunkPools(world, surfaceY, chunkFilled);

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
    public static void ApplyBeachSand(World world, int seed) {
        TileType sand = Db.tileTypeByName["sand"];
        TileType dirt = Db.tileTypeByName["dirt"];
        float seedOffX = seed * 0.7f;
        float seedOffY = seed * 1.3f;
        float sandFreq = config.SandFreq;
        float sandThreshold = config.SandThreshold;

        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile t = world.GetTileAt(x, y);
                if (t.type != dirt) continue;
                if (!HasAdjacentWater(world, x, y)) continue;
                float n = Mathf.PerlinNoise(
                    (x + seedOffX) * sandFreq,
                    (y + seedOffY) * sandFreq);
                if (n > sandThreshold) t.type = sand;
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
        int dirtDepth = config.DirtDepth;

        // Pass 1: place walls following the surface contour, with the near-surface
        // skylight relaxation for shallow caves.
        for (int x = 0; x < nx; x++) {
            int sy = surfaceY[x];
            int yMax = Mathf.Min(sy, ny);
            for (int y = 0; y < yMax; y++) {
                Tile t = world.GetTileAt(x, y);
                if (!t.type.solid && y >= sy - 2) continue;
                t.backgroundType = (y >= sy - dirtDepth)
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
    static void SeedMoisture(World world) {
        byte startingMoisture = config.StartingMoisture;
        for (int x = 0; x < world.nx; x++)
            for (int y = 0; y < world.ny; y++) {
                Tile t = world.GetTileAt(x, y);
                if (t.type.solid) t.moisture = startingMoisture;
            }
    }

    // ── Surface terrain ──────────────────────────────────────────────────

    // Computes per-column surface height using layered Perlin noise (FBM) with
    // domain warping and slow region-level amplitude variation.
    //
    // Domain warp: before sampling the surface FBM at column x, shift x by a
    // second noise. Vanilla FBM looks "wavy" because the noise is statistically
    // uniform along x — every wavelength looks like every other. Warping the
    // input coordinate breaks that regularity, producing twisted, organic-
    // looking surface shapes (peaks lean, valleys curl, features compress and
    // stretch unevenly). WarpFreq must be on the same order as SurfaceFreq or
    // the warp just uniformly slides whole regions of noise around.
    //
    // Region amp variation: a very slow noise signal multiplies the surface
    // amplitude per column, so some stretches read as plains (small bumps) and
    // others as hills/mountains (taller features). Decorrelated from the warp
    // signal via a separate seed offset. The starting zone is forced flat with
    // smooth blending at the edges.
    static int[] GenerateSurfaceHeights(int nx, int seed) {
        var cfg = config;
        int[] heights = new int[nx];

        for (int x = 0; x < nx; x++) {
            // Domain-warp the sample coordinate before feeding it to the surface
            // FBM. Spawn-zone blending and the integer column index are untouched —
            // only the noise lookup sees a shifted x.
            float warp = FBM1D(x, seed + cfg.SurfaceWarpSeedOffset, cfg.SurfaceWarpOctaves, cfg.SurfaceWarpFreq, cfg.SurfaceWarpAmp);
            // Slow region multiplier: FBM1D with amp=1 returns roughly [-1, 1];
            // remap to [0, 1] then lerp to the configured min/max amplitude scale.
            float ampSignal = FBM1D(x, seed + cfg.SurfaceAmpVarSeedOffset, 1, cfg.SurfaceAmpVarFreq, 1f);
            float ampMult = Mathf.Lerp(cfg.SurfaceAmpVarMin, cfg.SurfaceAmpVarMax, Mathf.Clamp01((ampSignal + 1f) * 0.5f));
            float noiseH = cfg.BaseHeight + FBM1D(x + warp, seed, cfg.SurfaceOctaves, cfg.SurfaceFreq, cfg.SurfaceAmp * ampMult);
            float flat = cfg.BaseHeight;

            // Blend toward flat in the spawn zone
            float blend = SpawnBlendFactor(x);
            float finalH = Mathf.Lerp(noiseH, flat, blend);
            heights[x] = Mathf.Clamp(Mathf.RoundToInt(finalH), cfg.SurfaceMin, cfg.SurfaceMax);
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
        var cfg = config;
        if (x >= cfg.SpawnMinX && x <= cfg.SpawnMaxX) return 1f;

        float dist;
        if (x < cfg.SpawnMinX) dist = cfg.SpawnMinX - x;
        else dist = x - cfg.SpawnMaxX;

        if (dist > cfg.SpawnBlend) return 0f;
        float t = 1f - (dist / cfg.SpawnBlend);
        return t * t * (3f - 2f * t); // smoothstep
    }

    // Fills the grid with dirt and stone based on surface heights.
    // All underground solid is seeded as limestone; vein passes (ApplyVeins) then
    // convert bands of it into granite / slate to create mining variety.
    static void FillTerrain(World world, int[] surfaceY) {
        TileType dirt = Db.tileTypeByName["dirt"];
        TileType limestone = Db.tileTypeByName["limestone"];
        int dirtDepth = config.DirtDepth;

        for (int x = 0; x < world.nx; x++) {
            int surface = surfaceY[x];
            for (int y = 0; y < surface; y++) {
                if (y >= surface - dirtDepth)
                    world.GetTileAt(x, y).type = dirt;
                else
                    world.GetTileAt(x, y).type = limestone;
            }
            // y >= surface stays empty (default TileType)
        }
    }

    // ── Stone vein generation ────────────────────────────────────────────
    // Per-stone vein pass: samples its own FBM and converts limestone tiles to
    // the target stone where the noise drops below `Threshold × depthBias`.
    // depthBias is 1.0 at DepthCenter (0=surface, 1=bedrock), tapering linearly
    // to 0 at ±DepthWidth. Thresholds are tuned for "veins" rather than
    // "layers": FBM noise mean is ~0.5, so threshold ~0.4 at peak bias converts
    // roughly 30-40% of tiles at the depth center. Bump up for more abundant
    // veins, down for rarer ones.
    //
    // Later passes can overwrite earlier ones only if they also start from
    // limestone — since granite/slate pass checks the current tile against
    // limestone, veins don't overlap (first pass wins per tile). Ordering:
    // granite first, then slate.
    static void ApplyVeins(World world, int[] surfaceY, int seed) {
        var cfg = config;
        ApplyVeinPass(world, surfaceY, "granite", cfg.GraniteFreq, cfg.GraniteThreshold,
                      cfg.GraniteDepthCenter, cfg.GraniteDepthWidth, seed + cfg.GraniteSeedOffset);
        ApplyVeinPass(world, surfaceY, "slate",   cfg.SlateFreq,   cfg.SlateThreshold,
                      cfg.SlateDepthCenter,   cfg.SlateDepthWidth,   seed + cfg.SlateSeedOffset);
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
        int bedrockY = config.BedrockY;
        for (int x = 0; x < world.nx; x++) {
            int surface = surfaceY[x];
            // depthSpan protects against divide-by-zero on degenerate columns.
            int depthSpan = Math.Max(1, surface - bedrockY);
            for (int y = bedrockY + 1; y < surface; y++) {
                Tile t = world.GetTileAt(x, y);
                // Only convert base limestone — leaves dirt, already-carved veins, and empties alone.
                if (t.type != limestone) continue;
                float depth = 1f - (float)(y - bedrockY) / depthSpan;
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
        var cfg = config;
        float[,] field = new float[nx, ny];
        float seedOffX = seed * 0.7f;
        float seedOffY = seed * 1.3f;
        int caveExclusion = cfg.CaveExclusionBelow;
        int bedrockY = cfg.BedrockY;
        int spawnMinX = cfg.SpawnMinX;
        int spawnMaxX = cfg.SpawnMaxX;

        for (int x = 0; x < nx; x++) {
            int surface = surfaceY[x];
            int caveTop = surface - caveExclusion;
            bool inSpawn = x >= spawnMinX - 2 && x <= spawnMaxX + 2;

            for (int y = 0; y < ny; y++) {
                if (inSpawn || y <= bedrockY || y >= surface) {
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
                    (x + seedOffX) * cfg.CaveFreqX,
                    (y + seedOffY) * cfg.CaveFreqY,
                    cfg.CaveOctaves, cfg.CavePersistence, cfg.CaveLacunarity
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
        var cfg = config;
        int bedrockY = cfg.BedrockY;
        int caveExclusion = cfg.CaveExclusionBelow;
        float threshSurface = cfg.CaveThresholdSurface;
        float threshDeep = cfg.CaveThresholdDeep;
        bool[,] isCave = new bool[nx, ny];
        for (int x = 0; x < nx; x++) {
            int caveTop = surfaceY[x] - caveExclusion;
            for (int y = bedrockY + 1; y < surfaceY[x]; y++) {
                float depthRatio = 1f - (float)(y - bedrockY) / (caveTop - bedrockY);
                float threshold = Mathf.Lerp(threshSurface, threshDeep, depthRatio);
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
        int cycles = config.CACycles;
        bool[,] buffer = new bool[nx, ny];

        for (int iter = 0; iter < cycles; iter++) {
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
        int minCaveSize = config.MinCaveSize;
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
                if (region.Count < minCaveSize) {
                    foreach (var (rx, ry) in region)
                        isCave[rx, ry] = false;
                }
            }
        }
    }

    // ── Cave generation — Worm tunnel blending ────────────────────────

    // Walks worm paths and lowers the noise field along them with soft falloff,
    // so tunnels merge naturally with surrounding Perlin caves. Each world rolls
    // a worm count in [WormCountMin, WormCountMax] and picks distinct random
    // start columns with WormMinSeparation enforced. The walk reflects off the
    // spawn-zone boundary so even after random direction reversals the worm
    // stays on its starting side — the player's starting area never gets
    // tunneled into.
    static void BlendWormTunnels(float[,] caveNoise, int[] surfaceY, int nx, int ny, int seed) {
        var cfg = config;
        System.Random rng = new(seed + 777);

        // Eligible start columns: away from world edges and outside the spawn-zone
        // buffer. The walk below also reflects off the spawn boundary, so a worm
        // that wanders toward spawn turns back rather than tunneling in.
        List<int> candidates = new();
        int leftBound = cfg.SpawnMinX - cfg.WormSpawnBuffer;   // left walks must stay strictly below this
        int rightBound = cfg.SpawnMaxX + cfg.WormSpawnBuffer;  // right walks must stay strictly above this
        for (int x = 5; x < nx - 5; x++) {
            if (x >= leftBound && x <= rightBound) continue;
            candidates.Add(x);
        }

        // Pick worm count, then random distinct start columns with WormMinSeparation
        // between them. If the separation can't be met within a few tries (narrow
        // map, dense placement), accept whatever's left so the count target still hits.
        int wormCount = rng.Next(cfg.WormCountMin, cfg.WormCountMax + 1);
        int wormMinSeparation = cfg.WormMinSeparation;
        List<int> starts = new();
        for (int w = 0; w < wormCount && candidates.Count > 0; w++) {
            int startX = -1;
            for (int attempt = 0; attempt < 12; attempt++) {
                int pick = candidates[rng.Next(candidates.Count)];
                bool farEnough = true;
                foreach (int s in starts) if (Math.Abs(s - pick) < wormMinSeparation) { farEnough = false; break; }
                if (farEnough) { startX = pick; break; }
            }
            if (startX < 0) startX = candidates[rng.Next(candidates.Count)]; // relaxed fallback
            starts.Add(startX);
            candidates.Remove(startX);
        }

        // Walk each worm. Initial xDir is forced outward if the start sits right at
        // the spawn buffer so the first step doesn't immediately bounce.
        int wormMinSteps = cfg.WormMinSteps;
        int wormMaxSteps = cfg.WormMaxSteps;
        int wormRadius = cfg.WormRadius;
        int wormFalloff = cfg.WormFalloff;
        int chamberRadius = cfg.ChamberRadius;
        int chamberInterval = cfg.ChamberInterval;
        float wormTurnChance = cfg.WormTurnChance;
        int spawnMinX = cfg.SpawnMinX;
        int bedrockY = cfg.BedrockY;
        foreach (int startX in starts) {
            int startY = surfaceY[startX] - 1; // start at surface, carves down through exclusion zone
            bool startsLeft = startX < spawnMinX;
            int steps = rng.Next(wormMinSteps, wormMaxSteps + 1);
            int cx = startX, cy = startY;
            int xDir;
            if (startsLeft && cx >= leftBound - 1) xDir = -1;
            else if (!startsLeft && cx <= rightBound + 1) xDir = 1;
            else xDir = rng.NextDouble() < 0.5 ? -1 : 1;

            for (int s = 0; s < steps; s++) {
                int r = (s % chamberInterval == 0 && s > 0) ? chamberRadius : wormRadius;
                SoftCarve(caveNoise, cx, cy, r + wormFalloff, nx, ny);

                // Small chance to reverse horizontal direction each step
                if (rng.NextDouble() < wormTurnChance) xDir = -xDir;

                // Move: down or sideways in current direction
                if (rng.NextDouble() < 0.3f) cy--;
                else cx += xDir;

                // Spawn-zone reflection: if the step pushed us into the buffer,
                // step back out and flip direction. Keeps the worm strictly on
                // its starting side for the whole walk.
                if (startsLeft && cx >= leftBound) { cx = leftBound - 1; xDir = -1; }
                else if (!startsLeft && cx <= rightBound) { cx = rightBound + 1; xDir = 1; }

                cx = Math.Clamp(cx, 1, nx - 2);
                cy = Math.Clamp(cy, bedrockY + 1, ny - 2);
            }
        }
    }

    // Lowers noise values in a soft circle — full push at center, fading to zero at edge.
    // This makes the worm path "attract" cave space from the Perlin field.
    static void SoftCarve(float[,] field, int cx, int cy, int radius, int nx, int ny) {
        float wormStrength = config.WormStrength;
        for (int dx = -radius; dx <= radius; dx++) {
            for (int dy = -radius; dy <= radius; dy++) {
                int x = cx + dx, y = cy + dy;
                if (x < 1 || x >= nx - 1 || y < 1 || y >= ny - 1) continue;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;
                // Smoothstep falloff: 1 at center, 0 at edge
                float t = 1f - dist / radius;
                float strength = t * t * (3f - 2f * t);
                field[x, y] -= strength * wormStrength;
            }
        }
    }

    // ── Free-floating chunk removal ──────────────────────────────────────

    // Deletes solid tile clusters that aren't connected (orthogonally) to the
    // mainland through either solid tiles OR background walls. Catches the
    // floating-spire artefact that arises when a worm carves through a column
    // with a sharp surfaceY transition and SetBackgrounds' Pass-2 erosion
    // strips the leftover wall (its neighbours have no wall there because
    // their surfaceY is well below). Connectivity uses post-erosion
    // backgroundType so the rule matches what the player actually sees as
    // "rooted vs floating."
    //
    // BFS seeds from every connectable cell on the bedrock row (y=0).
    // 4-connected to stay consistent with RemoveSmallCaves. Solid tiles not
    // reached become empty; their backgroundType is left alone (a stranded
    // wall under newly-bared air just reads as a cave wall, which is fine).
    static void RemoveFloatingChunks(World world) {
        int nx = world.nx;
        int ny = world.ny;

        bool[,] connectable = new bool[nx, ny];
        for (int x = 0; x < nx; x++) {
            for (int y = 0; y < ny; y++) {
                Tile t = world.GetTileAt(x, y);
                connectable[x, y] = t.type.solid || t.backgroundType != BackgroundType.None;
            }
        }

        bool[,] reached = new bool[nx, ny];
        Queue<(int x, int y)> queue = new();
        for (int x = 0; x < nx; x++) {
            if (connectable[x, 0]) {
                reached[x, 0] = true;
                queue.Enqueue((x, 0));
            }
        }
        while (queue.Count > 0) {
            var (cx, cy) = queue.Dequeue();
            TryEnqueueConnectable(connectable, reached, queue, cx - 1, cy, nx, ny);
            TryEnqueueConnectable(connectable, reached, queue, cx + 1, cy, nx, ny);
            TryEnqueueConnectable(connectable, reached, queue, cx, cy - 1, nx, ny);
            TryEnqueueConnectable(connectable, reached, queue, cx, cy + 1, nx, ny);
        }

        TileType empty = Db.tileTypeByName["empty"];
        int removed = 0;
        for (int x = 0; x < nx; x++) {
            for (int y = 0; y < ny; y++) {
                Tile t = world.GetTileAt(x, y);
                if (!t.type.solid) continue;
                if (reached[x, y]) continue;
                t.type = empty;
                removed++;
            }
        }
        if (removed > 0)
            Debug.Log($"RemoveFloatingChunks: deleted {removed} unrooted solid tiles.");
    }

    static void TryEnqueueConnectable(bool[,] connectable, bool[,] reached,
                                      Queue<(int x, int y)> queue,
                                      int x, int y, int nx, int ny) {
        if (x < 0 || x >= nx || y < 0 || y >= ny) return;
        if (!connectable[x, y] || reached[x, y]) return;
        reached[x, y] = true;
        queue.Enqueue((x, y));
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
    //
    // Returns a per-chunk bool[] marking which chunks actually placed water.
    // CarveDryChunkPools consumes this to top up the empty chunks.
    static bool[] FillDepressions(World world, int[] surfaceY, int seed) {
        var cfg = config;
        int nx = world.nx;
        ushort waterMax = WaterController.WaterMax;
        System.Random rng = new(seed + 555);
        int waterLine = cfg.WaterLine;

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
                int cappedLevel = Math.Min(waterLevel[x1], waterLine);
                volume += Math.Max(0, cappedLevel - effSurfaceY[x1]);
                if (effSurfaceY[x1] < floor) floor = effSurfaceY[x1];
                x1++;
            }

            // Reject worm-chimney basins: a column whose effective surface has
            // dropped well below the original heightmap is a vertical pit, not
            // a shallow surface depression. Filling it would dump a tower of
            // water down a chimney. The basin's solid floor itself (eff-1) is
            // always solid by ComputeEffectiveSurface's invariant, so caves
            // further below don't actually drain a pond sitting on top of it.
            bool drains = BasinShouldBeRejected(world, surfaceY, effSurfaceY, x0, x1, cfg.PitRejectDepth);

            if (!drains && volume >= 1)
                basins.Add((x0, x1, volume, Math.Min(waterLevel[x0], waterLine), floor));

            cursor = x1;
        }

        if (basins.Count == 0) {
            // Fallback: the WaterLine cap zeros out volume for basins whose floor
            // sits above it, so on maps with mostly-high terrain the primary scan
            // finds nothing. Re-collect using the natural water level (uncapped).
            // Drain check is preserved — we still don't want water falling into caves.
            basins = CollectBasinsUncapped(world, surfaceY, effSurfaceY, waterLevel, nx, cfg.PitRejectDepth);
            if (basins.Count == 0) {
                // No natural basins anywhere; CarveDryChunkPools will be invoked
                // for every chunk and create the world's surface water.
                return new bool[cfg.WaterChunkCount];
            }
        }

        // Pass 2: bucket basins into chunks by center column, then allocate
        // each chunk its own rolled budget. Inside a chunk, fill deepest-floor
        // first; tiny basins are skipped unless none qualify (per-chunk
        // fallback to the deepest available so the chunk doesn't go fully dry
        // when terrain offers nothing of MinPoolVolume).
        int chunkCount = cfg.WaterChunkCount;
        int budgetMin = cfg.WaterChunkBudgetMin;
        int budgetMax = cfg.WaterChunkBudgetMax;
        int minPoolVolume = cfg.MinPoolVolume;
        int chunkWidth = Math.Max(1, nx / chunkCount);
        var byChunk = new List<(int x0, int x1, int volume, int waterLevel, int floor)>[chunkCount];
        for (int i = 0; i < chunkCount; i++) byChunk[i] = new();
        foreach (var b in basins) {
            int center = (b.x0 + b.x1 - 1) / 2;
            int c = Math.Min(chunkCount - 1, center / chunkWidth);
            byChunk[c].Add(b);
        }

        bool[] chunkFilled = new bool[chunkCount];
        for (int c = 0; c < chunkCount; c++) {
            var chunkBasins = byChunk[c];
            if (chunkBasins.Count == 0) continue;
            chunkBasins.Sort((a, b) => a.floor.CompareTo(b.floor));
            int budget = rng.Next(budgetMin, budgetMax + 1);

            // Pre-filter: skip basins below MinPoolVolume entirely. Without
            // this gate the chunk-global level would get pulled down trying
            // to accommodate one-tile puddles, leaving the visible pools
            // shallower than the rolled budget warrants. The deepest-basin
            // fallback below handles the "chunk has only tiny basins" case.
            var eligible = new List<(int x0, int x1, int volume, int waterLevel, int floor)>();
            foreach (var b in chunkBasins)
                if (b.volume >= minPoolVolume) eligible.Add(b);

            bool anyFilled = false;
            if (eligible.Count > 0) {
                // Binary-search the highest chunk-global water level L such
                // that the total fill volume across all eligible basins
                // (each capped at its own natural rim) stays within budget.
                // Spreads water across multiple basins like a water table
                // instead of letting the deepest basin hog the whole chunk's
                // budget — the old per-basin-fill approach would either
                // overshoot wildly (a single huge basin balloons the chunk's
                // total) or stop after one basin and leave the others dry.
                // Per-basin rim cap keeps water from visually overflowing.
                int loL = int.MaxValue, hiL = int.MinValue;
                foreach (var b in eligible) {
                    if (b.floor < loL) loL = b.floor;
                    if (b.waterLevel > hiL) hiL = b.waterLevel;
                }
                int lo = loL, hi = hiL;
                while (lo < hi) {
                    int mid = (lo + hi + 1) / 2;
                    int vol = ChunkVolAtLevel(eligible, effSurfaceY, mid);
                    if (vol <= budget) lo = mid;
                    else hi = mid - 1;
                }
                int chosenL = lo;

                foreach (var b in eligible) {
                    int basinLevel = Math.Min(chosenL, b.waterLevel);
                    for (int x = b.x0; x < b.x1; x++) {
                        for (int y = effSurfaceY[x]; y < basinLevel; y++) {
                            Tile t = world.GetTileAt(x, y);
                            if (!t.type.solid) {
                                t.water = waterMax;
                                anyFilled = true;
                            }
                        }
                    }
                }
            }

            // Fallback: every basin was below MinPoolVolume, OR the binary
            // search settled at the deepest floor (budget can't cover even
            // the deepest basin's first row across its width). Partial-fill
            // the deepest basin so the chunk doesn't read as dry.
            if (!anyFilled && budget > 0) {
                var b = chunkBasins[0];
                int placed = FillBasinUpTo(world, effSurfaceY, b, Math.Min(b.volume, budget), waterMax);
                if (placed > 0) anyFilled = true;
            }
            chunkFilled[c] = anyFilled;
        }
        return chunkFilled;
    }

    // Sum of fill volume across all basins at chunk-global water level L,
    // each basin capped at its own natural rim so it can't visually spill.
    static int ChunkVolAtLevel(
        List<(int x0, int x1, int volume, int waterLevel, int floor)> basins,
        int[] effSurfaceY, int L) {
        int total = 0;
        foreach (var b in basins) {
            int basinLevel = Math.Min(L, b.waterLevel);
            for (int x = b.x0; x < b.x1; x++)
                total += Math.Max(0, basinLevel - effSurfaceY[x]);
        }
        return total;
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
        World world, int[] surfaceY, int[] effSurfaceY, int[] waterLevel, int nx, int pitRejectDepth) {
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

            bool drains = BasinShouldBeRejected(world, surfaceY, effSurfaceY, x0, x1, pitRejectDepth);

            if (!drains && volume >= 1)
                basins.Add((x0, x1, volume, waterLevel[x0], floor));

            cursor = x1;
        }
        return basins;
    }

    // Two-part basin reject used by both the primary and fallback scans:
    //   1. Worm-chimney guard. A column whose effective surface has fallen >>
    //      PitRejectDepth below the original heightmap surfaceY is the bottom
    //      of a worm-carved shaft; filling it would create a tower of water
    //      rather than a surface puddle.
    //   2. Floor-must-be-solid. The tile directly under the basin floor needs
    //      to be solid — water won't sit on air. By ComputeEffectiveSurface's
    //      invariant this is almost always true, but the check is cheap and
    //      catches y=0 bedrock-edge cases plus any future code change that
    //      breaks the invariant.
    // The old "scan 3 tiles below for any cave" check was too strict — a cave
    // 2-3 rows below a solid basin floor doesn't actually drain the pool, it
    // just sits below.
    static bool BasinShouldBeRejected(World world, int[] surfaceY, int[] effSurfaceY,
                                      int x0, int x1, int pitRejectDepth) {
        for (int x = x0; x < x1; x++) {
            if (surfaceY[x] - effSurfaceY[x] > pitRejectDepth) return true;
            int floorY = effSurfaceY[x] - 1;
            if (floorY < 0) return true;
            Tile t = world.GetTileAt(x, floorY);
            if (t == null || !t.type.solid) return true;
        }
        return false;
    }

    // ── Surface water — carved fallback pools ─────────────────────────────

    // For every chunk that FillDepressions failed to put water in, scan the
    // chunk's columns for the flattest low-elevation window and dig a small
    // basin into the surface. This guarantees each chunk ends up with at least
    // one pool even when the heightmap offers nothing the natural pass could
    // use. Skipped chunks (no fitting window away from the spawn zone) log
    // once so we notice degenerate maps without spamming.
    static void CarveDryChunkPools(World world, int[] surfaceY, bool[] chunkFilled) {
        var cfg = config;
        int chunkCount = cfg.WaterChunkCount;
        if (chunkCount <= 0) return;

        int nx = world.nx;
        int width = cfg.PoolCarveWidth;
        int depth = cfg.PoolCarveDepth;
        int maxRoughness = cfg.PoolCarveMaxRoughness;
        if (width < 1 || depth < 1) return;

        // Spawn-buffer guard: pools shouldn't carve into the starting flat
        // (it's deliberately level for player builds). Use the same blend band
        // the heightmap uses so we don't carve right at the transition either.
        int spawnGuardLo = cfg.SpawnMinX - cfg.SpawnBlend;
        int spawnGuardHi = cfg.SpawnMaxX + cfg.SpawnBlend;

        int[] effSurfaceY = ComputeEffectiveSurface(world, surfaceY);
        ushort waterMax = WaterController.WaterMax;
        TileType empty = Db.tileTypeByName["empty"];
        int chunkWidth = Math.Max(1, nx / chunkCount);
        int half = width / 2;

        for (int c = 0; c < chunkCount; c++) {
            if (c < chunkFilled.Length && chunkFilled[c]) continue;

            int cx0 = c * chunkWidth;
            int cx1 = (c == chunkCount - 1) ? nx : Math.Min(nx, cx0 + chunkWidth);

            // Strict-only window selection: roughness must fit
            // PoolCarveMaxRoughness — no loose fallback. Accepting a high-
            // roughness window means the rim sits well above some column's
            // actual surface, and the per-column water would land on
            // already-air cells connected to whatever carved the column
            // (typically a worm tunnel), draining as soon as WaterController
            // ticks. Better to leave a chunk dry than to spawn a leaking pool.
            int bestCenter = -1;
            int bestFloor = int.MaxValue;
            int bestRoughness = int.MaxValue;

            for (int cx = cx0 + half; cx < cx1 - half; cx++) {
                int wx0 = cx - half;
                int wx1 = cx - half + width;
                if (wx0 < 0 || wx1 > nx) continue;
                // Reject windows that overlap the spawn-zone guard.
                if (wx1 > spawnGuardLo && wx0 <= spawnGuardHi) continue;

                int hi = int.MinValue, lo = int.MaxValue;
                for (int x = wx0; x < wx1; x++) {
                    int s = effSurfaceY[x];
                    if (s > hi) hi = s;
                    if (s < lo) lo = s;
                }
                int rough = hi - lo;
                if (rough > maxRoughness) continue;
                if (hi < bestFloor || (hi == bestFloor && rough < bestRoughness)) {
                    bestFloor = hi;
                    bestRoughness = rough;
                    bestCenter = cx;
                }
            }

            if (bestCenter < 0) {
                Debug.Log($"CarveDryChunkPools: chunk {c} ({cx0}..{cx1}) has no carve-eligible window (roughness ≤ {maxRoughness}) — skipping.");
                continue;
            }
            int picked = bestCenter;

            int pwx0 = picked - half;
            int pwx1 = pwx0 + width;
            int floor = int.MinValue;
            for (int x = pwx0; x < pwx1; x++)
                if (effSurfaceY[x] > floor) floor = effSurfaceY[x];

            // Stepped trapezoidal depth: flat full-depth centre, edges stair-
            // step down by 1 row per column once we're more than (depth - 1)
            // tiles from the centre, clamped to 1 so every column still carves
            // at least 1 tile (the visible pool spans the full width). For
            // width=5 depth=2 that produces (1, 2, 2, 2, 1); for width=7
            // depth=3 it's (1, 2, 3, 3, 3, 2, 1). Reads as a bowl with
            // stepped sides, not a sharp parabola.
            int[] colDepth = new int[width];
            int centreFlatHalf = Math.Max(0, half - depth + 1);
            for (int i = 0; i < width; i++) {
                int dist = Math.Abs(i - half);
                int taper = Math.Max(0, dist - centreFlatHalf);
                int d = depth - taper;
                if (d < 1) d = 1;
                if (d > depth) d = depth;
                colDepth[i] = d;
            }

            // Per-column leak check: each carving column needs solid ground
            // directly below its own carve floor. If any column would leak,
            // skip this window — picking another candidate is future work.
            bool leaks = false;
            for (int i = 0; i < width && !leaks; i++) {
                int d = colDepth[i];
                if (d <= 0) continue;
                int colBottom = floor - d;
                if (colBottom <= 0) { leaks = true; break; }
                Tile below = world.GetTileAt(pwx0 + i, colBottom - 1);
                if (below == null || !below.type.solid) leaks = true;
            }
            if (leaks) {
                Debug.Log($"CarveDryChunkPools: chunk {c} window @ x={picked} would leak — skipping.");
                continue;
            }

            // Carve + flood per-column. Water surface sits at floor-1 across the
            // whole window; columns whose taper resolves to 0 stay solid (their
            // dirt walls visually wrap the rim). Cells above the column's own
            // effSurface were already empty — we just place water into the
            // already-air rim cells alongside the newly-carved ones.
            for (int i = 0; i < width; i++) {
                int d = colDepth[i];
                if (d <= 0) continue;
                int x = pwx0 + i;
                int top = effSurfaceY[x];
                int colBottom = floor - d;
                for (int y = colBottom; y < floor; y++) {
                    Tile t = world.GetTileAt(x, y);
                    if (t == null) continue;
                    if (y < top) t.type = empty;
                    t.water = waterMax;
                }
            }
        }
    }

    // ── Cave water ────────────────────────────────────────────────────────

    // For each cave region (flood-filled over the post-carve isCave mask),
    // roll CaveWaterChance and on hit fill a random fraction [0, 1] of the
    // region's volume with water. Uses a uniform fill level so paused worlds
    // start with flat-looking pools rather than tile-by-tile bottom stacks
    // that wouldn't settle until WaterController ticks.
    //
    // No sealing check — open caves (worm-tunneled to surface) drain naturally
    // via WaterController on the first ticks. Acceptable per design.
    static void FillCaveWater(World world, bool[,] isCave, int nx, int ny, int seed) {
        var cfg = config;
        float chance = cfg.CaveWaterChance;
        if (chance <= 0f) return;

        ushort waterMax = WaterController.WaterMax;
        System.Random rng = new(seed + 4242);

        bool[,] visited = new bool[nx, ny];
        List<(int x, int y)> region = new();
        Queue<(int x, int y)> queue = new();

        for (int sx = 0; sx < nx; sx++) {
            for (int sy = 0; sy < ny; sy++) {
                if (!isCave[sx, sy] || visited[sx, sy]) continue;

                region.Clear();
                queue.Clear();
                queue.Enqueue((sx, sy));
                visited[sx, sy] = true;

                while (queue.Count > 0) {
                    var (cx, cy) = queue.Dequeue();
                    region.Add((cx, cy));

                    // 4-connected — match RemoveSmallCaves so region IDs align.
                    TryEnqueue(isCave, visited, queue, cx - 1, cy, nx, ny);
                    TryEnqueue(isCave, visited, queue, cx + 1, cy, nx, ny);
                    TryEnqueue(isCave, visited, queue, cx, cy - 1, nx, ny);
                    TryEnqueue(isCave, visited, queue, cx, cy + 1, nx, ny);
                }

                if (rng.NextDouble() >= chance) continue;

                double fillFrac = rng.NextDouble();
                int targetTiles = (int)Math.Round(fillFrac * region.Count);
                if (targetTiles <= 0) continue;

                // Find region bounds in y so we can binary-search a uniform fill level.
                int minY = int.MaxValue, maxY = int.MinValue;
                foreach (var (rx, ry) in region) {
                    if (ry < minY) minY = ry;
                    if (ry > maxY) maxY = ry;
                }

                // Highest level L such that #(tiles with y < L) <= targetTiles.
                int lo = minY, hi = maxY + 1;
                while (lo < hi) {
                    int mid = (lo + hi + 1) / 2;
                    int count = 0;
                    foreach (var (rx, ry) in region) if (ry < mid) count++;
                    if (count <= targetTiles) lo = mid;
                    else hi = mid - 1;
                }
                int level = lo;

                // Flood the cells below level. If we have budget left over (targetTiles
                // didn't exactly land on a row boundary), top up tiles at level in
                // ascending-x order so partial fills still look intentional.
                int placed = 0;
                foreach (var (rx, ry) in region) {
                    if (ry >= level) continue;
                    Tile t = world.GetTileAt(rx, ry);
                    if (t == null || t.type.solid) continue;
                    t.water = waterMax;
                    placed++;
                }
                int remaining = targetTiles - placed;
                if (remaining > 0) {
                    // Sort by x for stable layout, then place remaining tiles at the rim.
                    region.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
                    foreach (var (rx, ry) in region) {
                        if (remaining <= 0) break;
                        if (ry != level) continue;
                        Tile t = world.GetTileAt(rx, ry);
                        if (t == null || t.type.solid) continue;
                        t.water = waterMax;
                        remaining--;
                    }
                }
            }
        }
    }

    static void TryEnqueue(bool[,] isCave, bool[,] visited, Queue<(int x, int y)> queue,
                           int x, int y, int nx, int ny) {
        if (x < 0 || x >= nx || y < 0 || y >= ny) return;
        if (!isCave[x, y] || visited[x, y]) return;
        visited[x, y] = true;
        queue.Enqueue((x, y));
    }

    // ── Plant scattering ──────────────────────────────────────────────────

    // Scatters random plant clusters across the surface. Each eligible column
    // has a small chance to seed a cluster of 1-3 plants (pine, apple, ramie).
    // Called from WorldController.GenerateDefault after terrain + caves + water.
    public static void ScatterPlants(World world, int[] surfaceY, int seed) {
        var cfg = config;
        System.Random rng = new(seed + 999);
        TileType dirt = Db.tileTypeByName["dirt"];
        int nx = world.nx;
        int spawnMinX = cfg.SpawnMinX;
        int spawnMaxX = cfg.SpawnMaxX;
        float plantChance = cfg.PlantChance;
        int clusterMin = cfg.ClusterMin;
        int clusterMax = cfg.ClusterMax;

        // Track which columns already have a plant to avoid overlap
        bool[] occupied = new bool[nx];

        for (int x = 0; x < nx; x++) {
            if (occupied[x]) continue;
            // Skip spawn zone — leave room for the player
            if (x >= spawnMinX - 2 && x <= spawnMaxX + 2) continue;
            // Must have solid dirt at surface and no water above
            if (!IsPlantEligible(world, surfaceY, x, dirt)) continue;

            if (rng.NextDouble() >= plantChance) continue;

            // Pick plant type weighted by `genWeight` in plantsDb.json. Null means
            // no plant has a positive genWeight — skip silently, PickPlantType
            // already logged the misconfiguration once.
            string plantName = PickPlantType(rng);
            if (plantName == null) return;
            int clusterSize = rng.Next(clusterMin, clusterMax + 1);

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

    // Weighted random pick over every PlantType with genWeight > 0. Weights are
    // unnormalized — tune in plantsDb.json. Returns null only if no plant has a
    // positive genWeight (legacy compat / accidental misconfiguration); caller
    // skips the cluster in that case.
    static string PickPlantType(System.Random rng) {
        float total = 0f;
        foreach (var pt in Db.plantTypeByName.Values)
            if (pt.genWeight > 0f) total += pt.genWeight;
        if (total <= 0f) {
            Debug.LogError("PickPlantType: no PlantTypes have genWeight > 0 — set genWeight on at least one entry in plantsDb.json.");
            return null;
        }
        double pick = rng.NextDouble() * total;
        float acc = 0f;
        foreach (var pt in Db.plantTypeByName.Values) {
            if (pt.genWeight <= 0f) continue;
            acc += pt.genWeight;
            if (pick < acc) return pt.name;
        }
        // Floating-point rounding can leave `pick` infinitesimally above `acc`
        // on the last entry; fall through to the last weighted name.
        string last = null;
        foreach (var pt in Db.plantTypeByName.Values)
            if (pt.genWeight > 0f) last = pt.name;
        return last;
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

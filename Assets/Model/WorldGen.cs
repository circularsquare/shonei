using System;
using System.Collections.Generic;
using UnityEngine;

// Pure C# world generation — no MonoBehaviour, all static methods.
// Called from WorldController.GenerateDefault() to fill the tile grid
// with terrain, caves, and natural features before graph.Initialize().
public static class WorldGen {

    // ── Terrain shape ─────────────────────────────────────────────────
    public const int BaseHeight = 50;       // nominal surface y in the 100x80 grid
    public const int SurfaceMin = 45;       // surface height never below this
    public const int SurfaceMax = 60;       // surface height never above this
    public const int DirtDepth = 3;         // dirt tiles above stone
    public const int BedrockY = 0;          // lowest row is always solid
    const float SurfaceFreq = 0.06f;        // noise frequency (lower = broader hills)
    const float SurfaceAmp = 4f;            // noise amplitude (height variation)
    const int SurfaceOctaves = 3;           // noise detail layers

    // ── Spawn zone ────────────────────────────────────────────────────
    public const int SpawnMinX = 25;        // flat starting zone x-range (inclusive)
    public const int SpawnMaxX = 37;        // >=12 tiles wide
    public const int SpawnBlend = 4;        // tiles over which terrain blends to flat

    // ── Caves ────────────────────────────────────────────────────────
    const float CaveFreqX = 0.06f;          // lower = wider caves horizontally
    const float CaveFreqY = 0.14f;          // higher = thinner caves vertically
    const int CaveOctaves = 2;              // FBM detail layers for cave noise (more = rougher walls)
    const float CavePersistence = 0.5f;     // amplitude falloff per octave
    const float CaveLacunarity = 2f;        // frequency multiplier per octave
    const float CaveThresholdSurface = 0.27f; // near surface: fewer caves
    const float CaveThresholdDeep = 0.34f;  // deep underground: more caves
    const int CaveExclusionBelow = 6;       // no caves within this many tiles below surface
    const int CACycles = 1;                 // cellular automata smoothing iterations (low = preserve FBM detail)
    const int MinCaveSize = 8;              // flood-fill removes voids smaller than this

    // ── Worm carvers ──────────────────────────────────────────────────
    const int WormCount = 1;                // number of worm tunnels (0 = disabled)
    const int WormMinSteps = 100;
    const int WormMaxSteps = 100;
    const int WormRadius = 1;               // carve radius (1 = 3x3 area)
    const int WormFalloff = 1;              // extra radius for soft falloff (must be wide enough to survive CA)
    const int ChamberRadius = 1;            // wider carve every N steps
    const int ChamberInterval = 10000;
    const float WormStrength = 0.4f;        // how much worm pushes noise toward cave (higher = guaranteed tunnel)
    const float WormTurnChance = 0.1f;      // chance per step to reverse horizontal direction

    // ── Water ────────────────────────────────────────────────────────
    public const int WaterLine = 50;        // depressions only fill below this y
    const int MaxPools = 2;                 // at most this many pools per map
    const int MinPoolVolume = 3;            // ignore basins smaller than this (widened so subtle terrain qualifies)
    const int MaxPoolVolume = 50;           // cap large basins to this many water tiles

    // ── Main entry point ─────────────────────────────────────────────────

    // Generates the full terrain: surface heightmap, dirt/stone fill, caves.
    // seed controls all randomness for reproducibility.
    // Returns the surface height array (surfaceY per column) for use by the caller.
    public static int[] Generate(World world, int seed) {
        int nx = world.nx;
        int ny = world.ny;

        int[] surfaceY = GenerateSurfaceHeights(nx, seed);
        FillTerrain(world, surfaceY);

        // Cave generation: build a continuous noise field, blend in worm tunnels,
        // then threshold + CA smooth into the final boolean mask.
        float[,] caveNoise = BuildCaveNoiseField(surfaceY, nx, ny, seed);
        BlendWormTunnels(caveNoise, surfaceY, nx, ny, seed);
        bool[,] isCave = ThresholdCaveField(caveNoise, surfaceY, nx, ny);
        RefineCavesCA(isCave, nx, ny);
        RemoveSmallCaves(isCave, nx, ny);
        ApplyCaves(world, isCave, surfaceY);
        RemoveSurfaceOutcroppings(world, surfaceY);

        FillDepressions(world, surfaceY);

        return surfaceY;
    }

    // ── Surface terrain ──────────────────────────────────────────────────

    // Computes per-column surface height using layered Perlin noise (FBM).
    // The starting zone is forced flat with smooth blending at the edges.
    static int[] GenerateSurfaceHeights(int nx, int seed) {
        int[] heights = new int[nx];

        for (int x = 0; x < nx; x++) {
            float noiseH = BaseHeight + FBM1D(x, seed, SurfaceOctaves, SurfaceFreq, SurfaceAmp);
            float flat = BaseHeight;

            // Blend toward flat in the spawn zone
            float blend = SpawnBlendFactor(x);
            float finalH = Mathf.Lerp(noiseH, flat, blend);
            heights[x] = Mathf.Clamp(Mathf.RoundToInt(finalH), SurfaceMin, SurfaceMax);
        }

        // Remove single-column nubs: if a column is higher or lower than both
        // neighbors, snap it to match so the surface stays smooth.
        for (int x = 1; x < nx - 1; x++) {
            int l = heights[x - 1], h = heights[x], r = heights[x + 1];
            if ((h > l && h > r) || (h < l && h < r))
                heights[x] = (l + r) / 2;
        }

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
    static void FillTerrain(World world, int[] surfaceY) {
        TileType dirt = Db.tileTypeByName["dirt"];
        TileType stone = Db.tileTypeByName["stone"];

        for (int x = 0; x < world.nx; x++) {
            int surface = surfaceY[x];
            for (int y = 0; y < surface; y++) {
                if (y >= surface - DirtDepth)
                    world.GetTileAt(x, y).type = dirt;
                else
                    world.GetTileAt(x, y).type = stone;
            }
            // y >= surface stays empty (default TileType)
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

        List<int> candidates = new();
        for (int x = 5; x < nx - 5; x++) {
            if (x >= SpawnMinX - 4 && x <= SpawnMaxX + 4) continue;
            candidates.Add(x);
        }

        for (int w = 0; w < WormCount && candidates.Count > 0; w++) {
            int idx = (int)((float)(w + 0.5f) / WormCount * candidates.Count);
            idx = Math.Clamp(idx, 0, candidates.Count - 1);
            int startX = candidates[idx];
            int startY = surfaceY[startX] - 1; // start at surface, carves down through exclusion zone

            int steps = rng.Next(WormMinSteps, WormMaxSteps + 1);
            int cx = startX, cy = startY;
            int xDir = rng.NextDouble() < 0.5 ? -1 : 1; // pick initial horizontal direction

            for (int s = 0; s < steps; s++) {
                int r = (s % ChamberInterval == 0 && s > 0) ? ChamberRadius : WormRadius;
                SoftCarve(caveNoise, cx, cy, r + WormFalloff, nx, ny);

                // Small chance to reverse horizontal direction each step
                if (rng.NextDouble() < WormTurnChance) xDir = -xDir;

                // Move: down or sideways in current direction
                if (rng.NextDouble() < 0.3f) cy--;
                else cx += xDir;

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

    // Finds natural basins in the surface heightmap and fills the top MaxPools
    // by volume with water. Two-pass "trapping rain water" to compute per-column
    // water level, then collect eligible basins and fill the largest ones.
    //
    // Capping at MaxPools (rather than filling every basin) keeps water presence
    // consistent across seeds — otherwise lumpy heightmaps produce swampy maps
    // and smooth ones produce dry maps.
    static void FillDepressions(World world, int[] surfaceY) {
        int nx = world.nx;
        ushort waterMax = WaterController.WaterMax;

        // Compute water level per column: min(maxLeft, maxRight)
        int[] waterLevel = new int[nx];
        int[] maxLeft = new int[nx];
        int[] maxRight = new int[nx];

        maxLeft[0] = surfaceY[0];
        for (int x = 1; x < nx; x++)
            maxLeft[x] = Math.Max(maxLeft[x - 1], surfaceY[x]);

        maxRight[nx - 1] = surfaceY[nx - 1];
        for (int x = nx - 2; x >= 0; x--)
            maxRight[x] = Math.Max(maxRight[x + 1], surfaceY[x]);

        for (int x = 0; x < nx; x++)
            waterLevel[x] = Math.Min(maxLeft[x], maxRight[x]);

        // Pass 1: collect all eligible basins (contiguous runs where
        // waterLevel > surfaceY, not draining into caves, big enough).
        List<(int x0, int x1, int volume, int waterLevel)> basins = new();
        int cursor = 0;
        while (cursor < nx) {
            if (waterLevel[cursor] <= surfaceY[cursor]) { cursor++; continue; }

            int x0 = cursor, x1 = cursor;
            int volume = 0;
            while (x1 < nx && waterLevel[x1] > surfaceY[x1]) {
                int cappedLevel = Math.Min(waterLevel[x1], WaterLine);
                volume += Math.Max(0, cappedLevel - surfaceY[x1]);
                x1++;
            }

            // Skip basins that would drain into caves — any column with an
            // empty tile just below the surface lets water out.
            bool drains = false;
            for (int x = x0; x < x1 && !drains; x++) {
                for (int y = surfaceY[x] - 1; y >= Math.Max(0, surfaceY[x] - 3); y--) {
                    if (!world.GetTileAt(x, y).type.solid) { drains = true; break; }
                }
            }

            if (!drains && volume >= MinPoolVolume)
                basins.Add((x0, x1, volume, Math.Min(waterLevel[x0], WaterLine)));

            cursor = x1;
        }

        // Pass 2: sort by volume descending, fill the top MaxPools.
        basins.Sort((a, b) => b.volume.CompareTo(a.volume));
        int toFill = Math.Min(MaxPools, basins.Count);
        for (int i = 0; i < toFill; i++) {
            var b = basins[i];
            int fillLevel = b.waterLevel;

            // Large basins: binary-search the highest uniform level that keeps
            // total volume ≤ MaxPoolVolume, so oversized depressions don't flood.
            if (b.volume > MaxPoolVolume) {
                int lo = surfaceY[b.x0], hi = fillLevel;
                for (int x = b.x0; x < b.x1; x++)
                    lo = Math.Min(lo, surfaceY[x]);
                while (lo < hi) {
                    int mid = (lo + hi + 1) / 2;
                    int vol = 0;
                    for (int x = b.x0; x < b.x1; x++)
                        vol += Math.Max(0, mid - surfaceY[x]);
                    if (vol <= MaxPoolVolume) lo = mid;
                    else hi = mid - 1;
                }
                fillLevel = lo;
            }

            for (int x = b.x0; x < b.x1; x++) {
                for (int y = surfaceY[x]; y < fillLevel; y++) {
                    Tile t = world.GetTileAt(x, y);
                    if (!t.type.solid)
                        t.water = waterMax;
                }
            }
        }
    }

    // ── Plant scattering ──────────────────────────────────────────────────

    // Scatters random plant clusters across the surface. Each eligible column
    // has a small chance to seed a cluster of 1-3 plants (pine, apple, ramie).
    // Called from WorldController.GenerateDefault after terrain + caves + water.
    const float PlantChance = 0.08f;   // per-column chance to start a cluster
    const int ClusterMin = 1;
    const int ClusterMax = 3;

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
        if (roll < 0.50) return "tree";       // pine
        if (roll < 0.75) return "appletree";
        return "ramie";
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

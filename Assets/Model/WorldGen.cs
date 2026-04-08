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
    const float CaveThresholdSurface = 0.27f; // near surface: fewer caves
    const float CaveThresholdDeep = 0.34f;  // deep underground: more caves
    const int CaveExclusionBelow = 6;       // no caves within this many tiles below surface
    const int CACycles = 4;                 // cellular automata smoothing iterations
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
    const int MinPoolVolume = 5;            // ignore basins smaller than this
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

                field[x, y] = Mathf.PerlinNoise(
                    (x + seedOffX) * CaveFreqX,
                    (y + seedOffY) * CaveFreqY
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

        // Remove 1-wide outcroppings: solid tiles with cave on both opposite sides.
        // These are thin nubs poking into cave interiors.
        for (int x = 1; x < nx - 1; x++) {
            for (int y = 1; y < ny - 1; y++) {
                if (isCave[x, y]) continue; // already cave
                if (isCave[x - 1, y] && isCave[x + 1, y])
                    isCave[x, y] = true;
            }
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

    // ── Surface water — depression filling ────────────────────────────────

    // Finds natural basins in the surface heightmap and fills them with water.
    // Uses the "trapping rain water" two-pass algorithm to compute the water level
    // at each column, then fills empty tiles in each basin up to the volume cap.
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

        // Identify contiguous basins (runs of columns where waterLevel > surfaceY)
        // and fill them, respecting volume thresholds.
        int x0 = 0;
        while (x0 < nx) {
            // Skip columns that aren't depressed
            if (waterLevel[x0] <= surfaceY[x0]) { x0++; continue; }

            // Find the extent of this basin, computing volume capped at the water line
            int x1 = x0;
            int volume = 0;
            while (x1 < nx && waterLevel[x1] > surfaceY[x1]) {
                int cappedLevel = Math.Min(waterLevel[x1], WaterLine);
                volume += Math.Max(0, cappedLevel - surfaceY[x1]);
                x1++;
            }

            if (volume >= MinPoolVolume) {
                // If volume exceeds the cap, binary search for the highest uniform
                // water level that keeps total volume ≤ MaxPoolVolume.
                // Water only spawns below the water line.
                int fillLevel = Math.Min(waterLevel[x0], WaterLine);
                if (volume > MaxPoolVolume) {
                    int lo = surfaceY[x0], hi = fillLevel;
                    for (int x = x0; x < x1; x++)
                        lo = Math.Min(lo, surfaceY[x]);
                    while (lo < hi) {
                        int mid = (lo + hi + 1) / 2;
                        int vol = 0;
                        for (int x = x0; x < x1; x++)
                            vol += Math.Max(0, mid - surfaceY[x]);
                        if (vol <= MaxPoolVolume) lo = mid;
                        else hi = mid - 1;
                    }
                    fillLevel = lo;
                }

                // Fill tiles up to the computed level
                for (int x = x0; x < x1; x++) {
                    for (int y = surfaceY[x]; y < fillLevel; y++) {
                        Tile t = world.GetTileAt(x, y);
                        if (!t.type.solid)
                            t.water = waterMax;
                    }
                }
            }

            x0 = x1;
        }
    }

    // ── Noise utilities ──────────────────────────────────────────────────

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

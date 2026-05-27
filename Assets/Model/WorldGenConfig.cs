using UnityEngine;

// ScriptableObject holding all WorldGen tuning parameters. WorldGen reads these
// at gen time via `WorldGen.config`. The live asset lives at
// `Assets/Resources/WorldGenConfig.asset` and is loaded lazily via
// Resources.Load.
//
// To author presets: duplicate the asset (Ctrl+D in the Project window) and
// rename. Swap which asset is "live" by moving it into Assets/Resources/ with
// the canonical filename, or by editing WorldGen.ConfigResourcePath.
//
// All ranges + tooltips are advisory — they shape the inspector but don't
// clamp at gen time. If you need a value outside a slider's range, the
// inspector also lets you type a number directly.
[CreateAssetMenu(fileName = "WorldGenConfig", menuName = "Shonei/WorldGen Config", order = 0)]
public class WorldGenConfig : ScriptableObject {

    // ── Terrain shape ─────────────────────────────────────────────────
    [Header("Terrain shape")]
    [Tooltip("Nominal surface y. Raises/lowers the horizon across the whole map. Keep WaterLine ≈ BaseHeight so basins fill at the expected horizon.")]
    [Range(20, 100)] public int BaseHeight = 60;
    [Tooltip("Surface height never goes below this row.")]
    [Range(10, 100)] public int SurfaceMin = 50;
    [Tooltip("Surface height never goes above this row.")]
    [Range(20, 110)] public int SurfaceMax = 72;
    [Tooltip("Number of dirt tiles above stone.")]
    [Range(0, 10)] public int DirtDepth = 3;
    [Tooltip("Lowest row that's always solid.")]
    [Range(0, 10)] public int BedrockY = 0;
    [Tooltip("Surface noise frequency. Lower = broader hills.")]
    [Range(0.005f, 0.3f)] public float SurfaceFreq = 0.06f;
    [Tooltip("Surface noise amplitude (tiles of height variation).")]
    [Range(0f, 40f)] public float SurfaceAmp = 5.0f;
    [Tooltip("Number of FBM octaves layered for surface detail.")]
    [Range(1, 6)] public int SurfaceOctaves = 3;

    // ── Surface domain warp ───────────────────────────────────────────
    [Header("Surface domain warp")]
    [Tooltip("Frequency of the warp signal. Must be on the same order as SurfaceFreq — slower warps just slide whole regions of noise around, looking like vanilla FBM.")]
    [Range(0.005f, 0.3f)] public float SurfaceWarpFreq = 0.06f;
    [Tooltip("Warp amplitude in tiles of x-shift. ~one surface-wavelength (≈16 at freq 0.06) is the upper bound before features start folding back on themselves chaotically.")]
    [Range(0f, 80f)] public float SurfaceWarpAmp = 10.0f;
    [Tooltip("Octaves layered in the warp signal itself. 2 adds inner detail to the twist.")]
    [Range(1, 4)] public int SurfaceWarpOctaves = 2;
    [Tooltip("Seed offset for the warp signal — decorrelates it from the surface noise.")]
    public int SurfaceWarpSeedOffset = 31337;

    // ── Surface amplitude variation ───────────────────────────────────
    [Header("Surface amplitude variation")]
    [Tooltip("Frequency of the amplitude-multiplier signal. Low so regions are dozens of tiles wide.")]
    [Range(0.001f, 0.05f)] public float SurfaceAmpVarFreq = 0.012f;
    [Tooltip("Minimum multiplier on SurfaceAmp in the calmest regions.")]
    [Range(0f, 2f)] public float SurfaceAmpVarMin = 0.6f;
    [Tooltip("Maximum multiplier on SurfaceAmp in the most dramatic regions.")]
    [Range(0f, 3f)] public float SurfaceAmpVarMax = 1.4f;
    [Tooltip("Seed offset for the amplitude-variation signal.")]
    public int SurfaceAmpVarSeedOffset = 7919;

    // ── Spawn zone ────────────────────────────────────────────────────
    [Header("Spawn zone")]
    [Tooltip("Flat starting zone x-range (inclusive).")]
    [Range(0, 200)] public int SpawnMinX = 25;
    [Tooltip("Flat starting zone x-range (inclusive). Should be at least 12 tiles wide.")]
    [Range(0, 200)] public int SpawnMaxX = 37;
    [Tooltip("Tiles over which terrain smoothly blends to flat at the spawn edges.")]
    [Range(0, 16)] public int SpawnBlend = 4;

    // ── Stone veins ───────────────────────────────────────────────────
    // Per-stone pass: noise < threshold × depthBias converts limestone to the
    // vein tile. depthBias = 1.0 at DepthCenter (0=surface, 1=bedrock), tapering
    // linearly to 0 at ±DepthWidth. Higher Threshold = more abundant veins.
    [Header("Veins — Granite")]
    [Range(0.01f, 0.3f)] public float GraniteFreq = 0.08f;
    [Range(0f, 1f)] public float GraniteThreshold = 0.48f;
    [Tooltip("0=surface, 1=bedrock — where the vein is most likely.")]
    [Range(0f, 1f)] public float GraniteDepthCenter = 0.4f;
    [Range(0.01f, 1f)] public float GraniteDepthWidth = 0.3f;
    public int GraniteSeedOffset = 1111;

    [Header("Veins — Slate")]
    [Range(0.01f, 0.3f)] public float SlateFreq = 0.09f;
    [Range(0f, 1f)] public float SlateThreshold = 0.5f;
    [Tooltip("0=surface, 1=bedrock — where the vein is most likely.")]
    [Range(0f, 1f)] public float SlateDepthCenter = 0.7f;
    [Range(0.01f, 1f)] public float SlateDepthWidth = 0.25f;
    public int SlateSeedOffset = 2222;

    // ── Caves ─────────────────────────────────────────────────────────
    [Header("Caves")]
    [Tooltip("Lower = wider caves horizontally.")]
    [Range(0.01f, 0.3f)] public float CaveFreqX = 0.06f;
    [Tooltip("Higher = thinner caves vertically.")]
    [Range(0.01f, 0.5f)] public float CaveFreqY = 0.14f;
    [Tooltip("FBM detail layers for cave noise. More = rougher walls.")]
    [Range(1, 5)] public int CaveOctaves = 2;
    [Tooltip("Amplitude falloff per octave.")]
    [Range(0.1f, 1f)] public float CavePersistence = 0.5f;
    [Tooltip("Frequency multiplier per octave.")]
    [Range(1f, 4f)] public float CaveLacunarity = 2.0f;
    [Tooltip("Threshold near surface — lower means fewer caves there.")]
    [Range(0f, 1f)] public float CaveThresholdSurface = 0.28f;
    [Tooltip("Threshold deep underground — higher means more caves there.")]
    [Range(0f, 1f)] public float CaveThresholdDeep = 0.38f;
    [Tooltip("No natural caves within this many tiles below the surface.")]
    [Range(0, 20)] public int CaveExclusionBelow = 4;
    [Tooltip("Cellular-automata smoothing iterations. Low preserves FBM detail.")]
    [Range(0, 6)] public int CACycles = 1;
    [Tooltip("Flood-fill removes cave voids smaller than this many tiles.")]
    [Range(1, 30)] public int MinCaveSize = 8;

    // ── Worm carvers ──────────────────────────────────────────────────
    [Header("Worm carvers")]
    [Tooltip("Lower bound on worm count rolled per world.")]
    [Range(0, 6)] public int WormCountMin = 2;
    [Tooltip("Upper bound on worm count rolled per world.")]
    [Range(0, 6)] public int WormCountMax = 3;
    [Tooltip("Minimum x-distance between worm starts. Relaxed if no candidate fits.")]
    [Range(0, 80)] public int WormMinSeparation = 25;
    [Tooltip("Worm walks reflect off (spawn zone ± this many tiles).")]
    [Range(0, 10)] public int WormSpawnBuffer = 2;
    [Range(10, 400)] public int WormMinSteps = 100;
    [Range(10, 400)] public int WormMaxSteps = 150;
    [Tooltip("Carve radius. 1 = 3x3 area.")]
    [Range(0, 4)] public int WormRadius = 1;
    [Tooltip("Extra radius for soft falloff. Must be wide enough to survive CA.")]
    [Range(0, 4)] public int WormFalloff = 1;
    [Tooltip("Wider carve radius applied every ChamberInterval steps.")]
    [Range(0, 5)] public int ChamberRadius = 1;
    [Range(1, 10000)] public int ChamberInterval = 10000;
    [Tooltip("How much the worm pushes noise toward cave. Higher = guaranteed tunnel.")]
    [Range(0f, 1f)] public float WormStrength = 0.4f;
    [Tooltip("Chance per step to reverse horizontal direction.")]
    [Range(0f, 1f)] public float WormTurnChance = 0.1f;

    // ── Water ─────────────────────────────────────────────────────────
    [Header("Water")]
    [Tooltip("Depressions only fill below this y (≈ BaseHeight).")]
    [Range(20, 100)] public int WaterLine = 60;
    [Tooltip("Horizontal slices the water budget is split across.")]
    [Range(1, 8)] public int WaterChunkCount = 3;
    [Tooltip("Per-chunk water tile budget, rolled per world.")]
    [Range(0, 200)] public int WaterChunkBudgetMin = 25;
    [Range(0, 300)] public int WaterChunkBudgetMax = 60;
    [Tooltip("Basins smaller than this are skipped (unless they're the only option in a chunk).")]
    [Range(1, 30)] public int MinPoolVolume = 3;
    [Tooltip("Reject basins where any column's effective surface has dropped more than this many tiles below the original heightmap surfaceY. Catches worm-chimney pits without rejecting shallow basins that happen to have caves a few rows below their solid floor.")]
    [Range(1, 20)] public int PitRejectDepth = 4;
    [Tooltip("Width of the carved fallback pool when a chunk gets no natural water. Cross-section is parabolic — edges taper to depth 0, centre carves to PoolCarveDepth — so the visible pool is roughly width-2 tiles wide.")]
    [Range(3, 15)] public int PoolCarveWidth = 7;
    [Tooltip("Maximum carve depth at the centre of the pool. Edges taper toward zero following a parabolic profile.")]
    [Range(1, 6)] public int PoolCarveDepth = 3;
    [Tooltip("Max (highest-lowest) effective surface y inside the carve window. Higher = will carve across rougher terrain.")]
    [Range(0, 8)] public int PoolCarveMaxRoughness = 2;
    [Tooltip("Per-cave-region chance to start partially flooded. Fill fraction is then rolled in [0, 1].")]
    [Range(0f, 1f)] public float CaveWaterChance = 0.25f;

    // ── Liquid settler ────────────────────────────────────────────────
    [Header("Liquid settler")]
    [Tooltip("Maximum SimulateStep iterations to run at gen time so water starts settled. Each step is one full CA pass (fall + spread + lookahead).")]
    [Range(0, 300)] public int LiquidSettleMaxSteps = 100;
    [Tooltip("Early-exit when total water transferred in a step is at or below this. 0 disables early exit.")]
    [Range(0, 50)] public int LiquidSettleMoveThreshold = 4;

    // ── Beach sand ────────────────────────────────────────────────────
    [Header("Beach sand")]
    [Tooltip("Frequency of the sand clump mask. Lower = larger clumps.")]
    [Range(0.01f, 0.5f)] public float SandFreq = 0.18f;
    [Tooltip("Perlin output threshold for sand. ~0.52 ≈ 40% of eligible tiles convert.")]
    [Range(0f, 1f)] public float SandThreshold = 0.52f;

    // ── Clay banks ────────────────────────────────────────────────────
    // Runs after the sand pass on the same water-adjacent-dirt pool, so
    // sand wins any overlap. Independent Perlin mask (own seed offsets) so
    // clay patches are uncorrelated with sand patches.
    [Header("Clay banks")]
    [Tooltip("Frequency of the clay clump mask. Lower = larger clumps.")]
    [Range(0.01f, 0.5f)] public float ClayFreq = 0.20f;
    [Tooltip("Perlin output threshold for clay. Higher = rarer. 1.0 disables clay entirely.")]
    [Range(0f, 1f)] public float ClayThreshold = 0.55f;

    // ── Plants ────────────────────────────────────────────────────────
    [Header("Plants")]
    [Tooltip("Per-column chance to start a plant cluster on eligible surface dirt.")]
    [Range(0f, 0.5f)] public float PlantChance = 0.08f;
    [Range(1, 6)] public int ClusterMin = 1;
    [Range(1, 8)] public int ClusterMax = 3;

    // ── Moisture ──────────────────────────────────────────────────────
    [Header("Moisture")]
    [Tooltip("Baseline soil dampness on every solid tile at gen time. Surface dries via MoistureSystem; underground holds.")]
    [Range(0, 255)] public byte StartingMoisture = 50;
}

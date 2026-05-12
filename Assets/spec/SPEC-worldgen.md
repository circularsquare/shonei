# World Generation

All world gen lives in `WorldGen.cs` (pure static class, no MonoBehaviour). Called from `WorldController.GenerateDefault()` with a random seed.

## Pipeline

1. **Surface heightmap** — FBM Perlin noise, clamped to `[SurfaceMin, SurfaceMax]`. Spawn zone forced flat with smoothstep blend at edges. Single-column nubs smoothed out.
2. **Fill terrain** — Dirt layer on top (`DirtDepth` tiles), limestone below (default stone variant).
3. **Stone veins** — `ApplyVeins` runs two passes (granite, slate). Each samples its own FBM field and converts limestone tiles to the vein tile where `noise < threshold × depthBias`. `depthBias` is 1.0 at the vein's `DepthCenter` (0=surface, 1=bedrock), tapering linearly to 0 at ±`DepthWidth`. Only limestone tiles are touched, so veins don't overlap (first pass wins). Granite is mid-depth biased; slate is deep. Seeds are offset per-pass for reproducibility without correlation between vein types.
4. **Cave noise field** — 2D FBM (2 octaves, persistence 0.5, lacunarity 2) stored as continuous floats, normalized to ~[0,1]. Spawn zone = 1.0 (solid), exclusion zone near surface = 0.4 (blocks natural caves but worms can push through), normal underground = FBM sample.
5. **Worm tunnel blending** — Worm walks from surface downward with a persistent horizontal direction (small chance to reverse per step). `SoftCarve` lowers noise values with smoothstep falloff along the path, merging naturally with surrounding Perlin caves.
6. **Threshold** — Continuous field → boolean mask. Depth-varying threshold (stricter near surface, looser deep). Covers full underground including exclusion zone.
7. **CA smoothing** — 1 round of cellular automata (>4 solid neighbors → solid). Kept minimal so FBM detail survives into the final mask; bump up if caves feel too noisy. Then a pass to remove 1-wide horizontal outcroppings.
8. **Small cave removal** — BFS flood-fill removes cave regions smaller than `MinCaveSize`.
9. **Apply caves** — Boolean mask → set tiles to empty. Caves carve through whichever stone variant is there (veins don't get special treatment).
10. **Depression filling** — "Trapping rain water" algorithm finds surface basins. Eligible basins (volume ≥ `MinPoolVolume`, below `WaterLine`, not draining into caves) are collected; the top `MaxPools` by volume are filled. Large basins are capped at `MaxPoolVolume` by binary-searching for a uniform water level.
11. **Beach sand** — `ApplyBeachSand` converts a clumpy ~40% of dirt tiles touching water (orthogonal OR diagonal) into sand. A single-octave Perlin mask (`SandFreq` ≈ 0.18, threshold ≈ 0.52) decides which eligible tiles flip, so beaches form as patches and dunes rather than a uniform sand ring around every pool. Runs after `FillDepressions` (water placement must be final) and before `PopulateOverlays` (so grass bits are seeded on dirt's final boundary — though the Tile.type setter would clear them on a later type-flip anyway).
12. **Tile overlays** — `PopulateOverlays` walks every overlay-bearing tile (today: dirt) and seeds `tile.overlayMask` bits on each cardinal edge whose neighbour is non-solid AND non-flooded. This is what makes surface dirt "have grass on top" by default and dirt walls of caves "have grass on the cave-facing side." Runs after depression filling so submerged dirt doesn't get grass on the under-water side. Mining never adds bits, so newly exposed sides stay bare. See SPEC-rendering "Tile overlays" for the rendering side.

## Key design choices

- **Continuous noise field** for caves (not immediate boolean) so worm tunnels blend with natural caves instead of looking stamped on.
- **FBM + minimal CA** — layering multiple octaves of Perlin adds high-frequency wall detail (pockets, niches, rough edges); CA is deliberately capped at 1 round so that detail survives into the final mask instead of being smoothed back into blobs.
- **Exclusion zone** uses an intermediate noise value (0.4) — high enough to block natural caves, low enough that worm strength can punch through. This lets worms bore from surface into caves while keeping the near-surface layer solid otherwise.
- **Volume-capped water** — large basins get a lowered uniform water level rather than filling left-to-right and cutting off.
- **Fixed pool count per map** — capping at `MaxPools` (instead of filling every eligible basin) keeps water presence consistent across seeds. Otherwise lumpy heightmaps produce swamps and smooth ones produce deserts. Top-N by volume picks the most visually meaningful basins.

## Tuning

All constants are grouped at the top of `WorldGen.cs` by category: terrain shape, spawn zone, stone veins, caves, worm carvers, water. Adjust there.

For vein passes in particular: `{Stone}Threshold` gates per-tile conversion (higher = more vein tiles at peak), `{Stone}DepthCenter` and `{Stone}DepthWidth` set the depth band where the vein appears, and `{Stone}Freq` controls vein shape (higher = smaller/tighter veins). To add a new stone type, add a matching block of constants and append another `ApplyVeinPass` call to `ApplyVeins`.

# World Generation

All world gen lives in `WorldGen.cs` (pure static class, no MonoBehaviour). Called from `WorldController.GenerateDefault()` with a random seed.

## Pipeline

1. **Surface heightmap** — FBM Perlin noise, clamped to `[SurfaceMin, SurfaceMax]`. Spawn zone forced flat with smoothstep blend at edges. Single-column nubs smoothed out.
2. **Fill terrain** — Dirt layer on top (`DirtDepth` tiles), stone below.
3. **Cave noise field** — 2D FBM (2 octaves, persistence 0.5, lacunarity 2) stored as continuous floats, normalized to ~[0,1]. Spawn zone = 1.0 (solid), exclusion zone near surface = 0.4 (blocks natural caves but worms can push through), normal underground = FBM sample.
4. **Worm tunnel blending** — Worm walks from surface downward with a persistent horizontal direction (small chance to reverse per step). `SoftCarve` lowers noise values with smoothstep falloff along the path, merging naturally with surrounding Perlin caves.
5. **Threshold** — Continuous field → boolean mask. Depth-varying threshold (stricter near surface, looser deep). Covers full underground including exclusion zone.
6. **CA smoothing** — 1 round of cellular automata (>4 solid neighbors → solid). Kept minimal so FBM detail survives into the final mask; bump up if caves feel too noisy. Then a pass to remove 1-wide horizontal outcroppings.
7. **Small cave removal** — BFS flood-fill removes cave regions smaller than `MinCaveSize`.
8. **Apply caves** — Boolean mask → set tiles to empty.
9. **Depression filling** — "Trapping rain water" algorithm finds surface basins. Eligible basins (volume ≥ `MinPoolVolume`, below `WaterLine`, not draining into caves) are collected; the top `MaxPools` by volume are filled. Large basins are capped at `MaxPoolVolume` by binary-searching for a uniform water level.

## Key design choices

- **Continuous noise field** for caves (not immediate boolean) so worm tunnels blend with natural caves instead of looking stamped on.
- **FBM + minimal CA** — layering multiple octaves of Perlin adds high-frequency wall detail (pockets, niches, rough edges); CA is deliberately capped at 1 round so that detail survives into the final mask instead of being smoothed back into blobs.
- **Exclusion zone** uses an intermediate noise value (0.4) — high enough to block natural caves, low enough that worm strength can punch through. This lets worms bore from surface into caves while keeping the near-surface layer solid otherwise.
- **Volume-capped water** — large basins get a lowered uniform water level rather than filling left-to-right and cutting off.
- **Fixed pool count per map** — capping at `MaxPools` (instead of filling every eligible basin) keeps water presence consistent across seeds. Otherwise lumpy heightmaps produce swamps and smooth ones produce deserts. Top-N by volume picks the most visually meaningful basins.

## Tuning

All constants are grouped at the top of `WorldGen.cs` by category: terrain shape, spawn zone, caves, worm carvers, water. Adjust there.

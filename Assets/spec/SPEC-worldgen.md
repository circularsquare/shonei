# World Generation

All world gen lives in `WorldGen.cs` (pure static class, no MonoBehaviour). Called from `WorldController.GenerateDefault()` with a random seed.

## Pipeline

1. **Surface heightmap** — FBM Perlin noise, clamped to `[SurfaceMin, SurfaceMax]`. Spawn zone forced flat with smoothstep blend at edges. Single-column nubs smoothed out.
2. **Fill terrain** — Dirt layer on top (`DirtDepth` tiles), stone below.
3. **Cave noise field** — 2D Perlin noise stored as continuous floats. Spawn zone = 1.0 (solid), exclusion zone near surface = 0.4 (blocks Perlin caves but worms can push through), normal underground = raw Perlin.
4. **Worm tunnel blending** — Worm walks from surface downward with a persistent horizontal direction (small chance to reverse per step). `SoftCarve` lowers noise values with smoothstep falloff along the path, merging naturally with surrounding Perlin caves.
5. **Threshold** — Continuous field → boolean mask. Depth-varying threshold (stricter near surface, looser deep). Covers full underground including exclusion zone.
6. **CA smoothing** — 4 rounds of cellular automata (>4 solid neighbors → solid). Then a pass to remove 1-wide horizontal outcroppings.
7. **Small cave removal** — BFS flood-fill removes cave regions smaller than `MinCaveSize`.
8. **Apply caves** — Boolean mask → set tiles to empty.
9. **Depression filling** — "Trapping rain water" algorithm finds surface basins. Basins below `WaterLine` with volume ≥ `MinPoolVolume` are filled. Large basins are capped at `MaxPoolVolume` by binary-searching for a uniform water level.

## Key design choices

- **Continuous noise field** for caves (not immediate boolean) so worm tunnels blend with Perlin caves instead of looking stamped on.
- **Exclusion zone** uses an intermediate noise value (0.4) — high enough to block natural caves, low enough that worm strength can punch through. This lets worms bore from surface into caves while keeping the near-surface layer solid otherwise.
- **Volume-capped water** — large basins get a lowered uniform water level rather than filling left-to-right and cutting off.

## Tuning

All constants are grouped at the top of `WorldGen.cs` by category: terrain shape, spawn zone, caves, worm carvers, water. Adjust there.

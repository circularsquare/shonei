// Named bits for the per-tile cardinal-side mask shared by tile-body, grass
// overlay, and snow rendering, by OverlayGrowthSystem's growth gate, and by the
// Tile.overlayMask / cMask / effective masks throughout. One canonical layout in
// one place, so the four-way duplication of "1=L 2=R 4=D 8=U" stops drifting.
//
// NOTE: distinct from PowerSystem.Side (Up=4/Down=8) — that enum predates this and
// uses a different layout for a different purpose. This is the tile-mask layout
// (D=4/U=8), matching Tile.overlayMask. Plain const ints (not a [Flags] enum) so
// the bitwise mask math reads without casts.
public static class TileSide {
    public const int L = 1; // left  neighbour
    public const int R = 2; // right neighbour
    public const int D = 4; // down  neighbour
    public const int U = 8; // up    neighbour
}

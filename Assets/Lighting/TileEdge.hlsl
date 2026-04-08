// Shared jagged-edge clipping for tiles.
// Included by both TileSprite.shader (visual) and NormalsCapture.shader (lighting)
// so clipped pixels are identical in both passes — no ghost normals.
//
// Usage:
//   float _AdjacencyMask; // 0–15, set via MPB (default 15 = all solid = no clip)
//   if (!TileEdgeClip(_AdjacencyMask, worldPos)) discard;
//
// Mask bits: 0=left  1=right  2=down  3=up  (same as TileNormalMaps.cs)
#ifndef TILE_EDGE_INCLUDED
#define TILE_EDGE_INCLUDED

// PCG-style hash — deterministic, good avalanche, pure ALU.
uint TileHash(uint x, uint y, uint edge, uint pixel) {
    uint state = x * 73856093u ^ y * 19349663u ^ edge * 83492791u ^ pixel * 39916801u;
    state = state * 747796405u + 2891336453u;
    state = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (state >> 22u) ^ state;
}

// How many pixels to carve inward (0 or 1) for one pixel-column on one edge.
// ~65% chance of 0px, ~35% chance of 1px — subtle irregular profile.
// tileX/tileY = integer world coords, edge = 0–3 (L/R/D/U), pixel = index along edge (0–15).
float JagDepth(int tileX, int tileY, uint edge, uint pixel) {
    uint h = TileHash((uint)tileX, (uint)tileY, edge, pixel);
    return (h % 100u) < 35u ? 1.0 : 0.0;
}

// Compute jag depth for one pixel on an exposed edge, with corner overrides.
// At an outward corner (both edges exposed) → always carve (return 1).
// At an inward corner (perpendicular neighbor solid) → never carve (return 0).
// Middle pixels → random 65/35 distribution.
// perpMask: whether the perpendicular neighbor at this end of the edge is solid.
//   perpLo = neighbor at pixel index 0 end, perpHi = neighbor at pixel index 15 end.
float JagDepthWithCorners(int tileX, int tileY, uint edge, int pixel,
                          bool perpLoSolid, bool perpHiSolid) {
    // Corner pixel at low end (pixel 0)
    if (pixel == 0) return perpLoSolid ? 0.0 : 1.0;
    // Corner pixel at high end (pixel 15)
    if (pixel == 15) return perpHiSolid ? 0.0 : 1.0;
    // Middle pixels: normal random
    return JagDepth(tileX, tileY, edge, (uint)pixel);
}

// Returns true if the pixel should be KEPT (not clipped).
// adjacencyMask: 4-bit float (0–15). 15 = fully surrounded = no clipping.
// worldPos: fragment world position (tile origin is at integer coords).
bool TileEdgeClip(float adjacencyMask, float2 worldPos) {
    int mask = (int)(adjacencyMask + 0.5); // round to int
    if (mask >= 15) return true; // fully surrounded — nothing to clip

    // Recover integer tile coordinate and local pixel position.
    // Tiles sit at integer world coords; sprite spans [-0.5, +0.5] around that.
    float2 tileOrigin = floor(worldPos + 0.5);
    int tileX = (int)tileOrigin.x;
    int tileY = (int)tileOrigin.y;

    // Local position within tile: 0–1 range, then to pixel coords 0–15.
    float2 local = worldPos - tileOrigin + 0.5; // 0–1
    int pixX = clamp((int)(local.x * 16.0), 0, 15);
    int pixY = clamp((int)(local.y * 16.0), 0, 15);

    // Neighbour flags from mask bits.
    bool hasLeft  = (mask & 1) != 0;
    bool hasRight = (mask & 2) != 0;
    bool hasDown  = (mask & 4) != 0;
    bool hasUp    = (mask & 8) != 0;

    // Check each exposed edge with corner-aware jag depth.
    // For each edge, the perpendicular neighbors at pixel 0 / pixel 15 determine
    // whether the corner is inward (solid → jag=0) or outward (absent → jag=1).

    // Left edge (runs along Y). perpLo=down, perpHi=up.
    if (!hasLeft) {
        float jag = JagDepthWithCorners(tileX, tileY, 0u, pixY, hasDown, hasUp);
        if ((float)pixX < jag) return false;
    }
    // Right edge (runs along Y). perpLo=down, perpHi=up.
    if (!hasRight) {
        float jag = JagDepthWithCorners(tileX, tileY, 1u, pixY, hasDown, hasUp);
        if ((float)(15 - pixX) < jag) return false;
    }
    // Bottom edge (runs along X). perpLo=left, perpHi=right.
    if (!hasDown) {
        float jag = JagDepthWithCorners(tileX, tileY, 2u, pixX, hasLeft, hasRight);
        if ((float)pixY < jag) return false;
    }
    // Top edge (runs along X). perpLo=left, perpHi=right.
    if (!hasUp) {
        float jag = JagDepthWithCorners(tileX, tileY, 3u, pixX, hasLeft, hasRight);
        if ((float)(15 - pixY) < jag) return false;
    }

    return true;
}

#endif

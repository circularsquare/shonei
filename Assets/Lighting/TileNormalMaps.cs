using UnityEngine;

// Generates and caches 256 normal map variants for solid 20×20 tiles.
// Each variant corresponds to an 8-bit adjacency mask describing which
// neighbours are solid. Cardinal neighbours control edge bevels; diagonal
// neighbours affect light penetration into inside corners.
//
// The 20×20 map matches the baked sprite size from TileSpriteCache:
// pixels (2,2)–(17,17) are the 16×16 tile interior with bevel normals.
// Border pixels (0-1 and 18-19) get flat normals and full edge depth.
//
// Mask bits:  0=left  1=right  2=down  3=up  4=BL  5=BR  6=TL  7=TR
// Cardinal bits (0-3) control beveled normals.
// Diagonal bits (4-7) only affect edge-depth (alpha) for light penetration.
//
// Usage: TileNormalMaps.Apply(spriteRenderer, mask)  — solid tiles
//        TileNormalMaps.Clear(spriteRenderer)         — non-solid tiles
public static class TileNormalMaps {
    const int TILE     = 16; // interior tile size
    const int BORDER   = 2;  // overhang per side
    const int SIZE     = TILE + BORDER * 2; // 20
    const int BEVEL_PX = 4;  // bevel gradient width in interior pixels
    const float BEVEL_Z = 1f;
    static readonly int NormalMapID = Shader.PropertyToID("_NormalMap");

    // 256 variants (8-bit adjacency: 4 cardinal + 4 diagonal) + 1 fully-flat fallback
    static Texture2D[] cache;
    static Texture2D   flatTex;

    public static void Apply(SpriteRenderer sr, int mask) {
        EnsureCache();
        var mpb = new MaterialPropertyBlock();
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(NormalMapID, cache[mask & 0xFF]);
        sr.SetPropertyBlock(mpb);
    }

    // Clears the normal map by applying a fully-flat texture.
    public static void Clear(SpriteRenderer sr) {
        EnsureCache();
        var mpb = new MaterialPropertyBlock();
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(NormalMapID, flatTex);
        sr.SetPropertyBlock(mpb);
    }

    static void EnsureCache() {
        if (cache != null) return;
        cache = new Texture2D[256];
        for (int mask = 0; mask < 256; mask++)
            cache[mask] = Generate(mask);
        flatTex = GenerateFlat();
    }

    // Flat fallback for non-solid tiles — alpha 255 (doesn't matter since these
    // tiles have no sprite, but keeps it safe for any non-tile sprite that gets
    // the flat texture via Clear).
    static Texture2D GenerateFlat() {
        var pixels = new Color32[SIZE * SIZE];
        byte flat = (byte)(1.0f * 127.5f + 128f); // packed +1 = flat forward (nz=1)
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color32(128, 128, flat, 255);

        var tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }

    static Texture2D Generate(int mask) {
        // Cardinal neighbours (bits 0-3)
        bool hasLeft  = (mask & 1) != 0;
        bool hasRight = (mask & 2) != 0;
        bool hasDown  = (mask & 4) != 0;
        bool hasUp    = (mask & 8) != 0;

        // Diagonal neighbours (bits 4-7)
        bool hasBL = (mask & 16) != 0;
        bool hasBR = (mask & 32) != 0;
        bool hasTL = (mask & 64) != 0;
        bool hasTR = (mask & 128) != 0;

        var pixels = new Color32[SIZE * SIZE];
        byte flatZ = (byte)(1.0f * 127.5f + 128f);

        for (int oy = 0; oy < SIZE; oy++) {
            for (int ox = 0; ox < SIZE; ox++) {
                // Interior coords: remap from 20×20 to 16×16 interior
                int x = ox - BORDER; // -2 to 17
                int y = oy - BORDER;

                bool isBorder = x < 0 || x >= TILE || y < 0 || y >= TILE;

                if (isBorder) {
                    // Border pixels: flat normal, full edge depth (at the surface)
                    pixels[oy * SIZE + ox] = new Color32(128, 128, flatZ, 255);
                    continue;
                }

                // ── Interior pixel: bevel + edge depth (same as before) ──

                // Bevel normal: gradient across the BEVEL_PX-wide border region.
                float bL = (!hasLeft  && x < BEVEL_PX)          ? 1f - x / (float)BEVEL_PX               : 0f;
                float bR = (!hasRight && (TILE-1-x) < BEVEL_PX) ? 1f - (TILE-1-x) / (float)BEVEL_PX     : 0f;
                float bD = (!hasDown  && y < BEVEL_PX)          ? 1f - y / (float)BEVEL_PX               : 0f;
                float bU = (!hasUp    && (TILE-1-y) < BEVEL_PX) ? 1f - (TILE-1-y) / (float)BEVEL_PX     : 0f;

                float nx = bR - bL;
                float ny = bU - bD;
                float nz = BEVEL_Z;

                float len = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 0f) { nx /= len; ny /= len; nz /= len; }
                else          { nz = 1f; }

                // ── Edge depth for light penetration ──────────────────────
                float minDist = TILE; // large default = deep interior
                if (!hasLeft)  minDist = Mathf.Min(minDist, x);
                if (!hasRight) minDist = Mathf.Min(minDist, TILE - 1 - x);
                if (!hasDown)  minDist = Mathf.Min(minDist, y);
                if (!hasUp)    minDist = Mathf.Min(minDist, TILE - 1 - y);

                // Diagonal openings: light enters from corner
                if (!hasBL && hasLeft && hasDown)
                    minDist = Mathf.Min(minDist, Mathf.Sqrt(x * x + y * y));
                if (!hasBR && hasRight && hasDown)
                    minDist = Mathf.Min(minDist, Mathf.Sqrt((TILE-1-x)*(TILE-1-x) + y*y));
                if (!hasTL && hasLeft && hasUp)
                    minDist = Mathf.Min(minDist, Mathf.Sqrt(x*x + (TILE-1-y)*(TILE-1-y)));
                if (!hasTR && hasRight && hasUp)
                    minDist = Mathf.Min(minDist, Mathf.Sqrt((TILE-1-x)*(TILE-1-x) + (TILE-1-y)*(TILE-1-y)));

                // Smoothstep falloff: 1.0 at edge, 0.0 at penetration depth.
                float depthPx = LightFeature.penetrationDepth * TILE;
                float t = Mathf.Clamp01(1f - minDist / depthPx);
                float edgeDepth = t * t * (3f - 2f * t); // smoothstep

                pixels[oy * SIZE + ox] = new Color32(
                    (byte)(nx * 127.5f + 128f),
                    (byte)(ny * 127.5f + 128f),
                    (byte)(nz * 127.5f + 128f),
                    (byte)(edgeDepth * 255f)
                );
            }
        }

        var tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }
}

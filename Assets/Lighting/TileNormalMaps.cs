using UnityEngine;

// Generates and caches 256 normal map variants for solid 16x16 tiles.
// Each variant corresponds to an 8-bit adjacency mask describing which
// neighbours are solid. Cardinal neighbours control edge bevels; diagonal
// neighbours affect light penetration into inside corners.
//
// Mask bits:  0=left  1=right  2=down  3=up  4=BL  5=BR  6=TL  7=TR
// Cardinal bits (0-3) control beveled normals and jagged edge clipping.
// Diagonal bits (4-7) only affect edge-depth (alpha) for light penetration
// into inside corners.
//
// Usage: TileNormalMaps.Apply(spriteRenderer, mask)  — solid tiles
//        TileNormalMaps.Clear(spriteRenderer)         — non-solid tiles
public static class TileNormalMaps {
    const int   SIZE     = 16;
    const float BEVEL_Z  = 1f;
    const float DEPTH_PX = 12f; // light penetration depth in pixels
    static readonly int NormalMapID     = Shader.PropertyToID("_NormalMap");
    static readonly int AdjacencyMaskID = Shader.PropertyToID("_AdjacencyMask");

    // 256 variants (8-bit adjacency: 4 cardinal + 4 diagonal) + 1 fully-flat fallback
    static Texture2D[] cache;
    static Texture2D   flatTex;

    public static void Apply(SpriteRenderer sr, int mask) {
        EnsureCache();
        var mpb = new MaterialPropertyBlock();
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(NormalMapID, cache[mask & 0xFF]);
        mpb.SetFloat(AdjacencyMaskID, mask & 0xF); // jagged edge shader uses cardinal bits only
        sr.SetPropertyBlock(mpb);
    }

    // Clears the normal map by applying a fully-flat texture.
    // Sets adjacency mask to 15 (fully surrounded = no jagged clipping).
    public static void Clear(SpriteRenderer sr) {
        EnsureCache();
        var mpb = new MaterialPropertyBlock();
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(NormalMapID, flatTex);
        mpb.SetFloat(AdjacencyMaskID, 15f);
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
        for (int y = 0; y < SIZE; y++) {
            for (int x = 0; x < SIZE; x++) {
                // An edge is "open" if it's on the texture border AND the neighbour in that
                // direction is absent (not solid), so it deserves an outward-facing normal.
                bool eL = x == 0      && !hasLeft;
                bool eR = x == SIZE-1 && !hasRight;
                bool eD = y == 0      && !hasDown;
                bool eU = y == SIZE-1 && !hasUp;

                float nx = (eR ? 1f : 0f) - (eL ? 1f : 0f);
                float ny = (eU ? 1f : 0f) - (eD ? 1f : 0f);
                float nz = BEVEL_Z;

                float len = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 0f) { nx /= len; ny /= len; nz /= len; }
                else          { nz = 1f; }

                // ── Edge depth for light penetration ──────────────────────
                // Distance to nearest exposed edge or corner in pixels.
                float minDist = SIZE; // large default = deep interior
                if (!hasLeft)  minDist = Mathf.Min(minDist, x);
                if (!hasRight) minDist = Mathf.Min(minDist, SIZE - 1 - x);
                if (!hasDown)  minDist = Mathf.Min(minDist, y);
                if (!hasUp)    minDist = Mathf.Min(minDist, SIZE - 1 - y);

                // Diagonal openings: light enters from corner when the diagonal
                // neighbour is absent AND both adjacent cardinals are solid (otherwise
                // the cardinal edge is already closer). Euclidean distance gives a
                // round quarter-circle falloff at each corner.
                if (!hasBL && hasLeft && hasDown)
                    minDist = Mathf.Min(minDist, Mathf.Sqrt(x * x + y * y));
                if (!hasBR && hasRight && hasDown)
                    minDist = Mathf.Min(minDist, Mathf.Sqrt((SIZE-1-x)*(SIZE-1-x) + y*y));
                if (!hasTL && hasLeft && hasUp)
                    minDist = Mathf.Min(minDist, Mathf.Sqrt(x*x + (SIZE-1-y)*(SIZE-1-y)));
                if (!hasTR && hasRight && hasUp)
                    minDist = Mathf.Min(minDist, Mathf.Sqrt((SIZE-1-x)*(SIZE-1-x) + (SIZE-1-y)*(SIZE-1-y)));

                // Smoothstep falloff: 1.0 at edge, 0.0 at DEPTH_PX deep.
                float t = Mathf.Clamp01(1f - minDist / DEPTH_PX);
                float edgeDepth = t * t * (3f - 2f * t); // smoothstep

                pixels[y * SIZE + x] = new Color32(
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

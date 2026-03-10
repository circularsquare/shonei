using UnityEngine;

// Generates and caches 16 normal map variants for solid 16x16 tiles.
// Each variant corresponds to a 4-bit adjacency mask describing which
// orthogonal neighbours are solid, so touching edges are kept flat.
//
// Mask bits:  0=left  1=right  2=down  3=up
//
// Usage: TileNormalMaps.Apply(spriteRenderer, mask)  — solid tiles
//        TileNormalMaps.Clear(spriteRenderer)         — non-solid tiles
public static class TileNormalMaps {
    const int   SIZE    = 16;
    const float BEVEL_Z = 1f;
    static readonly int NormalMapID = Shader.PropertyToID("_NormalMap");

    // 16 variants + 1 fully-flat fallback used for non-solid tiles
    static Texture2D[] cache;
    static Texture2D   flatTex;

    public static void Apply(SpriteRenderer sr, int mask) {
        EnsureCache();
        SetNormalMap(sr, cache[mask & 0xF]);
    }

    // Clears the normal map by applying a fully-flat texture.
    public static void Clear(SpriteRenderer sr) {
        EnsureCache();
        SetNormalMap(sr, flatTex);
    }

    static void EnsureCache() {
        if (cache != null) return;
        cache = new Texture2D[16];
        for (int mask = 0; mask < 16; mask++)
            cache[mask] = Generate(mask);
        flatTex = Generate(0xF); // all neighbours present = no exposed edges
    }

    static void SetNormalMap(SpriteRenderer sr, Texture2D tex) {
        var mpb = new MaterialPropertyBlock();
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(NormalMapID, tex);
        sr.SetPropertyBlock(mpb);
    }

    static Texture2D Generate(int mask) {
        bool hasLeft  = (mask & 1) != 0;
        bool hasRight = (mask & 2) != 0;
        bool hasDown  = (mask & 4) != 0;
        bool hasUp    = (mask & 8) != 0;

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

                pixels[y * SIZE + x] = new Color32(
                    (byte)(nx * 127.5f + 128f),
                    (byte)(ny * 127.5f + 128f),
                    (byte)(nz * 127.5f + 128f),
                    255
                );
            }
        }

        var tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }
}

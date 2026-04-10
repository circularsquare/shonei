using System.Collections.Generic;
using UnityEngine;

// Bakes 20×20 tile sprites at load time from 32×32 border atlases.
// Each tile type gets 16 variants (one per 4-bit cardinal adjacency mask).
// The 20×20 sprite has a 16×16 interior at (2,2)–(17,17) plus 2px border
// overhang on each side. Borders are shown on edges adjacent to air;
// masked out (transparent/interior) where the neighbor is solid.
//
// Atlas source format (32×32, in image coords Y=0 at top):
//   TL(0,0)  | sep | Top(8,0)    | sep | TR(28,0)
//   sep row  |     |             |     |
//   Left(0,8)| sep | Main(8,8)   | sep | Right(28,8)
//   sep row  |     |             |     |
//   BL(0,28) | sep | Bot(8,28)   | sep | BR(28,28)
//
// Usage: TileSpriteCache.Get("dirt", cardinalMask) → Sprite
// Mask bits: 0=left  1=right  2=down  3=up  (same as TileNormalMaps)
public static class TileSpriteCache {
    const int TILE = 16;  // interior tile size in pixels
    const int BORDER = 2; // overhang pixels per side
    const int SIZE = TILE + BORDER * 2; // 20

    // Atlas piece origins (32×32, texture coords Y=0 at bottom)
    // Image Y is flipped: image row 0 → texture row 31.
    const int ATLAS = 32;

    static Dictionary<string, Sprite[]> cache = new();
    static Sprite[] fallbackSprites;

    public static Sprite Get(string tileName, int cardinalMask) {
        if (!cache.TryGetValue(tileName, out var variants)) {
            variants = BuildVariants(tileName);
            cache[tileName] = variants;
        }
        return variants[cardinalMask & 0xF];
    }

    // ── Build 16 variants from a tile type's atlas ────────────────────
    static Sprite[] BuildVariants(string tileName) {
        var atlas = Resources.Load<Texture2D>("Sprites/Tiles/Sheets/" + tileName);

        if (atlas == null || !atlas.isReadable) {
            if (atlas != null && !atlas.isReadable)
                Debug.LogError($"TileSpriteCache: atlas '{tileName}' is not readable. Enable Read/Write in import settings.");
            return BuildFallbackVariants(tileName);
        }

        // Read atlas pixels (texture coords: Y=0 at bottom)
        var atlasPixels = atlas.GetPixels32();

        // Extract the 16×16 main interior from atlas center
        var mainPixels = new Color32[TILE * TILE];
        CopyRegion(atlasPixels, ATLAS, 8, 8, mainPixels, TILE, 0, 0, TILE, TILE);

        // Compose the "base" 20×20 (mask=0, all borders visible)
        var basePixels = new Color32[SIZE * SIZE];
        // 1. Paste main at (2,2)
        CopyRegion(atlasPixels, ATLAS, 8, 8, basePixels, SIZE, BORDER, BORDER, TILE, TILE);
        // 2. Overlay borders (inner 2px overwrite main edges, outer 2px are overhang)
        // Each border strip is 4px: placed so inner half overlaps interior, outer half is overhang.
        CopyRegion(atlasPixels, ATLAS, 8, 28, basePixels, SIZE, BORDER, TILE, TILE, 4); // top: rows 16-19
        CopyRegion(atlasPixels, ATLAS, 8,  0, basePixels, SIZE, BORDER, 0,    TILE, 4); // bottom: rows 0-3
        CopyRegion(atlasPixels, ATLAS, 0,  8, basePixels, SIZE, 0,      BORDER, 4, TILE); // left: cols 0-3
        CopyRegion(atlasPixels, ATLAS, 28, 8, basePixels, SIZE, TILE,   BORDER, 4, TILE); // right: cols 16-19
        // 3. Overlay corners (4×4 each)
        CopyRegion(atlasPixels, ATLAS, 0,  28, basePixels, SIZE, 0,    TILE, 4, 4); // TL
        CopyRegion(atlasPixels, ATLAS, 28, 28, basePixels, SIZE, TILE, TILE, 4, 4); // TR
        CopyRegion(atlasPixels, ATLAS, 0,   0, basePixels, SIZE, 0,    0,    4, 4); // BL
        CopyRegion(atlasPixels, ATLAS, 28,  0, basePixels, SIZE, TILE, 0,    4, 4); // BR

        var variants = new Sprite[16];
        for (int mask = 0; mask < 16; mask++)
            variants[mask] = BakeVariant(basePixels, mainPixels, mask);

        return variants;
    }

    // For tiles without a 32×32 atlas: center the base sprite in a 20×20
    static Sprite[] BuildFallbackVariants(string tileName) {
        if (fallbackSprites != null) return fallbackSprites;

        var baseTex = Resources.Load<Texture2D>("Sprites/Tiles/" + tileName);
        var mainPixels = new Color32[TILE * TILE];

        if (baseTex != null && baseTex.isReadable) {
            var srcPixels = baseTex.GetPixels32();
            // Copy what fits (may be smaller/larger than 16×16)
            int w = Mathf.Min(baseTex.width, TILE);
            int h = Mathf.Min(baseTex.height, TILE);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    mainPixels[y * TILE + x] = srcPixels[y * baseTex.width + x];
        } else {
            // Solid magenta fallback so it's obvious
            for (int i = 0; i < mainPixels.Length; i++)
                mainPixels[i] = new Color32(255, 0, 255, 255);
        }

        // Base is just main centered with transparent borders
        var basePixels = new Color32[SIZE * SIZE]; // default = transparent
        for (int y = 0; y < TILE; y++)
            for (int x = 0; x < TILE; x++)
                basePixels[(y + BORDER) * SIZE + (x + BORDER)] = mainPixels[y * TILE + x];

        fallbackSprites = new Sprite[16];
        for (int mask = 0; mask < 16; mask++)
            fallbackSprites[mask] = BakeVariant(basePixels, mainPixels, mask);

        return fallbackSprites;
    }

    // ── Mask a base 20×20 for a specific cardinal adjacency ───────────
    static Sprite BakeVariant(Color32[] basePixels, Color32[] mainPixels, int mask) {
        bool hasLeft  = (mask & 1) != 0;
        bool hasRight = (mask & 2) != 0;
        bool hasDown  = (mask & 4) != 0;
        bool hasUp    = (mask & 8) != 0;

        var pixels = (Color32[])basePixels.Clone();

        // Mask out border regions where the neighbor is solid.
        // Outer pixels (outside 16×16 interior) → transparent.
        // Inner pixels (overlapping interior) → main texture.
        for (int y = 0; y < SIZE; y++) {
            for (int x = 0; x < SIZE; x++) {
                bool inLeftZone  = x < BORDER + BORDER; // cols 0-3
                bool inRightZone = x >= TILE;            // cols 16-19
                bool inBotZone   = y < BORDER + BORDER;  // rows 0-3
                bool inTopZone   = y >= TILE;             // rows 16-19

                bool shouldMask = (hasLeft && inLeftZone) ||
                                  (hasRight && inRightZone) ||
                                  (hasDown && inBotZone) ||
                                  (hasUp && inTopZone);

                if (!shouldMask) continue;

                bool isInterior = x >= BORDER && x < BORDER + TILE &&
                                  y >= BORDER && y < BORDER + TILE;

                if (isInterior) {
                    pixels[y * SIZE + x] = mainPixels[(y - BORDER) * TILE + (x - BORDER)];
                } else {
                    pixels[y * SIZE + x] = new Color32(0, 0, 0, 0);
                }
            }
        }

        var tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };
        tex.SetPixels32(pixels);
        tex.Apply();

        // PPU=16 → 20/16 = 1.25 units. Pivot at center.
        return Sprite.Create(tex, new Rect(0, 0, SIZE, SIZE),
            new Vector2(0.5f, 0.5f), 16f);
    }

    // ── Pixel copy utility ────────────────────────────────────────────
    // Copies a rectangular region between two pixel arrays (both Y=0 at bottom).
    static void CopyRegion(
        Color32[] src, int srcStride, int srcX, int srcY,
        Color32[] dst, int dstStride, int dstX, int dstY,
        int width, int height) {
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                dst[(dstY + y) * dstStride + (dstX + x)] =
                    src[(srcY + y) * srcStride + (srcX + x)];
    }
}

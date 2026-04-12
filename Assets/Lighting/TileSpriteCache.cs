using System.Collections.Generic;
using UnityEngine;

// Bakes 20×20 tile sprites at load time from 32×32 border atlases.
// Each tile type gets 16 variants, one per 4-bit cardinal adjacency mask.
// The 20×20 sprite has a 16×16 interior at (2,2)–(17,17) plus a 2px overhang
// on each side so adjacent tiles can straddle their borders into each other.
//
// For every output pixel we pick which atlas piece provides its colour based
// on adjacency. The key subtlety is the 4×4 corner region (e.g. top-left):
// multiple pieces can cover it, and priority depends on the mask —
//   - Corner piece: shown iff BOTH neighbouring edges are exposed.
//   - Top/bottom border: extends through the inner corner cols (2-3, 16-17)
//     when only the vertical edge is exposed. This is what makes a long
//     horizontal row of tiles render a continuous top-grass strip instead of
//     dropping out in the 4px overlap at each tile boundary.
//   - Left/right border: symmetric, covers inner corner rows (2-3, 16-17).
//   - Main interior: fallback inside (2-17, 2-17) when the edge is hidden.
//   - Transparent: elsewhere (outer overhang when the edge is hidden).
//
// Transparent pixels in the atlas border/corner pieces bite into the sprite
// intentionally — neighbouring tiles' overhang fills the gap.
//
// Atlas source format (32×32, in image coords Y=0 at top):
//   TL(0,0)   | sep | Top(8,0)    | sep | TR(28,0)
//   sep row   |     |             |     |
//   Left(0,8) | sep | Main(8,8)   | sep | Right(28,8)
//   sep row   |     |             |     |
//   BL(0,28)  | sep | Bot(8,28)   | sep | BR(28,28)
//
// Usage: TileSpriteCache.Get("dirt", cardinalMask) → Sprite
// Mask bits: 0=left  1=right  2=down  3=up  (same as TileNormalMaps)
public static class TileSpriteCache {
    const int TILE = 16;                // main interior size in pixels
    const int BORDER = 2;               // overhang pixels per side
    const int SIZE = TILE + BORDER * 2; // 20
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

    // ── Atlas-based build ─────────────────────────────────────────────
    static Sprite[] BuildVariants(string tileName) {
        var atlas = Resources.Load<Texture2D>("Sprites/Tiles/Sheets/" + tileName);

        if (atlas == null || !atlas.isReadable) {
            if (atlas != null && !atlas.isReadable)
                Debug.LogError($"TileSpriteCache: atlas '{tileName}' is not readable. Enable Read/Write in import settings.");
            return BuildFallbackVariants(tileName);
        }

        var atlasPixels = atlas.GetPixels32();

        // Main 16×16 — used where a border is hidden by an adjacent solid tile.
        var mainPixels = new Color32[TILE * TILE];
        for (int y = 0; y < TILE; y++)
            for (int x = 0; x < TILE; x++)
                mainPixels[y * TILE + x] = atlasPixels[(y + 8) * ATLAS + (x + 8)];

        var variants = new Sprite[16];
        for (int mask = 0; mask < 16; mask++)
            variants[mask] = BakeVariant(atlasPixels, mainPixels, mask);

        return variants;
    }

    // Bake a single variant pixel-by-pixel, sampling from whichever atlas
    // piece should provide the colour given this mask.
    //
    // Atlas offsets (sprite-coord → atlas-coord):
    //   left strip  x ∈ [0,3]   → atlas x = sprite x
    //   top/bot/main x ∈ [2,17] → atlas x = sprite x + 6
    //   right strip x ∈ [16,19] → atlas x = sprite x + 12
    // (same pattern on y)
    static Sprite BakeVariant(Color32[] atlas, Color32[] mainPixels, int mask) {
        bool hasL = (mask & 1) != 0;
        bool hasR = (mask & 2) != 0;
        bool hasD = (mask & 4) != 0;
        bool hasU = (mask & 8) != 0;

        var pixels = new Color32[SIZE * SIZE];
        var clear = new Color32(0, 0, 0, 0);

        for (int y = 0; y < SIZE; y++) {
            for (int x = 0; x < SIZE; x++) {
                // Which 4px strips is this pixel in?
                bool inTop   = y >= TILE;               // rows 16-19
                bool inBot   = y < BORDER + BORDER;     // rows 0-3
                bool inLeft  = x < BORDER + BORDER;     // cols 0-3
                bool inRight = x >= TILE;               // cols 16-19

                // Outer overhang (beyond the 16×16 interior).
                bool outerH = x < BORDER || x >= TILE + BORDER;
                bool outerV = y < BORDER || y >= TILE + BORDER;
                bool inInterior = !outerH && !outerV;   // cols 2-17 × rows 2-17

                bool vHide = (inTop && hasU) || (inBot && hasD);
                bool hHide = (inLeft && hasL) || (inRight && hasR);

                bool inV = inTop || inBot;
                bool inH = inLeft || inRight;

                Color32 c;
                if (inV && inH) {
                    // Corner region (4×4): up to four pieces can cover parts of it.
                    if (!vHide && !hHide) {
                        // Corner piece wins when both edges are exposed.
                        int ax = inLeft ? x : x + 12;
                        int ay = inBot  ? y : y + 12;
                        c = atlas[ay * ATLAS + ax];
                    } else if (!vHide && !outerH) {
                        // Top/bot border extends through inner cols (2-3 or 16-17).
                        int ax = x + 6;
                        int ay = inBot ? y : y + 12;
                        c = atlas[ay * ATLAS + ax];
                    } else if (!hHide && !outerV) {
                        // Left/right border extends through inner rows (2-3 or 16-17).
                        int ax = inLeft ? x : x + 12;
                        int ay = y + 6;
                        c = atlas[ay * ATLAS + ax];
                    } else if (inInterior) {
                        c = mainPixels[(y - BORDER) * TILE + (x - BORDER)];
                    } else {
                        c = clear;
                    }
                } else if (inV) {
                    // Pure top or bottom zone (x ∈ [4, 15]).
                    if (!vHide) {
                        int ax = x + 6;
                        int ay = inBot ? y : y + 12;
                        c = atlas[ay * ATLAS + ax];
                    } else if (inInterior) {
                        c = mainPixels[(y - BORDER) * TILE + (x - BORDER)];
                    } else {
                        c = clear;
                    }
                } else if (inH) {
                    // Pure left or right zone (y ∈ [4, 15]).
                    if (!hHide) {
                        int ax = inLeft ? x : x + 12;
                        int ay = y + 6;
                        c = atlas[ay * ATLAS + ax];
                    } else if (inInterior) {
                        c = mainPixels[(y - BORDER) * TILE + (x - BORDER)];
                    } else {
                        c = clear;
                    }
                } else {
                    // Pure interior (cols 4-15 × rows 4-15).
                    c = mainPixels[(y - BORDER) * TILE + (x - BORDER)];
                }

                pixels[y * SIZE + x] = c;
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

    // ── Fallback (no atlas) ───────────────────────────────────────────
    // Tiles without a 32×32 atlas just display the plain 16×16 sprite
    // centred in a 20×20 canvas. No edge art → all 16 variants identical.
    static Sprite[] BuildFallbackVariants(string tileName) {
        if (fallbackSprites != null) return fallbackSprites;

        var baseTex = Resources.Load<Texture2D>("Sprites/Tiles/" + tileName);
        var pixels = new Color32[SIZE * SIZE];

        if (baseTex != null && baseTex.isReadable) {
            var srcPixels = baseTex.GetPixels32();
            int w = Mathf.Min(baseTex.width, TILE);
            int h = Mathf.Min(baseTex.height, TILE);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    pixels[(y + BORDER) * SIZE + (x + BORDER)] =
                        srcPixels[y * baseTex.width + x];
        } else {
            // Magenta so a missing texture is obvious.
            for (int y = 0; y < TILE; y++)
                for (int x = 0; x < TILE; x++)
                    pixels[(y + BORDER) * SIZE + (x + BORDER)] =
                        new Color32(255, 0, 255, 255);
        }

        var tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, SIZE, SIZE),
            new Vector2(0.5f, 0.5f), 16f);

        fallbackSprites = new Sprite[16];
        for (int i = 0; i < 16; i++) fallbackSprites[i] = sprite;
        return fallbackSprites;
    }
}

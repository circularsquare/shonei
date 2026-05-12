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
// Variants: a tile type may ship multiple artist-authored textures named
// <tileName>, <tileName>2, <tileName>3, … (either atlases in Sheets/ or flat
// sprites in Tiles/). All found variants are baked, and each world tile picks
// one deterministically from its (x,y) — stable across re-renders and loads.
//
// ── Normal maps ──────────────────────────────────────────────────────────
// Each baked 20×20 sprite ships with a matching 20×20 normal map derived
// from the sprite's own alpha. RGB encodes a world-facing bevel normal
// (outward at any alpha boundary); A encodes edge-distance falloff for
// LightComposite's underground-darkening fade. See BakeNormalMap.
//
// Sprites are keyed by the 4-bit cardinal mask (16 variants). Normal maps
// are keyed by an 8-bit mask (cardinals + diagonals, 256 variants). The
// diagonal bits affect both channels of the baked normal map: the RGB
// bevel uses a Sobel kernel (cardinals + diagonals), so an inside-corner
// tile (e.g. solid L + solid D + empty BL diagonal) gets a slight NE tilt
// on the interior corner pixel; and the alpha distance transform reaches
// through the transparent BL overhang into the interior corner, so light
// also penetrates via the edge-depth fade.
//
// A single flat normal map (FlatNormalMap) is exposed for non-solid tiles.
//
// Usage:
//   TileSpriteCache.Get(name, cardinalMask, x, y)         → Sprite (tile body)
//   TileSpriteCache.Get(name, cMask, trimMask, x, y)      → Sprite (body, inner-pixel trim variant)
//   TileSpriteCache.GetOverlay(name, cardinalMask, x, y)  → Sprite (overlay atlas, e.g. grass)
//   TileSpriteCache.GetNormalMap(name, mask8, x, y)       → Texture2D
//   TileSpriteCache.FlatNormalMap                         → Texture2D
// Mask bits: 0=L 1=R 2=D 3=U 4=BL 5=BR 6=TL 7=TR
//
// ── Chunked-mesh API (Texture2DArray slice indices) ──────────────────────
// Parallel to the per-Sprite/Texture2D outputs above, the cache also exposes
// the same baked content as slices of per-type Texture2DArrays so a chunked
// tile mesh can bind one array per type/overlay and address tiles via a
// per-vertex slice index. The arrays are populated lazily on first access:
//
//   int slice  = TileSpriteCache.GetBodySlice(name, cMask, trimMask, x, y);
//   int slice  = TileSpriteCache.GetNormalMapSlice(name, mask8, x, y);
//   int slice  = TileSpriteCache.GetOverlaySlice(overlayName, cMask, x, y);
//   var array  = TileSpriteCache.GetBodyArray(name);          // Texture2DArray
//   var array  = TileSpriteCache.GetNormalMapArray(name);     // Texture2DArray
//   var array  = TileSpriteCache.GetOverlayArray(overlayName);// Texture2DArray
//
// Slice indexing (deterministic, computed without a lookup table):
//   Body:    slot = cMask + 16 * trimMask + 256 * variantIdx     (depth 256 × N)
//   Normals: slot = mask8 + 256 * variantIdx                     (depth 256 × N)
//   Overlay: slot = cMask + 16 * variantIdx                      (depth 16 × N)
//
// Trim slices are baked on demand — uploading a new trim mask copies its
// 16 cMask sprites for every variant into the existing body array. Normals
// are uploaded once eagerly when the type's bundle is first allocated
// (small fixed cost — 256 × N slices).
//
// Slices are populated via Graphics.CopyTexture, GPU-side, so no GetPixels
// readback or main-thread stalls.
//
// ── Overlay atlases ──────────────────────────────────────────────────────
// Overlays (grass, future moss/snow/…) reuse the same 9-piece atlas layout
// but their Main interior is conceptually "no decoration here" — bits set in
// the inverted-cardinal mask should resolve to transparent, so the tile body
// underneath shows through unmodified. We *force* this by zeroing Main during
// the overlay bake regardless of what the artist authored: a stray opaque
// Main pixel would otherwise overwrite the body's bevelled edge piece on
// non-decorated sides (e.g. a flat dirt-brown band along the underside of a
// tile that only has side grass). Overlays also skip the 256-entry normal-
// map bake — overlay sprites point their `_NormalMap` at the body's normal
// map, never their own (see WorldController.OnTileOverlayChanged).
public static class TileSpriteCache {
    const int TILE = 16;                // main interior size in pixels
    const int BORDER = 2;               // overhang pixels per side
    const int SIZE = TILE + BORDER * 2; // 20
    const int ATLAS = 32;

    // Sobel-style bevel: cardinal neighbours contribute ±1 to the outward
    // normal, diagonals contribute ±BevelDiagWeight to both axes. Widens the
    // 1-pixel bevel ring so pixels only diagonally adjacent to empty space
    // also catch a gentle outward tilt (~1.5px-wide effective bevel). Lower
    // = closer to pure-cardinal (flatter 2nd ring), higher = softer/wider.
    const float BevelDiagWeight = 0.5f;

    // One artist-authored texture → one Variant. sprites is indexed by the
    // 4-bit cardinal mask (16); normals is indexed by the 8-bit mask (256,
    // cardinal | diagonal) because diagonal openings change the edge-depth
    // alpha of inside-corner pixels.
    struct Variant {
        public Sprite[]    sprites; // 16
        public Texture2D[] normals; // 256
    }

    // Outer array = variant index (0 = base, 1 = *2 sheet, …). Inner = mask.
    static Dictionary<string, Variant[]> cache = new();
    // Edge-trimmed sprite cache, keyed by (tileName, 4-bit trimMask). The sprite
    // is identical to the standard cache's entry except the inner 2 pixels of
    // each trim side (cols 2-3 / 16-17 for L/R, rows 2-3 / 16-17 for D/U) are
    // cleared to alpha 0. Used to "shave off" the body's Main extension on
    // sides where an overlay (e.g. grass) replaces the dirt edge — see
    // WorldController.ApplyTileNormalMap. trimMask=0 falls through to `cache`.
    static Dictionary<(string, int), Variant[]> trimCache = new();
    // Overlay sprite cache. Same atlas format and mask scheme as `cache`, but
    // baked with the Main 16×16 region forced transparent — see the §Overlay
    // atlases comment at the top of the file.
    static Dictionary<string, Variant[]> overlayCache = new();
    static Texture2D flatNormalMap;

    // ── Chunked-mesh Texture2DArray bundles ──────────────────────────────
    // One bundle per body tile type — owns the per-type body sprite array
    // (16 cMask × 16 trim × N variant slices) and normal-map array (256 × N).
    // Trim slice uploads are deferred; trim=0 + all normals upload eagerly
    // when the bundle is created (matches the existing eager-trim=0 bake).
    class TypeArrayBundle {
        public int             variantCount;
        public Texture2DArray  bodyArray;
        public Texture2DArray  normalArray;
        public HashSet<int>    bakedTrimMasks; // entries in [0..15] that have been uploaded
    }
    static Dictionary<string, TypeArrayBundle> typeArrayBundles = new();

    // One bundle per overlay name. Overlay normals are never sampled (overlay
    // SRs point _NormalMap at the body's normal map), so we don't carry a
    // normal-map array for overlays.
    class OverlayArrayBundle {
        public int             variantCount;
        public Texture2DArray  overlayArray;
    }
    static Dictionary<string, OverlayArrayBundle> overlayArrayBundles = new();

    // Reload-Domain-off support: Sprites and Texture2Ds in these caches get destroyed
    // by Unity when the previous play session ends, but the dictionary entries persist.
    // On the next play press, EnsureVariants finds a cached entry and returns dead
    // Unity Object refs, so tiles render blank with no error. Clear on entry.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() {
        cache.Clear();
        trimCache.Clear();
        overlayCache.Clear();
        flatNormalMap = null;
        typeArrayBundles.Clear();
        overlayArrayBundles.Clear();
    }

    public static Texture2D FlatNormalMap {
        get {
            if (flatNormalMap == null) flatNormalMap = BuildFlatNormalMap();
            return flatNormalMap;
        }
    }

    public static Sprite Get(string tileName, int cardinalMask, int x, int y) {
        var variants = EnsureVariants(tileName);
        int v = PickVariant(x, y, variants.Length);
        return variants[v].sprites[cardinalMask & 0xF];
    }

    // Variant of Get whose 16×16 Main interior has its inner 2 pixels cleared
    // on each side flagged in trimMask (LRDU bit layout, same as cardinalMask).
    // Used by the tile body when an overlay (grass on dirt) replaces an edge
    // piece — without this, the body's Main extension to col 17 / row 17 leaves
    // a straight silhouette poking out through transparent gaps in the grass.
    // trimMask=0 falls through to standard Get().
    public static Sprite Get(string tileName, int cardinalMask, int trimMask, int x, int y) {
        if ((trimMask & 0xF) == 0) return Get(tileName, cardinalMask, x, y);
        var variants = EnsureTrimmedVariants(tileName, trimMask & 0xF);
        int v = PickVariant(x, y, variants.Length);
        return variants[v].sprites[cardinalMask & 0xF];
    }

    // Overlay variant of Get. Bakes from the same atlas format as the body,
    // but with the Main 16×16 region forced transparent — so any cardinal bit
    // set in the inverted-cardinal mask resolves to transparent rather than
    // sampling whatever the artist left in Main. See §Overlay atlases at the
    // top of the file.
    public static Sprite GetOverlay(string overlayName, int cardinalMask, int x, int y) {
        var variants = EnsureOverlayVariants(overlayName);
        int v = PickVariant(x, y, variants.Length);
        return variants[v].sprites[cardinalMask & 0xF];
    }

    public static Texture2D GetNormalMap(string tileName, int mask, int x, int y) {
        var variants = EnsureVariants(tileName);
        int v = PickVariant(x, y, variants.Length);
        return variants[v].normals[mask & 0xFF];
    }

    // ── Chunked-mesh slice API ────────────────────────────────────────
    // Returns the slice index in GetBodyArray(tileName) corresponding to the
    // sprite that Get(tileName, cMask, trimMask, x, y) would return. Triggers
    // a lazy upload of the trim mask's 16 cMask × N variant sprites on first
    // request — subsequent calls with the same trim mask are O(1).
    public static int GetBodySlice(string tileName, int cardinalMask, int trimMask, int x, int y) {
        var bundle = EnsureTypeArrayBundle(tileName);
        if ((trimMask & 0xF) != 0 && !bundle.bakedTrimMasks.Contains(trimMask & 0xF))
            UploadBodyTrimSlices(tileName, bundle, trimMask & 0xF);
        int v = PickVariant(x, y, bundle.variantCount);
        return (cardinalMask & 0xF) + 16 * (trimMask & 0xF) + 256 * v;
    }

    // Returns the slice index in GetNormalMapArray(tileName) corresponding to
    // the texture that GetNormalMap(tileName, mask8, x, y) would return.
    public static int GetNormalMapSlice(string tileName, int mask8, int x, int y) {
        var bundle = EnsureTypeArrayBundle(tileName);
        int v = PickVariant(x, y, bundle.variantCount);
        return (mask8 & 0xFF) + 256 * v;
    }

    // Returns the slice index in GetOverlayArray(overlayName) corresponding
    // to the sprite that GetOverlay(overlayName, cMask, x, y) would return.
    public static int GetOverlaySlice(string overlayName, int cardinalMask, int x, int y) {
        var bundle = EnsureOverlayArrayBundle(overlayName);
        int v = PickVariant(x, y, bundle.variantCount);
        return (cardinalMask & 0xF) + 16 * v;
    }

    public static Texture2DArray GetBodyArray(string tileName) {
        return EnsureTypeArrayBundle(tileName).bodyArray;
    }

    public static Texture2DArray GetNormalMapArray(string tileName) {
        return EnsureTypeArrayBundle(tileName).normalArray;
    }

    public static Texture2DArray GetOverlayArray(string overlayName) {
        return EnsureOverlayArrayBundle(overlayName).overlayArray;
    }

#if UNITY_EDITOR
    // ── Editor-side bake (for offline precomputation, saved as .asset) ──
    // Builds the same Texture2DArrays the runtime path produces, but uploads
    // pixel data via SetPixels32 + Apply(makeNoLongerReadable=false) so the
    // resulting array survives `AssetDatabase.CreateAsset` serialization. The
    // runtime fallback path uses Graphics.CopyTexture (GPU-only, doesn't
    // persist) — that's fine because runtime arrays are throwaway, but the
    // editor bake needs CPU-side data for the disk write to be meaningful.
    //
    // All 16 trim masks are baked eagerly (the runtime path lazy-bakes them
    // on demand). The depth budget is `256 × N_variants` either way; here we
    // just populate every slot rather than leaving 15/16 of them empty.
    public static (Texture2DArray body, Texture2DArray normal) BakeTypeArraysForEditor(string tileName) {
        var baseVariants = EnsureVariants(tileName);
        int n = baseVariants.Length;
        for (int trim = 1; trim < 16; trim++) EnsureTrimmedVariants(tileName, trim);

        var bodyArr   = new Texture2DArray(SIZE, SIZE, 256 * n, TextureFormat.RGBA32, mipChain: false) {
            filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
        };
        var normalArr = new Texture2DArray(SIZE, SIZE, 256 * n, TextureFormat.RGBA32, mipChain: false) {
            filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
        };

        // Body slots: trim=0 from the base variants, trim>0 from the trim cache.
        for (int v = 0; v < n; v++)
            for (int c = 0; c < 16; c++) {
                int slot = c + 256 * v;
                bodyArr.SetPixels32(baseVariants[v].sprites[c].texture.GetPixels32(), slot);
            }
        for (int trim = 1; trim < 16; trim++) {
            var trimVariants = trimCache[(tileName, trim)];
            for (int v = 0; v < n; v++)
                for (int c = 0; c < 16; c++) {
                    int slot = c + 16 * trim + 256 * v;
                    bodyArr.SetPixels32(trimVariants[v].sprites[c].texture.GetPixels32(), slot);
                }
        }
        bodyArr.Apply(updateMipmaps: false, makeNoLongerReadable: false);

        // Normal slots: all 256 masks × N variants from the base bake.
        for (int v = 0; v < n; v++)
            for (int m = 0; m < 256; m++) {
                int slot = m + 256 * v;
                normalArr.SetPixels32(baseVariants[v].normals[m].GetPixels32(), slot);
            }
        normalArr.Apply(updateMipmaps: false, makeNoLongerReadable: false);

        return (bodyArr, normalArr);
    }

    public static Texture2DArray BakeOverlayArrayForEditor(string overlayName) {
        var variants = EnsureOverlayVariants(overlayName);
        int n = variants.Length;
        var arr = new Texture2DArray(SIZE, SIZE, 16 * n, TextureFormat.RGBA32, mipChain: false) {
            filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
        };
        for (int v = 0; v < n; v++)
            for (int c = 0; c < 16; c++) {
                int slot = c + 16 * v;
                arr.SetPixels32(variants[v].sprites[c].texture.GetPixels32(), slot);
            }
        arr.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        return arr;
    }
#endif

    static Variant[] EnsureVariants(string tileName) {
        if (cache.TryGetValue(tileName, out var variants)) return variants;
        variants = BuildVariants(tileName);
        cache[tileName] = variants;
        return variants;
    }

    static Variant[] EnsureTrimmedVariants(string tileName, int trimMask) {
        var key = (tileName, trimMask);
        if (trimCache.TryGetValue(key, out var variants)) return variants;
        variants = BuildVariants(tileName, trimMask);
        trimCache[key] = variants;
        return variants;
    }

    static Variant[] EnsureOverlayVariants(string overlayName) {
        if (overlayCache.TryGetValue(overlayName, out var variants)) return variants;
        variants = BuildVariants(overlayName, trimMask: 0, isOverlay: true);
        overlayCache[overlayName] = variants;
        return variants;
    }

    // Allocates a body+normal Texture2DArray pair for the type on first request.
    // Fast path: try `Resources/BakedTileAtlases/{name}_body.asset` + `_normal.asset`
    // (precomputed offline; see TileAtlasBaker editor tool). Negligible load cost.
    // Fallback path: live bake — uploads (a) trimMask=0 sprites for every variant × cMask
    // and (b) all 256×N normal-map slices on first call, defers trim>0 sprite slices
    // to UploadBodyTrimSlices.
    static TypeArrayBundle EnsureTypeArrayBundle(string tileName) {
        if (typeArrayBundles.TryGetValue(tileName, out var bundle)) return bundle;

        // Fast path: pre-baked .asset files.
        var preBody   = Resources.Load<Texture2DArray>($"BakedTileAtlases/{tileName}_body");
        var preNormal = Resources.Load<Texture2DArray>($"BakedTileAtlases/{tileName}_normal");
        if (preBody != null && preNormal != null) {
            // All 16 trim masks were eagerly baked offline — record them as already
            // present so GetBodySlice short-circuits the lazy upload path.
            var allTrimMasks = new HashSet<int>();
            for (int i = 0; i < 16; i++) allTrimMasks.Add(i);
            bundle = new TypeArrayBundle {
                variantCount   = preBody.depth / 256,
                bodyArray      = preBody,
                normalArray    = preNormal,
                bakedTrimMasks = allTrimMasks,
            };
            typeArrayBundles[tileName] = bundle;
            return bundle;
        }

        var variants = EnsureVariants(tileName);
        int n = variants.Length;

        bundle = new TypeArrayBundle {
            variantCount   = n,
            bodyArray      = NewArray(256 * n),
            normalArray    = NewArray(256 * n),
            bakedTrimMasks = new HashSet<int>(),
        };

        // Body slices for trim=0 (the eager-baked set inside variants[v].sprites).
        for (int v = 0; v < n; v++) {
            for (int c = 0; c < 16; c++) {
                int slot = c + 256 * v;
                Graphics.CopyTexture(variants[v].sprites[c].texture, 0, 0, bundle.bodyArray, slot, 0);
            }
        }
        bundle.bakedTrimMasks.Add(0);

        // Normal-map slices — all 256 masks × N variants, eager. Skipped for
        // overlay-only types (they have no normals baked), but bodies always
        // have all 256 normals available from BakeAtlas/BakeFlat/Magenta.
        for (int v = 0; v < n; v++) {
            for (int m = 0; m < 256; m++) {
                int slot = m + 256 * v;
                Graphics.CopyTexture(variants[v].normals[m], 0, 0, bundle.normalArray, slot, 0);
            }
        }

        typeArrayBundles[tileName] = bundle;
        return bundle;
    }

    // Bakes the trim-mask sprite set (16 cMask × N variants) and copies them
    // into the existing body array at slots [16*trim + 256*v .. +15]. Must be
    // called once per non-zero trim mask before sampling that mask's slices.
    static void UploadBodyTrimSlices(string tileName, TypeArrayBundle bundle, int trimMask) {
        var trimVariants = EnsureTrimmedVariants(tileName, trimMask);
        int n = bundle.variantCount;
        for (int v = 0; v < n; v++) {
            for (int c = 0; c < 16; c++) {
                int slot = c + 16 * trimMask + 256 * v;
                Graphics.CopyTexture(trimVariants[v].sprites[c].texture, 0, 0, bundle.bodyArray, slot, 0);
            }
        }
        bundle.bakedTrimMasks.Add(trimMask);
    }

    static OverlayArrayBundle EnsureOverlayArrayBundle(string overlayName) {
        if (overlayArrayBundles.TryGetValue(overlayName, out var bundle)) return bundle;

        // Fast path: pre-baked .asset.
        var preArr = Resources.Load<Texture2DArray>($"BakedTileAtlases/{overlayName}_overlay");
        if (preArr != null) {
            bundle = new OverlayArrayBundle {
                variantCount = preArr.depth / 16,
                overlayArray = preArr,
            };
            overlayArrayBundles[overlayName] = bundle;
            return bundle;
        }

        // Fallback: live bake.
        var variants = EnsureOverlayVariants(overlayName);
        int n = variants.Length;

        bundle = new OverlayArrayBundle {
            variantCount = n,
            overlayArray = NewArray(16 * n),
        };
        for (int v = 0; v < n; v++) {
            for (int c = 0; c < 16; c++) {
                int slot = c + 16 * v;
                Graphics.CopyTexture(variants[v].sprites[c].texture, 0, 0, bundle.overlayArray, slot, 0);
            }
        }

        overlayArrayBundles[overlayName] = bundle;
        return bundle;
    }

    static Texture2DArray NewArray(int depth) {
        return new Texture2DArray(SIZE, SIZE, depth, TextureFormat.RGBA32, mipChain: false) {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp,
        };
    }

    // Deterministic (x,y) → variant index. Using a cheap integer hash so the
    // same tile always picks the same variant regardless of when it's baked,
    // how many neighbours have changed, or whether the world was just loaded.
    static int PickVariant(int x, int y, int variantCount) {
        if (variantCount <= 1) return 0;
        uint h = (uint)((x * 73856093) ^ (y * 19349663));
        return (int)(h % (uint)variantCount);
    }

    // ── Variant discovery ─────────────────────────────────────────────
    // Try atlases first (Sheets/<name>, Sheets/<name>2, …). If none exist
    // for this tile type, fall back to flat sprites (Tiles/<name>, Tiles/<name>2, …).
    // If neither path has anything, fall back to a magenta error sprite.
    // trimMask>0 produces the same 16 sprites with the inner 2 pixels of each
    // trim side cleared. Normals are NOT re-baked for trim variants — overlay-
    // replaced sides use the body's untrimmed normal map (see EnsureVariants vs.
    // EnsureTrimmedVariants), and at trimmed pixels the sprite is alpha-clipped
    // anyway so NormalsCapture skips them.
    static Variant[] BuildVariants(string tileName, int trimMask = 0, bool isOverlay = false) {
        var atlasVariants = new List<Variant>();
        for (int i = 1; ; i++) {
            string suffix = i == 1 ? "" : i.ToString();
            var atlas = Resources.Load<Texture2D>("Sprites/Tiles/Sheets/" + tileName + suffix);
            if (atlas == null) break;
            if (!atlas.isReadable) {
                Debug.LogError($"TileSpriteCache: atlas '{tileName}{suffix}' is not readable. Enable Read/Write in import settings.");
                continue;
            }
            atlasVariants.Add(BakeAtlas(atlas, trimMask, isOverlay));
        }
        if (atlasVariants.Count > 0) return atlasVariants.ToArray();

        var flatVariants = new List<Variant>();
        for (int i = 1; ; i++) {
            string suffix = i == 1 ? "" : i.ToString();
            var flat = Resources.Load<Texture2D>("Sprites/Tiles/" + tileName + suffix);
            if (flat == null) break;
            if (!flat.isReadable) {
                Debug.LogError($"TileSpriteCache: flat sprite '{tileName}{suffix}' is not readable. Enable Read/Write in import settings.");
                continue;
            }
            flatVariants.Add(BakeFlat(flat));
        }
        if (flatVariants.Count > 0) return flatVariants.ToArray();

        Debug.LogError($"TileSpriteCache: no texture found for tile '{tileName}' in Sprites/Tiles/ or Sprites/Tiles/Sheets/. Using magenta fallback.");
        return new[] { BuildMagentaFallback() };
    }

    // ── Atlas bake (one variant = 16 sprites + 256 normals) ───────────
    // isOverlay=true: zero out Main and skip the 256-entry normal-map bake.
    // Overlay sprites must read transparent on any "buried" side (so the body
    // shows through unmodified), and overlay SRs always sample the body's
    // normal map at runtime — their own normals would never be queried.
    static Variant BakeAtlas(Texture2D atlas, int trimMask = 0, bool isOverlay = false) {
        var atlasPixels = atlas.GetPixels32();

        // Main 16×16 — used where a border is hidden by an adjacent solid tile.
        // Forced transparent for overlays; default `Color32` is (0,0,0,0).
        var mainPixels = new Color32[TILE * TILE];
        if (!isOverlay) {
            for (int y = 0; y < TILE; y++)
                for (int x = 0; x < TILE; x++)
                    mainPixels[y * TILE + x] = atlasPixels[(y + 8) * ATLAS + (x + 8)];
        }

        var v = new Variant {
            sprites = new Sprite[16],
            normals = new Texture2D[256],
        };
        // Bake 16 cardinal sprites first; retain pixel buffers so we can
        // reuse them when baking each of the 16 diagonal sub-variants.
        var spritePixels = new Color32[16][];
        for (int cMask = 0; cMask < 16; cMask++)
            (v.sprites[cMask], spritePixels[cMask]) = BakeSpriteVariant(atlasPixels, mainPixels, cMask, trimMask);
        if (!isOverlay) {
            for (int mask = 0; mask < 256; mask++)
                v.normals[mask] = BakeNormalMap(spritePixels[mask & 0xF], mask);
        }
        return v;
    }

    // Bake a single variant pixel-by-pixel, sampling from whichever atlas
    // piece should provide the colour given this mask.
    //
    // Atlas offsets (sprite-coord → atlas-coord):
    //   left strip  x ∈ [0,3]   → atlas x = sprite x
    //   top/bot/main x ∈ [2,17] → atlas x = sprite x + 6
    //   right strip x ∈ [16,19] → atlas x = sprite x + 12
    // (same pattern on y)
    //
    // trimMask (LRDU bit layout) clears the inner 2 pixels on each flagged
    // side AFTER the standard bake — i.e. cols 2-3 (L) / 16-17 (R), rows 2-3
    // (D) / 16-17 (U) become alpha 0. Used so the body's Main extension stops
    // short of the tile boundary on sides where an overlay (grass) replaces
    // the dirt edge — without it, Main would peek through transparent grass
    // gaps as a straight square silhouette.
    static (Sprite, Color32[]) BakeSpriteVariant(Color32[] atlas, Color32[] mainPixels, int cardinalMask, int trimMask = 0) {
        bool hasL = (cardinalMask & 1) != 0;
        bool hasR = (cardinalMask & 2) != 0;
        bool hasD = (cardinalMask & 4) != 0;
        bool hasU = (cardinalMask & 8) != 0;

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

        // Trim post-pass: clear the inner 2 pixels of each side flagged in
        // trimMask. Mirrors what an edge piece would have done (the edge piece
        // replaces those pixels with jagged art); here we replace them with
        // alpha 0 so the overlay's edge sprite gets sole say at the boundary.
        if ((trimMask & 0xF) != 0) {
            bool trimL = (trimMask & 1) != 0;
            bool trimR = (trimMask & 2) != 0;
            bool trimD = (trimMask & 4) != 0;
            bool trimU = (trimMask & 8) != 0;
            for (int y = 0; y < SIZE; y++) {
                for (int x = 0; x < SIZE; x++) {
                    bool inLeftInner  = trimL && x >= BORDER       && x < BORDER + BORDER;     // cols 2-3
                    bool inRightInner = trimR && x >= TILE         && x < TILE + BORDER;       // cols 16-17
                    bool inBotInner   = trimD && y >= BORDER       && y < BORDER + BORDER;     // rows 2-3
                    bool inTopInner   = trimU && y >= TILE         && y < TILE + BORDER;       // rows 16-17
                    if (inLeftInner || inRightInner || inBotInner || inTopInner)
                        pixels[y * SIZE + x] = clear;
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
        var sprite = Sprite.Create(tex, new Rect(0, 0, SIZE, SIZE),
            new Vector2(0.5f, 0.5f), 16f);
        return (sprite, pixels);
    }

    // ── Flat-sprite bake (no atlas) ───────────────────────────────────
    // Tiles without a 32×32 atlas just display the plain 16×16 sprite
    // centred in a 20×20 canvas. No edge art → 16 mask-indexed sprite
    // entries share one sprite, but the normal maps still differ because
    // their effective-opacity (and thus bevels / edge-depth) depends on
    // which sides are exposed.
    static Variant BakeFlat(Texture2D baseTex) {
        var pixels = new Color32[SIZE * SIZE];
        var srcPixels = baseTex.GetPixels32();
        int w = Mathf.Min(baseTex.width, TILE);
        int h = Mathf.Min(baseTex.height, TILE);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[(y + BORDER) * SIZE + (x + BORDER)] =
                    srcPixels[y * baseTex.width + x];

        var tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, SIZE, SIZE),
            new Vector2(0.5f, 0.5f), 16f);

        var v = new Variant {
            sprites = new Sprite[16],
            normals = new Texture2D[256],
        };
        for (int i = 0; i < 16; i++) v.sprites[i] = sprite;
        // Sprite pixels don't depend on cardinal mask (no atlas art), so
        // all 256 normal maps are baked from the same pixel buffer.
        for (int mask = 0; mask < 256; mask++) v.normals[mask] = BakeNormalMap(pixels, mask);
        return v;
    }

    // Magenta so a missing texture is obvious in-game.
    static Variant BuildMagentaFallback() {
        var pixels = new Color32[SIZE * SIZE];
        var magenta = new Color32(255, 0, 255, 255);
        for (int y = 0; y < TILE; y++)
            for (int x = 0; x < TILE; x++)
                pixels[(y + BORDER) * SIZE + (x + BORDER)] = magenta;

        var tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, SIZE, SIZE),
            new Vector2(0.5f, 0.5f), 16f);

        var v = new Variant {
            sprites = new Sprite[16],
            normals = new Texture2D[256],
        };
        for (int i = 0; i < 16; i++) v.sprites[i] = sprite;
        for (int mask = 0; mask < 256; mask++) v.normals[mask] = BakeNormalMap(pixels, mask);
        return v;
    }

    // ── Normal-map bake (alpha-driven) ────────────────────────────────
    // Derives a 20×20 normal map from the baked sprite's own alpha plus the
    // cardinal adjacency mask:
    //
    //   RGB = bevel normal. Sobel-style combine of the 8 neighbours: each
    //         effectively-transparent cardinal neighbour adds a full outward
    //         unit in that direction, and each transparent diagonal adds
    //         BevelDiagWeight to both axes. Effect: the directly-adjacent
    //         ring gets a full bevel (as before) and the ring one step in
    //         picks up a gentler diagonal tilt — a ~1.5px-wide soft bevel
    //         that lets inside corners and 2nd-row pixels catch grazing
    //         light.
    //
    //   A   = edge-distance falloff, Euclidean-distance transform to the
    //         nearest effectively-transparent pixel, then smoothstep over
    //         LightFeature.penetrationDepth * TILE. Used by LightComposite
    //         to blend tile interiors toward deepAmbient (underground
    //         darkening), completely separate from the RGB bevel.
    //
    // Effective opacity (see EffOpaque below) treats overhang slots owned by
    // a solid neighbour tile as opaque, and out-of-20×20 as transparent —
    // so bevels only form where there's actually a gap to empty space, never
    // at seams between adjacent solid tiles.
    static Texture2D BakeNormalMap(Color32[] pixels, int mask) {
        bool hasL  = (mask & 1)   != 0;
        bool hasR  = (mask & 2)   != 0;
        bool hasD  = (mask & 4)   != 0;
        bool hasU  = (mask & 8)   != 0;
        bool hasBL = (mask & 16)  != 0;
        bool hasBR = (mask & 32)  != 0;
        bool hasTL = (mask & 64)  != 0;
        bool hasTR = (mask & 128) != 0;

        bool EffOpaque(int ox, int oy) {
            // Outside the 20×20 — beyond any overhang. Treated as empty air;
            // only reached when computing bevel for an opaque overhang pixel
            // on the outermost row/col, which only happens when the matching
            // neighbour is absent. Returning false gives that pixel an outward
            // normal pointing away from the tile.
            if (ox < 0 || ox >= SIZE || oy < 0 || oy >= SIZE) return false;

            bool inInterior = ox >= BORDER && ox < BORDER + TILE
                           && oy >= BORDER && oy < BORDER + TILE;
            if (inInterior) return pixels[oy * SIZE + ox].a > 0;

            bool leftCol  = ox <  BORDER;
            bool rightCol = ox >= BORDER + TILE;
            bool botRow   = oy <  BORDER;
            bool topRow   = oy >= BORDER + TILE;

            // 2×2 diagonal corner regions — spatially owned by the diagonal
            // tile, so the diagonal bit (not the adjacent cardinals) decides
            // whether the slot is filled. An inside corner (hasL && hasD &&
            // !hasBL) leaves the BL 2×2 transparent, letting the distance
            // transform reach into the interior corner for edge-depth alpha —
            // same as the old TileNormalMaps !hasBL && hasLeft && hasDown
            // penetration branch.
            if (leftCol  && botRow) return hasBL || pixels[oy * SIZE + ox].a > 0;
            if (rightCol && botRow) return hasBR || pixels[oy * SIZE + ox].a > 0;
            if (leftCol  && topRow) return hasTL || pixels[oy * SIZE + ox].a > 0;
            if (rightCol && topRow) return hasTR || pixels[oy * SIZE + ox].a > 0;

            // Non-corner overhang — owned by the cardinal neighbour. If that
            // neighbour is solid the slot is filled; otherwise fall back to
            // local sprite alpha (opaque for grass-teeth straddling art,
            // transparent for gaps).
            if (leftCol  && hasL) return true;
            if (rightCol && hasR) return true;
            if (botRow   && hasD) return true;
            if (topRow   && hasU) return true;
            return pixels[oy * SIZE + ox].a > 0;
        }

        // Collect all effectively-transparent positions for the distance
        // transform. 20×20 = 400 cells, brute-force lookup is fine at load.
        var transparent = new List<(int x, int y)>(SIZE * SIZE);
        for (int oy = 0; oy < SIZE; oy++)
            for (int ox = 0; ox < SIZE; ox++)
                if (!EffOpaque(ox, oy)) transparent.Add((ox, oy));

        float depthPx = LightFeature.penetrationDepth * TILE;
        var result = new Color32[SIZE * SIZE];

        for (int oy = 0; oy < SIZE; oy++) {
            for (int ox = 0; ox < SIZE; ox++) {
                // Bevel normal — Sobel-style: cardinals contribute ±1,
                // diagonals contribute ±BevelDiagWeight to both axes. Pixels
                // one step in from the bevel ring pick up a gentle tilt
                // toward any diagonally-exposed corner, so inside-corner
                // interiors also catch grazing light.
                float bL  = EffOpaque(ox - 1, oy)     ? 0f : 1f;
                float bR  = EffOpaque(ox + 1, oy)     ? 0f : 1f;
                float bD  = EffOpaque(ox, oy - 1)     ? 0f : 1f;
                float bU  = EffOpaque(ox, oy + 1)     ? 0f : 1f;
                float bBL = EffOpaque(ox - 1, oy - 1) ? 0f : 1f;
                float bBR = EffOpaque(ox + 1, oy - 1) ? 0f : 1f;
                float bTL = EffOpaque(ox - 1, oy + 1) ? 0f : 1f;
                float bTR = EffOpaque(ox + 1, oy + 1) ? 0f : 1f;

                float nx = (bR - bL) + BevelDiagWeight * ((bBR + bTR) - (bBL + bTL));
                float ny = (bU - bD) + BevelDiagWeight * ((bTL + bTR) - (bBL + bBR));
                float nz = 1f;

                float len = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 0f) { nx /= len; ny /= len; nz /= len; }
                else          { nz = 1f; }

                // Edge-depth: nearest effectively-transparent pixel.
                float minDist = depthPx + 1f;
                for (int i = 0; i < transparent.Count; i++) {
                    float dx = ox - transparent[i].x;
                    float dy = oy - transparent[i].y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < minDist * minDist) minDist = Mathf.Sqrt(d2);
                }
                float t = Mathf.Clamp01(1f - minDist / depthPx);
                float edgeDepth = t * t * (3f - 2f * t); // smoothstep

                result[oy * SIZE + ox] = new Color32(
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
        tex.SetPixels32(result);
        tex.Apply();
        return tex;
    }

    // Flat forward normal, alpha 1. Used for non-solid tiles (and as a safe
    // default for any sprite whose _NormalMap gets cleared via this path).
    static Texture2D BuildFlatNormalMap() {
        var pixels = new Color32[SIZE * SIZE];
        var flat = new Color32(128, 128, 255, 255); // (0,0,1) packed + full alpha
        for (int i = 0; i < pixels.Length; i++) pixels[i] = flat;

        var tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }
}

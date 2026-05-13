using UnityEngine;

// Decorative flower / mushroom / moss variant — purely visual, no gameplay
// logic. Each entry in flowersDb.json becomes one FlowerType. FlowerController
// consults the loaded set when picking which variant to spawn on each
// eligible tile.
//
// Sprite + mask lookup is lazy. On first access:
//   - LoadSprite() pulls Resources/Sprites/Plants/Decorative/{name}.png
//   - LoadHeadMask() returns an auto-generated stem/head mask texture if the
//     sprite has any non-green opaque content (treated as a flower head), or
//     null if the whole sprite is green or has only one connected region of
//     "head" colour with negligible area — in which case the variant falls
//     back to single-SR uniform-bend rendering.
//
// Note on scope: flowers deliberately do NOT inherit from Structure or Plant.
// They have no save state, no work orders, no growth, and no per-tile slot
// occupancy. If they ever become harvestable (the user's "maybe later"
// direction), the natural upgrade is to add a parallel `harvestablePlant`
// field that points at a PlantType, and have the harvest path replace the
// decorative flower with a real Plant instance.
public class FlowerType {
    public int id { get; set; }
    public string name { get; set; }
    // Relative spawn weight within the flower set. Higher = more common.
    // Defaults to 1 when missing from JSON so authors can omit it for the
    // common case.
    public float weight { get; set; } = 1f;

    // Where this variant is allowed to spawn. FlowerController defines the
    // valid zones (currently "surface_grass" and "underground"); each
    // variant participates in exactly one. Defaults to surface so existing
    // entries don't need migrating.
    public string placement { get; set; } = "surface_grass";

    // 0..1 scalar attenuating wind sway amplitude. 1 = full sway (default,
    // matches plants); 0 = rigid (mushrooms, moss). Threaded through to the
    // PlantSprite shader as the `_SwayAmount` MPB property — see
    // LightReceiverUtil.SetPlantSwayMPB.
    public float windEffect { get; set; } = 1f;

    // ── Sprite cache ─────────────────────────────────────────────────────
    [Newtonsoft.Json.JsonIgnore] public Sprite cachedSprite;
    [Newtonsoft.Json.JsonIgnore] public bool spriteProbed;

    // ── Head-mask cache ──────────────────────────────────────────────────
    // headMaskTexture: auto-generated greyscale texture, R=255 for head
    //                  pixels, R=0 for stem pixels, A=255 for opaque source
    //                  pixels (transparent everywhere the source was transparent).
    // headCenterY:     world-Y offset (relative to sprite-bottom) of the head
    //                  pixels' centroid. Used by the shader to position the
    //                  head-SR's uniform shift at the right cantilever weight
    //                  (so a head sitting low in the sprite moves less than
    //                  one sitting at the top — physically natural).
    // hasHead:         true iff the auto-detector found enough non-green
    //                  opaque pixels to bother spawning a separate head SR.
    //                  When false, FlowerController falls back to single-SR
    //                  whole-quad bend.
    [Newtonsoft.Json.JsonIgnore] public Texture2D headMaskTexture;
    [Newtonsoft.Json.JsonIgnore] public float headCenterY;
    [Newtonsoft.Json.JsonIgnore] public bool hasHead;
    [Newtonsoft.Json.JsonIgnore] public bool maskProbed;

    // Min fraction of opaque pixels that must classify as "head" for the
    // split-SR path to engage. Below this we treat the variant as all-stem
    // (single SR, regular bend) — keeps moss / pure-green plants out of the
    // two-SR overhead and avoids spawning empty head SRs.
    const float MinHeadFractionToSplit = 0.04f;

    // Lazy single-shot loader. Returns null + logs once when the artwork
    // hasn't been authored yet — callers must null-check and skip the
    // variant rather than treat null as fatal.
    //
    // Flowers live under Sprites/Plants/Decorative/ — the folder is
    // intentionally generic ("decorative" rather than "flowers") so any
    // future non-flower scatter decoration (mushroom tufts, pebbles, small
    // grasses) can share the directory without forcing a rename.
    public Sprite LoadSprite() {
        if (spriteProbed) return cachedSprite;
        spriteProbed = true;
        string clean = name.Replace(" ", "");
        cachedSprite = Resources.Load<Sprite>("Sprites/Plants/Decorative/" + clean);
        if (cachedSprite == null)
            Debug.LogWarning($"FlowerType '{name}': missing sprite at Resources/Sprites/Plants/Decorative/{clean} — variant will be skipped until artwork is added.");
        return cachedSprite;
    }

    // Lazy mask generator. Returns the auto-generated head-mask texture, or
    // null if the variant has no detectable head (all-stem decorations like
    // moss). Caller should use `hasHead` to decide between two-SR and
    // single-SR spawn paths.
    //
    // Classification rule (per opaque pixel):
    //   - Convert RGB → HSV.
    //   - Pixel is "stem" if hue ∈ [60°, 170°] (yellow-green through teal)
    //     AND saturation > 0.15 (not near-grey) AND value ∈ [0.1, 0.95]
    //     (not pure black / pure white). Otherwise "head".
    //   - Stem → mask R = 0; head → mask R = 255.
    // The ranges are deliberately wide on the green side so mossy stems with
    // muted hues still classify correctly; we'd rather over-attribute stem
    // than over-attribute head, because a stem pixel mis-classed as head only
    // disappears from one SR and appears in the other (no visual hole).
    public Texture2D LoadHeadMask() {
        if (maskProbed) return headMaskTexture;
        maskProbed = true;

        Sprite source = LoadSprite();
        if (source == null) return null;
        if (source.texture == null) return null;
        if (!source.texture.isReadable) {
            Debug.LogWarning($"FlowerType '{name}': source texture isn't readable — auto head-mask generation skipped. Check DecorativeSpritePostprocessor or set Read/Write Enabled in the import inspector.");
            return null;
        }

        Rect rect = source.textureRect;
        int x0 = (int)rect.x;
        int y0 = (int)rect.y;
        int w  = (int)rect.width;
        int h  = (int)rect.height;
        Color32[] pixels = source.texture.GetPixels32();
        int texW = source.texture.width;

        var maskPixels = new Color32[w * h];
        long ySum = 0;
        int headCount = 0;
        int opaqueCount = 0;

        for (int py = 0; py < h; py++) {
            for (int px = 0; px < w; px++) {
                Color32 p = pixels[(y0 + py) * texW + (x0 + px)];
                int idx = py * w + px;
                if (p.a < 128) {
                    maskPixels[idx] = new Color32(0, 0, 0, 0);
                    continue;
                }
                opaqueCount++;
                bool isStem = IsStemColour(p);
                byte v = (byte)(isStem ? 0 : 255);
                maskPixels[idx] = new Color32(v, v, v, 255);
                if (!isStem) {
                    headCount++;
                    ySum += py;
                }
            }
        }

        if (opaqueCount == 0) {
            // Pure-transparent source — skip entirely (the sprite itself will
            // already render nothing, but we don't want to spawn an empty mask).
            hasHead = false;
            headMaskTexture = null;
            return null;
        }

        float headFraction = headCount / (float)opaqueCount;
        if (headFraction < MinHeadFractionToSplit) {
            hasHead = false;
            headMaskTexture = null;
            return null;
        }

        // Head centroid → world units relative to sprite-bottom. The sprite
        // is `h` pixels tall, the source PPU controls how many world units it
        // spans. We compute in sprite-local coords here; FlowerController
        // converts to baseY-relative once it knows the spawn anchor.
        float avgPixelY = (float)(ySum / (double)headCount);
        headCenterY = (avgPixelY + 0.5f) / source.pixelsPerUnit;

        headMaskTexture = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
        headMaskTexture.name = name + "_autoMask";
        headMaskTexture.filterMode = FilterMode.Point;
        headMaskTexture.wrapMode = TextureWrapMode.Clamp;
        headMaskTexture.SetPixels32(maskPixels);
        headMaskTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

        hasHead = true;
        return headMaskTexture;
    }

    // The colour-rule predicate. Kept static + small so future tweaks don't
    // disturb the caching state machine above.
    static bool IsStemColour(Color32 c) {
        float r = c.r / 255f;
        float g = c.g / 255f;
        float b = c.b / 255f;
        Color.RGBToHSV(new Color(r, g, b), out float h, out float s, out float v);
        // h is in [0, 1] in Unity's API. Green band ≈ 0.17 .. 0.47 in this
        // normalisation (roughly 60°–170°). Slightly wider than pure green so
        // olive / chartreuse / teal stems and leaves all classify together.
        bool hueGreen = h >= 0.17f && h <= 0.47f;
        bool notExtreme = v > 0.10f && v < 0.95f;
        bool saturated = s > 0.15f;
        return hueGreen && notExtreme && saturated;
    }
}

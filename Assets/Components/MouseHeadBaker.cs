using System.Collections.Generic;
using UnityEngine;

// Bakes BILINEAR UI sprites for the mouse-head portrait (MouseHeadIcon). Why bake instead of
// reusing the world art directly: the shared head/hat textures are Point-filtered, and the head's
// fur recolor runs in a shader that keys on EXACT pixel values — both break under the bilinear
// sampling the UI needs at its non-integer scale (bilinear-blended pixels stop matching the fur
// shades, so the recolor goes splotchy). So for the UI we pre-bake: read the head's pixels once,
// apply the same fur remap in C#, and emit a Bilinear Texture2D + Sprite per palette color. The
// in-world mouse is untouched (still shader + per-renderer _FurColor MPB, which keeps it batched).
//
// Hats need no recolor — just a Bilinear copy of their Point sprite so the portrait hat matches.
//
// Fur colors come from a small fixed palette (Db.FurColorForSeed → one of ~10 discrete colors), so
// the per-color cache stays tiny. Caches are static; reset on domain reload per the project rule.
public static class MouseHeadBaker {
    // Fur recolor — mirrors RemapFur in Sprite.shader (the in-world fur recolor). Each of the 5 gray fur
    // shades maps to (target fur + that shade's offset from the main shade); other pixels (eyes,
    // pink ears) pass through. Exact-pixel match within ±1.5/255. Keep in sync with the shaders.
    static readonly Color FurMain = new Color(145f / 255f, 152f / 255f, 156f / 255f);
    static readonly Color[] FurSrc = {
        new Color(145f / 255f, 152f / 255f, 156f / 255f), // main
        new Color(154f / 255f, 160f / 255f, 164f / 255f), // highlight
        new Color(137f / 255f, 142f / 255f, 145f / 255f), // shadow
        new Color(112f / 255f, 118f / 255f, 121f / 255f), // eep deep
        new Color(106f / 255f, 113f / 255f, 116f / 255f), // eep deepest
    };
    const float MatchTol = 1.5f / 255f;

    // Sharpness knob. Before applying the Bilinear filter, each source pixel is nearest-neighbour
    // upscaled into an S×S block, so bilinear only blends the thin block seams → sharper edges than
    // plain bilinear. S=1 = fully soft bilinear; higher S → closer to crisp Point. Tuned by eye.
    const int Supersample = 3;

    static readonly Dictionary<Color, Sprite> bakedHeads = new Dictionary<Color, Sprite>();
    static readonly Dictionary<Sprite, Sprite> bakedHats = new Dictionary<Sprite, Sprite>();
    static Color[] baseHeadPx;   // un-recolored head pixels, read once
    static int baseW, baseH;
    static Vector2 baseHeadPivot;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() {
        bakedHeads.Clear();
        bakedHats.Clear();
        baseHeadPx = null;
    }

    // A Bilinear, fur-recolored head sprite for `fur` (cached). Falls back to the raw source sprite
    // if the pixels can't be read.
    public static Sprite Head(Sprite source, Color fur) {
        if (bakedHeads.TryGetValue(fur, out Sprite cached)) return cached;
        if (baseHeadPx == null) {
            baseHeadPx = ReadSpritePixels(source, out baseW, out baseH);
            baseHeadPivot = PivotNorm(source);
        }
        if (baseHeadPx == null) return source;
        var px = new Color[baseHeadPx.Length];
        for (int i = 0; i < px.Length; i++) px[i] = RemapFur(baseHeadPx[i], fur);
        Sprite s = MakeBilinearSprite(px, baseW, baseH, baseHeadPivot, source.pixelsPerUnit);
        bakedHeads[fur] = s;
        return s;
    }

    // A Bilinear copy of a hat sprite (no recolor), cached by source. Falls back to the source.
    public static Sprite HatCopy(Sprite source) {
        if (source == null) return null;
        if (bakedHats.TryGetValue(source, out Sprite cached)) return cached;
        Color[] px = ReadSpritePixels(source, out int w, out int h);
        Sprite s = px != null
            ? MakeBilinearSprite(px, w, h, PivotNorm(source), source.pixelsPerUnit)
            : source;
        bakedHats[source] = s;
        return s;
    }

    static Color RemapFur(Color t, Color fur) {
        for (int k = 0; k < FurSrc.Length; k++) {
            Color s = FurSrc[k];
            if (Mathf.Abs(t.r - s.r) < MatchTol && Mathf.Abs(t.g - s.g) < MatchTol && Mathf.Abs(t.b - s.b) < MatchTol)
                return new Color(fur.r + (s.r - FurMain.r), fur.g + (s.g - FurMain.g), fur.b + (s.b - FurMain.b), t.a);
        }
        return t;
    }

    static Vector2 PivotNorm(Sprite s) =>
        new Vector2(s.pivot.x / s.rect.width, s.pivot.y / s.rect.height);

    // Reads a sprite's pixels even when its texture is non-readable / atlas-packed, by blitting the
    // sprite's textureRect into a temp RenderTexture and reading that back. Returns null on failure.
    static Color[] ReadSpritePixels(Sprite sprite, out int w, out int h) {
        w = 0; h = 0;
        if (sprite == null || sprite.texture == null) return null;
        Texture tex = sprite.texture;
        Rect tr = sprite.textureRect;
        w = Mathf.RoundToInt(tr.width);
        h = Mathf.RoundToInt(tr.height);
        if (w <= 0 || h <= 0) return null;

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        RenderTexture prevActive = RenderTexture.active;
        // Map the full RT onto just the sprite's sub-rect of the (possibly atlas) source texture.
        Vector2 scale = new Vector2(tr.width / tex.width, tr.height / tex.height);
        Vector2 offset = new Vector2(tr.x / tex.width, tr.y / tex.height);
        Graphics.Blit(tex, rt, scale, offset);

        RenderTexture.active = rt;
        var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
        readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        readable.Apply();
        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(rt);

        Color[] px = readable.GetPixels();
        Object.Destroy(readable);
        return px;
    }

    static Sprite MakeBilinearSprite(Color[] px, int w, int h, Vector2 pivotNorm, float ppu) {
        int s = Mathf.Max(1, Supersample);
        int uw = w * s, uh = h * s;
        Color[] up = px;
        if (s > 1) {
            up = new Color[uw * uh];
            for (int y = 0; y < uh; y++)
                for (int x = 0; x < uw; x++)
                    up[y * uw + x] = px[(y / s) * w + (x / s)]; // nearest-neighbour block fill
        }
        var tex = new Texture2D(uw, uh, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        tex.SetPixels(up);
        tex.Apply();
        // ppu × s so the upscaled sprite still represents the same physical size.
        return Sprite.Create(tex, new Rect(0, 0, uw, uh), pivotNorm, (ppu <= 0f ? 16f : ppu) * s);
    }
}

using UnityEngine;

// Fills the SkyCamera frustum with a vertical zenith→horizon gradient that
// follows SunController's time-of-day stops. Replaces what used to be a flat
// solid clear color on SkyCamera.
//
// Implementation: a child SpriteRenderer scaled to the SkyCamera's frustum
// each LateUpdate, displaying a 1×N RGBA32 texture that's refilled with a
// per-frame `lerpOkLab(horizonColor, skyColor, smoothstep(0, horizonY01, v))`.
//
// **Interpolation is in OkLab**, not raw RGB. OkLab is a perceptually uniform
// color space — lerping in it traces a hue-rotating arc through color space
// (cyan → green → yellow → orange) instead of cutting through muddy RGB
// midpoints (cyan → grey-brown → orange). Reference:
// https://bottosson.github.io/posts/oklab/
//
// All Sky-layer sprites use raw colors with no CPU-side ambient multiply —
// the LightFeature pipeline applies ambient × sun via the composite multiply.
// See SPEC-rendering.md §Sky / background.
//
// Scene setup:
//   1. Add a child GameObject under SkyCamera, attach this script.
//   2. (Sortinglayer should match the cloud sortinglayer; sortingOrder is
//       configured here at -100 so the gradient sits behind clouds.)
public class SkyGradient : MonoBehaviour {
    public static SkyGradient instance { get; private set; }

    [Tooltip("Resolution of the gradient texture (vertical pixels). 64 is plenty for a smooth gradient.")]
    [SerializeField] int textureHeight = 64;

    [SerializeField] int sortingOrder = -100;

    [Tooltip("Must match the cloud SRs' sorting layer (Background) so the gradient sits behind clouds. Unity's sortingLayer list orders Background→Default→Water→UI; if gradient is on Default and clouds are on Background, the gradient draws over the clouds.")]
    [SerializeField] string sortingLayerName = "Background";

    Camera bgCam;
    SpriteRenderer sr;
    Texture2D gradTex;
    Color[] pixels;

    void Awake() {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
    }

    void Start() {
        bgCam = transform.parent != null ? transform.parent.GetComponent<Camera>() : null;
        if (bgCam == null) { Debug.LogError("SkyGradient: parent must be a Camera (SkyCamera). Disabling."); enabled = false; return; }

        // Spawn our own child SR so this component is self-contained.
        var srGo = new GameObject("SkyGradientSR");
        srGo.transform.SetParent(transform, worldPositionStays: false);
        srGo.layer = gameObject.layer;
        sr = SpriteMaterialUtil.AddSpriteRenderer(srGo);
        sr.sortingLayerName = sortingLayerName;
        sr.sortingOrder = sortingOrder;

        gradTex = new Texture2D(1, textureHeight, TextureFormat.RGBA32, mipChain: false);
        gradTex.filterMode = FilterMode.Bilinear;
        gradTex.wrapMode = TextureWrapMode.Clamp;
        // PPU = 1 so the sprite's native world size is (1, textureHeight) and
        // localScale = (w, h / textureHeight) cleanly fills (w, h). Using
        // PPU = textureHeight would make the native width 1/textureHeight
        // (since the texture is 1 pixel wide), causing the rendered quad to
        // shrink to a vertical strip — bilinear can't widen a 1-pixel column.
        sr.sprite = Sprite.Create(gradTex, new Rect(0, 0, 1, textureHeight),
                                  new Vector2(0.5f, 0.5f), pixelsPerUnit: 1f);

        pixels = new Color[textureHeight];
    }

    void LateUpdate() {
        if (sr == null || bgCam == null) return;

        // Match the SkyCamera frustum exactly. SkyCamera's orthographicSize is
        // dampened by its own zoomFactor — must read from bgCam, not Camera.main.
        // Native sprite size is (1, textureHeight) (PPU = 1), so divide y by
        // textureHeight to land at exactly (w, h) units rendered.
        float h = bgCam.orthographicSize * 2f;
        float w = h * bgCam.aspect;
        sr.transform.localPosition = new Vector3(0f, 0f, 10f);
        sr.transform.localScale    = new Vector3(w, h / textureHeight, 1f);

        // Refill the gradient. Raw colors — lighting handles ambient.
        // Force alpha=1: authored stops sometimes carry alpha=0 (Unity color
        // picker quirk). A gradient texel with alpha<1 makes the quad partially
        // transparent and we see through to the camera clear.
        Color zenith  = SunController.skyColor;     zenith.a  = 1f;
        Color horizon = SunController.horizonColor; horizon.a = 1f;

        // Pre-convert the stops to OkLab once per frame, then per-pixel lerp
        // in lab space and convert each result back to sRGB. cbrt × textureHeight
        // is the per-frame cost — negligible at textureHeight=64.
        var labZenith  = RgbToOklab(zenith.linear);
        var labHorizon = RgbToOklab(horizon.linear);

        float horizonY01 = SunController.horizonY01;
        for (int i = 0; i < textureHeight; i++) {
            float v = (i + 0.5f) / textureHeight;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, horizonY01, v));
            Color lin = OklabToRgb(
                Mathf.Lerp(labHorizon.L, labZenith.L, t),
                Mathf.Lerp(labHorizon.a, labZenith.a, t),
                Mathf.Lerp(labHorizon.b, labZenith.b, t));
            Color srgb = lin.gamma;
            srgb.a = 1f;
            pixels[i] = srgb;
        }
        gradTex.SetPixels(pixels);
        gradTex.Apply(updateMipmaps: false);
    }

    // Samples the same blend the gradient quad uses, at a given viewport-Y.
    // Returns raw sRGB color (no ambient) — callers feed it to a Sky-layer
    // sprite, which gets ambient × sun via the lighting composite. Falls back
    // to SunController.skyColor before the gradient is initialized.
    public static Color SampleAtViewportY(float v01) {
        if (instance == null) { var fallback = SunController.skyColor; fallback.a = 1f; return fallback; }
        float horizonY01 = SunController.horizonY01;
        float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, horizonY01, v01));
        Color zenith  = SunController.skyColor;     zenith.a  = 1f;
        Color horizon = SunController.horizonColor; horizon.a = 1f;
        var labZenith  = RgbToOklab(zenith.linear);
        var labHorizon = RgbToOklab(horizon.linear);
        Color lin = OklabToRgb(
            Mathf.Lerp(labHorizon.L, labZenith.L, t),
            Mathf.Lerp(labHorizon.a, labZenith.a, t),
            Mathf.Lerp(labHorizon.b, labZenith.b, t));
        Color srgb = lin.gamma;
        srgb.a = 1f;
        return srgb;
    }

    void OnDestroy() {
        if (gradTex != null) Destroy(gradTex);
    }

    // ── OkLab color-space conversions ──────────────────────────────────────
    // Reference: https://bottosson.github.io/posts/oklab/
    // Inputs/outputs are LINEAR sRGB. Use Color.linear / Color.gamma at the
    // boundary to convert to/from the sRGB-encoded values Unity stores in Color.

    static (float L, float a, float b) RgbToOklab(Color linRgb) {
        float l = 0.4122214708f * linRgb.r + 0.5363325363f * linRgb.g + 0.0514459929f * linRgb.b;
        float m = 0.2119034982f * linRgb.r + 0.6806995451f * linRgb.g + 0.1073969566f * linRgb.b;
        float s = 0.0883024619f * linRgb.r + 0.2817188376f * linRgb.g + 0.6299787005f * linRgb.b;
        float lc = Cbrt(l);
        float mc = Cbrt(m);
        float sc = Cbrt(s);
        return (
            0.2104542553f * lc + 0.7936177850f * mc - 0.0040720468f * sc,
            1.9779984951f * lc - 2.4285922050f * mc + 0.4505937099f * sc,
            0.0259040371f * lc + 0.7827717662f * mc - 0.8086757660f * sc
        );
    }

    static Color OklabToRgb(float L, float a, float b) {
        float lc = L + 0.3963377774f * a + 0.2158037573f * b;
        float mc = L - 0.1055613458f * a - 0.0638541728f * b;
        float sc = L - 0.0894841775f * a - 1.2914855480f * b;
        float l = lc * lc * lc;
        float m = mc * mc * mc;
        float s = sc * sc * sc;
        return new Color(
            Mathf.Clamp01(+4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s),
            Mathf.Clamp01(-1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s),
            Mathf.Clamp01(-0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s),
            1f
        );
    }

    // Mathf.Pow can't take fractional exponents of negatives. The OkLab matrix
    // coefficients are all positive so RGB→LMS values stay non-negative for
    // any valid sRGB input, but guard for safety against extrapolated stops.
    static float Cbrt(float x) {
        return x < 0f ? -Mathf.Pow(-x, 1f / 3f) : Mathf.Pow(x, 1f / 3f);
    }
}

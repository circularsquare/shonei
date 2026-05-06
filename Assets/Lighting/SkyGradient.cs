using UnityEngine;

// Fills the SkyCamera frustum with a vertical zenith→horizon gradient that
// follows SunController's time-of-day stops. Replaces what used to be a flat
// solid clear color on SkyCamera.
//
// Implementation: a child SpriteRenderer scaled to the SkyCamera's frustum
// each LateUpdate, displaying a 1×N RGBA32 texture that's refilled with the
// blend `lerp(horizonColor, skyColor, smoothstep(0, horizonY01, v))`.
//
// All Sky-layer sprites use raw colors with no CPU-side ambient multiply —
// the LightFeature pipeline applies ambient × sun via the composite multiply.
// See SPEC-rendering.md §Sky / background.
//
// Scene setup:
//   1. Add a child GameObject under SkyCamera, attach this script.
//   2. Add a child SpriteRenderer GameObject and wire it as `sr`.
//   3. (Sortinglayer should match the cloud sortinglayer; sortingOrder is
//       configured here at -100 so the gradient sits behind clouds.)
public class SkyGradient : MonoBehaviour {
    public static SkyGradient instance { get; private set; }

    [Tooltip("Viewport V at which the horizon→zenith blend completes. Below this is the transition zone, above is full zenith. 1.0 = gradient spans the entire viewport (no solid-zenith band).")]
    [Range(0f, 1f)] public float horizonY01 = 0.4f;

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
        for (int i = 0; i < textureHeight; i++) {
            float v = (i + 0.5f) / textureHeight;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, horizonY01, v));
            Color c = Color.Lerp(horizon, zenith, t);
            c.a = 1f;
            pixels[i] = c;
        }
        gradTex.SetPixels(pixels);
        gradTex.Apply(updateMipmaps: false);
    }

    // Samples the same blend the gradient quad uses, at a given viewport-Y.
    // Returns raw color (no ambient) — callers feed it to a Sky-layer sprite,
    // which gets ambient × sun via the lighting composite. Falls back to
    // SunController.skyColor before the gradient is initialized.
    public static Color SampleAtViewportY(float v01) {
        if (instance == null) { var fallback = SunController.skyColor; fallback.a = 1f; return fallback; }
        float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, instance.horizonY01, v01));
        Color zenith  = SunController.skyColor;     zenith.a  = 1f;
        Color horizon = SunController.horizonColor; horizon.a = 1f;
        Color c = Color.Lerp(horizon, zenith, t);
        c.a = 1f;
        return c;
    }

    void OnDestroy() {
        if (gradTex != null) Destroy(gradTex);
    }
}

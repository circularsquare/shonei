using UnityEngine;

// Atmospheric haze: a low-opacity sky-gradient overlay drawn in front of
// the background hills, stars, and clouds. Lifts their luminance toward the
// sky color (atmospheric perspective trick) so distant elements read as
// further away rather than competing with foreground saturation.
//
// Re-samples SkyGradient.SampleAtViewportY each frame to build a 1×N gradient
// texture with `opacity` baked into alpha — the haze matches whatever the
// sky behind it looks like (horizon-tinted low, zenith-tinted high) at the
// chosen transparency. Uses the lit Sprite material via SpriteMaterialUtil so
// the LightFeature composite multiplies in ambient × sun like every other
// Sky-layer sprite — at night the haze dims along with the rest of the sky.
//
// ── Scene wiring ───────────────────────────────────────────────────────
// Lives as a child of SkyCamera (same parent as SkyGradient). The component
// finds its camera via transform.parent. Default sortingOrder = 10 in the
// Background sortingLayer — in front of the cloud layer (0) but still
// within the Sky-camera's Background pass.
//
// ── NormalsCapture interaction ─────────────────────────────────────────
// The texture alpha is `opacity` (> 0.1 by default) so NormalsCapture does
// not clip; it overwrites the underlying normals contribution with this
// sprite's flat normal at the directionalOnly shadowAlpha. That's fine
// here because the hills and clouds underneath also use flat camera-facing
// normals (see BackgroundLayer's `_flatNormalTex` MPB binding and the
// flattened cloud normalRT), so the lit result is unchanged.
public class HazeLayer : MonoBehaviour {
    [Range(0f, 1f)]
    [Tooltip("Per-pixel alpha of the haze overlay. Higher = stronger sky tint over hills/clouds.")]
    [SerializeField] float opacity = 0.15f;

    [Tooltip("Vertical resolution of the haze gradient texture. 64 matches SkyGradient — plenty for a smooth gradient.")]
    [SerializeField] int textureHeight = 64;

    [Tooltip("Sorting order within the Background sortingLayer. Must be greater than the frontmost item to sit on top of (clouds = 0).")]
    [SerializeField] int sortingOrder = 10;

    [Tooltip("Must match SkyGradient's sortingLayerName so the haze renders in the same group as the sky/hills/clouds.")]
    [SerializeField] string sortingLayerName = "Background";

    Camera bgCam;
    SpriteRenderer sr;
    Texture2D gradTex;
    Color[] pixels;
    static Texture2D _flatNormalTex;

    static readonly int NormalMapId = Shader.PropertyToID("_NormalMap");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { _flatNormalTex = null; }

    void Start() {
        bgCam = transform.parent != null ? transform.parent.GetComponent<Camera>() : null;
        if (bgCam == null) {
            Debug.LogError("HazeLayer: parent must be a Camera (SkyCamera). Disabling.");
            enabled = false;
            return;
        }

        var srGo = new GameObject("HazeLayerSR");
        srGo.transform.SetParent(transform, worldPositionStays: false);
        srGo.layer = gameObject.layer;
        sr = SpriteMaterialUtil.AddSpriteRenderer(srGo);
        sr.sortingLayerName = sortingLayerName;
        sr.sortingOrder = sortingOrder;

        gradTex = new Texture2D(1, textureHeight, TextureFormat.RGBA32, mipChain: false) {
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
            hideFlags  = HideFlags.HideAndDontSave,
        };
        // PPU=1 → native sprite size is (1, textureHeight); localScale.y is
        // divided by textureHeight in LateUpdate to land at (w, h) world units.
        // See the "Sprite PPU pitfall" note in SkyGradient.cs.
        sr.sprite = Sprite.Create(gradTex, new Rect(0, 0, 1, textureHeight),
                                  new Vector2(0.5f, 0.5f), pixelsPerUnit: 1f);

        pixels = new Color[textureHeight];

        // Bind a flat camera-facing normal so NormalsCapture writes a
        // consistent normal across the haze, matching hills/clouds. Without
        // this MPB, the lit material would sample Unity's default white 1×1
        // texture and decode an off-axis normal that disagrees with everything
        // underneath the haze.
        var mpb = new MaterialPropertyBlock();
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(NormalMapId, GetFlatNormalTex());
        sr.SetPropertyBlock(mpb);
    }

    void LateUpdate() {
        if (sr == null || bgCam == null) return;

        // Match the SkyCamera frustum (same scaling pattern as SkyGradient).
        float h = bgCam.orthographicSize * 2f;
        float w = h * bgCam.aspect;
        sr.transform.localPosition = new Vector3(0f, 0f, 10f);
        sr.transform.localScale    = new Vector3(w, h / textureHeight, 1f);

        // Re-fill the gradient from SkyGradient's blend each frame so the
        // haze tracks twilight color shifts in lock-step with the sky behind.
        for (int i = 0; i < textureHeight; i++) {
            float v = (i + 0.5f) / textureHeight;
            Color c = SkyGradient.SampleAtViewportY(v);
            c.a = opacity;
            pixels[i] = c;
        }
        gradTex.SetPixels(pixels);
        gradTex.Apply(updateMipmaps: false);

        // Re-sync sortingOrder in case it was tweaked in the inspector at runtime.
        sr.sortingOrder = sortingOrder;
    }

    void OnDestroy() {
        if (gradTex != null) Destroy(gradTex);
    }

    static Texture2D GetFlatNormalTex() {
        if (_flatNormalTex != null) return _flatNormalTex;
        _flatNormalTex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false) {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp,
            hideFlags  = HideFlags.HideAndDontSave,
        };
        // (0.5, 0.5, 1.0) decodes via (rgb*2 - 1) to (0, 0, 1) — a
        // tangent-space normal pointing straight at the camera.
        _flatNormalTex.SetPixel(0, 0, new Color(0.5f, 0.5f, 1.0f, 1.0f));
        _flatNormalTex.Apply();
        return _flatNormalTex;
    }
}

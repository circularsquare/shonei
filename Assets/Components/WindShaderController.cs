using UnityEngine;

// Pushes wind-related shader globals once per frame so PlantSprite.shader
// (and Phase 2's NormalsCapture sway) can read them. Tunables live here so
// they're inspector-editable; pushed once at Start. _Wind is sampled from
// WeatherSystem each frame.
//
// Attach to a scene root alongside SunController. WeatherSystem.instance is
// initialised by World.Awake() before any Update runs, so the null-coalesce
// in Update() is belt-and-braces — protects against very-early frames or
// scene reloads where the singleton hasn't caught up yet.
//
// Why a dedicated MonoBehaviour rather than inlining into WorldController:
// the four sway tunables want inspector exposure, and grouping wind-shader
// globals in one place makes the dependency graph obvious. One-line in
// WorldController would force tunables into code constants or another
// scriptable, which is more friction for visual iteration.
public class WindShaderController : MonoBehaviour {
    // ── Sway tunables (pushed once at Start) ─────────────────────────
    // Sized so peak top-of-plant sway is ~2 px at full wind (PPU=16,
    // 2 px = 0.125 tiles). Worst-case alignment of all three terms ~1.9 px.

    [Tooltip("Wind-driven steady lean (world units per unit-wind, at top of plant). 0.08 ≈ 1.3 px max.")]
    public float swayLean = 0.08f;

    [Tooltip("Wind-driven oscillation amplitude on top of lean. 0.025 ≈ 0.4 px.")]
    public float swayGust = 0.025f;

    [Tooltip("Still-air idle sway amplitude. 0.015 ≈ 0.25 px.")]
    public float swayAmbient = 0.015f;

    [Tooltip("Base oscillation frequency in radians/second. 1.4 ≈ slow lazy sway.")]
    public float swayFreq = 1.4f;

    static readonly int WindId        = Shader.PropertyToID("_Wind");
    static readonly int SwayLeanId    = Shader.PropertyToID("_SwayLean");
    static readonly int SwayGustId    = Shader.PropertyToID("_SwayGust");
    static readonly int SwayAmbientId = Shader.PropertyToID("_SwayAmbient");
    static readonly int SwayFreqId    = Shader.PropertyToID("_SwayFreq");
    static readonly int SwayMaskId    = Shader.PropertyToID("_SwayMask");

    void Start() {
        PushTunables();
        // Belt-and-braces default for the shared NormalsCapture override path:
        // non-plant sprites don't bind _SwayMask via secondary textures, so a
        // global white texture covers the (gated, never-actually-used) sample.
        Shader.SetGlobalTexture(SwayMaskId, Texture2D.whiteTexture);
    }

    void Update() {
        Shader.SetGlobalFloat(WindId, WeatherSystem.instance?.wind ?? 0f);

        // Tunables are normally only pushed once at Start, but we keep
        // re-pushing in the editor so live inspector tweaks take effect
        // without a domain reload. Skipping in builds is a minor optimisation
        // that costs nothing to omit — kept for consistency with iteration.
#if UNITY_EDITOR
        PushTunables();
#endif
    }

    void PushTunables() {
        Shader.SetGlobalFloat(SwayLeanId,    swayLean);
        Shader.SetGlobalFloat(SwayGustId,    swayGust);
        Shader.SetGlobalFloat(SwayAmbientId, swayAmbient);
        Shader.SetGlobalFloat(SwayFreqId,    swayFreq);
    }
}

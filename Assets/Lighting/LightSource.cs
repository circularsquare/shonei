using System.Collections.Generic;
using UnityEngine;

// Add to any GameObject to make it a custom light source.
// Registered automatically; the LightFeature render pass reads this list.
//   isDirectional = false  → point light (torch, lantern, etc.)
//   isDirectional = true   → directional light (sun); drawn fullscreen, no falloff
//
// For fuel-consuming light sources (e.g. torch), set reservoir after AddComponent.
// The Update() loop will burn fuel and set isLit=false when the fuel inv is empty.
// SunController reads isLit to zero out intensity for unlit sources.
public class LightSource : MonoBehaviour {
    public Color lightColor   = new Color(1f, 0.8f, 0.5f);
    public float intensity    = 0.0f;
    public float baseIntensity = 0.85f;

    [Header("Point light")]
    public float outerRadius = 10f;
    public float innerRadius = 4f;
    [Tooltip("Z height above the sprite plane — controls how steep the NdotL angle is")]
    public float lightHeight = 0.4f;
    [Tooltip("0 = raw point-light NdotL (bright hot-spot directly under the light). 1 = flatten the " +
             "center: a flat surface reads uniformly across the disc while normals still shade it — " +
             "recesses darken, faces toward the light brighten (the highlight headroom grows with " +
             "this value, so normals keep their pop). See LightCircle.shader.")]
    [Range(0f, 1f)] public float centerFlatten = 0f;

    [Header("Directional (sun)")]
    public bool isDirectional = false;

    [Header("Sort-aware height")]
    [Tooltip("This light's sortingOrder for the sort-aware effective-height ramp. " +
             "-1 = auto-detect from a SpriteRenderer on this GameObject or its parents " +
             "(e.g. the torch sprite). Receivers whose sortingOrder > this value are lit " +
             "as if the light were behind them. See LightCircle.shader.")]
    public int sortOrderOverride = -1;

    // Cached effective sortingOrder used to build sortBucket. Resolved in OnEnable.
    private int _effectiveSort;

    // Normalized sort value (0–1) consumed by LightCircle.shader as _LightSortBucket.
    // Phase 4: must match the receiver's bucket scheme (SortBucketUtil), otherwise
    // light vs receiver are compared on different scales and the sortDelta math
    // flips sign (e.g. torch at sortingOrder 64 read as 0.25 on the old scale
    // vs receiver building at bucket 2 read as 0.4 on the new scale — building
    // looks unlit from the torch).
    public float sortBucket => SortBucketUtil.BucketToNormalized(SortBucketUtil.GetBucket(_effectiveSort));

    // Set to the Reservoir that powers this light. Null = no fuel needed (always lit).
    [HideInInspector] public Reservoir reservoir;
    // Owning Building, if any. Null for the sun and debug-cursor lights.
    // When non-null, the LightSource pauses burn + emission while building.disabled is true.
    [HideInInspector] public Building building;
    // False when fuel has run out, outside the active time window, or owning building is disabled.
    public bool isLit = true;

    // When true, SunController modulates intensity by time of day (torches, fireplaces).
    [HideInInspector] public bool sunModulated = false;

    // Subtle fire flicker. flickerAmount is the intensity wobble as a fraction (0 = steady,
    // 0.06 = ±6%); flickerPhase offsets this light's noise lane so neighbours don't pulse in sync.
    [HideInInspector] public float flickerAmount = 0f;
    [HideInInspector] public float flickerPhase  = 0f;
    private const float FlickerSpeed = 3f; // noise lanes/sec — how fast the wobble evolves

    // Optional time gate: fuel only burns and light only shows within [activeStartHour, activeEndHour).
    // Hours 0–24; end < start wraps midnight (e.g. 16→6 = 4pm–6am). -1 = always active.
    [HideInInspector] public float activeStartHour = -1f;
    [HideInInspector] public float activeEndHour   =  0f;

    public static readonly List<LightSource> all = new();

    // LightSources with an _EmissionMap (torch fire children, fireplace
    // bodies, etc.). LightFeature § 5 iterates this — for each entry it
    // sets `_EmissionScale` and `_SortBucket` as globals, then draws the
    // emission via DrawRenderer(EmissionReceiver, emissionMat). Skips
    // directional lights (sun has no _EmissionMap).
    //
    // Storing LightSource (not Renderer) lets the emission pass read the
    // per-emitter scale + bucket without a back-pointer lookup. Replaces
    // the older List<Renderer> + per-renderer MPB scheme — see Phase 4 of
    // the GPU perf plan.
    public static readonly List<LightSource> emitters = new();

    // Fractional-fen accumulator so sub-fen burn rates work correctly across frames.
    private float _fuelAccumulator = 0f;

    // The SpriteRenderer that carries the _EmissionMap. Resolved in
    // OnEnable / Start (fire child if present, else parent SR).
    private SpriteRenderer _emissionReceiver;
    public SpriteRenderer EmissionReceiver => _emissionReceiver;

    // Current emission scale, derived from intensity / baseIntensity each frame.
    // Read by LightFeature § 5 (set as global per emitter before drawing
    // EmissionWriter), and by Update() to gate the fire-child GameObject's
    // visibility (`scale > 0.05f` ≈ visible glow).
    //   night, lit, fueled  → intensity = baseIntensity → scale ≈ 1
    //   daytime / unlit     → intensity = 0             → scale = 0
    //   twilight ramp       → intensity in between      → smooth fade
    public float CurrentEmissionScale {
        get {
            if (baseIntensity <= 0.001f) return 1f; // fallback for non-real-LS sprites
            return Mathf.Clamp01(intensity / baseIntensity);
        }
    }

    void OnEnable() {
        all.Add(this);
        ResolveSortOrder();
        // If the building has a fire child, _EmissionMap lives on its SpriteRenderer.
        // Otherwise fall back to the parent SR (legacy path for non-fire emissive buildings).
        SetEmissionReceiver(building?.fireSR ?? GetComponentInParent<SpriteRenderer>());
    }
    void Start() {
        // Re-resolve: building is null during the OnEnable triggered by
        // AddComponent — the caller assigns ls.building = this afterward.
        // By Start(), all fields are set, so we can target fireSR correctly.
        SetEmissionReceiver(building?.fireSR ?? GetComponentInParent<SpriteRenderer>());
    }
    void OnDisable() {
        all.Remove(this);
        SetEmissionReceiver(null);
        if (building?.fireGO != null) building.fireGO.SetActive(false);
    }

    // Keeps _emissionReceiver and the emitters registry in lockstep.
    // Skips registering directional lights (sun) — they have no _EmissionMap and
    // would just add a wasted draw call to LightFeature § 5.
    private void SetEmissionReceiver(SpriteRenderer next) {
        if (_emissionReceiver != null) emitters.Remove(this);
        _emissionReceiver = next;
        if (next != null && !isDirectional) emitters.Add(this);
    }

    private void ResolveSortOrder() {
        if (sortOrderOverride >= 0) {
            _effectiveSort = sortOrderOverride;
            return;
        }
        // Walk up the hierarchy so a LightSource attached to a child GameObject
        // (common for torches — the emitter is often a separate pivot) still
        // picks up the structure's sortingOrder from the parent sprite.
        var sr = GetComponentInParent<SpriteRenderer>();
        if (sr == null) {
            Debug.LogError($"LightSource on {name}: no SpriteRenderer in parents and no sortOrderOverride set. " +
                           $"Defaulting to sortingOrder 0 for sort-aware lighting.");
            _effectiveSort = 0;
            return;
        }
        _effectiveSort = sr.sortingOrder;
    }

    void Update() {
        UpdateLitState();
        // Sun-modulated point lights (torches, fireplaces) scale their
        // intensity by how dark it is outside — `torchFactor` ramps from
        // 0 in daylight up to 1 by mid-twilight. SunController owns
        // computing that scalar each frame; we just pull it.
        // (Execution order is the default — SunController runs before
        // LightSource in practice; one-frame lag would be invisible
        // anyway because torchFactor changes smoothly over twilight.)
        if (sunModulated && !isDirectional)
            intensity = isLit ? baseIntensity * EnvDarkness() : 0f;
        // Subtle organic flicker on top of the (time-of-day-scaled) intensity. One Perlin sample
        // along a per-instance lane (flickerPhase) — visual only, and the light pass already
        // redraws every frame, so this adds no rendering cost (just one cheap noise lookup).
        if (flickerAmount > 0f && isLit && !isDirectional)
            intensity *= 1f + flickerAmount * (Mathf.PerlinNoise(flickerPhase, Time.time * FlickerSpeed) - 0.5f) * 2f;
        // Fire child visibility tracks emission scale — fire appears/disappears
        // in sync with the emission glow, including smooth twilight fade.
        if (building?.fireGO != null)
            building.fireGO.SetActive(CurrentEmissionScale > 0.05f);
    }

    private void UpdateLitState() {
        if (reservoir == null) return; // no fuel needed — always lit

        // Disabled or broken buildings: don't consume fuel, don't emit light.
        // SunController zeros intensity for !isLit sun-modulated sources next frame.
        if (building != null && (building.disabled || building.IsBroken)) {
            isLit = false;
            return;
        }

        bool inWindow = IsInActiveWindow();
        // Only burn fuel while in the active time window and torch is emitting light.
        if (inWindow && EnvDarkness() > 0f)
            reservoir.Burn(Time.deltaTime, ref _fuelAccumulator);
        isLit = inWindow && reservoir.HasFuel();
    }

    private bool IsInActiveWindow() => SunController.IsHourInRange(activeStartHour, activeEndHour);

    // How dark it is where this light sits, 0 (full daylight) → 1 (full night). Normally just
    // the outside time-of-day ramp (SunController.torchFactor). But a light against a solid
    // background wall (in a cave or dug-out interior) is in permanent shade, so it reads fully
    // dark regardless of the time outside — letting players light caves by day. backgroundType is
    // fixed at worldgen and persists through digging, so a torch in a tunnel qualifies.
    private float EnvDarkness() {
        if (building != null && building.tile != null && building.tile.hasBackground)
            return 1f;
        return SunController.torchFactor;
    }

}

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
    public float sortBucket => Mathf.Clamp01(_effectiveSort / 255f);

    // Set to the Reservoir that powers this light. Null = no fuel needed (always lit).
    [HideInInspector] public Reservoir reservoir;
    // Owning Building, if any. Null for the sun and debug-cursor lights.
    // When non-null, the LightSource pauses burn + emission while building.disabled is true.
    [HideInInspector] public Building building;
    // False when fuel has run out, outside the active time window, or owning building is disabled.
    public bool isLit = true;

    // When true, SunController modulates intensity by time of day (torches, fireplaces).
    [HideInInspector] public bool sunModulated = false;

    // Optional time gate: fuel only burns and light only shows within [activeStartHour, activeEndHour).
    // Hours 0–24; end < start wraps midnight (e.g. 16→6 = 4pm–6am). -1 = always active.
    [HideInInspector] public float activeStartHour = -1f;
    [HideInInspector] public float activeEndHour   =  0f;

    public static readonly List<LightSource> all = new();

    // Renderers with a real _EmissionMap (torch fire children, fireplace bodies, etc.).
    // LightFeature § 5 iterates this instead of DrawRenderers(litMask) so the emission
    // pass only touches sprites that actually contribute, not every visible lit sprite.
    // Keep in lockstep with _emissionReceiver via SetEmissionReceiver().
    public static readonly List<Renderer> emissiveReceivers = new();

    // Fractional-fen accumulator so sub-fen burn rates work correctly across frames.
    private float _fuelAccumulator = 0f;

    // Per-renderer MPB plumbing for emission gating. When intensity drops to 0
    // (daytime, out of fuel, disabled, outside active hours), we write
    // _EmissionScale = 0 onto the receiver SpriteRenderer so EmissionWriter
    // suppresses the per-pixel glow. Smooth — tracks `intensity / baseIntensity`,
    // so torches fade in/out across twilight rather than popping.
    private SpriteRenderer _emissionReceiver;
    private float _lastEmissionScale = -1f; // sentinel, forces first write
    private static MaterialPropertyBlock _scratchMpb;
    private static readonly int EmissionScaleId = Shader.PropertyToID("_EmissionScale");

    void OnEnable() {
        all.Add(this);
        ResolveSortOrder();
        // If the building has a fire child, _EmissionMap lives on its SpriteRenderer.
        // Otherwise fall back to the parent SR (legacy path for non-fire emissive buildings).
        SetEmissionReceiver(building?.fireSR ?? GetComponentInParent<SpriteRenderer>());
        UpdateEmissionMpb();
    }
    void Start() {
        // Re-resolve: building is null during the OnEnable triggered by
        // AddComponent — the caller assigns ls.building = this afterward.
        // By Start(), all fields are set, so we can target fireSR correctly.
        SetEmissionReceiver(building?.fireSR ?? GetComponentInParent<SpriteRenderer>());
        UpdateEmissionMpb();
    }
    void OnDisable() {
        all.Remove(this);
        // Restore default emission on disable so the sprite doesn't stay dark
        // if the LightSource is removed but the structure persists.
        if (_emissionReceiver != null) WriteEmissionMpb(1f);
        SetEmissionReceiver(null);
        if (building?.fireGO != null) building.fireGO.SetActive(false);
    }

    // Keeps _emissionReceiver and the emissiveReceivers registry in lockstep.
    // Skips registering directional lights (sun) — they have no _EmissionMap and
    // would just add a wasted draw call to LightFeature § 5.
    private void SetEmissionReceiver(SpriteRenderer next) {
        if (_emissionReceiver != null) emissiveReceivers.Remove(_emissionReceiver);
        _emissionReceiver = next;
        if (next != null && !isDirectional) emissiveReceivers.Add(next);
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
        UpdateEmissionMpb();
        // Fire child visibility tracks emission scale — fire appears/disappears
        // in sync with the emission glow, including smooth twilight fade.
        if (building?.fireGO != null)
            building.fireGO.SetActive(_lastEmissionScale > 0.05f);
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
        if (inWindow && SunController.torchFactor > 0f)
            reservoir.Burn(Time.deltaTime, ref _fuelAccumulator);
        isLit = inWindow && reservoir.HasFuel();
    }

    private bool IsInActiveWindow() => SunController.IsHourInRange(activeStartHour, activeEndHour);

    // ── Emission MPB ─────────────────────────────────────────────────────────
    // Mirrors the source's current visible intensity (set by SunController) onto
    // the receiver SpriteRenderer's _EmissionScale MPB. The shader multiplies
    // emission by this value, so emission tracks the actual light output:
    //   night, lit, fueled  → intensity = baseIntensity → scale ≈ 1
    //   daytime / unlit     → intensity = 0             → scale = 0
    //   twilight ramp       → intensity in between      → smooth fade
    private void UpdateEmissionMpb() {
        if (_emissionReceiver == null) return;
        float scale = baseIntensity > 0.001f
            ? Mathf.Clamp01(intensity / baseIntensity)
            : 1f;
        // Tolerate sub-1/255 jitter — that's below visual resolution anyway.
        if (Mathf.Abs(scale - _lastEmissionScale) < 0.004f) return;
        WriteEmissionMpb(scale);
    }

    private void WriteEmissionMpb(float scale) {
        _scratchMpb ??= new MaterialPropertyBlock();
        _emissionReceiver.GetPropertyBlock(_scratchMpb);
        _scratchMpb.SetFloat(EmissionScaleId, scale);
        _emissionReceiver.SetPropertyBlock(_scratchMpb);
        _lastEmissionScale = scale;
    }
}

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
    public float lightHeight = 1.0f;

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

    /// <summary>Normalized sort value (0–1) consumed by LightCircle.shader as _LightSortBucket.</summary>
    public float sortBucket => Mathf.Clamp01(_effectiveSort / 255f);

    /// <summary>Set to the Reservoir that powers this light. Null = no fuel needed (always lit).</summary>
    [HideInInspector] public Reservoir reservoir;
    /// <summary>False when fuel has run out or outside the active time window.</summary>
    public bool isLit = true;

    /// <summary>When true, SunController modulates intensity by time of day (torches, fireplaces).</summary>
    [HideInInspector] public bool sunModulated = false;

    // Optional time gate: fuel only burns and light only shows within [activeStartHour, activeEndHour).
    // Hours 0–24; end < start wraps midnight (e.g. 16→6 = 4pm–6am). -1 = always active.
    [HideInInspector] public float activeStartHour = -1f;
    [HideInInspector] public float activeEndHour   =  0f;

    public static readonly List<LightSource> all = new();

    // Fractional-fen accumulator so sub-fen burn rates work correctly across frames.
    private float _fuelAccumulator = 0f;

    void OnEnable() {
        all.Add(this);
        ResolveSortOrder();
    }
    void OnDisable() => all.Remove(this);

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
        if (reservoir == null) return; // no fuel needed — always lit

        bool inWindow = IsInActiveWindow();
        // Only burn fuel while in the active time window and torch is emitting light.
        if (inWindow && SunController.torchFactor > 0f)
            reservoir.Burn(Time.deltaTime, ref _fuelAccumulator);
        isLit = inWindow && reservoir.HasFuel();
    }

    private bool IsInActiveWindow() => SunController.IsHourInRange(activeStartHour, activeEndHour);
}

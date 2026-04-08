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
    public Color lightColor   = new Color(1f, 0.75f, 0.3f);
    public float intensity    = 0.0f;
    public float baseIntensity = 0.80f;

    [Header("Point light")]
    public float outerRadius = 10f;
    public float innerRadius = 2f;
    [Tooltip("Z height above the sprite plane — controls how steep the NdotL angle is")]
    public float lightHeight = 0.5f;

    [Header("Directional (sun)")]
    public bool isDirectional = false;

    /// <summary>Set to the Reservoir that powers this light. Null = no fuel needed (always lit).</summary>
    [HideInInspector] public Reservoir reservoir;
    /// <summary>False when fuel has run out or outside the active time window.</summary>
    public bool isLit = true;

    // Optional time gate: fuel only burns and light only shows within [activeStartHour, activeEndHour).
    // Hours 0–24; end < start wraps midnight (e.g. 16→6 = 4pm–6am). -1 = always active.
    [HideInInspector] public float activeStartHour = -1f;
    [HideInInspector] public float activeEndHour   =  0f;

    public static readonly List<LightSource> all = new();

    // Fractional-fen accumulator so sub-fen burn rates work correctly across frames.
    private float _fuelAccumulator = 0f;

    void OnEnable()  => all.Add(this);
    void OnDisable() => all.Remove(this);

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

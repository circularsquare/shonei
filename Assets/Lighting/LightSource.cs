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
    public float lightHeight = 4f;

    [Header("Directional (sun)")]
    public bool isDirectional = false;

    /// <summary>Set to the Reservoir that powers this light. Null = no fuel needed (always lit).</summary>
    [HideInInspector] public Reservoir reservoir;
    /// <summary>False when fuel has run out; SunController sets intensity to 0 when false.</summary>
    public bool isLit = true;

    public static readonly List<LightSource> all = new();

    // Fractional-fen accumulator so sub-fen burn rates work correctly across frames.
    private float _fuelAccumulator = 0f;

    void OnEnable()  => all.Add(this);
    void OnDisable() => all.Remove(this);

    void Update() {
        if (reservoir == null) return; // no fuel needed — always lit

        // Only burn fuel while the torch is emitting light (torchFactor > 0).
        // During full day (torchFactor == 0) the torch is off and consumes nothing.
        // Partial twilight (torchFactor > 0) counts as on — still consumes.
        if (SunController.torchFactor > 0f)
            reservoir.Burn(Time.deltaTime, ref _fuelAccumulator);
        isLit = reservoir.HasFuel();
    }
}

using System.Collections.Generic;
using UnityEngine;

// Add to any GameObject to make it a custom light source.
// Registered automatically; the LightFeature render pass reads this list.
//   isDirectional = false  → point light (torch, lantern, etc.)
//   isDirectional = true   → directional light (sun); drawn fullscreen, no falloff
public class LightSource : MonoBehaviour {
    public Color lightColor  = new Color(1f, 0.75f, 0.35f);
    public float intensity   = 1f;

    [Header("Point light")]
    public float outerRadius = 5f;
    public float innerRadius = 1.5f;
    [Tooltip("Z height above the sprite plane — controls how steep the NdotL angle is")]
    public float lightHeight = 1f;

    [Header("Directional (sun)")]
    public bool isDirectional = false;

    public static readonly List<LightSource> all = new();

    void OnEnable()  => all.Add(this);
    void OnDisable() => all.Remove(this);
}

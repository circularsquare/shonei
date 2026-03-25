using System.Collections.Generic;
using UnityEngine;

// Add to any GameObject to make it a custom light source.
// Registered automatically; the LightFeature render pass reads this list.
//   isDirectional = false  → point light (torch, lantern, etc.)
//   isDirectional = true   → directional light (sun); drawn fullscreen, no falloff
//
// For fuel-consuming light sources (e.g. torch), set fuelBuilding after AddComponent.
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
    public float lightHeight = 2f;

    [Header("Directional (sun)")]
    public bool isDirectional = false;

    /// <summary>Set to the Building that owns this light's fuel inventory. Null = no fuel needed (always lit).</summary>
    [HideInInspector] public Building fuelBuilding;
    /// <summary>False when fuel has run out; SunController sets intensity to 0 when false.</summary>
    public bool isLit = true;

    public static readonly List<LightSource> all = new();

    // Fractional-fen accumulator so sub-fen burn rates work correctly across frames.
    private float _fuelAccumulator = 0f;

    void OnEnable()  => all.Add(this);
    void OnDisable() => all.Remove(this);

    void Update() {
        if (fuelBuilding?.fuelInv == null) return; // no fuel needed — always lit

        Item fuelItem = fuelBuilding.structType.fuelItem;
        // Convert liang/day burn rate to fen/second:
        //   burnRate (liang/day) × 100 (fen/liang) / ticksInDay (seconds/day)
        float fenPerSecond = fuelBuilding.structType.fuelBurnRate * 100f / World.ticksInDay;
        _fuelAccumulator += fenPerSecond * Time.deltaTime;

        if (_fuelAccumulator >= 1f) {
            int toConsume = Mathf.FloorToInt(_fuelAccumulator);
            // Consume from the actual leaf stacks (fuelItem may be a group like "wood").
            int remaining = toConsume;
            foreach (ItemStack stack in fuelBuilding.fuelInv.itemStacks) {
                if (stack.item == null || stack.quantity == 0 || remaining <= 0) continue;
                int fromThisStack = Mathf.Min(remaining, stack.quantity);
                fuelBuilding.fuelInv.Produce(stack.item, -fromThisStack); // remove from inv + ginv
                remaining -= fromThisStack;
            }
            int consumed = toConsume - remaining;
            _fuelAccumulator -= consumed > 0 ? consumed : toConsume;
            if (_fuelAccumulator < 0f) _fuelAccumulator = 0f;
        }

        isLit = fuelBuilding.fuelInv.Quantity(fuelItem) > 0;
    }
}

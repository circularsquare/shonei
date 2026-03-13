using UnityEngine;

// Central registry for all gameplay modifier constants and query methods.
//
// To add a new modifier:
//   1. Add a named constant below.
//   2. Update (or add) the relevant query method to include it.
//
// Scope: intentional design modifiers — tools, research, buildings, traits.
// Out of scope: hunger/sleep penalties, which are biological and live on Animal.efficiency.
public class ModifierSystem {
    public static readonly ModifierSystem instance = new ModifierSystem();

    // --- Constants ---
    public const float ToolWorkBonus               = 1.25f;
    public const float ResearchEfficiencyPerUnlock = 1.2f;
    public const float BaseAnimalSpeed             = 1.5f;

    // --- Query methods ---

    // Combined work speed multiplier for an animal (efficiency × tool bonus).
    public float GetWorkMultiplier(Animal animal) {
        bool hasTool = animal.toolSlotInv.itemStacks[0].item != null;
        float toolMult = hasTool ? ToolWorkBonus : 1f;
        return animal.efficiency * toolMult;
    }

    // Research speed multiplier from unlocked research nodes.
    public float GetResearchMultiplier() {
        if (ResearchSystem.instance == null) return 1f;
        int unlocks = ResearchSystem.instance.CountUnlocks("research_efficiency");
        return Mathf.Pow(ResearchEfficiencyPerUnlock, unlocks);
    }

    // Travel speed for an animal (base × efficiency).
    public float GetTravelSpeedMultiplier(Animal animal) {
        return BaseAnimalSpeed * animal.efficiency;
        // future: × speed research bonus, × road tile bonus, etc.
    }

    // Recipe output multiplier (placeholder for future bonuses).
    public float GetRecipeOutputMultiplier() {
        return 1f;
    }
}

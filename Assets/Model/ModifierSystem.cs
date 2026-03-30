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
    public const float FloorItemSpeedPenalty        = 0.8f;
    public const float CrowdingSpeedPenalty         = 0.8f;

    // --- Query methods ---

    // Combined work speed multiplier for an animal (efficiency × tool bonus × skill level bonus).
    // Pass skill=null for tasks that have no associated skill domain (e.g. hauling).
    public float GetWorkMultiplier(Animal animal, Skill? skill = null) {
        bool hasTool = animal.toolSlotInv.itemStacks[0].item != null;
        float toolMult  = hasTool ? ToolWorkBonus : 1f;
        float skillMult = skill.HasValue ? animal.skills.GetBonus(skill.Value) : 1f;
        return animal.efficiency * toolMult * skillMult;
    }

    // Base work efficiency before the skill bonus — used to calculate XP gain so that
    // the skill bonus doesn't accelerate its own XP accumulation.
    public float GetBaseWorkEfficiency(Animal animal) {
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

    // Travel speed for an animal on its current tile.
    // Combines base speed, efficiency, road bonus, and tile-based penalties.
    // All factors are multiplicative.
    public float GetTravelSpeedMultiplier(Animal animal) {
        float speed = BaseAnimalSpeed * animal.efficiency;

        Tile tile = animal.TileHere();
        if (tile == null) return speed;

        // Road bonus: per-tile only (not averaged with destination).
        // Doubles pathCostReduction to match the old two-endpoint system's feel.
        float roadReduction = tile.structs[3]?.structType.pathCostReduction ?? 0f;
        if (roadReduction > 0f)
            speed /= Mathf.Max(0.1f, 1.0f - roadReduction * 2f);

        // Floor items on tile: 25% slowdown (storage inventories don't count)
        if (tile.inv != null && tile.inv.invType == Inventory.InvType.Floor && !tile.inv.IsEmpty())
            speed *= FloorItemSpeedPenalty;

        // Crowding: any other mice on this tile = 25% slowdown
        if (AnimalController.instance.HasMultipleAnimalsOnTile(tile))
            speed *= CrowdingSpeedPenalty;

        return speed;
    }

    // Recipe output multiplier (placeholder for future bonuses).
    public float GetRecipeOutputMultiplier() {
        return 1f;
    }
}

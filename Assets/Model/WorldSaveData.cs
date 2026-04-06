using System.Collections.Generic;

// Save data classes used with Newtonsoft.Json only — no [Serializable] needed.
// [Serializable] would cause Unity's serializer to materialize default instances
// on MonoBehaviour fields (e.g. pendingSaveData) instead of leaving them null.

public class WorldSaveData {
    public float timer;
    public TileSaveData[] tiles;             // tile types and floor inventories only
    public StructureSaveData[] structures;   // all structures (buildings, plants, platforms, ladders, roads)
    public BlueprintSaveData[] blueprints;   // all blueprints
    public AnimalSaveData[] animals;
    public ResearchSaveData research;
    public int[] disabledRecipeIds; // null = all enabled
    public ushort[] waterLevels;    // flat array, index = y * nx + x; null if all-dry
    public bool isRaining;          // false = clear (safe default for old saves)
    // Global item targets set by the player via ItemDisplay UI (item name → target qty in fen).
    // Only non-default entries (≠ 10000) are stored; absent entries load as default (10000).
    public System.Collections.Generic.Dictionary<string, int> globalItemTargets;
    public float? cameraX; // null on old saves → camera not repositioned on load
    public float? cameraY;
    // Market targets set by the player (item name → quantity in fen). null = no market or all zero.
    public System.Collections.Generic.Dictionary<string, int> marketTargets;
}

public class ResearchSaveData {
    public System.Collections.Generic.Dictionary<int, float> progress;
    public int   activeResearchId;
    public int[] unlockedIds;
    public int[] maintainIds;  // null on old saves → empty set
}

public class TileSaveData {
    public int x, y;
    public string tileType;
    public InventorySaveData inv; // floor inventory only; storage building inventories are filled after structures are restored
}

public class StructureSaveData {
    public int x, y; // anchor (bottom-left) tile position
    public string typeName;
    // Plant-specific fields (only populated when structure is a Plant)
    public int plantAge;
    public int plantGrowthStage;
    public bool plantHarvestable;
    public int uses;
    // Workstation-only: persists the player-set worker slot limit (Building.workstation.workerLimit).
    // null = field absent (old saves) → defaults to full capacity on load.
    // 0 = explicitly disabled. 1..capacity = player-set limit.
    public int? workOrderEffectiveCapacity;
    // Fuel buildings only: leaf-item stack data for building.fuel.inv at save time.
    // null = no fuel inv, or inv was empty (treated as empty on load).
    public InventorySaveData fuelInvData;
    // Storage buildings only: the building's storage inventory data.
    // null = no storage, or storage was empty with default config.
    public InventorySaveData storageInvData;
    public bool mirrored;
    // Building-only: player-set disabled state. false on old saves (default).
    public bool disabled;
}

public class BlueprintSaveData {
    public int x, y; // anchor tile position
    public string typeName;
    public int state; // Blueprint.BlueprintState cast to int
    public float constructionProgress;
    public InventorySaveData inv;
    public int priority = 0;
    public bool mirrored;
    public bool disabled;
}

public class InventorySaveData {
    public string invType;
    public ItemStackSaveData[] stacks;
    public int[] allowedItemIds; // item IDs explicitly allowed; only saved for Storage/Liquid types (others default to all-allowed)
    // Market inventories only: item name → target quantity in fen. null on all other inventory types.
    public System.Collections.Generic.Dictionary<string, int> marketTargets;
}

public class ItemStackSaveData {
    public string itemName; // empty string for empty stack
    public int quantity;
    public int decayCounter;
}

public class AnimalSaveData {
    public string aName;
    public float x, y;
    public string jobName;
    public float energy;
    public float food;
    public float eep;
    public Dictionary<string, float> satisfactions;
    public float warmth;
    public InventorySaveData inv;
    public InventorySaveData foodSlotInv;
    public InventorySaveData toolSlotInv;
    public InventorySaveData clothingSlotInv;
    public float[] skillXp;
    public int[]   skillLevel;
    public bool  isTraveling;     // was animal mid-journey (hidden) at save time?
    public float travelProgress;  // ticks elapsed so far in current travel leg
    public int   travelDuration;  // total ticks for current travel leg
}

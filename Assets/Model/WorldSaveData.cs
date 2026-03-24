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
}

public class ResearchSaveData {
    public System.Collections.Generic.Dictionary<int, float> progress;
    public int   activeResearchId;
    public int[] unlockedIds;
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
    // Workstation-only: persists the WOM Craft order's effectiveCapacity (player-set worker slot limit).
    // null = field absent (old saves) → RegisterWorkstation defaults to full capacity.
    // 0 = explicitly disabled. 1..capacity = player-set limit.
    public int? workOrderEffectiveCapacity;
}

public class BlueprintSaveData {
    public int x, y; // anchor tile position
    public string typeName;
    public int state; // Blueprint.BlueprintState cast to int
    public float constructionProgress;
    public InventorySaveData inv;
    public int priority = 0;
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
    public float timeSinceAteWheat;   // happiness
    public float timeSinceAteFruit;   // happiness
    public float timeSinceAteSoymilk; // happiness
    public InventorySaveData inv;
    public InventorySaveData foodSlotInv; // null on old saves → slot starts empty
    public InventorySaveData toolSlotInv;
    public float[] skillXp;    // null on old saves → all skills start at 0 xp
    public int[]   skillLevel; // null on old saves → all skills start at level 0
}

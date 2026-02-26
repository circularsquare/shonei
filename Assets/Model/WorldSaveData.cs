// Save data classes used with Newtonsoft.Json only — no [Serializable] needed.
// [Serializable] would cause Unity's serializer to materialize default instances
// on MonoBehaviour fields (e.g. pendingSaveData) instead of leaving them null.

public class WorldSaveData {
    public float timer;
    public TileSaveData[] tiles;
    public AnimalSaveData[] animals;
}

public class TileSaveData {
    public int x, y;
    public string tileType;
    public StructureSaveData building;
    public StructureSaveData mStruct;
    public StructureSaveData fStruct;
    public BlueprintSaveData bBlueprint;
    public BlueprintSaveData mBlueprint;
    public BlueprintSaveData fBlueprint;
    public InventorySaveData inv;
}

public class StructureSaveData {
    public string typeName;
    // Plant-specific fields (only populated when structure is a Plant)
    public int plantAge;
    public int plantGrowthStage;
    public bool plantHarvestable;
}

public class BlueprintSaveData {
    public string typeName;
    public int state; // Blueprint.BlueprintState cast to int
    public float constructionProgress;
    public ItemQuantitySaveData[] deliveredResources;
}

public class ItemQuantitySaveData {
    public string itemName;
    public int quantity;
}

public class InventorySaveData {
    public int nStacks;
    public int stackSize;
    public string invType;
    public ItemStackSaveData[] stacks;
    public int[] disallowedItemIds; // item IDs that have been explicitly disallowed
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
    public float food;  // eating.food
    public float eep;   // eeping.eep
    public InventorySaveData inv;
}

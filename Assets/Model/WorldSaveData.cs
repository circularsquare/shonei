using System.Collections.Generic;

// Save data classes used with Newtonsoft.Json only — no [Serializable] needed.
// [Serializable] would cause Unity's serializer to materialize default instances
// on MonoBehaviour fields (e.g. pendingSaveData) instead of leaving them null.

public class WorldSaveData {
    public float timer;
    // RNG seed used to drive Rng (gameplay randomness). 0 on old saves → reproducibility
    // for those starts from 0 on load, which is fine — they had no reproducibility before.
    public int worldSeed;
    public TileSaveData[] tiles;             // tile types and floor inventories only
    public StructureSaveData[] structures;   // all structures (buildings, plants, platforms, ladders, roads)
    public BlueprintSaveData[] blueprints;   // all blueprints
    public AnimalSaveData[] animals;
    public ResearchSaveData research;
    public int[] disabledRecipeIds; // null = all enabled
    public ushort[] waterLevels;    // flat array, index = y * nx + x; null if all-dry
    public byte[] moistureLevels;   // flat array, same layout as waterLevels; null if every tile is dry soil
    public bool isRaining;          // false = clear (safe default for old saves)
    // Global item targets set by the player via ItemDisplay UI (item name → target qty in fen).
    // Only non-default entries (≠ 10000) are stored; absent entries load as default (10000).
    public Dictionary<string, int> globalItemTargets;
    public float? cameraX;  // null on old saves → camera not repositioned on load
    public float? cameraY;
    public int?   cameraPPU; // null on old saves → zoom not changed on load
    // Market targets set by the player (item name → quantity in fen). null = no market or all zero.
    public Dictionary<string, int> marketTargets;
    // Inventory tree open/collapsed state for the always-visible global panel.
    // Only deltas vs item.defaultOpen are stored (group item name → current open state), so the
    // dict stays small and new items automatically pick up their JSON default. Null on old saves.
    public Dictionary<string, bool> inventoryTreeOpen;
}

public class ResearchSaveData {
    public Dictionary<int, float> progress;
    public int[] unlockedIds;
    public int[] studiedIds;   // was maintainIds; null on old saves → empty set
    public Dictionary<int, int> unlockTimestamps; // null on old saves → derive from unlockedIds order
    public int   unlockCounter;

    // Legacy fields — read for migration, never written.
    public int   activeResearchId;   // old saves only; migrated into studiedIds
    public int[] maintainIds;        // old saves only; migrated into studiedIds
}

public class TileSaveData {
    public int x, y;
    public string tileType;
    public InventorySaveData inv;     // floor inventory only; storage building inventories are filled after structures are restored
    public int backgroundWallType;    // BackgroundType enum (0=None, 1=Stone, 2=Dirt) — authoritative
    public bool hasBackgroundWall;    // legacy: read for migration of pre-typed saves; no longer written
}

public class StructureSaveData {
    public int x, y; // anchor (bottom-left) tile position
    public string typeName;
    // Plant-specific fields (only populated when structure is a Plant)
    public int plantAge;
    public int plantGrowthStage;
    public bool plantHarvestable;
    // Nullable so old saves deserialize as null → treated as false → plant is unflagged on load.
    // Only saved when true (compact-when-false, matching the `disabled` pattern).
    public bool? plantHarvestFlagged;
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
    // 90° clockwise rotation steps (0..3). Default 0 covers old saves.
    public int rotation;
    // Variable-shape index. Default 0 covers old saves and StructTypes without `shapes`
    // — both resolve to "first authored shape" / base nx,ny via StructType.GetShape.
    public int shapeIndex;
    // Building-only: player-set disabled state. false on old saves (default).
    public bool disabled;
    // Structure maintenance condition, 0..1. Set to 1.0 by GatherStructure when saving; old
    // saves (field absent) deserialize as 0 which the restore path treats as "missing → default
    // to 1.0" so pre-maintenance saves don't load every structure as broken.
    public float condition;
    // Quarry only: name of the stone tile the quarry was placed on. Drives the
    // per-stone extraction distribution. null on non-quarry / old saves.
    public string capturedTileType;
    // Flywheel only: stored mechanical-power energy. 0 on non-flywheels / old saves.
    // Without this, flywheels would reset to empty on every load and surrender any
    // energy buffered during the play session.
    public float flywheelCharge;
    // Elevator only: platform's last continuous Y position in tile units (0 = bottom of
    // chassis, ny-1 = top). 0 on non-elevators / old saves — which lands the platform at
    // the bottom on load, fine since dispatch state is reset to Idle anyway.
    public float elevatorCurrentY;
    // Elevator only: rolling history buffers for the cost estimator and InfoPanel display.
    // recentTripTicks feeds avgTrip in EstimatedTransitCost; recentEndToEndTicks is purely
    // diagnostic. null on non-elevators / old saves; restore path treats null as empty.
    public int[] elevatorRecentTripTicks;
    public int[] elevatorRecentEndToEndTicks;
}

public class BlueprintSaveData {
    public int x, y; // anchor tile position
    public string typeName;
    public int state; // Blueprint.BlueprintState cast to int
    public float constructionProgress;
    public InventorySaveData inv;
    public int priority = 0;
    public bool mirrored;
    public int rotation;
    // Variable-shape index. Default 0 covers old saves and StructTypes without `shapes`.
    public int shapeIndex;
    public bool disabled;
}

public class InventorySaveData {
    public string invType;
    public ItemStackSaveData[] stacks;
    public int[] allowedItemIds; // item IDs explicitly allowed; only saved for Storage/Liquid types (others default to all-allowed)
    // Market inventories only: item name → target quantity in fen. null on all other inventory types.
    public Dictionary<string, int> marketTargets;
}

public class ItemStackSaveData {
    public string itemName; // empty string for empty stack
    public int quantity;
    public int decayCounter;
}

public class AnimalSaveData {
    public string aName;
    public float x, y;
    // Per-animal RNG seed. 0 on old saves → Animal.Start re-seeds via Rng.NextInt
    // and persists from then on. Stable across save/load so animal AI is reproducible.
    public int rngSeed;
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
    public InventorySaveData bookSlotInv;
    public float[] skillXp;
    public int[]   skillLevel;
    public bool  isTraveling;     // was animal mid-journey (hidden) at save time?
    public float travelProgress;  // ticks elapsed so far in current travel leg
    public int   travelDuration;  // total ticks for current travel leg

    // Task descriptor so a mid-journey merchant resumes the full task tail (deliver
    // to market / receive + travel back + deliver to storage), not just the remaining
    // travel ticks. Null taskType indicates a legacy save — falls back to ResumeTravelTask.
    public string travelTaskType;     // "HaulToMarket" | "HaulFromMarket" | null
    public string travelItemName;
    public int    travelItemQty;      // fen
    public int?   travelStorageX;     // HaulFromMarket only — destination Building.storage tile
    public int?   travelStorageY;
    public bool   travelReturnLeg;    // HaulFromMarket only — true if past the ReceiveFromInventory
}

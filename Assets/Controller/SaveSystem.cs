using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

// Handles saving and loading world state to/from JSON files.
//
// -----------------------------------------------------------------------
// ADDING NEW SAVEABLE DATA — checklist for future changes:
//   1. Add fields to the relevant class in WorldSaveData.cs
//      - Top-level world data (timer, etc.)  → WorldSaveData
//      - Per-tile data (building, mStruct, fStruct, road, blueprints) → TileSaveData
//      - Per-structure data                   → StructureSaveData
//      - Per-blueprint data                   → BlueprintSaveData
//      - Per-inventory data                   → InventorySaveData
//      - Per-animal data                      → AnimalSaveData
//      - Research system data                 → ResearchSaveData
//   2. Add a Gather* method in the SAVE section and call it from the
//      appropriate parent (GatherSaveData, GatherTile, GatherAnimal, etc.)
//   3. Add a Restore* method in the LOAD section and call it from the
//      appropriate parent (ApplySaveData, RestoreTile, etc.)
// -----------------------------------------------------------------------

public class SaveSystem : MonoBehaviour {
    public static SaveSystem instance;

    string SaveDir {
        get {
#if UNITY_EDITOR
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../SaveData"));
#else
            return System.IO.Path.Combine(Application.persistentDataPath, "saves");
#endif
        }
    }

    void Awake() {
        if (instance != null) { Debug.LogError("there should only be one SaveSystem"); }
        instance = this;
        if (!System.IO.Directory.Exists(SaveDir)) System.IO.Directory.CreateDirectory(SaveDir);
    }

    // -----------------------------------------------------------------------
    // SAVE
    // -----------------------------------------------------------------------

    public void Save(string slotName) {
        WorldSaveData data = GatherSaveData();
        string json = JsonConvert.SerializeObject(data, Formatting.Indented,
            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        System.IO.File.WriteAllText(SlotPath(slotName), json);
        Debug.Log("Saved world to slot: " + slotName);
    }

    WorldSaveData GatherSaveData() {
        World world = World.instance;
        WorldSaveData data = new WorldSaveData();
        data.timer = world.timer;

        var tiles = new List<TileSaveData>();
        for (int x = 0; x < world.nx; x++) {
            for (int y = world.ny - 1; y >= 0; y--) {
                TileSaveData tsd = GatherTile(world.GetTileAt(x, y));
                if (tsd != null) tiles.Add(tsd);
            }
        }
        data.tiles = tiles.ToArray();

        AnimalController ac = AnimalController.instance;
        var animals = new List<AnimalSaveData>();
        for (int i = 0; i < ac.na; i++) {
            animals.Add(GatherAnimal(ac.animals[i]));
        }
        data.animals = animals.ToArray();

        var rs = ResearchSystem.instance;
        if (rs != null) {
            var ids = new int[rs.unlockedIds.Count];
            rs.unlockedIds.CopyTo(ids);
            data.research = new ResearchSaveData {
                pointHistory = (float[])rs.pointHistory.Clone(),
                historyIndex = rs.historyIndex,
                totalSpent   = rs.totalSpent,
                tickCounter  = rs.tickCounter,
                unlockedIds  = ids
            };
        }

        return data;
    }

    TileSaveData GatherTile(Tile tile) {
        bool hasContent =
            tile.type.name != "empty" ||
            tile.building != null ||
            tile.mStruct != null ||
            tile.fStruct != null ||
            tile.road != null ||
            tile.bBlueprint != null ||
            tile.mBlueprint != null ||
            tile.fBlueprint != null ||
            tile.roadBlueprint != null ||
            (tile.inv != null && !tile.inv.IsEmpty());
        if (!hasContent) return null;

        return new TileSaveData {
            x = tile.x,
            y = tile.y,
            tileType   = tile.type.name,
            building      = tile.building      != null ? GatherStructure(tile.building)      : null,
            mStruct       = tile.mStruct       != null ? GatherStructure(tile.mStruct)       : null,
            fStruct       = tile.fStruct       != null ? GatherStructure(tile.fStruct)       : null,
            road          = tile.road          != null ? GatherStructure(tile.road)          : null,
            bBlueprint    = tile.bBlueprint    != null ? GatherBlueprint(tile.bBlueprint)    : null,
            mBlueprint    = tile.mBlueprint    != null ? GatherBlueprint(tile.mBlueprint)    : null,
            fBlueprint    = tile.fBlueprint    != null ? GatherBlueprint(tile.fBlueprint)    : null,
            roadBlueprint = tile.roadBlueprint != null ? GatherBlueprint(tile.roadBlueprint) : null,
            inv           = tile.inv           != null ? GatherInventory(tile.inv)           : null,
        };
    }

    StructureSaveData GatherStructure(Structure s) {
        var ssd = new StructureSaveData { typeName = s.structType.name };
        if (s is Plant plant) {
            ssd.plantAge         = plant.age;
            ssd.plantGrowthStage = plant.growthStage;
            ssd.plantHarvestable = plant.harvestable;
        }
        if (s is Building b && b.uses > 0) ssd.uses = b.uses;
        return ssd;
    }

    BlueprintSaveData GatherBlueprint(Blueprint bp) {
        return new BlueprintSaveData {
            typeName             = bp.structType.name,
            state                = (int)bp.state,
            constructionProgress = bp.constructionProgress,
            inv                  = bp.costs.Length > 0 ? GatherInventory(bp.inv) : null,
            priority             = bp.priority
        };
    }

    InventorySaveData GatherInventory(Inventory inv) {
        var stacks = new ItemStackSaveData[inv.itemStacks.Length];
        for (int i = 0; i < inv.itemStacks.Length; i++) {
            ItemStack stack = inv.itemStacks[i];
            stacks[i] = new ItemStackSaveData {
                itemName     = stack.item?.name ?? "",
                quantity     = stack.quantity,
                decayCounter = stack.decayCounter
            };
        }
        var disallowed = new List<int>();
        foreach (var kv in inv.allowed) {
            if (!kv.Value) disallowed.Add(kv.Key);
        }
        return new InventorySaveData {
            nStacks            = inv.nStacks,
            stackSize          = inv.stackSize,
            invType            = inv.invType.ToString(),
            stacks             = stacks,
            disallowedItemIds  = disallowed.Count > 0 ? disallowed.ToArray() : null
        };
    }

    AnimalSaveData GatherAnimal(Animal a) {
        return new AnimalSaveData {
            aName              = a.aName,
            x                  = a.x,
            y                  = a.y,
            jobName            = a.job.name,
            energy             = a.energy,
            food               = a.eating.food,
            eep                = a.eeping.eep,
            timeSinceAteWheat  = a.happiness.timeSinceAteWheat,
            timeSinceAteFruit  = a.happiness.timeSinceAteFruit,
            inv                = GatherInventory(a.inv),
            foodSlotInv        = GatherInventory(a.foodSlotInv),
            toolSlotInv        = GatherInventory(a.toolSlotInv),
        };
    }

    // -----------------------------------------------------------------------
    // LOAD
    // -----------------------------------------------------------------------

    public void Load(string slotName) {
        string path = SlotPath(slotName);
        if (!System.IO.File.Exists(path)) { Debug.LogError("Save slot not found: " + slotName); return; }
        string json = System.IO.File.ReadAllText(path);
        WorldSaveData data = JsonConvert.DeserializeObject<WorldSaveData>(json);
        WorldController.instance.ClearWorld();
        ApplySaveData(data);

        if (data.research != null && ResearchSystem.instance != null) {
            var rs = ResearchSystem.instance;
            if (data.research.pointHistory != null)
                System.Array.Copy(data.research.pointHistory, rs.pointHistory,
                    System.Math.Min(data.research.pointHistory.Length, rs.pointHistory.Length));
            rs.historyIndex = data.research.historyIndex;
            rs.totalSpent   = data.research.totalSpent;
            rs.tickCounter  = data.research.tickCounter;
            rs.unlockedIds.Clear();
            if (data.research.unlockedIds != null)
                foreach (int id in data.research.unlockedIds)
                    rs.unlockedIds.Add(id);
            rs.ReapplyAllEffects();
        }

        StartCoroutine(PostLoadInit());
    }

    void ApplySaveData(WorldSaveData save) {
        World world = World.instance;
        world.timer = save.timer;

        foreach (TileSaveData tsd in save.tiles) {
            Tile tile = world.GetTileAt(tsd.x, tsd.y);
            if (tile == null) continue;

            if (!string.IsNullOrEmpty(tsd.tileType) && Db.tileTypeByName.ContainsKey(tsd.tileType))
                tile.type = Db.tileTypeByName[tsd.tileType];

            // Blueprints before structures so deconstruct blueprints can coexist with structures
            if (tsd.bBlueprint    != null) RestoreBlueprint(tsd.bBlueprint,    tile);
            if (tsd.mBlueprint    != null) RestoreBlueprint(tsd.mBlueprint,    tile);
            if (tsd.fBlueprint    != null) RestoreBlueprint(tsd.fBlueprint,    tile);
            if (tsd.roadBlueprint != null) RestoreBlueprint(tsd.roadBlueprint, tile);

            if (tsd.building != null) RestoreStructure(tsd.building, tile);
            if (tsd.mStruct  != null) RestoreStructure(tsd.mStruct,  tile);
            if (tsd.fStruct  != null) RestoreStructure(tsd.fStruct,  tile);
            if (tsd.road     != null) RestoreStructure(tsd.road,     tile);

            if (tsd.inv != null) RestoreInventory(tsd.inv, tile);
        }

        world.graph.Initialize();

        if (save.animals != null)
            foreach (AnimalSaveData asd in save.animals)
                AnimalController.instance.LoadAnimal(asd);

        InventoryController.instance.ValidateGlobalInventory();
    }

    void RestoreStructure(StructureSaveData ssd, Tile tile) {
        if (!Db.structTypeByName.ContainsKey(ssd.typeName)) {
            Debug.LogError("Unknown struct type on load: " + ssd.typeName); return;
        }
        StructType st = Db.structTypeByName[ssd.typeName];
        Structure structure = null;

        if (st.isPlant) {
            Plant plant = new Plant(st as PlantType, tile.x, tile.y);
            plant.age          = ssd.plantAge;
            plant.growthStage  = ssd.plantGrowthStage;
            plant.harvestable  = ssd.plantHarvestable;
            plant.UpdateSprite();
            structure = plant;
        } else if (st.depth == "b") {
            structure = new Building(st, tile.x, tile.y) { uses = ssd.uses };
        } else if (st.name == "platform") {
            structure = new Platform(st, tile.x, tile.y);
        } else if (st.name == "stairs") {
            structure = new Stairs(st, tile.x, tile.y);
        } else if (st.name == "ladder") {
            structure = new Ladder(st, tile.x, tile.y);
        } else if (st.depth == "r") {
            structure = new Structure(st, tile.x, tile.y);
            tile.road = structure;
        } else {
            Debug.LogError("Unhandled struct type on load: " + ssd.typeName); return;
        }

        if (structure != null) {
            StructController.instance.Place(structure);
            World.instance.graph.UpdateNeighbors(tile.x, tile.y);
            World.instance.graph.UpdateNeighbors(tile.x, tile.y + 1);
        }
    }

    void RestoreBlueprint(BlueprintSaveData bsd, Tile tile) {
        if (!Db.structTypeByName.ContainsKey(bsd.typeName)) {
            Debug.LogError("Unknown blueprint struct type on load: " + bsd.typeName); return;
        }
        StructType st = Db.structTypeByName[bsd.typeName];
        Blueprint bp = new Blueprint(st, tile.x, tile.y);
        bp.state                = (Blueprint.BlueprintState)bsd.state;
        bp.constructionProgress = bsd.constructionProgress;
        bp.priority             = bsd.priority;

        if (bsd.inv != null) {
            for (int i = 0; i < bsd.inv.stacks.Length && i < bp.inv.itemStacks.Length; i++) {
                var ssd = bsd.inv.stacks[i];
                if (!string.IsNullOrEmpty(ssd.itemName) && Db.itemByName.ContainsKey(ssd.itemName) && ssd.quantity > 0) {
                    bp.inv.itemStacks[i].item         = Db.itemByName[ssd.itemName];
                    bp.inv.itemStacks[i].quantity      = ssd.quantity;
                    bp.inv.itemStacks[i].res.capacity  = ssd.quantity;
                    bp.inv.itemStacks[i].res.reserved  = 0;
                    GlobalInventory.instance.AddItem(Db.itemByName[ssd.itemName], ssd.quantity);
                }
            }
        }
        bp.RefreshColor();
    }

    void RestoreInventory(InventorySaveData isd, Tile tile) {
        Inventory inv = tile.inv ?? tile.EnsureFloorInventory();

        for (int i = 0; i < isd.stacks.Length && i < inv.itemStacks.Length; i++) {
            ItemStackSaveData ssd = isd.stacks[i];
            if (!string.IsNullOrEmpty(ssd.itemName) && Db.itemByName.ContainsKey(ssd.itemName) && ssd.quantity > 0) {
                Item item = Db.itemByName[ssd.itemName];
                inv.itemStacks[i].item         = item;
                inv.itemStacks[i].quantity      = ssd.quantity;
                inv.itemStacks[i].decayCounter  = ssd.decayCounter;
                inv.itemStacks[i].res.capacity  = ssd.quantity;
                inv.itemStacks[i].res.reserved  = 0;
                GlobalInventory.instance.AddItem(item, ssd.quantity);
            }
        }
        foreach (Item item in Db.itemsFlat) { inv.AllowItem(item); }
        if (isd.disallowedItemIds != null)
            foreach (int id in isd.disallowedItemIds)
                if (id < Db.items.Length && Db.items[id] != null)
                    inv.DisallowItem(Db.items[id]);

        inv.UpdateSprite();
    }

    // Restores items from save data into an existing inventory instance (used by AnimalController).
    public static void LoadInventory(Inventory inv, InventorySaveData data) {
        foreach (ItemStackSaveData ssd in data.stacks) {
            if (!string.IsNullOrEmpty(ssd.itemName) && Db.itemByName.ContainsKey(ssd.itemName) && ssd.quantity > 0)
                inv.Produce(Db.itemByName[ssd.itemName], ssd.quantity);
        }
    }

    // -----------------------------------------------------------------------
    // LIFECYCLE
    // -----------------------------------------------------------------------

    // FRAME 2 — one frame after GenerateDefault / ApplySaveData.
    // By this point all Animal.Start() calls have run. Safe to call Load() / UpdateColonyStats().
    // Started on all three paths: initial world gen, Reset, and Load.
    public IEnumerator PostLoadInit() {
        yield return null;
        AnimalController.instance.Load();
    }

    public void Reset() {
        WorldController.instance.ClearWorld();
        WorldController.instance.GenerateDefault();
        StartCoroutine(PostLoadInit());
    }

    // -----------------------------------------------------------------------
    // SLOTS
    // -----------------------------------------------------------------------

    public List<string> GetSaveSlots() {
        var slots = new List<string>();
        if (System.IO.Directory.Exists(SaveDir))
            foreach (string file in System.IO.Directory.GetFiles(SaveDir, "*.json"))
                slots.Add(System.IO.Path.GetFileNameWithoutExtension(file));
        return slots;
    }

    public bool SlotExists(string slotName) => System.IO.File.Exists(SlotPath(slotName));

    string SlotPath(string slotName) => System.IO.Path.Combine(SaveDir, slotName + ".json");
}

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;

// Handles saving and loading world state to/from JSON files.
//
// -----------------------------------------------------------------------
// ADDING NEW SAVEABLE DATA — checklist for future changes:
//   1. Add fields to the relevant class in WorldSaveData.cs
//      - Top-level world data (timer, etc.)  → WorldSaveData
//      - Per-tile data (structs[4], blueprints[4])                    → TileSaveData
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
    public static SaveSystem instance { get; protected set; }

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

        // Water levels — only write if any tile has water (keeps save files clean for dry worlds)
        bool anyWater = false;
        ushort[] wl = new ushort[world.nx * world.ny];
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                ushort w = world.GetTileAt(x, y).water;
                wl[y * world.nx + x] = w;
                if (w > 0) anyWater = true;
            }
        }
        if (anyWater) data.waterLevels = wl;

        var structures = new List<StructureSaveData>();
        foreach (Structure s in StructController.instance.GetStructures())
            structures.Add(GatherStructure(s));
        data.structures = structures.ToArray();

        var blueprints = new List<BlueprintSaveData>();
        foreach (Blueprint bp in StructController.instance.GetBlueprints())
            blueprints.Add(GatherBlueprint(bp));
        data.blueprints = blueprints.ToArray();

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
                progress         = new System.Collections.Generic.Dictionary<int, float>(rs.progress),
                activeResearchId = rs.activeResearchId,
                unlockedIds      = ids
            };
        }

        var rp = RecipePanel.instance;
        if (rp != null) {
            var disabled = new int[rp.DisabledCount];
            rp.CopyDisabledIds(disabled);
            if (disabled.Length > 0) data.disabledRecipeIds = disabled;
        }

        data.isRaining = WeatherSystem.instance?.isRaining ?? false;

        var ic = InventoryController.instance;
        if (ic?.targets != null) {
            var savedTargets = new Dictionary<string, int>();
            foreach (var kv in ic.targets) {
                if (kv.Value == 10000) continue; // skip default — no need to save
                Item item = kv.Key < Db.items.Length ? Db.items[kv.Key] : null;
                if (item != null) savedTargets[item.name] = kv.Value;
            }
            if (savedTargets.Count > 0) data.globalItemTargets = savedTargets;
        }

        return data;
    }

    TileSaveData GatherTile(Tile tile) {
        bool hasContent =
            tile.type.name != "empty" ||
            (tile.inv != null && !tile.inv.IsEmpty());
        if (!hasContent) return null;

        return new TileSaveData {
            x        = tile.x,
            y        = tile.y,
            tileType = tile.type.name,
            inv      = tile.inv != null ? GatherInventory(tile.inv) : null,
        };
    }

    StructureSaveData GatherStructure(Structure s) {
        var ssd = new StructureSaveData { x = s.x, y = s.y, typeName = s.structType.name };
        if (s is Plant plant) {
            ssd.plantAge         = plant.age;
            ssd.plantGrowthStage = plant.growthStage;
            ssd.plantHarvestable = plant.harvestable;
        }
        if (s is Building b) {
            if (b.uses > 0) ssd.uses = b.uses;
            if (b.structType.isWorkstation && WorkOrderManager.instance != null) {
                var order = WorkOrderManager.instance.FindOrdersForBuilding(b)
                    .FirstOrDefault(o => o.type == WorkOrderManager.OrderType.Craft);
                if (order != null)
                    ssd.workOrderEffectiveCapacity = order.res.effectiveCapacity;
            }
        }
        return ssd;
    }

    BlueprintSaveData GatherBlueprint(Blueprint bp) {
        return new BlueprintSaveData {
            x                    = bp.x,
            y                    = bp.y,
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
        // Only Storage/Liquid use explicit allow lists — Floor/Animal default to all-allowed so skip them.
        int[] allowedIds = null;
        if (inv.invType == Inventory.InvType.Storage || inv.invType == Inventory.InvType.Liquid) {
            var allowedList = new List<int>();
            foreach (var kv in inv.allowed)
                if (kv.Value) allowedList.Add(kv.Key);
            allowedIds = allowedList.Count > 0 ? allowedList.ToArray() : null;
        }

        System.Collections.Generic.Dictionary<string, int> marketTargets = null;
        if (inv.targets != null) {
            marketTargets = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var kv in inv.targets)
                if (kv.Value != 0) marketTargets[kv.Key.name] = kv.Value;
        }

        return new InventorySaveData {
            invType        = inv.invType.ToString(),
            stacks         = stacks,
            allowedItemIds = allowedIds,
            marketTargets  = marketTargets != null && marketTargets.Count > 0 ? marketTargets : null
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
            timeSinceAteWheat   = a.happiness.timeSinceAteWheat,
            timeSinceAteFruit   = a.happiness.timeSinceAteFruit,
            timeSinceAteSoymilk = a.happiness.timeSinceAteSoymilk,
            inv                = GatherInventory(a.inv),
            foodSlotInv        = GatherInventory(a.foodSlotInv),
            toolSlotInv        = GatherInventory(a.toolSlotInv),
            skillXp            = a.skills.SerializeXp(),
            skillLevel         = a.skills.SerializeLevel(),
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
        InventoryController.instance?.ResetState();
        ApplySaveData(data);

        if (data.research != null && ResearchSystem.instance != null) {
            var rs = ResearchSystem.instance;
            if (data.research.progress != null)
                foreach (var kv in data.research.progress)
                    rs.progress[kv.Key] = kv.Value;
            rs.activeResearchId = data.research.activeResearchId;
            rs.unlockedIds.Clear();
            if (data.research.unlockedIds != null)
                foreach (int id in data.research.unlockedIds)
                    rs.unlockedIds.Add(id);
            rs.CheckTransitions();
            rs.ReapplyAllEffects();
        }

        StartCoroutine(PostLoadInit());
    }

    void ApplySaveData(WorldSaveData save) {
        World world = World.instance;
        world.timer = save.timer;

        if (save.waterLevels != null) {
            for (int x = 0; x < world.nx; x++) {
                for (int y = 0; y < world.ny; y++) {
                    int idx = y * world.nx + x;
                    if (idx < save.waterLevels.Length)
                        world.GetTileAt(x, y).water = save.waterLevels[idx];
                }
            }
        }

        if (save.tiles != null) {
            foreach (TileSaveData tsd in save.tiles) {
                Tile tile = world.GetTileAt(tsd.x, tsd.y);
                if (tile == null) continue;
                if (!string.IsNullOrEmpty(tsd.tileType) && Db.tileTypeByName.ContainsKey(tsd.tileType))
                    tile.type = Db.tileTypeByName[tsd.tileType];
            }
        }

        // Blueprints before structures so deconstruct blueprints can coexist with buildings
        if (save.blueprints != null)
            foreach (BlueprintSaveData bsd in save.blueprints)
                RestoreBlueprint(bsd);

        if (save.structures != null)
            foreach (StructureSaveData ssd in save.structures)
                RestoreStructure(ssd);

        // Restore tile inventories after structures (storage inventories are created by Building constructor)
        if (save.tiles != null) {
            foreach (TileSaveData tsd in save.tiles) {
                Tile tile = world.GetTileAt(tsd.x, tsd.y);
                if (tile == null) continue;
                if (tsd.inv != null) RestoreInventory(tsd.inv, tile);
            }
        }

        world.graph.Initialize();

        if (save.animals != null)
            foreach (AnimalSaveData asd in save.animals)
                AnimalController.instance.LoadAnimal(asd);

        if (save.globalItemTargets != null && InventoryController.instance != null) {
            foreach (var kv in save.globalItemTargets)
                if (Db.itemByName.TryGetValue(kv.Key, out Item item))
                    InventoryController.instance.targets[item.id] = kv.Value;
        }

        InventoryController.instance.ValidateGlobalInventory();

        var rp = RecipePanel.instance;
        if (rp != null) {
            rp.ClearDisabled();
            if (save.disabledRecipeIds != null)
                foreach (int id in save.disabledRecipeIds)
                    rp.SetAllowed(id, false);
        }

        WeatherSystem.instance?.RestoreState(save.isRaining);
    }

    void RestoreStructure(StructureSaveData ssd) {
        if (!Db.structTypeByName.ContainsKey(ssd.typeName)) {
            Debug.LogError("Unknown struct type on load: " + ssd.typeName); return;
        }
        StructType st = Db.structTypeByName[ssd.typeName];
        Tile tile = World.instance.GetTileAt(ssd.x, ssd.y);
        if (tile == null) { Debug.LogError("Null tile on load for struct: " + ssd.typeName); return; }
        Structure structure = null;

        if (st.isPlant) {
            Plant plant = new Plant(st as PlantType, ssd.x, ssd.y);
            plant.age          = ssd.plantAge;
            plant.growthStage  = ssd.plantGrowthStage;
            plant.harvestable  = ssd.plantHarvestable;
            plant.UpdateSprite();
            WorkOrderManager.instance?.RegisterHarvest(plant);
            structure = plant;
        } else if (st.depth == 0) {
            // Dispatch to subclass — keep in sync with StructController.Construct
            structure = st.name == "pump"
                ? new PumpBuilding(st, ssd.x, ssd.y) { uses = ssd.uses }
                : new Building(st, ssd.x, ssd.y) { uses = ssd.uses };
        } else if (st.name == "platform") {
            structure = new Platform(st, ssd.x, ssd.y);
        } else if (st.name == "stairs") {
            structure = new Stairs(st, ssd.x, ssd.y);
        } else if (st.name == "ladder") {
            structure = new Ladder(st, ssd.x, ssd.y);
        } else if (st.depth == 1) {
            structure = new Platform(st, ssd.x, ssd.y);
        } else if (st.depth == 2) {
            structure = new ForegroundStructure(st, ssd.x, ssd.y);
        } else if (st.depth == 3) {
            structure = new Structure(st, ssd.x, ssd.y);
            tile.structs[3] = structure;
        } else {
            Debug.LogError("Unhandled struct type on load: " + ssd.typeName); return;
        }

        if (structure != null) {
            StructController.instance.Place(structure);
            World.instance.graph.UpdateNeighbors(ssd.x, ssd.y);
            World.instance.graph.UpdateNeighbors(ssd.x, ssd.y + 1);
            if (st.isWorkstation && structure is Building ws) {
                // null → old save; pass -1 so RegisterWorkstation defaults to full capacity
                int ec = ssd.workOrderEffectiveCapacity ?? -1;
                WorkOrderManager.instance?.RegisterWorkstation(ws, ec);
            }
        }
    }

    void RestoreBlueprint(BlueprintSaveData bsd) {
        if (!Db.structTypeByName.ContainsKey(bsd.typeName)) {
            Debug.LogError("Unknown blueprint struct type on load: " + bsd.typeName); return;
        }
        StructType st = Db.structTypeByName[bsd.typeName];
        Blueprint bp = new Blueprint(st, bsd.x, bsd.y, autoRegister: false);
        bp.state                = (Blueprint.BlueprintState)bsd.state;
        bp.constructionProgress = bsd.constructionProgress;
        bp.priority             = bsd.priority;

        if (bsd.inv != null) {
            for (int i = 0; i < bsd.inv.stacks.Length && i < bp.inv.itemStacks.Length; i++) {
                var ssd = bsd.inv.stacks[i];
                if (!string.IsNullOrEmpty(ssd.itemName) && Db.itemByName.ContainsKey(ssd.itemName) && ssd.quantity > 0) {
                    bp.inv.itemStacks[i].item         = Db.itemByName[ssd.itemName];
                    bp.inv.itemStacks[i].quantity      = ssd.quantity;
                    bp.inv.itemStacks[i].resAmount = 0;
                    GlobalInventory.instance.AddItem(Db.itemByName[ssd.itemName], ssd.quantity);
                }
            }
        }
        bp.RefreshColor();
        if (bp.state == Blueprint.BlueprintState.Deconstructing && bp.tile.building?.storage != null)
            bp.tile.building.storage.locked = true;
        switch (bp.state) {
            case Blueprint.BlueprintState.Constructing:
                WorkOrderManager.instance?.RegisterConstruct(bp); break;
            case Blueprint.BlueprintState.Receiving:
                WorkOrderManager.instance?.RegisterSupplyBlueprint(bp); break;
            case Blueprint.BlueprintState.Deconstructing:
                WorkOrderManager.instance?.RegisterDeconstruct(bp); break;
        }
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
                inv.itemStacks[i].resAmount = 0;
                GlobalInventory.instance.AddItem(item, ssd.quantity);
                if (inv.invType == Inventory.InvType.Floor)
                    WorkOrderManager.instance?.RegisterHaul(inv.itemStacks[i]);
            }
        }
        // Constructor defaults serve as baseline (Storage=all-disallowed, Liquid=liquids-allowed, others=all-allowed).
        // Apply the explicit allow list on top; null means "use defaults as-is" (e.g. Floor/Animal, or old saves).
        if (isd.allowedItemIds != null)
            foreach (int id in isd.allowedItemIds)
                if (id < Db.items.Length && Db.items[id] != null)
                    inv.AllowItem(Db.items[id]);

        if (isd.marketTargets != null && inv.targets != null) {
            foreach (var kv in isd.marketTargets)
                if (Db.itemByName.TryGetValue(kv.Key, out Item item))
                    inv.targets[item] = kv.Value;
        }

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
        InventoryController.instance?.ResetState();
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

    public void DeleteSlot(string slotName) {
        string path = SlotPath(slotName);
        if (!System.IO.File.Exists(path)) { Debug.LogError("DeleteSlot: slot not found: " + slotName); return; }
        System.IO.File.Delete(path);
        Debug.Log("Deleted slot: " + slotName);
    }

    // Renames a save file on disk. Returns true on success.
    public bool RenameSlot(string oldName, string newName) {
        string oldPath = SlotPath(oldName);
        string newPath = SlotPath(newName);
        if (!System.IO.File.Exists(oldPath)) { Debug.LogError("RenameSlot: source not found: " + oldName); return false; }
        if (System.IO.File.Exists(newPath)) { Debug.LogError("RenameSlot: destination exists: " + newName); return false; }
        System.IO.File.Move(oldPath, newPath);
        Debug.Log("Renamed slot \"" + oldName + "\" → \"" + newName + "\"");
        return true;
    }

    // Returns animal count stored in a save file (reads from disk). Returns 0 on failure.
    public int GetAnimalCount(string slotName) {
        string path = SlotPath(slotName);
        if (!System.IO.File.Exists(path)) { Debug.LogError("GetAnimalCount: slot not found: " + slotName); return 0; }
        try {
            string json = System.IO.File.ReadAllText(path);
            WorldSaveData data = JsonConvert.DeserializeObject<WorldSaveData>(json);
            return data?.animals?.Length ?? 0;
        } catch (System.Exception e) {
            Debug.LogError("GetAnimalCount: failed to parse \"" + slotName + "\": " + e.Message);
            return 0;
        }
    }

    string SlotPath(string slotName) => System.IO.Path.Combine(SaveDir, slotName + ".json");
}

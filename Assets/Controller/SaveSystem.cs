using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

// Handles saving and loading world state to/from JSON files.
public class SaveSystem : MonoBehaviour {
    public static SaveSystem instance;

    string SaveDir => System.IO.Path.Combine(Application.persistentDataPath, "saves");

    void Awake() {
        if (instance != null) { Debug.LogError("there should only be one SaveSystem"); }
        instance = this;
        if (!System.IO.Directory.Exists(SaveDir)) System.IO.Directory.CreateDirectory(SaveDir);
    }

    // SAVE

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
            tile.bBlueprint != null ||
            tile.mBlueprint != null ||
            tile.fBlueprint != null ||
            (tile.inv != null && !tile.inv.IsEmpty());
        if (!hasContent) return null;

        return new TileSaveData {
            x = tile.x,
            y = tile.y,
            tileType = tile.type.name,
            building   = tile.building   != null ? GatherStructure(tile.building)   : null,
            mStruct    = tile.mStruct    != null ? GatherStructure(tile.mStruct)    : null,
            fStruct    = tile.fStruct    != null ? GatherStructure(tile.fStruct)    : null,
            bBlueprint = tile.bBlueprint != null ? GatherBlueprint(tile.bBlueprint) : null,
            mBlueprint = tile.mBlueprint != null ? GatherBlueprint(tile.mBlueprint) : null,
            fBlueprint = tile.fBlueprint != null ? GatherBlueprint(tile.fBlueprint) : null,
            inv        = tile.inv        != null ? GatherInventory(tile.inv)        : null,
        };
    }

    StructureSaveData GatherStructure(Structure s) {
        var ssd = new StructureSaveData { typeName = s.structType.name };
        if (s is Plant plant) {
            ssd.plantAge = plant.age;
            ssd.plantGrowthStage = plant.growthStage;
            ssd.plantHarvestable = plant.harvestable;
        }
        if (s is Building b && b.uses > 0) ssd.uses = b.uses;
        return ssd;
    }

    BlueprintSaveData GatherBlueprint(Blueprint bp) {
        return new BlueprintSaveData {
            typeName = bp.structType.name,
            state = (int)bp.state,
            constructionProgress = bp.constructionProgress,
            inv = bp.costs.Length > 0 ? GatherInventory(bp.inv) : null,
            priority = bp.priority
        };
    }

    InventorySaveData GatherInventory(Inventory inv) {
        var stacks = new ItemStackSaveData[inv.itemStacks.Length];
        for (int i = 0; i < inv.itemStacks.Length; i++) {
            ItemStack stack = inv.itemStacks[i];
            stacks[i] = new ItemStackSaveData {
                itemName = stack.item?.name ?? "",
                quantity = stack.quantity,
                decayCounter = stack.decayCounter
            };
        }
        var disallowed = new List<int>();
        foreach (var kv in inv.allowed) {
            if (!kv.Value) disallowed.Add(kv.Key);
        }
        return new InventorySaveData {
            nStacks = inv.nStacks,
            stackSize = inv.stackSize,
            invType = inv.invType.ToString(),
            stacks = stacks,
            disallowedItemIds = disallowed.Count > 0 ? disallowed.ToArray() : null
        };
    }

    AnimalSaveData GatherAnimal(Animal a) {
        return new AnimalSaveData {
            aName = a.aName,
            x = a.x,
            y = a.y,
            jobName = a.job.name,
            energy = a.energy,
            food = a.eating.food,
            eep = a.eeping.eep,
            timeSinceAteWheat = a.happiness.timeSinceAteWheat,
            timeSinceAteFruit = a.happiness.timeSinceAteFruit,
            inv = GatherInventory(a.inv)
        };
    }

    // LOAD
    public void Load(string slotName) {
        string path = SlotPath(slotName);
        if (!System.IO.File.Exists(path)) { Debug.LogError("Save slot not found: " + slotName); return; }
        string json = System.IO.File.ReadAllText(path);
        WorldSaveData data = JsonConvert.DeserializeObject<WorldSaveData>(json);
        WorldController.instance.ClearWorld();
        WorldController.instance.ApplySaveData(data);
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

    // FRAME 2 — one frame after GenerateDefault / ApplySaveData.
    // By this point all Animal.Start() calls have run. Safe to call Load() / UpdateColonyStats().
    // Started on all three paths: initial world gen, Reset, and Load.
    public IEnumerator PostLoadInit() {
        yield return null; // wait one frame for all Animal.Start() calls to complete
        AnimalController.instance.Load();
    }

    public void Reset() {
        WorldController.instance.ClearWorld();
        WorldController.instance.GenerateDefault();
        StartCoroutine(PostLoadInit());
    }

    // SLOT INFO
    public List<string> GetSaveSlots() {
        var slots = new List<string>();
        if (System.IO.Directory.Exists(SaveDir)) {
            foreach (string file in System.IO.Directory.GetFiles(SaveDir, "*.json")) {
                slots.Add(System.IO.Path.GetFileNameWithoutExtension(file));
            }
        }
        return slots;
    }

    public bool SlotExists(string slotName) {
        return System.IO.File.Exists(SlotPath(slotName));
    }

    string SlotPath(string slotName) {
        return System.IO.Path.Combine(SaveDir, slotName + ".json");
    }
}

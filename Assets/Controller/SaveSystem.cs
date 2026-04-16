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
//      appropriate parent. Place the call in the matching phase of
//      ApplySaveData — see SPEC-lifecycle.md "Load phase ordering" and the
//      `// ── Phase N: <name> ──` headers in ApplySaveData itself.
//      Common slots: world skeleton (1), structures (2), contents (3),
//      spatial caches (4), configuration/knobs (5), agents (7), view (9).
//   4. Add a reset line in ResetSystemState() so LoadDefault() also clears it.
// -----------------------------------------------------------------------
// Current saveable state checklist:
//   [x] World timer
//   [x] Tile types, floor inventories, and background wall
//   [x] Structures (type, position, uses, workOrderEffectiveCapacity, fuelInvData, storageInvData, mirrored, disabled, plantHarvestFlagged)
//   [x] Blueprints (type, position, state, constructionProgress, inv, priority, mirrored, disabled)
//   [x] Animals (position, job, energy, food, happiness, decoration happiness, socialization, fireplace warmth, inv, foodSlotInv, toolSlotInv, clothingSlotInv)
//   [x] Mid-transit merchant task descriptor (travelTaskType + iq + storage tile + leg)
//   [x] Research (progress, unlockedIds, studiedIds, unlockTimestamps, unlockCounter)
//   [x] Disabled recipe ids
//   [x] Water levels
//   [x] Is raining
//   [x] Global item targets
//   [x] Market targets (via MarketBuilding.instance)
//   [x] Camera position and zoom (PPU)
// -----------------------------------------------------------------------

public class SaveSystem : MonoBehaviour {
    public static SaveSystem instance { get; protected set; }

    // Name of the slot that was last loaded or saved. Null for a fresh/reset world.
    public string currentSlot { get; private set; }

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

    // ── Save ─────────────────────────────────────────────────────────────────

    public void Save(string slotName) {
        WorldSaveData data = GatherSaveData();
        string json = JsonConvert.SerializeObject(data, Formatting.Indented,
            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        System.IO.File.WriteAllText(SlotPath(slotName), json);
        currentSlot = slotName;
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
            var sids = new int[rs.studiedIds.Count];
            rs.studiedIds.CopyTo(sids);
            data.research = new ResearchSaveData {
                progress         = new System.Collections.Generic.Dictionary<int, float>(rs.progress),
                unlockedIds      = ids,
                studiedIds       = sids,
                unlockTimestamps = new System.Collections.Generic.Dictionary<int, int>(rs.unlockTimestamps),
                unlockCounter    = rs.unlockCounter
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

        if (MarketBuilding.instance?.storage?.targets != null) {
            var mt = new Dictionary<string, int>();
            foreach (var kv in MarketBuilding.instance.storage.targets)
                if (kv.Value != 0) mt[kv.Key.name] = kv.Value;
            if (mt.Count > 0) data.marketTargets = mt;
        }

        var cam = Camera.main;
        if (cam != null) {
            data.cameraX   = cam.transform.position.x;
            data.cameraY   = cam.transform.position.y;
            data.cameraPPU = MouseController.instance?.GetZoomPPU();
        }

        return data;
    }

    TileSaveData GatherTile(Tile tile) {
        // tile.inv is now always Floor or null (storage lives on building.storage)
        bool hasContent =
            tile.type.name != "empty" ||
            (tile.inv != null && !tile.inv.IsEmpty()) ||
            tile.hasBackground;
        if (!hasContent) return null;

        return new TileSaveData {
            x        = tile.x,
            y        = tile.y,
            tileType = tile.type.name,
            inv      = tile.inv != null ? GatherInventory(tile.inv) : null,
            hasBackgroundWall = tile.hasBackground,
        };
    }

    StructureSaveData GatherStructure(Structure s) {
        var ssd = new StructureSaveData { x = s.x, y = s.y, typeName = s.structType.name, mirrored = s.mirrored, condition = s.condition };
        if (s is Plant plant) {
            ssd.plantAge         = plant.age;
            ssd.plantGrowthStage = plant.growthStage;
            ssd.plantHarvestable = plant.harvestable;
            if (plant.harvestFlagged) ssd.plantHarvestFlagged = true;
        }
        if (s is Building b) {
            if (b.workstation != null && b.workstation.uses > 0) ssd.uses = b.workstation.uses;
            if (b.workstation != null)
                ssd.workOrderEffectiveCapacity = b.workstation.workerLimit;
            if (b.reservoir != null && !b.reservoir.inv.IsEmpty())
                ssd.fuelInvData = GatherInventory(b.reservoir.inv);
            if (b.storage != null)
                ssd.storageInvData = GatherInventory(b.storage);
            if (b.disabled) ssd.disabled = true;
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
            priority             = bp.priority,
            mirrored             = bp.mirrored,
            disabled             = bp.disabled
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
        // Only Storage uses explicit allow lists — Floor/Animal default to all-allowed so skip them.
        int[] allowedIds = null;
        if (inv.invType == Inventory.InvType.Storage) {
            var allowedList = new List<int>();
            foreach (var kv in inv.allowed)
                if (kv.Value) allowedList.Add(kv.Key);
            allowedIds = allowedList.Count > 0 ? allowedList.ToArray() : null;
        }

        return new InventorySaveData {
            invType        = inv.invType.ToString(),
            stacks         = stacks,
            allowedItemIds = allowedIds,
        };
    }

    AnimalSaveData GatherAnimal(Animal a) {
        AnimalSaveData asd = new AnimalSaveData {
            aName              = a.aName,
            x                  = a.x,
            y                  = a.y,
            jobName            = a.job.name,
            energy             = a.energy,
            food               = a.eating.food,
            eep                = a.eeping.eep,
            satisfactions        = new Dictionary<string, float>(a.happiness.satisfactions),
            warmth               = a.happiness.warmth,
            inv                = GatherInventory(a.inv),
            foodSlotInv        = GatherInventory(a.foodSlotInv),
            toolSlotInv        = GatherInventory(a.toolSlotInv),
            clothingSlotInv    = GatherInventory(a.clothingSlotInv),
            skillXp            = a.skills.SerializeXp(),
            skillLevel         = a.skills.SerializeLevel(),
            isTraveling        = a.state == Animal.AnimalState.Traveling,
            travelProgress     = a.state == Animal.AnimalState.Traveling ? a.workProgress : 0f,
            travelDuration     = (a.state == Animal.AnimalState.Traveling
                                  && a.task?.currentObjective is TravelingObjective tObj)
                                  ? tObj.durationTicks : 0,
        };

        // Travel task descriptor — lets Animal.Start() rebuild the full task tail on load
        // (deliver to market / receive + return + deliver to storage) rather than dropping
        // the merchant at the portal with a bare finish-travel. See HaulToMarketTask /
        // HaulFromMarketTask resume constructors.
        //
        // Piggyback phase mapping: a HaulToMarketTask that has already completed its
        // market delivery AND received pickup goods is carrying those goods home on
        // the return leg — functionally indistinguishable from a HaulFromMarketTask
        // on its return leg. We emit a "HaulFromMarket" descriptor in that case so
        // Animal.Start rebuilds the [Travel → Go(storage) → DeliverHome] tail.
        // Pre-delivery (outbound) saves always use the HaulToMarket descriptor; the
        // planned pickup is silently dropped on load — we lose an opportunistic
        // trip, not a committed task.
        if (asd.isTraveling) {
            if (a.task is HaulToMarketTask ht && ht.iq?.item != null) {
                if (ht.IsReturnLeg && ht.PickupReceived && ht.pickupStorageTile != null) {
                    asd.travelTaskType  = "HaulFromMarket";
                    asd.travelItemName  = ht.pickupIq.item.name;
                    asd.travelItemQty   = ht.pickupIq.quantity;
                    asd.travelStorageX  = ht.pickupStorageTile.x;
                    asd.travelStorageY  = ht.pickupStorageTile.y;
                    asd.travelReturnLeg = true;
                } else {
                    asd.travelTaskType  = "HaulToMarket";
                    asd.travelItemName  = ht.iq.item.name;
                    asd.travelItemQty   = ht.iq.quantity;
                    asd.travelReturnLeg = ht.IsReturnLeg;
                }
            } else if (a.task is HaulFromMarketTask hf && hf.iq?.item != null && hf.storageTile != null) {
                asd.travelTaskType  = "HaulFromMarket";
                asd.travelItemName  = hf.iq.item.name;
                asd.travelItemQty   = hf.iq.quantity;
                asd.travelStorageX  = hf.storageTile.x;
                asd.travelStorageY  = hf.storageTile.y;
                asd.travelReturnLeg = hf.IsReturnLeg;
            }
        }
        return asd;
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    // Resets all persistent singleton systems to blank defaults.
    // Called by both LoadDefault() and Load() before world content is recreated.
    // When adding a new system with saveable state, add its reset here AND
    // add Gather*/Restore* methods for the load path (see checklist above).
    void ResetSystemState() {
        InventoryController.instance?.ResetState();
        WeatherSystem.instance?.RestoreState(false);
        RecipePanel.instance?.ClearDisabled();
        ResearchSystem.instance?.ResetAll();
    }

    public void Load(string slotName) {
        string path = SlotPath(slotName);
        if (!System.IO.File.Exists(path)) { Debug.LogError("Save slot not found: " + slotName); return; }
        string json = System.IO.File.ReadAllText(path);
        WorldSaveData data = JsonConvert.DeserializeObject<WorldSaveData>(json);
        currentSlot = slotName;
        WorldController.instance.ClearWorld();
        ResetSystemState();
        ApplySaveData(data);
        StartCoroutine(PostLoadInit());
    }

    // ApplySaveData runs in a fixed phase order — see SPEC-lifecycle.md "Load phase ordering".
    // The load-bearing rule: state must be restored before any cross-system observer runs.
    // When adding new state, place it in the matching phase, not at the convenient end.
    void ApplySaveData(WorldSaveData save) {
        World world = World.instance;

        // ── Phase 1: World skeleton ────────────────────────────────────────────────────
        // Tile types, water, walls, world timer. Pure data on the world grid; nothing
        // downstream queries observe these directly yet.
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
            bool anyWall = false;
            foreach (TileSaveData tsd in save.tiles) {
                Tile tile = world.GetTileAt(tsd.x, tsd.y);
                if (tile == null) continue;
                if (!string.IsNullOrEmpty(tsd.tileType) && Db.tileTypeByName.ContainsKey(tsd.tileType))
                    tile.type = Db.tileTypeByName[tsd.tileType];
                tile.hasBackground = tsd.hasBackgroundWall;
                anyWall |= tsd.hasBackgroundWall;
            }

            // Old saves predate hasBackground — apply default y <= 43 threshold.
            if (!anyWall) {
                for (int x = 0; x < world.nx; x++)
                    for (int y = 0; y <= 43 && y < world.ny; y++)
                        world.GetTileAt(x, y).hasBackground = true;
            }
        }

        // ── Phase 2: Structures ────────────────────────────────────────────────────────
        // Blueprints before structures so deconstruct blueprints can coexist with buildings.
        // Building constructors create their own storage inventories with default state
        // (e.g. Market.targets all-zeros) — Phase 5 overrides those defaults.
        if (save.blueprints != null)
            foreach (BlueprintSaveData bsd in save.blueprints)
                RestoreBlueprint(bsd);

        if (save.structures != null)
            foreach (StructureSaveData ssd in save.structures)
                RestoreStructure(ssd);

        // Deconstruct blueprints tint their underlying structure's SR red. That tint can only be
        // applied once the structure itself exists, so re-run RefreshColor after Phase 2's
        // structure restore. No-op for construct blueprints (RefreshColor is idempotent).
        foreach (Blueprint bp in StructController.instance.GetBlueprints())
            if (bp.state == Blueprint.BlueprintState.Deconstructing)
                bp.RefreshColor();

        // ── Phase 3: Contents ──────────────────────────────────────────────────────────
        // Fill tile inventories with their saved item stacks. Structure storage contents
        // are filled inside RestoreStructure (Phase 2), since the storage inventory is
        // owned by the Building. This pass covers floor/loose-tile inventories.
        if (save.tiles != null) {
            foreach (TileSaveData tsd in save.tiles) {
                Tile tile = world.GetTileAt(tsd.x, tsd.y);
                if (tile == null) continue;
                if (tsd.inv != null) RestoreInventory(tsd.inv, tile);
            }
        }

        // ── Phase 4: Spatial indexes ───────────────────────────────────────────────────
        // Derived spatial caches that depend on final tile + structure geometry.
        // Order matches WorldController.GenerateDefault for symmetry between paths.
        SkyExposure.InitializeWorld(world);
        BackgroundTile.InitializeWorld(world);
        world.graph.Initialize();

        // ── Phase 5: Configuration ─────────────────────────────────────────────────────
        // "Knob state" — overrides applied on top of defaults set by constructors. This
        // MUST happen before Phase 6 (observers), since observers read configuration to
        // decide what work to register. Bug history: market HaulFrom orders were
        // registered against default zero-targets when this phase ran after Reconcile.
        if (save.globalItemTargets != null && InventoryController.instance != null) {
            foreach (var kv in save.globalItemTargets)
                if (Db.itemByName.TryGetValue(kv.Key, out Item item))
                    InventoryController.instance.targets[item.id] = kv.Value;
        }

        if (save.marketTargets != null && MarketBuilding.instance?.storage?.targets != null) {
            foreach (var kv in save.marketTargets)
                if (Db.itemByName.TryGetValue(kv.Key, out Item item))
                    MarketBuilding.instance.storage.targets[item] = kv.Value;
        }

        var rp = RecipePanel.instance;
        if (rp != null && save.disabledRecipeIds != null)
            foreach (int id in save.disabledRecipeIds)
                rp.SetAllowed(id, false);

        RestoreResearch(save.research);

        WeatherSystem.instance?.RestoreState(save.isRaining);

        // ── Phase 6: Observers ─────────────────────────────────────────────────────────
        // Register all WOM orders in one pass now that the world + configuration is final
        // and the pathfinding graph is built. Reconcile scans plants, blueprints, floor
        // stacks, workstations, labs, fuel buildings, markets, and storage evictions.
        // silent=true suppresses warnings (every registration is expected during load).
        WorkOrderManager.instance?.Reconcile(silent: true);

        // Rebuild MaintenanceSystem bookkeeping (registered + broken sets) from restored
        // condition values. Runs after Reconcile so the WOM Maintenance orders it registers
        // are consistent with the sets MaintenanceSystem tracks. Tint refresh catches any
        // structure that loaded already broken.
        MaintenanceSystem.instance?.RebuildFromWorld();
        foreach (Structure s in StructController.instance.GetStructures())
            if (s != null) s.RefreshTint();

        // ── Phase 7: Agents ────────────────────────────────────────────────────────────
        // LoadAnimal stages save data on Animal.pendingSaveData. Animal.Start() consumes
        // it on frame 2 — see PostLoadInit and SPEC-lifecycle.md.
        if (save.animals != null)
            foreach (AnimalSaveData asd in save.animals)
                AnimalController.instance.LoadAnimal(asd);

        // ── Phase 8: Validation ────────────────────────────────────────────────────────
        // Cross-system audits that need every prior phase to have completed. Animal
        // inventories count toward global totals, so this must run after Phase 7.
        InventoryController.instance.ValidateGlobalInventory();

        // ── Phase 9: View ──────────────────────────────────────────────────────────────
        // Camera and UI panel state. Pure presentation; no game logic depends on it.
        var cam = Camera.main;
        if (cam != null) {
            // Position first, then zoom: SetZoomPPU clamps against the new half-height,
            // so restoring the saved position before it means the single clamp uses the
            // correct position under the correct zoom. Swapping the order would clamp a
            // stale (pre-load) position against the new zoom, then overwrite it anyway.
            if (save.cameraX.HasValue && save.cameraY.HasValue)
                cam.transform.position = new Vector3(save.cameraX.Value, save.cameraY.Value, cam.transform.position.z);
            if (save.cameraPPU.HasValue)
                MouseController.instance?.SetZoomPPU(save.cameraPPU.Value);
            else
                MouseController.instance?.ClampCamera(); // old saves: no PPU → still belt-and-braces clamp at current zoom
        }
    }

    // Phase 5 helper — restores research progress and re-applies effects (building/job
    // unlocks). Lives inside ApplySaveData so any future effect that touches building
    // state is in place before Reconcile registers orders against it.
    //
    // Handles old-save migration: missing studiedIds falls back to maintainIds,
    // old activeResearchId is merged into studiedIds, and missing unlockTimestamps
    // are derived from unlockedIds order (earlier entries = older = higher priority).
    void RestoreResearch(ResearchSaveData rsd) {
        if (rsd == null || ResearchSystem.instance == null) return;
        var rs = ResearchSystem.instance;
        if (rsd.progress != null)
            foreach (var kv in rsd.progress)
                rs.progress[kv.Key] = kv.Value;

        rs.unlockedIds.Clear();
        if (rsd.unlockedIds != null)
            foreach (int id in rsd.unlockedIds)
                rs.unlockedIds.Add(id);

        // Study set: prefer new field, fall back to legacy maintainIds.
        rs.studiedIds.Clear();
        int[] srcStudied = rsd.studiedIds ?? rsd.maintainIds;
        if (srcStudied != null)
            foreach (int id in srcStudied)
                rs.studiedIds.Add(id);
        // Legacy: old activeResearchId → ensure it's studied.
        if (rsd.studiedIds == null && rsd.activeResearchId >= 0)
            rs.studiedIds.Add(rsd.activeResearchId);

        // Unlock timestamps: use saved data or derive from unlockedIds order.
        rs.unlockTimestamps.Clear();
        if (rsd.unlockTimestamps != null) {
            foreach (var kv in rsd.unlockTimestamps)
                rs.unlockTimestamps[kv.Key] = kv.Value;
            rs.unlockCounter = rsd.unlockCounter;
        } else if (rsd.unlockedIds != null) {
            // Old save — assign incrementing counters so array order = priority order.
            for (int i = 0; i < rsd.unlockedIds.Length; i++)
                rs.unlockTimestamps[rsd.unlockedIds[i]] = i + 1;
            rs.unlockCounter = rsd.unlockedIds.Length;
        }

        rs.CheckTransitions();
        rs.ReapplyAllEffects();
    }

    void RestoreStructure(StructureSaveData ssd) {
        if (!Db.structTypeByName.ContainsKey(ssd.typeName)) {
            Debug.LogError("Unknown struct type on load: " + ssd.typeName); return;
        }
        StructType st = Db.structTypeByName[ssd.typeName];
        Tile tile = World.instance.GetTileAt(ssd.x, ssd.y);
        if (tile == null) { Debug.LogError("Null tile on load for struct: " + ssd.typeName); return; }
        Structure structure = null;

        structure = Structure.Create(st, ssd.x, ssd.y, ssd.mirrored);
        if (structure == null) {
            Debug.LogError("Structure.Create returned null on load: " + ssd.typeName); return;
        }

        // Condition: treat 0 (old saves — field absent) as "missing" → default to 1.0. Saved
        // values are always > 0 in practice since MaintenanceSystem clamps at 0 and we write
        // the current value regardless.
        structure.condition = ssd.condition > 0f ? Mathf.Clamp01(ssd.condition) : 1.0f;

        // Restore subclass-specific state that Create() can't know about.
        if (structure is Plant plant) {
            plant.age          = ssd.plantAge;
            plant.growthStage  = ssd.plantGrowthStage;
            plant.harvestable  = ssd.plantHarvestable;
            plant.SetHarvestFlagged(ssd.plantHarvestFlagged ?? false);
            plant.UpdateSprite();
        }
        if (structure is Building b) {
            if (b.workstation != null) b.workstation.uses = ssd.uses;
            b.disabled = ssd.disabled;
        }

        StructController.instance.Place(structure);
        World.instance.graph.UpdateNeighbors(ssd.x, ssd.y);
        World.instance.graph.UpdateNeighbors(ssd.x, ssd.y + 1);
        if (structure is Building ws && ws.workstation != null) {
            // null → old save without this field; default to full capacity
            ws.workstation.workerLimit = ssd.workOrderEffectiveCapacity ?? ws.workstation.capacity;
        }
        if (structure is Building fb && fb.reservoir != null && ssd.fuelInvData != null) {
            foreach (ItemStackSaveData sd in ssd.fuelInvData.stacks ?? System.Array.Empty<ItemStackSaveData>()) {
                if (string.IsNullOrEmpty(sd.itemName) || sd.quantity <= 0) continue;
                if (!Db.itemByName.TryGetValue(sd.itemName, out Item leafItem)) {
                    Debug.LogError($"RestoreStructure: unknown fuel item '{sd.itemName}' in fuelInv of {st.name} at ({ssd.x},{ssd.y})");
                    continue;
                }
                fb.reservoir.inv.Produce(leafItem, sd.quantity);
            }
        }
        // Restore storage inventory (items + allowed filter)
        if (structure is Building sb && sb.storage != null && ssd.storageInvData != null) {
            for (int i = 0; i < ssd.storageInvData.stacks.Length && i < sb.storage.itemStacks.Length; i++) {
                ItemStackSaveData sd = ssd.storageInvData.stacks[i];
                if (!string.IsNullOrEmpty(sd.itemName) && Db.itemByName.TryGetValue(sd.itemName, out Item item) && sd.quantity > 0) {
                    sb.storage.itemStacks[i].item         = item;
                    sb.storage.itemStacks[i].quantity      = sd.quantity;
                    sb.storage.itemStacks[i].decayCounter  = sd.decayCounter;
                    sb.storage.itemStacks[i].resAmount = 0;
                    GlobalInventory.instance.AddItem(item, sd.quantity);
                }
            }
            if (ssd.storageInvData.allowedItemIds != null)
                foreach (int id in ssd.storageInvData.allowedItemIds)
                    if (id < Db.items.Length && Db.items[id] != null)
                        sb.storage.AllowItem(Db.items[id]);
            sb.storage.UpdateSprite();
        }
        // WOM orders (harvest, workstation, fuel supply) are registered by Reconcile() after all objects are restored.
    }

    void RestoreBlueprint(BlueprintSaveData bsd) {
        if (!Db.structTypeByName.ContainsKey(bsd.typeName)) {
            Debug.LogError("Unknown blueprint struct type on load: " + bsd.typeName); return;
        }
        StructType st = Db.structTypeByName[bsd.typeName];
        Blueprint bp = new Blueprint(st, bsd.x, bsd.y, mirrored: bsd.mirrored, autoRegister: false);
        bp.state                = (Blueprint.BlueprintState)bsd.state;
        bp.constructionProgress = bsd.constructionProgress;
        bp.priority             = bsd.priority;
        bp.disabled             = bsd.disabled;

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
        // Heal race condition: if the game was saved after all materials were delivered but before
        // DeliverToBlueprintObjective had a chance to transition state to Constructing, restore
        // directly into Constructing so we don't spin a supply order that immediately fails.
        if (bp.state == Blueprint.BlueprintState.Receiving && bp.IsFullyDelivered())
            bp.state = Blueprint.BlueprintState.Constructing;
        bp.RefreshColor();
        if (bp.state == Blueprint.BlueprintState.Deconstructing && bp.tile.building?.storage != null)
            bp.tile.building.storage.locked = true;
        // WOM orders are registered by Reconcile(silent:true) at the end of ApplySaveData(), once the graph is fully built.
    }

    // Restores a floor inventory from save data. Storage inventories are restored in RestoreStructure.
    void RestoreInventory(InventorySaveData isd, Tile tile) {
        Inventory inv = tile.EnsureFloorInventory();

        for (int i = 0; i < isd.stacks.Length && i < inv.itemStacks.Length; i++) {
            ItemStackSaveData ssd = isd.stacks[i];
            if (!string.IsNullOrEmpty(ssd.itemName) && Db.itemByName.ContainsKey(ssd.itemName) && ssd.quantity > 0) {
                Item item = Db.itemByName[ssd.itemName];
                inv.itemStacks[i].item         = item;
                inv.itemStacks[i].quantity      = ssd.quantity;
                inv.itemStacks[i].decayCounter  = ssd.decayCounter;
                inv.itemStacks[i].resAmount = 0;
                GlobalInventory.instance.AddItem(item, ssd.quantity);
            }
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

    public void LoadDefault() {
        currentSlot = null;
        WorldController.instance.ClearWorld();
        ResetSystemState();
        WorldController.instance.GenerateDefault();
        StartCoroutine(PostLoadInit());
    }

    // -----------------------------------------------------------------------
    // SLOTS
    // -----------------------------------------------------------------------

    public List<string> GetSaveSlots() {
        var slots = new List<string>();
        if (!System.IO.Directory.Exists(SaveDir)) return slots;
        var files = new System.IO.DirectoryInfo(SaveDir).GetFiles("*.json");
        System.Array.Sort(files, (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
        foreach (var fi in files)
            slots.Add(System.IO.Path.GetFileNameWithoutExtension(fi.Name));
        return slots;
    }

    // Returns the name of the most recently modified save slot, or null if none exist.
    public string GetMostRecentSlot() {
        if (!System.IO.Directory.Exists(SaveDir)) return null;
        var files = new System.IO.DirectoryInfo(SaveDir).GetFiles("*.json");
        if (files.Length == 0) return null;
        System.IO.FileInfo newest = files[0];
        for (int i = 1; i < files.Length; i++)
            if (files[i].LastWriteTime > newest.LastWriteTime) newest = files[i];
        return System.IO.Path.GetFileNameWithoutExtension(newest.Name);
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

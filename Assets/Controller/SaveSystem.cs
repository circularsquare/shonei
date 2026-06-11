using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;

// Handles saving and loading world state to/from JSON files.
//
// -----------------------------------------------------------------------
// SAVE FORMAT VERSION:
//   Current version: 1.
//   Bump WorldSaveData.saveVersion when the on-disk schema changes in a way
//   that can't be inferred from nullable-field presence alone (e.g. semantic
//   reinterpretation of an existing field, or a migration that must run before
//   normal Restore* methods). Pure additive changes — new nullable fields with
//   sensible null-fallbacks — do NOT require a bump; the existing pattern of
//   "absent field → default" handles them.
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
//   [x] World RNG seed (drives Rng — gameplay randomness reproduces on reload)
//   [x] Per-animal RNG seed (drives Animal.random — animal AI reproduces on reload)
//   [x] Tile types, floor inventories (incl. wetUntil rain-soaked timer), background wall, overlay masks (grass on dirt), overlay health state (live/dying/dead), and snow cover
//   [x] Per-column original surface heights (worldgen ground line, persisted so decoration depth gates survive column mining)
//   [x] Structures (type, position, uses, workOrderEffectiveCapacity, fuelInvData, storageInvData, furnishingInvData + furnishingRemainingDays, processor state/progress/inputData/outputData, mirrored, rotation, shapeIndex, disabled, plantHarvestFlagged, quarry/digging pit capturedTileType, digging pit digDir, flywheel charge, elevator currentY + history buffers, bridge-post partnerX/Y, savedNx/savedNy footprint, build materials materialItems/materialFen)
//   [x] Blueprints (type, position, state, constructionProgress, inv, priority, mirrored, rotation, shapeIndex, disabled, two-click x2/y2)
//   [x] Animals (position, job, energy, food, starvation countdown, happiness, decoration happiness, socialization, fireplace warmth, inv, foodSlotInv, toolSlotInv, clothingSlotInv, bookSlotInv)
//   [x] Mid-transit merchant task descriptor (travelTaskType + iq + storage tile + leg)
//   [x] Research (progress, unlockedIds, studiedIds, unlockTimestamps, unlockCounter)
//   [x] Disabled recipe ids
//   [x] Expanded recipe groups (Recipes panel — workstation collapse state)
//   [x] Disabled processes (Recipes panel — paused fermentation etc., by building name)
//   [x] Water levels
//   [x] Moisture levels
//   [x] Is raining + atmospheric humidity (drives rain via threshold) + temperature noise anomaly
//   [x] Global item targets
//   [x] Discovered items (so once-seen items stay visible even after going extinct)
//   [x] Market targets (via MarketBuilding.instance)
//   [x] Camera position and zoom (PPU)
//   [x] Global inventory panel tree collapse state (deltas vs item.defaultOpen)
//   [x] Panel collapse state for CollapsibleHeader-equipped panels (deltas vs default-open)
//   [x] Onboarding PlayerTask progress (current task index; null on old saves → onboarding skipped)
//   [x] Cumulative colony births (gates the early-growth birth-rate boost; null on old saves → boost spent)
//   [x] Decorative flower layout (position + variant; restored directly instead of re-derived, which drifted across reload)
// -----------------------------------------------------------------------

public class SaveSystem : MonoBehaviour {
    public static SaveSystem instance { get; protected set; }

    // On-disk schema version (see SAVE FORMAT VERSION above). Stamped into every
    // save and surfaced in the cloud-save metadata so an older client refuses to
    // download a newer-schema blob.
    public const int SaveVersion = 1;

    // Name of the slot that was last loaded or saved. Null for a fresh/reset world.
    public string currentSlot { get; private set; }

    // Filesystem slot ops (enumerate / metadata / delete / rename) live in the static
    // SaveStore so the Menu scene can list saves without a live SaveSystem/World. This
    // class keeps only currentSlot (world state) and the gather/restore logic.

    void Awake() {
        if (instance != null) { Debug.LogError("there should only be one SaveSystem"); }
        instance = this;
        SaveStore.EnsureDir();
    }

    void Start() {
        // Surface a mid-session token lapse: cloud sync has stopped (the pump halts on
        // 401) and only a re-login at the menu restarts it. Without this the only
        // signal is the save menu's badge quietly reading "offline". Subscribed in
        // Start (not Awake) per SPEC-eventfeed.md.
        SaveSync.OnAuthExpired += HandleAuthExpired;
    }

    void OnDestroy() {
        SaveSync.OnAuthExpired -= HandleAuthExpired;
    }

    static void HandleAuthExpired() {
        EventFeed.instance?.Post("<color=#cc3333>session expired - saves not syncing - log in at main menu</color>");
    }

    // ── Autosave ─────────────────────────────────────────────────────────────
    // Periodically writes the world to a rotating set of "autosave" slots (never clobbers a
    // manual save). Interval is SettingsManager.autosaveIntervalMinutes (0 = off), re-read
    // live. Save() is synchronous and stalls the frame on large worlds, so the routine shows
    // savingOverlay first and yields a frame to let it paint before the freeze.
    const int    MaxAutosaves   = 3;          // keep at most this many; oldest is deleted first
    const string AutosavePrefix = "autosave"; // reserved slot-name prefix for rotation
    [SerializeField] GameObject savingOverlay; // centered "saving..." box (scene object), shown during a save
    float autosaveTimer;
    bool  autosaving;

    void Update() {
        // No world yet (main menu / mid-reset) or feature off → hold the clock at zero.
        var sm = SettingsManager.instance;
        if (World.instance == null || sm == null || !sm.autosaveEnabled) {
            autosaveTimer = 0f;
            return;
        }
        if (autosaving) return;
        autosaveTimer += Time.unscaledDeltaTime; // real time — fires even while the game is paused
        if (autosaveTimer >= sm.autosaveIntervalMinutes * 60f) {
            autosaveTimer = 0f;
            StartCoroutine(AutosaveRoutine());
        }
    }

    IEnumerator AutosaveRoutine() {
        autosaving = true;
        if (savingOverlay != null) savingOverlay.SetActive(true);
        // Let the overlay render before Save() blocks the main thread for the frame.
        yield return null;
        yield return new WaitForEndOfFrame();
        WriteRotatingAutosave();
        if (savingOverlay != null) savingOverlay.SetActive(false);
        autosaving = false;
    }

    // Writes a fresh timestamped autosave, first deleting the oldest while ≥ MaxAutosaves
    // exist so we keep at most MaxAutosaves. Only ever touches slots under AutosavePrefix —
    // that prefix is reserved for autosaves, so manual saves must not use it.
    void WriteRotatingAutosave() {
        // GetSaveSlots is newest-first, so autosaves sort newest→oldest; the oldest is last.
        var autos = SaveStore.GetSaveSlots().Where(s => s.StartsWith(AutosavePrefix)).ToList();
        while (autos.Count >= MaxAutosaves) {
            SaveStore.DeleteSlot(autos[autos.Count - 1]);
            autos.RemoveAt(autos.Count - 1);
        }
        string name = AutosavePrefix + " " + System.DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        Save(name, setCurrent: false);
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    // setCurrent=false leaves currentSlot untouched — used by autosave so writing the
    // background "autosave" slot doesn't hijack the player's active named slot.
    public void Save(string slotName, bool setCurrent = true) {
        string json = SerializeToJson();
        System.IO.File.WriteAllText(SaveStore.SlotPath(slotName), json);
        if (setCurrent) currentSlot = slotName;
        autosaveTimer = 0f; // any save (manual or auto) restarts the autosave clock
        int animals = AnimalController.instance != null ? AnimalController.instance.na : -1;
        // Refresh the cache from the live animal count — avoids re-parsing the file we just wrote.
        SaveStore.SetAnimalCount(slotName, animals);
        // Mirror to the account's cloud store (async, best-effort — the local write above
        // is authoritative). No-op when logged out. Reuses the JSON we just serialized.
        if (Session.LoggedIn)
            SaveSync.QueueUpload(slotName, json, System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                 animals < 0 ? 0 : animals, SaveVersion);
        Debug.Log("Saved world to slot: " + slotName);
    }

    // Serialize the current world state to JSON without touching the filesystem.
    // Snapshot tests use this to capture state for golden-file comparison.
    public string SerializeToJson() {
        WorldSaveData data = GatherSaveData();
        return JsonConvert.SerializeObject(data, Formatting.Indented,
            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
    }

    WorldSaveData GatherSaveData() {
        World world = World.instance;
        WorldSaveData data = new WorldSaveData();
        data.saveVersion = SaveVersion;
        data.savedNx = world.nx;
        data.savedNy = world.ny;
        data.timer = world.timer;
        data.worldSeed = Rng.worldSeed;

        var tiles = new List<TileSaveData>();
        for (int x = 0; x < world.nx; x++) {
            for (int y = world.ny - 1; y >= 0; y--) {
                TileSaveData tsd = GatherTile(world.GetTileAt(x, y));
                if (tsd != null) tiles.Add(tsd);
            }
        }
        data.tiles = tiles.ToArray();

        // Decorative flower layout (position + variant). Persisted so reload reproduces the
        // exact flowers instead of re-deriving from live grass/snow state (which drifts).
        data.flowers = FlowerController.instance != null ? FlowerController.instance.GatherSave() : null;

        // Original ground-line per column (immutable from worldgen). Persisted so
        // the "natural surface" gate used by FlowerController / OverlayGrowthSystem
        // survives across saves even when the player has mined the top off a column.
        // Cloned defensively — World.surfaceY is the live array, not a snapshot.
        if (world.surfaceY != null)
            data.surfaceY = (int[])world.surfaceY.Clone();

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

        // Moisture levels — omitted (null) when every tile is dry soil, matching waterLevels' pattern.
        bool anyMoisture = false;
        byte[] ml = new byte[world.nx * world.ny];
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                byte m = world.GetTileAt(x, y).moisture;
                ml[y * world.nx + x] = m;
                if (m > 0) anyMoisture = true;
            }
        }
        if (anyMoisture) data.moistureLevels = ml;

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

        data.buildingsEverBuilt = AnimalController.instance?.BuiltTypesSnapshot();

        var rp = RecipePanel.instance;
        if (rp != null) {
            var disabled = new int[rp.DisabledCount];
            rp.CopyDisabledIds(disabled);
            if (disabled.Length > 0) data.disabledRecipeIds = disabled;

            var expanded = rp.CopyExpandedGroups();
            if (expanded.Length > 0) data.expandedRecipeGroups = expanded;

            var disabledProc = rp.CopyDisabledProcesses();
            if (disabledProc.Length > 0) data.disabledProcesses = disabledProc;
        }

        data.isRaining   = WeatherSystem.instance?.isRaining   ?? false;
        data.humidity    = WeatherSystem.instance?.humidity    ?? 0f;
        data.tempAnomaly = WeatherSystem.instance?.tempAnomaly ?? 0f;

        var ic = InventoryController.instance;
        if (ic?.targets != null) {
            var savedTargets = new Dictionary<string, int>();
            foreach (var kv in ic.targets) {
                Item item = kv.Key < Db.items.Length ? Db.items[kv.Key] : null;
                if (item == null) continue;
                if (kv.Value == item.DefaultTargetFen) continue; // skip per-item default — no need to save
                savedTargets[item.name] = kv.Value;
            }
            if (savedTargets.Count > 0) data.globalItemTargets = savedTargets;
        }

        // Discovered items — persist by name so a once-seen item (rare ore mined out, research
        // unlocked then forgotten, etc.) stays visible. Skip items flagged startDiscovered: those
        // are seeded automatically by InventoryController on every load.
        if (ic?.discoveredItems != null) {
            var disc = new List<string>();
            foreach (var kv in ic.discoveredItems) {
                if (!kv.Value) continue;
                Item item = kv.Key < Db.items.Length ? Db.items[kv.Key] : null;
                if (item == null || item.startDiscovered) continue;
                disc.Add(item.name);
            }
            if (disc.Count > 0) data.discoveredItems = disc.ToArray();
        }

        // Global inventory panel collapse state — store only groups whose current open state
        // differs from their JSON defaultOpen, so new items pick up their authored default.
        if (ic?.itemDisplayGos != null) {
            var openDeltas = new Dictionary<string, bool>();
            foreach (var kv in ic.itemDisplayGos) {
                GameObject go = kv.Value;
                if (go == null) continue;
                ItemDisplay disp = go.GetComponent<ItemDisplay>();
                if (disp == null || disp.item == null || !disp.item.IsGroup) continue;
                bool def = ItemDisplay.DefaultOpenForGroup(disp.item);
                if (disp.open != def) openDeltas[disp.item.name] = disp.open;
            }
            if (openDeltas.Count > 0) data.inventoryTreeOpen = openDeltas;
        }

        // Per-panel collapse state (CollapsibleHeader). Default is open; only deltas stored.
        var panelDeltas = new Dictionary<string, bool>();
        var invHeader = InventoryController.instance?.inventoryHeader;
        if (invHeader != null && !string.IsNullOrEmpty(invHeader.saveKey) && !invHeader.open)
            panelDeltas[invHeader.saveKey] = false;
        var jobsHeader = AnimalController.instance?.jobsHeader;
        if (jobsHeader != null && !string.IsNullOrEmpty(jobsHeader.saveKey) && !jobsHeader.open)
            panelDeltas[jobsHeader.saveKey] = false;
        if (panelDeltas.Count > 0) data.panelsOpen = panelDeltas;

        if (PlayerTaskController.instance != null)
            data.playerTaskIndex = PlayerTaskController.instance.currentIndex;

        if (AnimalController.instance != null)
            data.births = AnimalController.instance.births;

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
            tile.hasBackground ||
            tile.overlayMask != 0 ||
            tile.snow;
        if (!hasContent) return null;

        return new TileSaveData {
            x        = tile.x,
            y        = tile.y,
            tileType = tile.type.name,
            inv      = tile.inv != null ? GatherInventory(tile.inv) : null,
            backgroundWallType = (int)tile.backgroundType,
            // Nullable: emit only when nonzero so on-disk JSON stays small and
            // existing snapshot-test goldens diff minimally.
            overlayMask  = tile.overlayMask != 0 ? tile.overlayMask : (byte?)null,
            // overlayState only meaningful when a mask exists; skip the field on bare or Live tiles.
            overlayState = (tile.overlayMask != 0 && tile.overlayState != OverlayState.Live)
                ? (byte?)(byte)tile.overlayState : null,
            snow = tile.snow ? (bool?)true : null,
            // Pre-snow grass snapshot: only emit when actually buried under snow
            // AND there was real grass to preserve (mask != 0). State is paired
            // with mask — meaningless on its own.
            preSnowOverlayMask  = (tile.snow && tile.preSnowOverlayMask != 0)
                ? (byte?)tile.preSnowOverlayMask : null,
            preSnowOverlayState = (tile.snow && tile.preSnowOverlayMask != 0
                    && tile.preSnowOverlayState != OverlayState.Live)
                ? (byte?)(byte)tile.preSnowOverlayState : null,
        };
    }

    StructureSaveData GatherStructure(Structure s) {
        var ssd = new StructureSaveData { x = s.x, y = s.y, typeName = s.structType.name, mirrored = s.mirrored, rotation = s.rotation, shapeIndex = s.shapeIndex, condition = s.condition, savedNx = s.Shape.nx, savedNy = s.Shape.ny };
        // Build-materials record (the exact leaves consumed) — parallel name/fen arrays, omitted
        // when absent. Restored by RestoreStructure; absent → first-leaf fallback on deconstruct.
        if (s.materials != null && s.materials.Count > 0) {
            ssd.materialItems = new string[s.materials.Count];
            ssd.materialFen   = new int[s.materials.Count];
            for (int i = 0; i < s.materials.Count; i++) {
                ssd.materialItems[i] = s.materials[i].item.name;
                ssd.materialFen[i]   = s.materials[i].quantity;
            }
        }
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
            if (b.furnishingSlots != null) {
                int n = b.furnishingSlots.SlotCount;
                bool anyFilled = false;
                for (int i = 0; i < n; i++) if (!b.furnishingSlots.IsEmpty(i)) { anyFilled = true; break; }
                if (anyFilled) {
                    ssd.furnishingInvData = new InventorySaveData[n];
                    ssd.furnishingRemainingDays = new float[n];
                    for (int i = 0; i < n; i++) {
                        ssd.furnishingInvData[i] = GatherInventory(b.furnishingSlots.slotInvs[i]);
                        ssd.furnishingRemainingDays[i] = b.furnishingSlots.slotRemainingDays[i];
                    }
                }
            }
            if (b.processor != null) {
                ssd.processorState    = (int)b.processor.state;
                ssd.processorProgress = b.processor.progress;
                if (!b.processor.inputBuffer.IsEmpty()) ssd.processorInputData  = GatherInventory(b.processor.inputBuffer);
                if (!b.processor.output.IsEmpty())      ssd.processorOutputData = GatherInventory(b.processor.output);
            }
            if (b.disabled) ssd.disabled = true;
        }
        if (s is Quarry q && q.capturedTile != null)
            ssd.capturedTileType = q.capturedTile.name;
        if (s is DiggingPit dp) {
            if (dp.capturedTile != null) ssd.capturedTileType = dp.capturedTile.name;
            ssd.digDir = (int)dp.digDir;   // persist direction even if the substrate didn't capture
        }
        if (s is Flywheel fw)
            ssd.flywheelCharge = fw.charge;
        if (s is Elevator el) {
            ssd.elevatorCurrentY            = el.currentY;
            ssd.elevatorRecentTripTicks     = el.recentTripTicks.ToArray();
            ssd.elevatorRecentEndToEndTicks = el.recentEndToEndTicks.ToArray();
        }
        if (s is BridgePost post) {
            ssd.partnerX = post.partnerX;
            ssd.partnerY = post.partnerY;
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
            rotation             = bp.rotation,
            shapeIndex           = bp.shapeIndex,
            disabled             = bp.disabled,
            x2                   = bp.x2,
            y2                   = bp.y2
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
            wetUntil       = inv.wetUntil,
        };
    }

    AnimalSaveData GatherAnimal(Animal a) {
        AnimalSaveData asd = new AnimalSaveData {
            aName              = a.aName,
            x                  = a.x,
            y                  = a.y,
            rngSeed            = a.rngSeed,
            jobName            = a.job.name,
            energy             = a.energy,
            food               = a.eating.food,
            starvingTicks      = a.eating.starvingTicks,
            eep                = a.eeping.eep,
            satisfactions        = new Dictionary<string, float>(a.happiness.satisfactions),
            warmth               = a.happiness.warmth,
            inv                = GatherInventory(a.inv),
            foodSlotInv        = GatherInventory(a.foodSlotInv),
            toolSlotInv        = GatherInventory(a.toolSlotInv),
            clothingSlotInv    = GatherInventory(a.clothingSlotInv),
            bookSlotInv        = GatherInventory(a.bookSlotInv),
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
        // Home reservation. Persist the building's anchor coords; on load Animal.Start
        // resolves the live Building ref via World.GetTileAt(x,y).building. insideBuilding
        // is not saved — it's derived from the animal's position on load.
        if (a.homeBuilding != null) {
            asd.homeBuildingX = a.homeBuilding.x;
            asd.homeBuildingY = a.homeBuilding.y;
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
        WeatherSystem.instance?.RestoreState(false, 0f);
        RecipePanel.instance?.ClearDisabled();
        RecipePanel.instance?.ClearExpandedGroups();
        RecipePanel.instance?.ClearDisabledProcesses();
        ResearchSystem.instance?.ResetAll();
        // Reset panel collapse state — both panels start open on a fresh world.
        InventoryController.instance?.inventoryHeader?.SetOpenSilent(true);
        AnimalController.instance?.jobsHeader?.SetOpenSilent(true);
        PlayerTaskController.instance?.ResetState(); // fresh world → onboarding starts at task 0
        FlowerController.instance?.ResetState();     // clear any stale flower layout / pending restore
        if (AnimalController.instance != null) AnimalController.instance.births = 0; // fresh colony → early-growth boost active
    }

    public void Load(string slotName) {
        string path = SaveStore.SlotPath(slotName);
        if (!System.IO.File.Exists(path)) { Debug.LogError("Save slot not found: " + slotName); return; }
        string json = System.IO.File.ReadAllText(path);
        currentSlot = slotName;
        LoadFromJson(json);
    }

    // Load world state from a JSON string instead of a file. Snapshot tests use this
    // so scenarios can live anywhere on disk and be loaded without a SaveDir round-trip.
    // Caller is responsible for waiting one frame after this call before reading
    // animal-aggregate state — PostLoadInit runs as a coroutine on next frame.
    public void LoadFromJson(string json) {
        WorldSaveData data = JsonConvert.DeserializeObject<WorldSaveData>(json);
        if (data == null) { Debug.LogError("Load aborted: save data failed to deserialize."); return; }

        WorldController.instance.ClearWorld();

        // Re-size the world grid if the save's recorded dimensions differ from the
        // live ones. Old saves (no savedNx/Ny) are assumed to match the legacy
        // 100×80 baseline. ReallocateGrid fires World.OnWorldAllocated, which
        // dependent systems (WaterController, TileMeshController) listen for to
        // re-init their world-area-sized buffers and per-tile subscriptions.
        // Must run AFTER ClearWorld (which iterates the OLD tiles to destroy
        // structures/animals) and BEFORE ApplySaveData (which fills the new grid).
        World world = World.instance;
        int savedNx = data.savedNx ?? 100;
        int savedNy = data.savedNy ?? 80;
        if (savedNx != world.nx || savedNy != world.ny)
            world.ReallocateGrid(savedNx, savedNy);

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
        // Reseed Rng from the persisted world seed before any phase that consumes Rng
        // (animal AI, weather rolls, recipe scoring, name draws). save.worldSeed = 0 on
        // pre-seed saves; Rng.Init(0) gives those a deterministic-from-zero stream from now on.
        Rng.Init(save.worldSeed);

        if (save.waterLevels != null) {
            for (int x = 0; x < world.nx; x++) {
                for (int y = 0; y < world.ny; y++) {
                    int idx = y * world.nx + x;
                    if (idx < save.waterLevels.Length)
                        world.GetTileAt(x, y).water = save.waterLevels[idx];
                }
            }
        }

        // Moisture levels — restore here unconditionally (byte→byte copy, doesn't need tile
        // types yet). Back-compat seeding for null / pre-moisture saves happens AFTER the
        // tile types loop below, where tile.type.solid is trustworthy.
        if (save.moistureLevels != null) {
            for (int x = 0; x < world.nx; x++) {
                for (int y = 0; y < world.ny; y++) {
                    int idx = y * world.nx + x;
                    if (idx < save.moistureLevels.Length)
                        world.GetTileAt(x, y).moisture = save.moistureLevels[idx];
                }
            }
        }

        if (save.tiles != null) {
            bool anyWall = false;
            foreach (TileSaveData tsd in save.tiles) {
                Tile tile = world.GetTileAt(tsd.x, tsd.y);
                if (tile == null) continue;
                // TEMPORARY: pre-split saves referenced a single "stone" tile type; map it to limestone.
                // Remove once old saves are no longer in circulation.
                string typeName = tsd.tileType == "stone" ? "limestone" : tsd.tileType;
                // Renamed placed-tile variant (was "limestone_brick"). Remove once old saves are gone.
                if (typeName == "limestone_brick") typeName = "limestone_placed";
                if (!string.IsNullOrEmpty(typeName) && Db.tileTypeByName.ContainsKey(typeName))
                    tile.type = Db.tileTypeByName[typeName];
                BackgroundType bt = (BackgroundType)tsd.backgroundWallType;
                // Legacy migration: pre-typed saves stored only hasBackgroundWall.
                // Type info is lost; default to Stone.
                if (bt == BackgroundType.None && tsd.hasBackgroundWall) bt = BackgroundType.Stone;
                tile.backgroundType = bt;
                if (bt != BackgroundType.None) anyWall = true;

                // Restore the overlay mask AFTER tile.type is set above — the type
                // setter may zero overlayMask if the saved type doesn't carry an
                // overlay, which is the correct behaviour for migration. Belt-and-
                // braces: also gate on tile.type.overlay here so a save with stale
                // bits on a non-overlay type doesn't sneak them through.
                byte mask = tsd.overlayMask ?? 0;
                tile.overlayMask = (mask != 0 && tile.type.overlay != null) ? mask : (byte)0;
                // overlayState only matters when grass actually exists on the tile.
                // Old saves (field absent) load as Live (default).
                if (tile.overlayMask != 0 && tsd.overlayState.HasValue)
                    tile.overlayState = (OverlayState)tsd.overlayState.Value;
                // Snow only meaningful on solid tiles; the type setter would have
                // cleared a stale flag during migration if the saved type were
                // non-solid, but gate here too for old saves that wrote `snow:true`
                // alongside an empty type.
                if (tsd.snow == true && tile.type.solid) {
                    tile.snow = true;
                    // Restore the under-snow grass snapshot. Old saves (pre-
                    // preservation) wrote `snow:true` without these fields;
                    // they load as null → defaults of (0, Live), which means
                    // "nothing to restore on melt" — the right migration.
                    if (tsd.preSnowOverlayMask.HasValue)
                        tile.preSnowOverlayMask  = tsd.preSnowOverlayMask.Value;
                    if (tsd.preSnowOverlayState.HasValue)
                        tile.preSnowOverlayState = (OverlayState)tsd.preSnowOverlayState.Value;
                }
            }

            // Ancient saves predate any wall data — apply the old y <= 43 Stone default.
            if (!anyWall) {
                for (int x = 0; x < world.nx; x++)
                    for (int y = 0; y <= 43 && y < world.ny; y++)
                        world.GetTileAt(x, y).backgroundType = BackgroundType.Stone;
            }
        }

        // Back-compat: saves predating soil moisture come in with moistureLevels=null.
        // Seed solid tiles to the worldgen default so sheltered soil isn't uniformly 0
        // (which would starve cave plants). Runs AFTER the tile types loop so .solid
        // reflects the save's terrain, not the default.
        if (save.moistureLevels == null) {
            for (int x = 0; x < world.nx; x++)
                for (int y = 0; y < world.ny; y++) {
                    Tile tile = world.GetTileAt(x, y);
                    if (tile.type.solid) tile.moisture = 50;
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
        // Pair up loaded rope-bridge posts and materialise each bridge's waypoint
        // chain + visuals BEFORE graph.Initialize so the resulting edges are
        // present in the first RebuildComponents sweep — otherwise mice can't
        // path across a saved bridge until something else perturbs the graph.
        // OnPlaced runs gameplay-only, so the live-build path doesn't reach here.
        RopeBridge.PairAllAfterLoad();
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

        // Restore discoveries from save. Uses DiscoverItem so parent-chain walks happen
        // (ancestor group rows light up). Items added since the save was written show up
        // as undiscovered, which is correct — they hadn't been seen yet.
        if (save.discoveredItems != null && InventoryController.instance != null) {
            foreach (string name in save.discoveredItems)
                if (Db.itemByName.TryGetValue(name, out Item item))
                    InventoryController.instance.DiscoverItem(item);
        }

        // Stage tree-collapse overrides before the first TickUpdate creates ItemDisplays.
        // ItemDisplay.Start reads pendingGroupOpenOverrides and falls back to defaultOpen when absent.
        if (InventoryController.instance != null)
            InventoryController.instance.pendingGroupOpenOverrides = save.inventoryTreeOpen;

        // Restore per-panel collapse state. Headers default to open; we only override when the
        // save explicitly recorded a collapsed panel.
        if (save.panelsOpen != null) {
            var ih = InventoryController.instance?.inventoryHeader;
            if (ih != null && !string.IsNullOrEmpty(ih.saveKey)
                    && save.panelsOpen.TryGetValue(ih.saveKey, out bool invOpen))
                ih.SetOpenSilent(invOpen);
            var jh = AnimalController.instance?.jobsHeader;
            if (jh != null && !string.IsNullOrEmpty(jh.saveKey)
                    && save.panelsOpen.TryGetValue(jh.saveKey, out bool jobsOpen))
                jh.SetOpenSilent(jobsOpen);
        }

        // Restore onboarding progress. Null (pre-feature save) → skip onboarding entirely so
        // returning players aren't re-shown tasks they've effectively already completed.
        if (PlayerTaskController.instance != null)
            PlayerTaskController.instance.currentIndex = save.playerTaskIndex ?? int.MaxValue;

        // Null (pre-feature save) → treat the early-growth boost as already spent.
        if (AnimalController.instance != null)
            AnimalController.instance.births = save.births ?? AnimalController.EarlyBirthBoostBirths;

        if (save.marketTargets != null && MarketBuilding.instance?.storage?.targets != null) {
            foreach (var kv in save.marketTargets)
                if (Db.itemByName.TryGetValue(kv.Key, out Item item))
                    MarketBuilding.instance.storage.targets[item] = kv.Value;
        }

        var rp = RecipePanel.instance;
        if (rp != null && save.disabledRecipeIds != null)
            foreach (int id in save.disabledRecipeIds)
                rp.SetAllowed(id, false);
        if (rp != null && save.expandedRecipeGroups != null)
            rp.SetExpandedGroups(save.expandedRecipeGroups);
        if (rp != null && save.disabledProcesses != null)
            rp.SetDisabledProcesses(save.disabledProcesses);

        RestoreResearch(save.research);

        // One-way building gate on jobs (woodworker/scientist). Seeds from the persisted set and
        // from the structures in this save, so it must read save.structures (not live structures);
        // runs before the lazy AddJobCounts so building-gated rows show on the first loaded frame.
        AnimalController.instance?.RestoreBuiltTypes(save.buildingsEverBuilt, save.structures);

        WeatherSystem.instance?.RestoreState(save.isRaining, save.humidity, save.tempAnomaly);

        // Original ground-line per column. Persisted from worldgen as of the
        // surfaceY-in-save-data change; old saves (and any save where the field
        // was somehow lost) fall back to re-deriving from current geometry,
        // which is best-effort — a player who mined the top off a column before
        // saving will get the new top, not the original line.
        if (save.surfaceY != null && save.surfaceY.Length == world.nx)
            world.surfaceY = (int[])save.surfaceY.Clone();
        else
            world.RecomputeSurfaceY();

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

        // Rebuild PowerSystem registries from world state. Power topology is fully derived
        // from placed shafts/producers/consumers, so no per-structure save data — same
        // pattern as the maintenance rebuild above. The first PowerSystem.Tick after load
        // (next 1-second cadence) recomputes networks and allocations.
        PowerSystem.instance?.RebuildFromWorld();

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

        // Populate the global inventory panel now. A loaded world may open paused, in which
        // case the tick-driven refresh never fires and the panel would stay empty until the
        // first unpause. RefreshDisplay also builds the ItemDisplay tree, consuming the
        // pendingGroupOpenOverrides staged above. Pure presentation; no game logic depends on it.
        InventoryController.instance?.RefreshDisplay();

        // Stash the saved flower layout for FlowerController.OnWorldReady (runs just after this,
        // from WorldController), which restores it directly instead of re-scanning. Null on old
        // saves → it falls back to a fresh worldgen scatter.
        FlowerController.instance?.StashRestore(save.flowers);
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
        // Size-mismatch drop: if the structure was saved at a footprint that no longer
        // matches the current StructType (most commonly because the building was upsized
        // since this save was written), silently drop the entry. Old saves without
        // savedNx/Ny are trusted — see SPEC-checklists.md "Upsizing an existing building".
        if (ssd.savedNx is int snx && ssd.savedNy is int sny) {
            Shape current = st.GetShape(ssd.shapeIndex);
            if (snx != current.nx || sny != current.ny) {
                Debug.Log($"RestoreStructure: dropping {ssd.typeName} at ({ssd.x},{ssd.y}) — saved size {snx}×{sny} != current {current.nx}×{current.ny}");
                return;
            }
        }
        Tile tile = World.instance.GetTileAt(ssd.x, ssd.y);
        if (tile == null) { Debug.LogError("Null tile on load for struct: " + ssd.typeName); return; }
        Structure structure = null;

        int partnerX = ssd.partnerX ?? -1;
        int partnerY = ssd.partnerY ?? -1;
        structure = Structure.Create(st, ssd.x, ssd.y, ssd.mirrored, ssd.rotation, ssd.shapeIndex, partnerX, partnerY);
        if (structure == null) {
            Debug.LogError("Structure.Create returned null on load: " + ssd.typeName); return;
        }

        // Restore the build-materials record (exact leaves consumed). Resolve names via Db;
        // skip unknown items (logged). Absent → materials stays null → first-leaf fallback.
        if (ssd.materialItems != null && ssd.materialFen != null) {
            var mats = new List<ItemQuantity>(ssd.materialItems.Length);
            for (int i = 0; i < ssd.materialItems.Length && i < ssd.materialFen.Length; i++) {
                if (Db.itemByName.TryGetValue(ssd.materialItems[i], out Item item))
                    mats.Add(new ItemQuantity(item, ssd.materialFen[i]));
                else
                    Debug.LogError($"RestoreStructure: unknown material item '{ssd.materialItems[i]}' on {st.name} at ({ssd.x},{ssd.y})");
            }
            if (mats.Count > 0) structure.materials = mats;
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
            // Multi-tile plants re-claim their upper tiles + rebuild sprite children.
            // Single-tile plants just redraw the anchor via the internal UpdateSprite call.
            plant.RebuildExtensionTiles();
        }
        if (structure is Building b) {
            if (b.workstation != null) b.workstation.uses = ssd.uses;
            b.disabled = ssd.disabled;
        }
        if (structure is Quarry qr && !string.IsNullOrEmpty(ssd.capturedTileType)) {
            if (Db.tileTypeByName.TryGetValue(ssd.capturedTileType, out TileType tt))
                qr.capturedTile = tt;
            else
                Debug.LogError($"RestoreStructure: unknown capturedTileType '{ssd.capturedTileType}' for quarry at ({ssd.x},{ssd.y})");
        }
        if (structure is DiggingPit drp) {
            // OnPlaced (which picks the dig direction and wires the door) is skipped on
            // load, so restore digDir verbatim and let RestoreOnLoad wire the single
            // door + rebuild the dish. ssd.uses was applied above (the Building branch);
            // workNode was repointed in the Structure ctor. Door wiring is independent of
            // the substrate, so a pit whose tile fails to resolve stays reachable, not orphaned.
            TileType tt = null;
            if (!string.IsNullOrEmpty(ssd.capturedTileType)
                && !Db.tileTypeByName.TryGetValue(ssd.capturedTileType, out tt))
                Debug.LogError($"RestoreStructure: unknown capturedTileType '{ssd.capturedTileType}' for digging pit at ({ssd.x},{ssd.y})");
            drp.RestoreOnLoad((DigDir)(ssd.digDir ?? 0), tt);
        }
        if (structure is Flywheel fw)
            fw.charge = Mathf.Clamp(ssd.flywheelCharge, 0f, Flywheel.Capacity);
        if (structure is Elevator el) {
            // Clamp to legal platform range — defends against corrupted saves or shape
            // changes between versions. Dispatch state, queue, and passenger are NOT
            // persisted; they reset to Idle/empty on load by virtue of being defaults
            // on the freshly-constructed Elevator.
            el.currentY = Mathf.Clamp(ssd.elevatorCurrentY, 0f, el.Shape.ny - 1f);
            el.recentTripTicks.LoadFrom(ssd.elevatorRecentTripTicks);
            el.recentEndToEndTicks.LoadFrom(ssd.elevatorRecentEndToEndTicks);
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
        // Restore furnishing slots (items + per-slot remaining lifetime). The ctor-time
        // FurnishingVisuals.Init iterated empty slots (fills happen here, after
        // Structure.Create returns), so we manually fire onSlotChanged on every filled
        // slot at the end via NotifyAllInstalled — that spawns the sprite GOs. Happiness
        // recompute fans out from the same callback but no-ops here since AnimalController
        // is still empty; the per-animal recompute happens later via FindHome.
        if (structure is Building fsBuilding && fsBuilding.furnishingSlots != null && ssd.furnishingInvData != null) {
            var fs = fsBuilding.furnishingSlots;
            int n = Mathf.Min(fs.SlotCount, ssd.furnishingInvData.Length);
            for (int i = 0; i < n; i++) {
                InventorySaveData isd = ssd.furnishingInvData[i];
                if (isd?.stacks == null) continue;
                foreach (ItemStackSaveData sd in isd.stacks) {
                    if (string.IsNullOrEmpty(sd.itemName) || sd.quantity <= 0) continue;
                    if (!Db.itemByName.TryGetValue(sd.itemName, out Item leaf)) {
                        Debug.LogError($"RestoreStructure: unknown furnishing item '{sd.itemName}' on {st.name} at ({ssd.x},{ssd.y})");
                        continue;
                    }
                    fs.slotInvs[i].Produce(leaf, sd.quantity);
                    fs.slotItems[i] = leaf;
                }
                if (ssd.furnishingRemainingDays != null && i < ssd.furnishingRemainingDays.Length)
                    fs.slotRemainingDays[i] = ssd.furnishingRemainingDays[i];
            }
            fs.NotifyAllInstalled();
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
        // Restore processor state (lifecycle + progress + the two internal inventories).
        // State/progress are set directly — do NOT re-fire transitions. ScanOrders re-registers
        // the fill/tap orders (and any haul-out for a Tapped tank) after all objects are restored.
        if (structure is Building pb && pb.processor != null) {
            pb.processor.state    = (Processor.State)ssd.processorState;
            pb.processor.progress = ssd.processorProgress;
            RestoreProcessorInv(ssd.processorInputData,  pb.processor.inputBuffer);
            RestoreProcessorInv(ssd.processorOutputData, pb.processor.output);
        }
        // WOM orders (harvest, workstation, fuel supply, processor) are registered by Reconcile() after all objects are restored.
    }

    // Restores a processor inventory (inputBuffer or output) by Producing each saved stack.
    // Produce re-adds to the global inventory too — correct, since ginv is rebuilt from
    // scratch on load. Null isd (empty inv / old save) is a no-op.
    void RestoreProcessorInv(InventorySaveData isd, Inventory inv) {
        if (isd?.stacks == null) return;
        foreach (ItemStackSaveData sd in isd.stacks) {
            if (string.IsNullOrEmpty(sd.itemName) || sd.quantity <= 0) continue;
            if (!Db.itemByName.TryGetValue(sd.itemName, out Item item)) {
                Debug.LogError($"RestoreProcessorInv: unknown item '{sd.itemName}'");
                continue;
            }
            inv.Produce(item, sd.quantity);
        }
    }

    void RestoreBlueprint(BlueprintSaveData bsd) {
        if (!Db.structTypeByName.ContainsKey(bsd.typeName)) {
            Debug.LogError("Unknown blueprint struct type on load: " + bsd.typeName); return;
        }
        StructType st = Db.structTypeByName[bsd.typeName];
        Blueprint bp = new Blueprint(st, bsd.x, bsd.y, mirrored: bsd.mirrored, autoRegister: false, rotation: bsd.rotation, shapeIndex: bsd.shapeIndex, x2: bsd.x2, y2: bsd.y2);
        bp.state                = (Blueprint.BlueprintState)bsd.state;
        bp.constructionProgress = bsd.constructionProgress;
        bp.priority             = bsd.priority;
        bp.disabled             = bsd.disabled;

        if (bsd.inv != null) {
            // Route saved stacks to their cost slot (the stack whose slotConstraint matches
            // the item), not by saved index. Heals saves from before slot-constraint routing
            // existed, where a smaller cost delivered first could squat in a slot sized for
            // a larger cost. Without this, those saves would load back into the same broken
            // arrangement and stay stuck.
            for (int i = 0; i < bsd.inv.stacks.Length; i++) {
                var ssd = bsd.inv.stacks[i];
                if (string.IsNullOrEmpty(ssd.itemName) || !Db.itemByName.ContainsKey(ssd.itemName) || ssd.quantity <= 0) continue;
                Item item = Db.itemByName[ssd.itemName];
                int slot = -1;
                for (int j = 0; j < bp.costs.Length; j++) {
                    if (Inventory.MatchesItem(item, bp.costs[j].item)) { slot = j; break; }
                }
                if (slot < 0 || slot >= bp.inv.itemStacks.Length) {
                    Debug.LogWarning($"RestoreBlueprint: saved item '{item.name}' on blueprint '{bsd.typeName}' at ({bsd.x},{bsd.y}) matches no cost slot — discarded.");
                    continue;
                }
                if (bp.inv.itemStacks[slot].item != null && bp.inv.itemStacks[slot].item != item) {
                    Debug.LogWarning($"RestoreBlueprint: cost slot {slot} on '{bsd.typeName}' at ({bsd.x},{bsd.y}) already holds '{bp.inv.itemStacks[slot].item.name}'; can't also accept '{item.name}' — discarded.");
                    continue;
                }
                bp.inv.itemStacks[slot].item     = item;
                bp.inv.itemStacks[slot].quantity = bp.inv.itemStacks[slot].quantity + ssd.quantity;
                bp.inv.itemStacks[slot].resAmount = 0;
                GlobalInventory.instance.AddItem(item, ssd.quantity);
            }
        }
        // Heal race condition: if the game was saved after all materials were delivered but before
        // DeliverToBlueprintObjective had a chance to transition state to Constructing, restore
        // directly into Constructing so we don't spin a supply order that immediately fails.
        if (bp.state == Blueprint.BlueprintState.Receiving && bp.IsFullyDelivered())
            bp.state = Blueprint.BlueprintState.Constructing;
        bp.RefreshColor();
        // Re-lock storage only when the deconstruct targets slot 0 (the building itself).
        // A deconstruct bp on a road/foreground co-located with a building must not lock that
        // building's storage on load.
        if (bp.state == Blueprint.BlueprintState.Deconstructing && bp.structType.depth == 0 && bp.tile.building?.storage != null)
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
        inv.wetUntil = isd.wetUntil;
        inv.UpdateSprite();
    }

    // Restores items from save data into an existing inventory instance (used by AnimalController).
    // Null-safe for backward compat: pre-bookSlotInv saves have no entry for that slot, and any
    // future newly-added slot inventories will likewise be missing from old files.
    public static void LoadInventory(Inventory inv, InventorySaveData data) {
        if (data == null || data.stacks == null) return;
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
        // Force a water render refresh: WaterController.Start's initial mask
        // upload races with worldgen, and Tick-driven updates don't run while
        // paused. This ensures water is visible immediately after every gen
        // path (initial, reset, load), including a pause-on-start.
        WaterController.instance?.UpdateSurfaceMask();
        // Build the decorative flower layer from the now-restored world: restores the saved
        // layout (stashed by ApplySaveData) or scatters fresh if none was saved. Here rather
        // than the boot coroutine so it covers every world-creation path — initial gen, reset,
        // and load — exactly like AnimalController.Load above. ResetSystemState cleared any
        // stale stash, so the reset/gen paths see pendingRestore == null and scatter.
        FlowerController.instance?.OnWorldReady(World.instance, Rng.worldSeed);
        // Warm the storage allow-tree now (off the loading screen) instead of on the player's
        // first storage click, which would otherwise instantiate one ItemDisplay per item and
        // hitch a frame. Idempotent across world-creation paths via the panel's build guard.
        StoragePanel.instance?.PreloadAllowTree();
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
    // Enumeration / metadata / delete live in SaveStore (filesystem only). The one
    // slot op that touches world state is rename, which must follow currentSlot.

    // Renames a save file, and if it was the currently-loaded slot, follows the
    // rename so a later save targets the same slot instead of triggering a spurious
    // "overwrite?" confirmation (the old auto-generated name no longer matches
    // currentSlot otherwise). Returns true on success.
    public bool RenameSlot(string oldName, string newName) {
        bool ok = SaveStore.RenameSlot(oldName, newName);
        if (ok && currentSlot == oldName) currentSlot = newName;
        return ok;
    }
}

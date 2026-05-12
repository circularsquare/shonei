using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Sub-view for InfoPanel that displays info for a single Structure or Blueprint.
// Handles buildings, plants, and blueprints with appropriate controls for each.
// Controls: enable/disable toggle, blueprint priority +/-, worker slots +/-, harvest flag toggle (plants).
public class StructureInfoView : MonoBehaviour {
    [SerializeField] TextMeshProUGUI text;

    [Header("Enable/Disable")]
    [SerializeField] GameObject enableBar;  // the full row; hidden when enable isn't applicable
    [SerializeField] Button enableDisableButton;
    [SerializeField] TextMeshProUGUI enableDisableLabel;
    [SerializeField] Sprite spriteEnabled;
    [SerializeField] Sprite spriteDisabled;

    [Header("Harvest Flag (plants only)")]
    [SerializeField] GameObject harvestFlagBar;           // full row; hidden for non-plants
    [SerializeField] Button harvestFlagButton;
    [SerializeField] TextMeshProUGUI harvestFlagLabel;

    [Header("Blueprint Priority")]
    [SerializeField] Button priorityUpButton;
    [SerializeField] Button priorityDownButton;
    [SerializeField] TextMeshProUGUI priorityText;  // label next to priority +/- buttons

    [Header("Worker Slots")]
    [SerializeField] Button workerSlotsUpButton;
    [SerializeField] Button workerSlotsDownButton;
    [SerializeField] TextMeshProUGUI workerSlotsText;  // label next to worker +/- buttons

    [Header("Deconstruct")]
    [SerializeField] GameObject deconstructBar;  // the full row; hidden for blueprints and already-deconstructing tiles
    [SerializeField] Button deconstructButton;

    [Header("Cancel")]
    [SerializeField] GameObject cancelBar;       // hidden for completed structures; shown on any blueprint tab
    [SerializeField] Button cancelButton;

    private Structure structure;
    private Blueprint blueprint;

    void Awake() {
        if (enableDisableButton != null)
            enableDisableButton.onClick.AddListener(OnClickEnableDisable);
        if (harvestFlagButton != null)
            harvestFlagButton.onClick.AddListener(OnClickHarvestFlag);
        if (priorityUpButton != null)
            priorityUpButton.onClick.AddListener(() => ChangeBlueprintPriority(1));
        if (priorityDownButton != null)
            priorityDownButton.onClick.AddListener(() => ChangeBlueprintPriority(-1));
        if (workerSlotsUpButton != null)
            workerSlotsUpButton.onClick.AddListener(() => ChangeWorkerSlots(1));
        if (workerSlotsDownButton != null)
            workerSlotsDownButton.onClick.AddListener(() => ChangeWorkerSlots(-1));
        if (deconstructButton != null)
            deconstructButton.onClick.AddListener(OnClickDeconstruct);
        if (cancelButton != null)
            cancelButton.onClick.AddListener(OnClickCancel);
    }

    // Show info for a completed structure (building, plant, or base structure).
    public void ShowStructure(Structure s) {
        structure = s;
        blueprint = null;
        gameObject.SetActive(true);
        Refresh();
    }

    // Show info for a blueprint (construction in progress).
    public void ShowBlueprint(Blueprint bp) {
        structure = null;
        blueprint = bp;
        gameObject.SetActive(true);
        Refresh();
    }

    public void Hide() {
        gameObject.SetActive(false);
        structure = null;
        blueprint = null;
        SetDeconstructVisible(false);
        SetCancelVisible(false);
        SetHarvestFlagVisible(false);
    }

    public void Refresh() {
        if (structure != null)
            RefreshStructure();
        else if (blueprint != null)
            RefreshBlueprint();
    }

    void RefreshStructure() {
        var sb = new System.Text.StringBuilder();
        sb.Append(structure.structType.name);

        // Condition display for maintained structures. BROKEN prominently below 50%,
        // plain percentage otherwise.
        if (structure.NeedsMaintenance) {
            int pct = Mathf.RoundToInt(structure.condition * 100f);
            if (structure.IsBroken)
                sb.Append($"\n <color=#d04040><b>BROKEN</b></color> condition: {pct}%");
            else
                sb.Append($"\n condition: {pct}%");
        }

        if (structure is Plant plant) {
            int maxStage = 4 * plant.plantType.maxHeight - 1;
            sb.Append($"\n stage: {plant.growthStage}/{maxStage}");
            if (plant.plantType.maxHeight > 1)
                sb.Append($"  height: {plant.height}/{plant.plantType.maxHeight}");
            AppendPlantComfort(sb, plant);
            // Surface the target-gated dormancy from RegisterHarvest's isActive — without this
            // the player sees a flagged, ripe crop sitting un-harvested with no in-game cue.
            if (plant.harvestFlagged && plant.harvestable
                && Recipe.AllItemsSatisfied(plant.plantType.products, InventoryController.instance?.targets))
                sb.Append("\n <color=#d04040>will not harvest: outputs above target</color>");
            AppendTileOrders(sb, plant.tile);
        } else if (structure is Building bldg) {
            if (bldg.structType.depleteAt > 0 && bldg.workstation != null)
                sb.Append("\n uses: " + bldg.workstation.uses + "/" + bldg.structType.depleteAt);
            if (bldg.reservoir != null) {
                int fuelQty = bldg.reservoir.Quantity();
                sb.Append($"\n fuel: {ItemStack.FormatQ(fuelQty)}/{ItemStack.FormatQ(bldg.reservoir.capacity)} {bldg.reservoir.fuelItem.name}");
            }
            // Only houses surface their Structure.res — it's the home-assignment count.
            // Other building types either don't have res (workstations, leisure, capacity==0)
            // or have it but never reserve into it.
            if (bldg.structType.name == "house" && bldg.res != null)
                sb.Append("\n occupants: " + bldg.res.reserved + "/" + bldg.res.capacity);
            if (bldg.furnishingSlots != null)
                AppendFurnishingSlots(sb, bldg.furnishingSlots);
            AppendTileOrders(sb, bldg.tile);
            AppendBuildingOrders(sb, bldg);
            if (bldg.storage != null)
                AppendInvOrders(sb, bldg.storage);
        }

        // Power info — applies to shafts (Structure but not Building/Plant), producers,
        // and consumers. No-op for everything else.
        AppendPowerInfo(sb, structure);

        // Elevator transit info — current state, queue depth, rolling-avg trip time.
        // No-op for non-elevators.
        AppendElevatorInfo(sb, structure);

        text.text = sb.ToString();

        // Show/hide controls.
        // Enable/disable is only meaningful for buildings whose disabled flag actually
        // gates behaviour: workstations (craft/research orders), reservoir buildings
        // (fuel supply orders), and leisure buildings (animals skip disabled ones in
        // Animal.TryPickLeisure). Hiding it for storage-only, beds, decorative, etc.
        Building b = structure as Building;
        bool canDisable = b != null
            && (b.structType.isWorkstation
                || b.reservoir != null
                || !string.IsNullOrEmpty(b.structType.leisureNeed));
        SetEnableDisableVisible(canDisable);
        if (canDisable) {
            enableDisableLabel.text = b.disabled ? "enable: " : "disable: ";
            UpdateEnableDisableSprite(b.disabled);
        }
        bool isPlant = structure is Plant;
        SetHarvestFlagVisible(isPlant);
        if (isPlant && harvestFlagLabel != null) {
            Plant p = (Plant)structure;
            harvestFlagLabel.text = p.harvestFlagged ? "unflag harvest" : "flag for harvest";
        }
        SetPriorityVisible(false);
        // Hide deconstruct if *this specific structure* already has a pending deconstruct
        // bp at its slot. Other slots' deconstructs on the same tile shouldn't gray out
        // this one (a deconstruct bp on the road doesn't preclude deconstructing the
        // building above it).
        Blueprint slotBp = structure.tile?.GetBlueprintAt(structure.structType.depth);
        bool alreadyDeconstructing = slotBp != null && slotBp.state == Blueprint.BlueprintState.Deconstructing;
        SetDeconstructVisible(!alreadyDeconstructing);
        SetCancelVisible(false);
        bool showWorkerSlots = b != null && b.structType.isWorkstation && b.structType.capacity > 1;
        SetWorkerSlotsVisible(showWorkerSlots);
        if (showWorkerSlots) {
            var bldg = b;
            var order = WorkOrderManager.instance?.FindOrdersForBuilding(bldg)
                .FirstOrDefault(o => o.type == WorkOrderManager.OrderType.Craft);
            if (order != null && workerSlotsText != null) {
                string capStr = order.res.effectiveCapacity < order.res.capacity
                    ? $"workers: {order.res.reserved}/{order.res.effectiveCapacity}/{order.res.capacity}"
                    : $"workers: {order.res.reserved}/{order.res.capacity}";
                workerSlotsText.text = capStr;
            }
        }
    }

    void RefreshBlueprint() {
        var sb = new System.Text.StringBuilder();
        sb.Append("blueprint: " + blueprint.structType.name);
        sb.Append("\n progress: " + blueprint.GetProgress());
        // Surface the silent failure paths in ConstructTask.Initialize for deconstructs:
        // both predicates make Initialize return false (with a haul re-promotion side effect),
        // leaving the order at 0/1 forever with no other in-game cue.
        if (blueprint.state == Blueprint.BlueprintState.Deconstructing) {
            if (blueprint.WouldCauseItemsFall())
                sb.Append("\n <color=#d04040>blocked: items above would fall</color>");
            if (blueprint.StorageNeedsEmptying())
                sb.Append("\n <color=#d04040>blocked: storage not empty</color>");
        }
        // Construction blueprints can be suspended (waiting for tileRequirements like water /
        // standability / mustBeSolidTile, or for bottom-row support). Suspended bps register no
        // work orders, so without this line the player sees "progress: 0/N" with nothing
        // happening and no in-game cue. The half-alpha tint from RefreshColor is the visual hint.
        else if (blueprint.IsSuspended())
            sb.Append("\n <color=#d04040>suspended: conditions not met</color>");
        var bpOrder = WorkOrderManager.instance?.FindOrderForBlueprint(blueprint);
        if (bpOrder != null)
            sb.Append("\n wo: " + bpOrder.type + " " + bpOrder.res.reserved + "/" + bpOrder.res.capacity);

        text.text = sb.ToString();

        // Show/hide controls
        SetEnableDisableVisible(true);
        if (enableDisableLabel != null)
            enableDisableLabel.text = blueprint.disabled ? "Enable" : "Disable";
        UpdateEnableDisableSprite(blueprint.disabled);
        SetPriorityVisible(true);
        if (priorityText != null)
            priorityText.text = "priority: " + blueprint.priority;
        SetWorkerSlotsVisible(false);
        SetDeconstructVisible(false);
        SetCancelVisible(true);
        SetHarvestFlagVisible(false);
    }

    // ── Controls ──

    void OnClickEnableDisable() {
        if (structure is Building bldg) {
            bldg.disabled = !bldg.disabled;
            Refresh();
        } else if (blueprint != null) {
            blueprint.SetDisabled(!blueprint.disabled);
            Refresh();
        }
    }

    // Toggles harvestFlagged on the selected plant. Routes through SetHarvestFlagged so
    // the WOM order gets registered / unregistered and the overlay sprite updates.
    void OnClickHarvestFlag() {
        if (structure is Plant plant) {
            plant.SetHarvestFlagged(!plant.harvestFlagged);
            Refresh();
        }
    }

    void ChangeBlueprintPriority(int delta) {
        if (blueprint != null) {
            blueprint.priority = Mathf.Max(0, blueprint.priority + delta);
            Refresh();
        }
    }

    void ChangeWorkerSlots(int delta) {
        if (structure is Building building && building.workstation != null && building.workstation.capacity > 1) {
            int current = building.workstation.workerLimit;
            WorkOrderManager.instance?.SetWorkstationCapacity(building, current + delta);
            Refresh();
        }
    }

    // Spawns a deconstruct blueprint for the *specific* structure shown in the active
    // tab. We bypass BuildPanel.Remove deliberately: that path operates on the tile and
    // would (a) cancel any unrelated pending construction bp on the same tile, and
    // (b) pick the first non-null slot, ignoring which structure tab the player chose.
    // The InfoPanel auto-rebuilds (and switches to the new bp tab) inside
    // CreateDeconstructBlueprint.
    void OnClickDeconstruct() {
        if (structure == null) return;
        Blueprint.CreateDeconstructBlueprint(structure.tile, structure);
    }

    // Cancels a blueprint (regular → refund + destroy, deconstruct → just destroy).
    // Routes through BuildPanel.Remove so every cancel path shares the same logic.
    // InfoPanel rebuild happens inside Blueprint.Destroy().
    void OnClickCancel() {
        if (blueprint == null) return;
        if (BuildPanel.instance == null) {
            Debug.LogError("StructureInfoView.OnClickCancel: BuildPanel.instance is null");
            return;
        }
        BuildPanel.instance.Remove(blueprint.tile);
    }

    // ── Visibility helpers ──

    void SetEnableDisableVisible(bool visible) {
        if (enableBar != null) enableBar.SetActive(visible);
    }

    void SetHarvestFlagVisible(bool visible) {
        if (harvestFlagBar != null) harvestFlagBar.SetActive(visible);
    }

    void UpdateEnableDisableSprite(bool disabled) {
        if (enableDisableButton == null) return;
        var sprite = disabled ? spriteDisabled : spriteEnabled;
        if (sprite != null) enableDisableButton.image.sprite = sprite;
    }

    void SetPriorityVisible(bool visible) {
        if (priorityUpButton != null) priorityUpButton.gameObject.SetActive(visible);
        if (priorityDownButton != null) priorityDownButton.gameObject.SetActive(visible);
        if (priorityText != null) priorityText.gameObject.SetActive(visible);
    }

    void SetWorkerSlotsVisible(bool visible) {
        if (workerSlotsUpButton != null) workerSlotsUpButton.gameObject.SetActive(visible);
        if (workerSlotsDownButton != null) workerSlotsDownButton.gameObject.SetActive(visible);
        if (workerSlotsText != null) workerSlotsText.gameObject.SetActive(visible);
    }

    void SetDeconstructVisible(bool visible) {
        if (deconstructBar != null) deconstructBar.SetActive(visible);
    }

    void SetCancelVisible(bool visible) {
        if (cancelBar != null) cancelBar.SetActive(visible);
    }

    // ── Work order display helpers (moved from InfoPanel) ──

    // Appends "temp: nowC  comfort: lo-hi C" and the equivalent moisture line for a plant.
    // Moisture is read from the soil tile directly below the plant (matches growth logic).
    // Null comfort bounds render as "?" so authors see at a glance that that side is unbounded.
    // ASCII-only: the m5x7 font doesn't cover °, en/em-dash.
    static void AppendPlantComfort(System.Text.StringBuilder sb, Plant plant) {
        PlantType pt = plant.plantType;
        float? nowT = WeatherSystem.instance?.temperature;
        string nowTempStr = nowT.HasValue ? $"{nowT.Value:F1}C" : "?";
        sb.Append($"\n temp: {nowTempStr}  comfort: {FormatBound(pt.tempMin, 0, "C")}-{FormatBound(pt.tempMax, 0, "C")}");

        Tile soil = World.instance.GetTileAt(plant.tile.x, plant.tile.y - 1);
        string nowMoistStr = soil != null ? $"{soil.moisture}/{MoistureSystem.MoistureMax}" : "?";
        sb.Append($"\n moisture: {nowMoistStr}  comfort: {FormatBound(pt.moistureMin, 0, "")}-{FormatBound(pt.moistureMax, 0, "")}");
    }

    static string FormatBound(float? v, int decimals, string suffix) {
        return v.HasValue ? v.Value.ToString("F" + decimals) + suffix : "?";
    }
    static string FormatBound(int? v, int decimals, string suffix) {
        return v.HasValue ? v.Value.ToString() + suffix : "?";
    }

    // Appends work orders keyed by tile (harvest, research). Mirrors AppendBuildingOrders'
    // [inactive] suffix so a target-gated or unripe harvest order is visible as such.
    static void AppendTileOrders(System.Text.StringBuilder sb, Tile tile) {
        if (WorkOrderManager.instance == null) return;
        var found = new List<string>();
        foreach (var o in WorkOrderManager.instance.FindOrdersForTile(tile)) {
            string active = o.isActive != null && !o.isActive() ? " [inactive]" : "";
            found.Add($"{o.type} {o.res.reserved}/{o.res.capacity}{active}");
        }
        if (found.Count > 0)
            sb.Append("\n wo: " + string.Join(", ", found));
    }

    // Appends work orders keyed by building (craft).
    // When effectiveCapacity < capacity, shows three-part "reserved/effective/max".
    static void AppendBuildingOrders(System.Text.StringBuilder sb, Building building) {
        if (WorkOrderManager.instance == null) return;
        var found = new List<string>();
        foreach (var o in WorkOrderManager.instance.FindOrdersForBuilding(building)) {
            string active = o.isActive != null && !o.isActive() ? " [inactive]" : "";
            string capStr = o.res.effectiveCapacity < o.res.capacity
                ? $"{o.res.reserved}/{o.res.effectiveCapacity}/{o.res.capacity}"
                : $"{o.res.reserved}/{o.res.capacity}";
            found.Add($"{o.type} {capStr}{active}");
        }
        if (found.Count > 0)
            sb.Append("\n wo: " + string.Join(", ", found));
    }

    // Renders mechanical-power state for any structure that participates in PowerSystem.
    // Layout (compose-by-role; multiple roles can apply, e.g. flywheel = storage):
    //   - "power: net N"                          for any participant
    //   - "  out: X.X"                             producers (current output)
    //   - "  status: powered (consuming X.X)"      consumers — X.X is current demand;
    //                                              0.0 means powered network but no active crafter
    //   - "  status: unpowered"                    consumers (network short on supply)
    //   - "  charge: X.X / Y.Y"                    storage (current / capacity)
    //   - "  (supply X.X)"                         trailing — disambiguates "net N" from "0 power"
    //   - "power: disconnected"                    if no port reaches a shaft
    // Silent for non-participants.
    static void AppendPowerInfo(System.Text.StringBuilder sb, Structure s) {
        var ps = PowerSystem.instance;
        if (ps == null || s == null) return;
        bool isProducer = s is PowerSystem.IPowerProducer;
        bool isConsumer = s is PowerSystem.IPowerConsumer;
        bool isStorage  = s is PowerSystem.IPowerStorage;
        bool isShaft    = s is PowerShaft;
        // Wrapped consumers — pump/press — own a BuildingPowerConsumer, not the Building itself.
        BuildingPowerConsumer wrapper = (s as Building)?.powerConsumer;
        if (!isProducer && !isConsumer && !isStorage && !isShaft && wrapper == null) return;

        int? netId = ps.GetNetworkId(s);
        if (netId == null) {
            sb.Append("\n power: disconnected");
            return;
        }
        sb.Append($"\n power: net {netId.Value}");

        if (isProducer && s is PowerSystem.IPowerProducer p)
            sb.Append($"  out: {p.CurrentOutput:F1}");

        if (isConsumer || wrapper != null) {
            Building b = wrapper != null ? wrapper.building : (s as Building);
            PowerSystem.IPowerConsumer consumer = wrapper ?? (s as PowerSystem.IPowerConsumer);
            // "powered" for display purposes: either the allocator satisfied us this tick,
            // or we're idle (CurrentDemand == 0) on a network that *would* satisfy our
            // nominal demand if a crafter showed up. The latter avoids the InfoPanel
            // flickering between "powered (1.0)" while crafting and "unpowered" the moment
            // the operator finishes a round and walks off — semantically the network is
            // still fine.
            bool powered = b != null && ps.IsBuildingPowered(b);
            if (!powered && consumer.CurrentDemand <= 0f) {
                PowerSystem.PowerNetwork idleNet = ps.GetNetwork(netId.Value);
                if (idleNet != null) {
                    float available = idleNet.supply;
                    foreach (PowerSystem.IPowerStorage st in idleNet.storage)
                        available += Mathf.Max(0f, st.MaxDischarge);
                    if (available + 1e-4f >= BuildingPowerConsumer.Demand) powered = true;
                }
            }
            if (powered)
                sb.Append($"  status: <color=#40d040>powered</color> (consuming {consumer.CurrentDemand:F1})");
            else
                sb.Append("  status: unpowered");
        }

        if (s is Flywheel fw)
            sb.Append($"  charge: {fw.charge:F1}/{Flywheel.Capacity:F0}");

        // Network supply — the disambiguator. Without this, "net 0" looks like "0 power".
        var net = ps.GetNetwork(netId.Value);
        if (net != null)
            sb.Append($"  (supply {net.supply:F1})");
    }

    // Per-elevator status: dispatch state, queue depth (real + tentative), recent
    // mouse-perceived end-to-end transit time. Diagnostic snapshot for the player to
    // see whether the elevator is busy, idle, or contended.
    static void AppendElevatorInfo(System.Text.StringBuilder sb, Structure s) {
        if (!(s is Elevator e)) return;
        sb.Append($"\n elevator: {e.dispatchState}  queue: {e.QueueCountForInfo}  pending: {e.PendingCountForInfo}");
        float avg = e.recentEndToEndTicks.Average(fallback: -1f);
        if (avg > 0f) sb.Append($"  avg ride: {avg:F1}s");
    }

    // Per-house furnishing slot summary: one line per slot showing slot name, the installed
    // item (or "empty"), and remaining lifetime in days for filled slots. The SupplyFurnishing
    // WOM order shows separately via AppendBuildingOrders, so this focuses on slot state.
    // ASCII-only (m5x7 font, no special glyphs).
    static void AppendFurnishingSlots(System.Text.StringBuilder sb, FurnishingSlots fs) {
        if (fs == null || fs.SlotCount == 0) return;
        sb.Append("\n furnishings:");
        for (int i = 0; i < fs.SlotCount; i++) {
            Item item = fs.Get(i);
            if (item == null) {
                sb.Append($"\n  {fs.slotNames[i]}: empty");
            } else {
                float days = fs.slotRemainingDays[i];
                sb.Append($"\n  {fs.slotNames[i]}: {item.name} ({days:F1}d left)");
            }
        }
    }

    // Appends work orders keyed by inventory (market hauls).
    static void AppendInvOrders(System.Text.StringBuilder sb, Inventory inv) {
        if (WorkOrderManager.instance == null) return;
        var found = new List<string>();
        foreach (var o in WorkOrderManager.instance.FindOrdersForInv(inv))
            found.Add($"{o.type} {o.res.reserved}/{o.res.capacity}");
        if (found.Count > 0)
            sb.Append("\n wo: " + string.Join(", ", found));
    }
}

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
            sb.Append("\n growth: " + plant.growthStage);
            if (structure.res != null)
                sb.Append("\n res: " + structure.res.reserved + "/" + structure.res.capacity);
            AppendTileOrders(sb, plant.tile);
        } else if (structure is Building bldg) {
            if (bldg.structType.depleteAt > 0 && bldg.workstation != null)
                sb.Append("\n uses: " + bldg.workstation.uses + "/" + bldg.structType.depleteAt);
            if (bldg.reservoir != null) {
                int fuelQty = bldg.reservoir.Quantity();
                sb.Append($"\n fuel: {ItemStack.FormatQ(fuelQty)}/{ItemStack.FormatQ(bldg.reservoir.capacity)} {bldg.reservoir.fuelItem.name}");
            }
            if (structure.res != null)
                sb.Append("\n res: " + structure.res.reserved + "/" + structure.res.capacity);
            AppendTileOrders(sb, bldg.tile);
            AppendBuildingOrders(sb, bldg);
            if (bldg.storage != null)
                AppendInvOrders(sb, bldg.storage);
        } else {
            // Base structure (platform, ladder, road, etc.)
            if (structure.res != null)
                sb.Append("\n res: " + structure.res.reserved + "/" + structure.res.capacity);
        }

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
        // Hide deconstruct if this tile already has a pending deconstruct blueprint —
        // clicking it would now cancel the deconstruct (BuildPanel.Remove is unified),
        // which is confusing for a button labelled "deconstruct".
        bool alreadyDeconstructing = structure.tile?.GetMatchingBlueprint(
            bp => bp.state == Blueprint.BlueprintState.Deconstructing) != null;
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

    // Spawns a deconstruct blueprint for the selected structure by delegating to
    // BuildPanel.Remove — same path as right-click in the Build panel. The InfoPanel
    // auto-rebuilds (and switches to the new bp tab) inside CreateDeconstructBlueprint.
    void OnClickDeconstruct() {
        if (structure == null) return;
        if (BuildPanel.instance == null) {
            Debug.LogError("StructureInfoView.OnClickDeconstruct: BuildPanel.instance is null");
            return;
        }
        BuildPanel.instance.Remove(structure.tile);
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

    // Appends work orders keyed by tile (harvest, research).
    static void AppendTileOrders(System.Text.StringBuilder sb, Tile tile) {
        if (WorkOrderManager.instance == null) return;
        var found = new List<string>();
        foreach (var o in WorkOrderManager.instance.FindOrdersForTile(tile))
            found.Add($"{o.type} {o.res.reserved}/{o.res.capacity}");
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

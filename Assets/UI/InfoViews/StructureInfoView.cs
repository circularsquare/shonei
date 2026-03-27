using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Sub-view for InfoPanel that displays info for a single Structure or Blueprint.
/// Handles buildings, plants, and blueprints with appropriate controls for each.
/// Controls: enable/disable toggle, blueprint priority +/-, worker slots +/-.
/// </summary>
public class StructureInfoView : MonoBehaviour {
    [SerializeField] TextMeshProUGUI text;

    [Header("Enable/Disable")]
    [SerializeField] GameObject enableBar;  // the full row; hidden when enable isn't applicable
    [SerializeField] Button enableDisableButton;
    [SerializeField] TextMeshProUGUI enableDisableLabel;

    [Header("Blueprint Priority")]
    [SerializeField] Button priorityUpButton;
    [SerializeField] Button priorityDownButton;
    [SerializeField] TextMeshProUGUI priorityText;  // label next to priority +/- buttons

    [Header("Worker Slots")]
    [SerializeField] Button workerSlotsUpButton;
    [SerializeField] Button workerSlotsDownButton;
    [SerializeField] TextMeshProUGUI workerSlotsText;  // label next to worker +/- buttons

    private Structure structure;
    private Blueprint blueprint;

    void Awake() {
        if (enableDisableButton != null)
            enableDisableButton.onClick.AddListener(OnClickEnableDisable);
        if (priorityUpButton != null)
            priorityUpButton.onClick.AddListener(() => ChangeBlueprintPriority(1));
        if (priorityDownButton != null)
            priorityDownButton.onClick.AddListener(() => ChangeBlueprintPriority(-1));
        if (workerSlotsUpButton != null)
            workerSlotsUpButton.onClick.AddListener(() => ChangeWorkerSlots(1));
        if (workerSlotsDownButton != null)
            workerSlotsDownButton.onClick.AddListener(() => ChangeWorkerSlots(-1));
    }

    /// <summary>Show info for a completed structure (building, plant, or base structure).</summary>
    public void ShowStructure(Structure s) {
        structure = s;
        blueprint = null;
        gameObject.SetActive(true);
        Refresh();
    }

    /// <summary>Show info for a blueprint (construction in progress).</summary>
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

        if (structure is Plant plant) {
            sb.Append("\n growth: " + plant.growthStage);
            if (structure.res != null)
                sb.Append("\n res: " + structure.res.reserved + "/" + structure.res.capacity);
            AppendTileOrders(sb, plant.tile);
        } else if (structure is Building bldg) {
            if (bldg.disabled)
                sb.Append("  [DISABLED]");
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

        // Show/hide controls
        bool isBuilding = structure is Building;
        SetEnableDisableVisible(isBuilding);
        if (isBuilding) {
            Building b = (Building)structure;
            enableDisableLabel.text = b.disabled ? "Enable" : "Disable";
        }
        SetPriorityVisible(false);
        bool showWorkerSlots = isBuilding && ((Building)structure).structType.isWorkstation
            && ((Building)structure).structType.capacity > 1;
        SetWorkerSlotsVisible(showWorkerSlots);
        if (showWorkerSlots) {
            var bldg = (Building)structure;
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
        if (blueprint.disabled)
            sb.Append("\n [DISABLED]");
        sb.Append("\n progress: " + blueprint.GetProgress());
        var bpOrder = WorkOrderManager.instance?.FindOrderForBlueprint(blueprint);
        if (bpOrder != null)
            sb.Append("\n wo: " + bpOrder.type + " " + bpOrder.res.reserved + "/" + bpOrder.res.capacity);

        text.text = sb.ToString();

        // Show/hide controls
        SetEnableDisableVisible(true);
        if (enableDisableLabel != null)
            enableDisableLabel.text = blueprint.disabled ? "Enable" : "Disable";
        SetPriorityVisible(true);
        if (priorityText != null)
            priorityText.text = "priority: " + blueprint.priority;
        SetWorkerSlotsVisible(false);
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

    void ChangeBlueprintPriority(int delta) {
        if (blueprint != null) {
            blueprint.priority = Mathf.Max(0, blueprint.priority + delta);
            Refresh();
        }
    }

    void ChangeWorkerSlots(int delta) {
        if (structure is Building building && building.workstation != null && building.workstation.capacity > 1) {
            var order = WorkOrderManager.instance?.FindOrdersForBuilding(building)
                .FirstOrDefault(o => o.type == WorkOrderManager.OrderType.Craft);
            if (order == null) return;
            order.res.effectiveCapacity = Mathf.Clamp(
                order.res.effectiveCapacity + delta, 0, order.res.capacity);
            building.workstation.effectiveCapacity = order.res.effectiveCapacity;
            Refresh();
        }
    }

    // ── Visibility helpers ──

    void SetEnableDisableVisible(bool visible) {
        if (enableBar != null) enableBar.SetActive(visible);
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

    // ── Work order display helpers (moved from InfoPanel) ──

    /// <summary>Appends work orders keyed by tile (harvest, research).</summary>
    static void AppendTileOrders(System.Text.StringBuilder sb, Tile tile) {
        if (WorkOrderManager.instance == null) return;
        var found = new List<string>();
        foreach (var o in WorkOrderManager.instance.FindOrdersForTile(tile))
            found.Add($"{o.type} {o.res.reserved}/{o.res.capacity}");
        if (found.Count > 0)
            sb.Append("\n wo: " + string.Join(", ", found));
    }

    /// <summary>
    /// Appends work orders keyed by building (craft).
    /// When effectiveCapacity &lt; capacity, shows three-part "reserved/effective/max".
    /// </summary>
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

    /// <summary>Appends work orders keyed by inventory (market hauls).</summary>
    static void AppendInvOrders(System.Text.StringBuilder sb, Inventory inv) {
        if (WorkOrderManager.instance == null) return;
        var found = new List<string>();
        foreach (var o in WorkOrderManager.instance.FindOrdersForInv(inv))
            found.Add($"{o.type} {o.res.reserved}/{o.res.capacity}");
        if (found.Count > 0)
            sb.Append("\n wo: " + string.Join(", ", found));
    }
}

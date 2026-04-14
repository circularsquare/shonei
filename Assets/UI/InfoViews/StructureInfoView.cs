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
    [SerializeField] Sprite spriteEnabled;
    [SerializeField] Sprite spriteDisabled;

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
        if (deconstructButton != null)
            deconstructButton.onClick.AddListener(OnClickDeconstruct);
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
        SetDeconstructVisible(false);
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
            enableDisableLabel.text = b.disabled ? "enable: " : "disable: ";
            UpdateEnableDisableSprite(b.disabled);
        }
        SetPriorityVisible(false);
        // Hide deconstruct if this tile already has a pending deconstruct blueprint —
        // matches the guard in BuildPanel.Remove so the button behavior stays in lockstep.
        bool alreadyDeconstructing = structure.tile?.GetMatchingBlueprint(
            bp => bp.state == Blueprint.BlueprintState.Deconstructing) != null;
        SetDeconstructVisible(!alreadyDeconstructing);
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
            int current = building.workstation.workerLimit;
            WorkOrderManager.instance?.SetWorkstationCapacity(building, current + delta);
            Refresh();
        }
    }

    // Spawns a deconstruct blueprint for the selected structure by delegating to
    // BuildPanel.Remove — same path as right-click in the Build panel, so guards
    // (no double-deconstruct, refund-on-cancel) stay centralized in one place.
    void OnClickDeconstruct() {
        if (structure == null) return;
        // Capture tile before calling Remove: RebuildSelection below will null `structure`.
        Tile t = structure.tile;
        if (BuildPanel.instance == null) {
            Debug.LogError("StructureInfoView.OnClickDeconstruct: BuildPanel.instance is null");
            return;
        }
        if (!BuildPanel.instance.Remove(t)) return; // no-op if already deconstructing
        // Rebuild tabs so the new deconstruct blueprint shows up alongside the structure tab.
        InfoPanel.instance?.RebuildSelection();
    }

    // ── Visibility helpers ──

    void SetEnableDisableVisible(bool visible) {
        if (enableBar != null) enableBar.SetActive(visible);
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

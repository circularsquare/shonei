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

    [Header("Comfort Bars (plants only)")]
    [SerializeField] ComfortBar tempBar;       // temperature range bar; hidden for non-plants
    [SerializeField] ComfortBar moistureBar;   // soil-moisture range bar; hidden for non-plants

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

    [Header("Blueprint Cost Rows")]
    [SerializeField] Transform costRowContainer;   // VerticalLayoutGroup parent for per-ingredient rows
    [SerializeField] GameObject costRowPrefab;     // BlueprintCostRow prefab

    [Header("Foundry Cast Target")]
    [SerializeField] Transform castOptionContainer;  // HorizontalLayoutGroup parent for cast-target option buttons; hidden for non-foundries

    [Header("Housing Occupants")]
    [SerializeField] Transform occupantContainer;   // VerticalLayoutGroup parent for resident rows; hidden for non-housing
    [SerializeField] GameObject occupantRowPrefab;  // OccupantRow prefab (reused for the work-flag rows too)

    [Header("Work Flag Assignment")]
    [SerializeField] Transform flagRosterContainer; // VLG parent for assigned + available mouse rows; hidden for non-flags

    private Structure structure;
    private Blueprint blueprint;

    // Cast-target option buttons for the selected foundry (barId 0 = "auto"). Built once when the
    // selected foundry changes, then only re-highlighted each tick (so hovering doesn't churn them).
    private readonly List<CastOption> castOptions = new List<CastOption>();
    private Foundry castOptionsBuiltFor;
    private class CastOption { public GameObject go; public TextMeshProUGUI label; public int barId; }

    // Per-ingredient rows for the active blueprint. Built once per tab-show (and after a ban
    // toggle), then only dynamically refreshed each tick — never rebuilt per tick, so hovered
    // X/chip tooltips survive. costRowsBuiltFor tracks which blueprint the rows belong to.
    private readonly List<GameObject> costRows = new List<GameObject>();
    private Blueprint costRowsBuiltFor;

    // Resident rows for a housing building. Built only when the resident set changes (not per
    // tick), so the head-icon hover tooltip isn't torn down mid-hover. occupantRowsBuiltFor
    // mirrors the residents the current rows represent, for change detection.
    private readonly List<GameObject> occupantRows = new List<GameObject>();
    private readonly List<Animal> occupantRowsBuiltFor = new List<Animal>();

    // Work-flag rows (assigned + available mice). Rebuilt only when the flag, its assigned set, or
    // the colony size changes — same tooltip-preserving discipline as the other row lists.
    private readonly List<GameObject> flagRows = new List<GameObject>();
    private Building flagRowsBuiltFor;
    private readonly List<Animal> flagAssignedCache = new List<Animal>();
    private int flagTotalCache = -1;

    // Action-button glyphs, loaded once. buttonx = evict/unassign, buttonplus = assign.
    private static Sprite _xSprite, _plusSprite;
    private static Sprite XSprite => _xSprite != null ? _xSprite : (_xSprite = Resources.Load<Sprite>("Sprites/Misc/buttonx"));
    private static Sprite PlusSprite => _plusSprite != null ? _plusSprite : (_plusSprite = Resources.Load<Sprite>("Sprites/Misc/buttonplus"));

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
        // Self-wire the inline-help hover handler onto the text blob (no scene step needed).
        if (text != null && text.GetComponent<InfoTextHover>() == null)
            text.gameObject.AddComponent<InfoTextHover>();
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
        ClearCostRows();
        if (costRowContainer != null) costRowContainer.gameObject.SetActive(false);
        ClearOccupantRows();
        if (occupantContainer != null) occupantContainer.gameObject.SetActive(false);
        ClearFlagRows();
        if (flagRosterContainer != null) flagRosterContainer.gameObject.SetActive(false);
        SetDeconstructVisible(false);
        SetCancelVisible(false);
        SetHarvestFlagVisible(false);
        SetComfortVisible(false);
    }

    public void Refresh() {
        if (structure != null)
            RefreshStructure();
        else if (blueprint != null)
            RefreshBlueprint();
    }

    void RefreshStructure() {
        // Cost rows belong to blueprints only — clear them when a completed structure is shown.
        ClearCostRows();
        if (costRowContainer != null) costRowContainer.gameObject.SetActive(false);

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
            sb.Append(Help.Icon("condition"));
        }

        if (structure is Plant plant) {
            int maxStage = plant.plantType.maxStage;
            sb.Append($"\n stage: {plant.growthStage}/{maxStage}");
            if (plant.plantType.maxHeight > 1)
                sb.Append($"  height: {plant.height}/{plant.plantType.maxHeight}");
            ShowPlantComfort(plant);
            // Why growth is impeded, if at all (season dormancy, no open space above, soil too dry,
            // etc.). Complements the comfort bars: the bars show the input value, this states the
            // growth verdict (frozen vs merely slowed). No line when the plant is growing fine / done.
            AppendGrowthBlock(sb, plant.GetGrowthBlock());
            // Sun exposure — only surfaced when overhead shade is actually reducing it. Force a
            // live recompute for the inspected plant so the % reacts immediately when the player
            // builds/removes a roof (the growth loop itself only re-samples on a throttle).
            plant.RecomputeSunExposure();
            int sunPct = Mathf.RoundToInt(plant.SunOpen01 * 100f);
            if (sunPct < 100)
                sb.Append($"\n <color=#b35a14>sun: {sunPct}%</color>{Help.Icon("sun")}");
            // Surface the target-gated dormancy from RegisterHarvest's isActive — without this
            // the player sees a flagged, ripe crop sitting un-harvested with no in-game cue.
            if (plant.harvestFlagged && plant.IsDoneGrowing()
                && Recipe.AllItemsSatisfied(plant.plantType.products, InventoryController.instance?.targets))
                sb.Append("\n <color=#d04040>will not harvest: outputs above target</color>");
            AppendTileOrders(sb, plant.tile);
        } else if (structure is Building bldg) {
            if (bldg.structType.depleteAt > 0 && bldg.workstation != null) {
                sb.Append("\n uses: " + bldg.workstation.uses + "/" + bldg.structType.depleteAt);
                // Extraction buildings (quarry / digging pit) hang their per-dig yield list off
                // a help hover on the uses line, rather than spelling it out inline.
                if (bldg is IExtractor ext && ext.CapturedProducts != null) {
                    Help.SetDynamic("mining", "Yields", BuildExtractionYields(ext.CapturedProducts));
                    sb.Append(Help.Icon("mining"));
                }
            }
            if (bldg.reservoir != null) {
                int fuelQty = bldg.reservoir.Quantity();
                // Show what's actually stocked (the concrete leaf — consistent across restricted and
                // any-fuel reservoirs). Empty: fall back to the restriction name, else generic "fuel".
                string fuelName = bldg.reservoir.HeldLeaf()?.name ?? bldg.reservoir.fuelItem?.name ?? "fuel";
                sb.Append($"\n fuel: {ItemStack.FormatQ(fuelQty)}/{ItemStack.FormatQ(bldg.reservoir.capacity)} {fuelName}");
            }
            // Foundry: surface the live temperature so the player can see it heating up / cooling,
            // with a help icon explaining the heat system + cast target.
            if (bldg is Foundry fdyTemp) {
                sb.Append($"\n temp: {Mathf.RoundToInt(fdyTemp.temperature)}°");
                sb.Append(Help.Icon("foundry"));
            }
            // Thermometer: the ambient temperature, same readout as the top-bar date display.
            if (bldg is Thermometer && WeatherSystem.instance != null)
                sb.Append($"\n temp: {WeatherSystem.FormatTemp(WeatherSystem.instance.temperature)}");
            // Greenhouse: surface its moisture mode. A self-contained frame shows its isolated pool
            // (farmers refill it); a ground frame just notes it draws from the soil below.
            if (bldg is Greenhouse ghb) {
                if (ghb.selfContained) sb.Append($"\n water: {ghb.selfMoisture}/100");
                else                   sb.Append("\n draws from soil below");
            }
            // Well: pooled groundwater stored vs total shaft capacity, in liang.
            if (bldg is Well wl)
                sb.Append($"\n water: {ItemStack.FormatQ(wl.StoredWaterFen)}/{ItemStack.FormatQ(wl.CapacityFen)}");
            // Only housing surfaces its Structure.res — it's the home-assignment count.
            // Other building types either don't have res (workstations, leisure, capacity==0)
            // or have it but never reserve into it.
            if (bldg.structType.isHousing && bldg.res != null)
                sb.Append("\n occupants: " + bldg.res.reserved + "/" + bldg.res.capacity);
            if (bldg.furnishingSlots != null)
                AppendFurnishingSlots(sb, bldg.furnishingSlots);
            if (bldg.processor != null)
                AppendProcessor(sb, bldg.processor);
            if (bldg is Foundry fdy)
                AppendFoundry(sb, fdy);
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
        // Set comfort visibility once (not toggle off→on) — toggling SetActive each tick
        // fires Tooltippable.OnDisable, which hid the bar's tooltip ~1 tick after hover.
        SetComfortVisible(isPlant);
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
        RefreshCastOptions(structure as Foundry);
        RefreshOccupants(structure as Building);
        RefreshFlag(structure as Building);
    }

    void RefreshBlueprint() {
        RefreshCastOptions(null);
        RefreshOccupants(null);
        RefreshFlag(null);
        var sb = new System.Text.StringBuilder();
        SetComfortVisible(false);
        sb.Append("blueprint: " + blueprint.structType.name);
        // Per-cost ingredient lines now render as interactive rows (see cost rows below); the text
        // blob only carries the construction fraction once building/tearing-down is underway.
        if (blueprint.state == Blueprint.BlueprintState.Constructing
            || blueprint.state == Blueprint.BlueprintState.Deconstructing)
            sb.Append($"\n progress: {blueprint.constructionProgress:F0}/{blueprint.constructionCost}");
        if (blueprint.structType.job != null)
            sb.Append("\n job: " + blueprint.structType.job.name);
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
        if (DebugMode.Enabled) {
            var bpOrder = WorkOrderManager.instance?.FindOrderForBlueprint(blueprint);
            if (bpOrder != null)
                sb.Append("\n wo: " + bpOrder.type + " " + bpOrder.res.reserved + "/" + bpOrder.res.capacity);
        }

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

        RefreshCostRows();
    }

    // ── Blueprint cost rows ──

    // Drives the per-ingredient rows. Deconstruct blueprints show no costs. Rows are structurally
    // (re)built only when the blueprint identity changes (tab-show) or after a ban toggle requests
    // it via BlueprintCostRow's onChanged callback; otherwise each tick only dynamically refreshes
    // existing rows (label + X visibility), so a hovered tooltip is never torn down.
    void RefreshCostRows() {
        bool show = blueprint != null
                 && blueprint.state != Blueprint.BlueprintState.Deconstructing
                 && blueprint.costs.Length > 0;
        if (costRowContainer != null && costRowContainer.gameObject.activeSelf != show)
            costRowContainer.gameObject.SetActive(show);
        if (!show) { ClearCostRows(); return; }

        if (costRowsBuiltFor != blueprint)
            RebuildCostRows();
        else
            foreach (GameObject go in costRows)
                go.GetComponent<BlueprintCostRow>()?.UpdateDynamic();
    }

    void RebuildCostRows() {
        ClearCostRows();
        if (costRowContainer == null || costRowPrefab == null || blueprint == null) return;
        for (int i = 0; i < blueprint.costs.Length; i++) {
            GameObject go = Instantiate(costRowPrefab, costRowContainer);
            go.SetActive(true);
            costRows.Add(go);
            go.GetComponent<BlueprintCostRow>()?.Setup(blueprint, i, RebuildCostRows);
        }
        costRowsBuiltFor = blueprint;
        LayoutUtil.RebuildImmediate((RectTransform)costRowContainer);
    }

    void ClearCostRows() {
        foreach (GameObject go in costRows) {
            if (go == null) continue;
            go.SetActive(false); // drop from layout immediately (Destroy is deferred to frame end)
            Destroy(go);
        }
        costRows.Clear();
        costRowsBuiltFor = null;
    }

    // ── Foundry cast-target picker ──
    // A row of clickable options — "auto" + one per castable bar — under the foundry's info. Built
    // once per selected-foundry change (like cost rows, so hovering doesn't churn them), then only the
    // highlight (active option = full colour, others dimmed) refreshes each tick. `f` null → hidden.
    void RefreshCastOptions(Foundry f) {
        bool show = f != null && castOptionContainer != null;
        if (castOptionContainer != null && castOptionContainer.gameObject.activeSelf != show)
            castOptionContainer.gameObject.SetActive(show);
        if (!show) { ClearCastOptions(); return; }
        if (castOptionsBuiltFor != f) RebuildCastOptions(f);
        RefreshCastHighlight(f);
    }

    void RebuildCastOptions(Foundry f) {
        ClearCastOptions();
        if (castOptionContainer == null || text == null) return;
        AddCastOption("auto", 0); // 0 = auto (scorer picks by production need)
        var casts = Db.GetFoundryCastRecipes();
        if (casts != null)
            foreach (Recipe r in casts) {
                if (r.outputs.Length == 0 || r.outputs[0].item == null) continue;
                if (!r.IsEligibleForPicking()) continue; // hide locked / globally-disabled casts
                AddCastOption(r.outputs[0].item.name, r.outputs[0].item.id);
            }
        castOptionsBuiltFor = f;
        LayoutUtil.RebuildImmediate((RectTransform)castOptionContainer);
    }

    // Builds one option as a single GameObject: a TMP label that doubles as the Button's target
    // graphic (clicking the text bounds fires). Transition None so the Button's hover-tint doesn't
    // fight the manual highlight colour. Font/size copied from the info text blob for consistency.
    void AddCastOption(string labelText, int barId) {
        var go = new GameObject("CastOpt_" + barId, typeof(RectTransform));
        go.transform.SetParent(castOptionContainer, false);
        var lbl = go.AddComponent<TextMeshProUGUI>();
        lbl.font = text.font;
        lbl.fontSize = text.fontSize;
        lbl.alignment = TextAlignmentOptions.BottomLeft;
        lbl.text = labelText;
        var btn = go.AddComponent<Button>();
        btn.transition = Selectable.Transition.None;
        btn.targetGraphic = lbl;
        int captured = barId;
        btn.onClick.AddListener(() => OnClickCastOption(captured));
        castOptions.Add(new CastOption { go = go, label = lbl, barId = barId });
    }

    void RefreshCastHighlight(Foundry f) {
        int activeId = f.castMode == Foundry.CastMode.Auto ? 0 : f.manualTargetBarId;
        Color active = text.color;
        Color dim = new Color(active.r, active.g, active.b, 0.4f);
        foreach (CastOption o in castOptions)
            if (o.label != null) o.label.color = o.barId == activeId ? active : dim;
    }

    void OnClickCastOption(int barId) {
        if (structure is not Foundry f) return;
        if (barId == 0) f.castMode = Foundry.CastMode.Auto;
        else { f.castMode = Foundry.CastMode.Manual; f.manualTargetBarId = barId; }
        Refresh();
    }

    void ClearCastOptions() {
        foreach (CastOption o in castOptions) {
            if (o.go == null) continue;
            o.go.SetActive(false);
            Destroy(o.go);
        }
        castOptions.Clear();
        castOptionsBuiltFor = null;
    }

    // ── Housing occupant rows ──
    // One row per resident (head icon + name + evict). Shown only for housing buildings.
    // Rebuilt only when the resident set changes — like cost/cast rows, so a hovered head
    // tooltip survives — then otherwise left alone.
    void RefreshOccupants(Building bldg) {
        bool show = bldg != null && bldg.structType.isHousing
                 && occupantContainer != null && occupantRowPrefab != null;
        if (occupantContainer != null && occupantContainer.gameObject.activeSelf != show)
            occupantContainer.gameObject.SetActive(show);
        if (!show) { ClearOccupantRows(); return; }

        List<Animal> residents = bldg.GetResidents();
        if (!ResidentsUnchanged(residents))
            RebuildOccupantRows(residents);
    }

    bool ResidentsUnchanged(List<Animal> residents) {
        if (residents.Count != occupantRowsBuiltFor.Count) return false;
        for (int i = 0; i < residents.Count; i++)
            if (residents[i] != occupantRowsBuiltFor[i]) return false;
        return true;
    }

    void RebuildOccupantRows(List<Animal> residents) {
        ClearOccupantRows();
        if (occupantContainer == null || occupantRowPrefab == null) return;
        foreach (Animal a in residents) {
            GameObject go = Instantiate(occupantRowPrefab, occupantContainer);
            go.SetActive(true);
            occupantRows.Add(go);
            // Refresh re-fetches residents → an evicted mouse drops out and the list rebuilds.
            Animal m = a;
            go.GetComponent<OccupantRow>()?.Setup(m, XSprite, "evict", () => { m.EvictFromHome(); Refresh(); });
        }
        occupantRowsBuiltFor.Clear();
        occupantRowsBuiltFor.AddRange(residents);
        LayoutUtil.RebuildImmediate((RectTransform)occupantContainer);
    }

    void ClearOccupantRows() {
        foreach (GameObject go in occupantRows) {
            if (go == null) continue;
            go.SetActive(false); // drop from layout immediately (Destroy is deferred to frame end)
            Destroy(go);
        }
        occupantRows.Clear();
        occupantRowsBuiltFor.Clear();
    }

    // ── Work flag assignment rows ──
    // For a work flag, lists assigned mice (button = unassign) followed by every other mouse
    // (button = assign). Clicking a head selects that mouse. Rebuilt only when the flag, its
    // assigned set, or the colony size changes — so a hovered head tooltip survives.
    void RefreshFlag(Building bldg) {
        bool show = bldg != null && bldg.structType.isWorkFlag
                 && flagRosterContainer != null && occupantRowPrefab != null;
        if (flagRosterContainer != null && flagRosterContainer.gameObject.activeSelf != show)
            flagRosterContainer.gameObject.SetActive(show);
        if (!show) { ClearFlagRows(); return; }

        List<Animal> assigned = bldg.GetAssignedMice();
        int total = AnimalController.instance != null ? AnimalController.instance.na : 0;
        // Change detection: same flag, same assigned set, same colony size → available set is
        // unchanged too (it's everyone minus the assigned), so leave the rows alone.
        if (bldg == flagRowsBuiltFor && total == flagTotalCache && SameAnimals(assigned, flagAssignedCache))
            return;
        RebuildFlagRows(bldg, assigned);
        flagRowsBuiltFor = bldg;
        flagTotalCache = total;
        flagAssignedCache.Clear();
        flagAssignedCache.AddRange(assigned);
    }

    static bool SameAnimals(List<Animal> a, List<Animal> b) {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    void RebuildFlagRows(Building bldg, List<Animal> assigned) {
        ClearFlagRows();
        if (flagRosterContainer == null || occupantRowPrefab == null) return;
        // Assigned first — unassign button (reverts the mouse to its home anchor).
        foreach (Animal a in assigned) {
            Animal m = a;
            GameObject go = Instantiate(occupantRowPrefab, flagRosterContainer);
            go.SetActive(true);
            flagRows.Add(go);
            go.GetComponent<OccupantRow>()?.Setup(m, XSprite, "unassign", () => { m.UnassignFlag(); Refresh(); });
        }
        // Everyone else — assign button (sets this flag as their work anchor).
        AnimalController ac = AnimalController.instance;
        if (ac != null) {
            for (int i = 0; i < ac.na; i++) {
                Animal a = ac.animals[i];
                if (a == null || a.assignedFlag == bldg) continue;
                Animal m = a;
                GameObject go = Instantiate(occupantRowPrefab, flagRosterContainer);
                go.SetActive(true);
                flagRows.Add(go);
                go.GetComponent<OccupantRow>()?.Setup(m, PlusSprite, "assign", () => { m.AssignToFlag(bldg); Refresh(); });
            }
        }
        LayoutUtil.RebuildImmediate((RectTransform)flagRosterContainer);
    }

    void ClearFlagRows() {
        foreach (GameObject go in flagRows) {
            if (go == null) continue;
            go.SetActive(false);
            Destroy(go);
        }
        flagRows.Clear();
        flagRowsBuiltFor = null;
        flagAssignedCache.Clear();
        flagTotalCache = -1;
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

    // Drives the temperature + moisture comfort bars for a plant and reveals them.
    // Temp "now" is the (greenhouse-regulated) ambient; moisture "now" is the plant's reservoir —
    // the soil below or a self-contained greenhouse pool (matches growth logic) — null if there's
    // none, which hides that bar's marker. Comfortable bounds come straight from PlantType
    // (null = unbounded; the bar runs its green band to the domain edge).
    void ShowPlantComfort(Plant plant) {
        PlantType pt = plant.plantType;
        if (tempBar != null) {
            // Show the temperature the plant actually grows at: a greenhouse regulates ambient
            // toward its target, so the bar marker reflects the regulated value (matches Plant.Grow).
            float? nowTemp = WeatherSystem.instance?.temperature;
            // A broken greenhouse no longer regulates, so the bar shows raw ambient (matches Plant.Grow).
            Structure ghStruct = plant.tile.greenhouse;
            StructType gh = (ghStruct != null && !ghStruct.IsBroken) ? ghStruct.structType : null;
            if (nowTemp.HasValue && gh != null) nowTemp = gh.RegulatedTemp(nowTemp.Value);
            tempBar.Set(pt.tempMin, pt.tempMax, nowTemp);
        }
        if (moistureBar != null) {
            // "Now" is the plant's reservoir — soil below, or a self-contained greenhouse's pool —
            // matching the growth gate. -1 (no reservoir) hides the marker.
            int rm = plant.ReservoirMoisture();
            moistureBar.Set(pt.moistureMin, pt.moistureMax, rm >= 0 ? (float?)rm : null);
        }
    }

    void SetComfortVisible(bool visible) {
        if (tempBar != null) tempBar.gameObject.SetActive(visible);
        if (moistureBar != null) moistureBar.gameObject.SetActive(visible);
    }

    // One concise line naming why a plant isn't advancing. Frozen reasons render red (growth
    // halted), the soft "growing slowly" reasons amber (still creeping). No line when growing
    // normally / fully grown (GrowthBlock.None). Wording lives here, not in the model.
    static void AppendGrowthBlock(System.Text.StringBuilder sb, Plant.GrowthBlock block) {
        string text;
        bool frozen = true;
        switch (block) {
            case Plant.GrowthBlock.TooCold:      text = "dormant: too cold";        break;
            case Plant.GrowthBlock.TooHot:       text = "dormant: too hot";         break;
            case Plant.GrowthBlock.OutOfSeason:  text = "dormant: out of season";   break;
            case Plant.GrowthBlock.NoSpaceAbove: text = "blocked: no space above";  break;
            case Plant.GrowthBlock.SoilTooDry:   text = "stalled: soil too dry";    break;
            case Plant.GrowthBlock.SlowDry:      text = "growing slowly: soil dry"; frozen = false; break;
            case Plant.GrowthBlock.SlowWet:      text = "growing slowly: soil wet"; frozen = false; break;
            default: return; // None
        }
        string color = frozen ? "#d04040" : "#d0a040";
        sb.Append($"\n <color={color}>{text}</color>");
    }

    // Per-dig yield distribution for extraction buildings (quarry / digging pit), built
    // from the captured tile's data so JSON rebalances show up without a copy edit. One
    // item per line for the help tooltip, e.g. "limestone\ngypsum 10%\nmalachite 4%".
    // Quantity is shown only when it differs from the usual 1 liang; chance only when < 100%.
    static string BuildExtractionYields(ItemQuantity[] yields) {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < yields.Length; i++) {
            ItemQuantity iq = yields[i];
            if (i > 0) sb.Append("\n");
            sb.Append(iq.item.name);
            if (iq.quantity != ItemStack.LiangToFen(1f))
                sb.Append(" x " + ItemStack.FormatQ(iq.quantity, iq.item));
            if (iq.chance < 1f)
                sb.Append(" " + Mathf.RoundToInt(iq.chance * 100f) + "%");
        }
        return sb.ToString();
    }

    // Appends work orders keyed by tile (harvest, research). Mirrors AppendBuildingOrders'
    // [inactive] suffix so a target-gated or unripe harvest order is visible as such.
    static void AppendTileOrders(System.Text.StringBuilder sb, Tile tile) {
        if (!DebugMode.Enabled || WorkOrderManager.instance == null) return;
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
        if (!DebugMode.Enabled || WorkOrderManager.instance == null) return;
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

    // Minimal per-processor batch readout — dev visibility: lifecycle state, the batch recipe,
    // and while Working the progress vs duration (+ the temperature rate for an untended ferment).
    static void AppendProcessor(System.Text.StringBuilder sb, Processor p) {
        sb.Append($"\n processor: {p.state.ToString().ToLower()}");
        if (p.recipe != null) sb.Append($" ({p.recipe.description}){(p.batchRounds > 1 ? $" x{p.batchRounds}" : "")}");
        string loaded = DescribeInv(p.inputBuffer);
        if (loaded != null) sb.Append($"\n  loaded: {loaded}");
        string made = DescribeInv(p.output);
        if (made != null) sb.Append($"\n  output: {made}");
        if (p.state == Processor.State.Working) {
            sb.Append($"\n  {Recipe.FormatDuration(p.progress)}/{Recipe.FormatDuration(p.duration)}");
            if (!p.tended && p.TempScaled) {
                float t = WeatherSystem.instance != null ? WeatherSystem.instance.temperature : 17.5f;
                sb.Append($"  rate {p.Rate(t):P0}");
            }
        }
    }

    // Foundry melt-pool readout. Temp is shown in the main block above; the cast-target picker is a
    // separate interactive row (castOptionContainer / RefreshCastOptions).
    static void AppendFoundry(System.Text.StringBuilder sb, Foundry f) {
        foreach (MeltChunk c in f.chunks)
            sb.Append($"\n {ItemStack.FormatQ(c.fen, c.ore)} {c.ore.name} {Mathf.RoundToInt(c.meltProgress * 100)}% melted");
        if (f.moltenPool.Count > 0) {
            sb.Append("\n molten:");
            foreach (System.Collections.Generic.KeyValuePair<int, int> kv in f.moltenPool)
                sb.Append($" {Db.items[kv.Key].name} {ItemStack.FormatQ(kv.Value)}");
        }
        string made = DescribeInv(f.output);
        if (made != null) sb.Append($"\n output: {made}");
        sb.Append("\n cast target:"); // the clickable option list (castOptionContainer) renders below this
    }

    // Lists an inventory's non-empty stacks as "name qty, name qty", or null if empty.
    static string DescribeInv(Inventory inv) {
        if (inv == null) return null;
        var parts = new List<string>();
        foreach (ItemStack s in inv.itemStacks)
            if (s?.item != null && s.quantity > 0)
                parts.Add($"{s.item.name} {ItemStack.FormatQ(s.quantity, s.item)}");
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    // Same as DescribeInv but over a recipe's declared quantities (the not-yet-produced output).
    static string DescribeItems(ItemQuantity[] items) {
        if (items == null) return null;
        var parts = new List<string>();
        foreach (ItemQuantity iq in items)
            if (iq.item != null) parts.Add($"{iq.item.name} {ItemStack.FormatQ(iq.quantity, iq.item)}");
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    // Appends work orders keyed by inventory (market hauls).
    static void AppendInvOrders(System.Text.StringBuilder sb, Inventory inv) {
        if (!DebugMode.Enabled || WorkOrderManager.instance == null) return;
        var found = new List<string>();
        foreach (var o in WorkOrderManager.instance.FindOrdersForInv(inv))
            found.Add($"{o.type} {o.res.reserved}/{o.res.capacity}");
        if (found.Count > 0)
            sb.Append("\n wo: " + string.Join(", ", found));
    }
}

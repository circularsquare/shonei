using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Tabbed info panel that displays details about selected entities.
// When the player clicks a tile, InfoPanel builds tabs for each entity found
// (tile, structures, blueprints, animals) and delegates rendering to sub-views:
// TileInfoView, StructureInfoView, AnimalInfoView.
public class InfoPanel : MonoBehaviour {
    public static InfoPanel instance { get; protected set; }

    [Header("Highlights")]
    [SerializeField] GameObject animalHighlight;
    [SerializeField] GameObject tileHighlight;

    [Header("Tab Bar")]
    [SerializeField] ScrollRect tabScrollRect;        // wraps tabContent for horizontal scroll
    [SerializeField] Transform tabContent;            // HorizontalLayoutGroup parent for tab buttons
    [SerializeField] GameObject tabButtonPrefab;      // prefab: Button + TMP label child

    [Header("Body")]
    [SerializeField] ScrollRect contentScrollRect;    // vertical scroll wrapping the active sub-view; reset to top on tab switch

    [Header("Sub-Views")]
    [SerializeField] TileInfoView tileInfoView;
    [SerializeField] StructureInfoView structureInfoView;
    [SerializeField] AnimalInfoView animalInfoView;

    // ── Selection state ──
    private SelectionContext currentSelection;
    private List<TabEntry> tabs = new List<TabEntry>();
    private int activeTabIndex = -1;
    private List<GameObject> tabButtonGos = new List<GameObject>();

    // Backward-compat: returns the selected tile, used by Blueprint.cs to check if info needs refresh.
    public object obj => currentSelection?.tile;

    // What kind of entity each tab represents.
    private enum TabType { Tile, Structure, Blueprint, Animal }

    private struct TabEntry {
        public TabType type;
        public string label;
        public object data; // Tile, Structure, Blueprint, or Animal
    }

    public void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one " + this.GetType().ToString()); }
        instance = this;

        Deselect();
    }

    // ── Public API ──

    // Primary entry point for showing a structured selection.
    // Called by MouseController after building a SelectionContext.
    public void ShowSelection(SelectionContext ctx) {
        // A ctx with a null tile is valid iff it carries animals (e.g. merchants
        // in transit, clicked from MerchantJourneyDisplay — no tile context wanted).
        bool hasAnimals = ctx != null && ctx.animals != null && ctx.animals.Count > 0;
        if (ctx == null || (ctx.tile == null && !hasAnimals)) {
            Deselect();
            return;
        }

        currentSelection = ctx;
        gameObject.SetActive(true);
        BuildTabs();

        // Always select the first tab (ordering puts most relevant first)
        SelectTab(0);
    }

    // Backward-compatible entry point. Wraps raw objects into a SelectionContext.
    // Used by: World.UpdateInfo (tick refresh calls UpdateInfo directly),
    // WorldController.ShowInfo(null) (world clear), drag-select paths.
    public void ShowInfo(object obj) {
        if (obj == null) {
            Deselect();
            return;
        }
        if (obj is Tile tile) {
            ShowSelection(SelectionContext.FromTile(tile));
        } else if (obj is List<Animal> animals) {
            // Animals-only selection — no tile context. Used e.g. by
            // MerchantJourneyDisplay so clicking a merchant in transit doesn't
            // also surface the tile/building they happen to be standing on.
            ShowSelection(SelectionContext.FromTile(null, animals));
        }
    }

    // Refreshes the currently active sub-view. Called each tick from World.cs.
    public void UpdateInfo() {
        if (currentSelection == null) {
            Deselect();
            return;
        }
        RefreshActiveView();
    }

    // Rebuilds tabs from fresh tile state (e.g. after a blueprint completes or is deconstructed).
    // Optionally auto-selects a specific structure or blueprint tab.
    // Animals are preserved from the stored selection so their tabs aren't silently dropped.
    public void RebuildSelection(Structure preferStructure = null, Blueprint preferBlueprint = null) {
        if (currentSelection == null) return;
        currentSelection = SelectionContext.FromTile(currentSelection.tile, currentSelection.animals);
        BuildTabs();
        if (preferStructure != null) {
            for (int i = 0; i < tabs.Count; i++) {
                if (tabs[i].type == TabType.Structure && (Structure)tabs[i].data == preferStructure) {
                    SelectTab(i);
                    return;
                }
            }
        }
        if (preferBlueprint != null) {
            for (int i = 0; i < tabs.Count; i++) {
                if (tabs[i].type == TabType.Blueprint && (Blueprint)tabs[i].data == preferBlueprint) {
                    SelectTab(i);
                    return;
                }
            }
        }
        SelectTab(0);
    }

    public void Deselect() {
        currentSelection = null;
        activeTabIndex = -1;
        gameObject.SetActive(false);
        HideAllViews();
        ClearTabButtons();
        if (animalHighlight != null) animalHighlight.SetActive(false);
        if (tileHighlight != null) tileHighlight.SetActive(false);
    }

    // ── Tab management ──

    private void BuildTabs() {
        ClearTabButtons();
        tabs.Clear();

        var ctx = currentSelection;

        // Animals first
        foreach (var a in ctx.animals)
            tabs.Add(new TabEntry { type = TabType.Animal, label = a.aName, data = a });

        // Blueprints before structures: queued/in-progress work is usually what the player wants
        // to act on (e.g. cancel a deconstruct, adjust priority), so surface it to the left.
        foreach (var bp in ctx.blueprints)
            tabs.Add(new TabEntry { type = TabType.Blueprint, label = "bp: " + bp.structType.name, data = bp });

        // Structures by increasing depth (0=building, 1=platform, 2=foreground, 3=road)
        foreach (var s in ctx.structures)
            tabs.Add(new TabEntry { type = TabType.Structure, label = s.structType.name, data = s });

        // Tile tab last — use tile type name if available, otherwise generic "tile".
        // Skipped entirely when there's no tile context (animals-only selection).
        if (ctx.tile != null) {
            string tileName = ctx.tile.type?.name;
            string tileLabel = string.IsNullOrEmpty(tileName) || tileName == "empty" ? "tile" : tileName;
            tabs.Add(new TabEntry { type = TabType.Tile, label = tileLabel, data = ctx.tile });
        }

        // Spawn tab buttons
        for (int i = 0; i < tabs.Count; i++) {
            int idx = i; // capture for closure
            GameObject btnGo = Instantiate(tabButtonPrefab, tabContent);
            btnGo.SetActive(true);
            var label = btnGo.GetComponentInChildren<TMPro.TextMeshProUGUI>();
            if (label != null) label.text = tabs[i].label;
            var btn = btnGo.GetComponent<Button>();
            if (btn != null) btn.onClick.AddListener(() => SelectTab(idx));
            tabButtonGos.Add(btnGo);
        }

        // Force layout rebuild so tabs don't stack on first frame
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)tabContent);
    }

    private void SelectTab(int index) {
        if (index < 0 || index >= tabs.Count) return;
        activeTabIndex = index;
        HideAllViews();
        // Reset body scroll on tab switch. StopMovement() first kills any in-flight
        // velocity so the new content doesn't bounce past the top.
        if (contentScrollRect != null) {
            contentScrollRect.StopMovement();
            contentScrollRect.verticalNormalizedPosition = 1f;
        }

        var tab = tabs[index];
        switch (tab.type) {
            case TabType.Tile:
                tileInfoView.Show((Tile)tab.data);
                UpdateHighlights(null, currentSelection.tile);
                break;
            case TabType.Structure:
                structureInfoView.ShowStructure((Structure)tab.data);
                UpdateHighlights(null, currentSelection.tile);
                break;
            case TabType.Blueprint:
                structureInfoView.ShowBlueprint((Blueprint)tab.data);
                UpdateHighlights(null, currentSelection.tile);
                break;
            case TabType.Animal:
                var animal = (Animal)tab.data;
                animalInfoView.Show(animal);
                UpdateHighlights(animal, null);
                break;
        }

        // Visual feedback: highlight the active tab button
        for (int i = 0; i < tabButtonGos.Count; i++) {
            var img = tabButtonGos[i].GetComponent<Image>();
            if (img != null)
                img.color = i == index ? new Color(1f, 1f, 1f, 1f) : new Color(0.7f, 0.7f, 0.7f, 0.8f);
        }
    }

    private void RefreshActiveView() {
        if (activeTabIndex < 0 || activeTabIndex >= tabs.Count) return;
        var tab = tabs[activeTabIndex];
        switch (tab.type) {
            case TabType.Tile:      tileInfoView.Refresh(); break;
            case TabType.Structure: structureInfoView.Refresh(); break;
            case TabType.Blueprint: structureInfoView.Refresh(); break;
            case TabType.Animal:    animalInfoView.Refresh(); break;
        }
    }

    private void HideAllViews() {
        if (tileInfoView != null) tileInfoView.Hide();
        if (structureInfoView != null) structureInfoView.Hide();
        if (animalInfoView != null) animalInfoView.Hide();
    }

    private void ClearTabButtons() {
        foreach (var go in tabButtonGos)
            Destroy(go);
        tabButtonGos.Clear();
    }

    // ── Highlights ──

    private void UpdateHighlights(Animal animal, Tile tile) {
        if (animalHighlight != null)
            animalHighlight.SetActive(animal != null);
        if (tileHighlight != null) {
            tileHighlight.SetActive(tile != null);
            if (tile != null)
                tileHighlight.transform.position = new Vector3(tile.x, tile.y, -1);
        }
    }

    // ── Update ──

    void Update() {
        // Follow selected animal with highlight
        var animal = animalInfoView != null ? animalInfoView.SelectedAnimal : null;
        if (animal != null && animalHighlight != null && animalHighlight.activeSelf)
            animalHighlight.transform.position = animal.go.transform.position + new Vector3(0, 0.6f, -1);
    }
}

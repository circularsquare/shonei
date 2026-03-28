using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tabbed info panel that displays details about selected entities.
/// When the player clicks a tile, InfoPanel builds tabs for each entity found
/// (tile, structures, blueprints, animals) and delegates rendering to sub-views:
/// TileInfoView, StructureInfoView, AnimalInfoView.
/// </summary>
public class InfoPanel : MonoBehaviour {
    public static InfoPanel instance { get; protected set; }

    [Header("Highlights")]
    public GameObject animalHighlight;    // assign in Inspector; follows selected animal
    public GameObject tileHighlight;      // assign in Inspector; overlays selected tile

    [Header("Tab Bar")]
    [SerializeField] ScrollRect tabScrollRect;        // wraps tabContent for horizontal scroll
    [SerializeField] Transform tabContent;            // HorizontalLayoutGroup parent for tab buttons
    [SerializeField] GameObject tabButtonPrefab;      // prefab: Button + TMP label child

    [Header("Sub-Views")]
    [SerializeField] TileInfoView tileInfoView;
    [SerializeField] StructureInfoView structureInfoView;
    [SerializeField] AnimalInfoView animalInfoView;

    // ── Selection state ──
    private SelectionContext currentSelection;
    private List<TabEntry> tabs = new List<TabEntry>();
    private int activeTabIndex = -1;
    private List<GameObject> tabButtonGos = new List<GameObject>();

    /// <summary>Backward-compat: returns the selected tile, used by Blueprint.cs to check if info needs refresh.</summary>
    public object obj => currentSelection?.tile;

    /// <summary>What kind of entity each tab represents.</summary>
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

    /// <summary>
    /// Primary entry point for showing a structured selection.
    /// Called by MouseController after building a SelectionContext.
    /// </summary>
    public void ShowSelection(SelectionContext ctx) {
        if (ctx == null || ctx.tile == null) {
            Deselect();
            return;
        }

        currentSelection = ctx;
        gameObject.SetActive(true);
        BuildTabs();

        // Always select the first tab (ordering puts most relevant first)
        SelectTab(0);
    }

    /// <summary>
    /// Backward-compatible entry point. Wraps raw objects into a SelectionContext.
    /// Used by: World.UpdateInfo (tick refresh calls UpdateInfo directly),
    /// WorldController.ShowInfo(null) (world clear), drag-select paths.
    /// </summary>
    public void ShowInfo(object obj) {
        if (obj == null) {
            Deselect();
            return;
        }
        if (obj is Tile tile) {
            ShowSelection(SelectionContext.FromTile(tile));
        } else if (obj is List<Animal> animals) {
            // Animals without a tile context — pick tile from first animal if possible
            Tile aTile = animals.Count > 0 ? World.instance.GetTileAt((int)animals[0].x, (int)animals[0].y) : null;
            ShowSelection(SelectionContext.FromTile(aTile, animals));
        }
    }

    /// <summary>Refreshes the currently active sub-view. Called each tick from World.cs.</summary>
    public void UpdateInfo() {
        if (currentSelection == null) {
            Deselect();
            return;
        }
        RefreshActiveView();
    }

    /// <summary>
    /// Rebuilds tabs from fresh tile state (e.g. after a blueprint completes or is deconstructed).
    /// Optionally auto-selects the tab for a newly created structure.
    /// Animals are preserved from the stored selection so their tabs aren't silently dropped.
    /// </summary>
    public void RebuildSelection(Structure preferStructure = null) {
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

        // Structures by increasing depth (0=building, 1=platform, 2=foreground, 3=road)
        foreach (var s in ctx.structures)
            tabs.Add(new TabEntry { type = TabType.Structure, label = s.structType.name, data = s });

        // Blueprints by increasing depth
        foreach (var bp in ctx.blueprints)
            tabs.Add(new TabEntry { type = TabType.Blueprint, label = "bp: " + bp.structType.name, data = bp });

        // Tile tab last — use tile type name if available, otherwise generic "tile"
        string tileName = ctx.tile?.type?.name;
        string tileLabel = string.IsNullOrEmpty(tileName) || tileName == "empty" ? "tile" : tileName;
        tabs.Add(new TabEntry { type = TabType.Tile, label = tileLabel, data = ctx.tile });

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

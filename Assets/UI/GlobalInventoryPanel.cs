using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Full-screen, exclusive panel: a per-item detail view that the always-visible inventory panel
// links out to. Shows each item's location breakdown (storage / floor / carried / market + total),
// hosts target editing (moved off the always-visible panel), and a per-item "don't consume" toggle.
//
// Modeled on RecipePanel: UI.RegisterExclusive in Awake, UI.OpenExclusive in Toggle, a 0.5s
// refresh while open. The row tree reuses the always-visible panel's discovery + collapse rules
// (InventoryController.discoveredItems + per-row open state) so both trees behave identically.
//
// Rows are a flat sibling list (parents precede children, Db.items order); depth is an indent
// spacer, collapse is visibility — see InventoryDetailRow for why we avoid nested LayoutGroups.
public class GlobalInventoryPanel : MonoBehaviour {
    public static GlobalInventoryPanel instance { get; protected set; }

    // Reload-Domain-off: a plain static Object ref must be nulled on subsystem registration.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; }

    [SerializeField] Transform rowContainer;           // scroll Content (VerticalLayoutGroup + ContentSizeFitter)
    [SerializeField] InventoryDetailRow rowPrefab;     // Resources/Prefabs/InventoryDetailRow fallback if unwired
    [SerializeField] ScrollRect scrollRect;
    [SerializeField] Button closeButton;

    readonly Dictionary<int, InventoryDetailRow> rows = new Dictionary<int, InventoryDetailRow>();
    bool built;
    float refreshTimer;
    const float RefreshInterval = 0.5f;

    void Awake() {
        if (instance != null) Debug.LogError("two GlobalInventoryPanels!");
        instance = this;
        UI.RegisterExclusive(gameObject);
        if (closeButton != null) closeButton.onClick.AddListener(() => gameObject.SetActive(false));
    }

    void OnEnable() {
        BuildOnce();
        RefreshAll();
    }

    void Update() {
        refreshTimer += Time.deltaTime;
        if (refreshTimer >= RefreshInterval) { refreshTimer = 0f; RefreshAll(); }
    }

    public void Toggle() {
        if (gameObject.activeSelf) gameObject.SetActive(false);
        else UI.OpenExclusive(gameObject);
    }

    // ── Build ───────────────────────────────────────────────────────────
    void BuildOnce() {
        if (built) return;
        if (rowPrefab == null) rowPrefab = Resources.Load<InventoryDetailRow>("Prefabs/InventoryDetailRow");
        if (rowContainer == null || rowPrefab == null) {
            Debug.LogError("GlobalInventoryPanel: rowContainer/rowPrefab not assigned (and no Resources fallback).");
            return;
        }
        built = true;
        // Db.items is parent-before-child ordered, so each row's parent already exists when we
        // reach it — same assumption InventoryController.AddItemDisplay relies on.
        foreach (Item item in Db.items) AddRow(item);
    }

    void AddRow(Item item) {
        if (item == null || rows.ContainsKey(item.id)) return;
        var row = Instantiate(rowPrefab, rowContainer);
        row.name = "DetailRow_" + item.name;
        rows[item.id] = row;
        row.Init(item, Depth(item), this);
    }

    static int Depth(Item item) {
        int d = 0;
        for (Item p = item.parent; p != null; p = p.parent) d++;
        return d;
    }

    // ── Refresh ─────────────────────────────────────────────────────────
    // Public so rows can request a full repaint after a state change (e.g. consume toggle).
    public void RefreshAll() {
        if (!built) return;
        bool layoutDirty = false;
        foreach (var kv in rows) {
            InventoryDetailRow row = kv.Value;
            if (row == null) continue;
            bool visible = IsVisible(row.item);
            if (row.gameObject.activeSelf != visible) { row.gameObject.SetActive(visible); layoutDirty = true; }
            if (visible) row.Refresh();
        }
        if (layoutDirty) RebuildLayout();
    }

    // Called by a row when its dropdown toggles — recompute visibility + reflow.
    public void OnRowToggled() {
        foreach (var kv in rows) {
            InventoryDetailRow row = kv.Value;
            if (row == null) continue;
            bool visible = IsVisible(row.item);
            if (row.gameObject.activeSelf != visible) row.gameObject.SetActive(visible);
            if (visible) row.Refresh();
        }
        RebuildLayout();
    }

    // Discovered AND every ancestor row open. Mirrors InventoryController.IsVisibleInTree, but
    // reads each row's own `open` (the panel keeps its own collapse state, independent of the
    // always-visible panel's).
    bool IsVisible(Item item) {
        if (item == null || item.hidden) return false; // internal intermediary — see Item.hidden
        var ic = InventoryController.instance;
        if (ic == null || !ic.discoveredItems.TryGetValue(item.id, out bool disc) || !disc) return false;
        for (Item p = item.parent; p != null; p = p.parent) {
            if (rows.TryGetValue(p.id, out var prow) && prow != null && !prow.open) return false;
        }
        return true;
    }

    void RebuildLayout() {
        if (rowContainer is RectTransform rt) LayoutUtil.RebuildImmediate(rt);
    }
}

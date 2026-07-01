using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Full-screen exclusive panel: one row per mouse (PopulationRow), surfacing how each mouse spends
// its time (the "job load / idle time" metric). Modeled on GlobalInventoryPanel's lifecycle
// (singleton + UI.RegisterExclusive + 0.5s refresh) with a master-detail split — a scrolling row
// list on the left, a per-mouse detail pane on the right (Phase 3).
//
// Rows are pooled: grown on demand, never destroyed, activated/deactivated to match the colony.
// A row is re-Setup (re-bakes the head) only when its bound mouse changes; otherwise it's the cheap
// Refresh path. Rows are sorted by job then name.
public class PopulationPanel : MonoBehaviour {
    // Lazy getter resolves the (inactive) panel so AnimalInfoView's "details" button works on the
    // first click, before the panel has ever activated and run Awake. Mirrors GlobalHappinessPanel.
    static PopulationPanel _instance;
    public static PopulationPanel instance {
        get {
            if (_instance == null) _instance = FindObjectOfType<PopulationPanel>(true);
            return _instance;
        }
    }

    const float RefreshInterval = 0.5f;
    float refreshTimer;

    [SerializeField] Transform rowContainer;   // scroll Content (VerticalLayoutGroup + ContentSizeFitter)
    [SerializeField] PopulationRow rowPrefab;   // Resources/Prefabs/PopulationRow fallback if unwired
    [SerializeField] ScrollRect scrollRect;
    [SerializeField] Button closeButton;
    [SerializeField] Transform detailPane;      // right-side per-mouse detail container
    [SerializeField] MouseDetailView detailView; // renders the selected mouse into detailPane

    readonly List<PopulationRow> rows = new List<PopulationRow>(); // pooled, never destroyed
    Animal selected;
    int visibleCount;

    void Awake() {
        if (_instance != null && _instance != this) Debug.LogError("two PopulationPanels!");
        _instance = this;
        UI.RegisterExclusive(gameObject);
        if (closeButton != null) closeButton.onClick.AddListener(() => gameObject.SetActive(false));
    }

    void OnEnable() { Assign(forceLayout: true); }

    void Update() {
        refreshTimer += Time.deltaTime;
        if (refreshTimer >= RefreshInterval) { refreshTimer = 0f; Assign(forceLayout: false); }
    }

    public void Toggle() {
        if (gameObject.activeSelf) gameObject.SetActive(false);
        else UI.OpenExclusive(gameObject);
    }

    // Phase 4 entry point: open the panel with a mouse pre-selected (from AnimalInfoView).
    public void Open(Animal preselect) {
        selected = preselect;
        if (gameObject.activeSelf) Assign(forceLayout: false);
        else UI.OpenExclusive(gameObject); // OnEnable → Assign
    }

    // Sort the colony, bind it to the pooled rows (re-Setup only rows whose mouse changed), and
    // refresh content. Layout is only rebuilt when the visible row count changes or on open.
    void Assign(bool forceLayout) {
        if (rowPrefab == null) rowPrefab = Resources.Load<PopulationRow>("Prefabs/PopulationRow");
        if (rowContainer == null || rowPrefab == null) {
            Debug.LogError("PopulationPanel: rowContainer/rowPrefab not assigned (and no Resources fallback).");
            return;
        }

        var ac = AnimalController.instance;
        List<Animal> sorted = new List<Animal>();
        // Skip null / destroyed entries — animals[] can hold a transient null during a
        // spawn or death tick, and a.job below would NRE on it.
        if (ac != null && ac.animals != null)
            foreach (var a in ac.animals) if (a != null) sorted.Add(a);
        sorted.Sort((a, b) => {
            int j = string.Compare(a.job?.name, b.job?.name, System.StringComparison.Ordinal);
            return j != 0 ? j : string.Compare(a.aName, b.aName, System.StringComparison.Ordinal);
        });

        for (int i = 0; i < sorted.Count; i++) {
            PopulationRow row = i < rows.Count ? rows[i] : Grow();
            row.gameObject.SetActive(true);
            row.transform.SetSiblingIndex(i);
            if (row.Animal != sorted[i]) row.Setup(sorted[i], Select); // re-bind (re-bakes head)
            else                         row.Refresh();                 // cheap content repaint
        }
        for (int i = sorted.Count; i < rows.Count; i++) rows[i].gameObject.SetActive(false);

        bool countChanged = sorted.Count != visibleCount;
        visibleCount = sorted.Count;

        // Keep selection valid; default to the first mouse so the detail pane isn't empty on open.
        if (selected == null || !sorted.Contains(selected))
            selected = sorted.Count > 0 ? sorted[0] : null;
        RefreshDetail();

        if (forceLayout || countChanged)
            LayoutUtil.RebuildImmediate(rowContainer as RectTransform);
    }

    PopulationRow Grow() {
        var row = Instantiate(rowPrefab, rowContainer);
        rows.Add(row);
        return row;
    }

    void Select(Animal a) { selected = a; RefreshDetail(); }

    // Render `selected`'s detail (stats, activity, equipment, happiness, skills, comfort) into the
    // right pane via MouseDetailView. Clears when nothing is selected (empty colony).
    void RefreshDetail() {
        if (detailView == null) return;
        if (selected != null) detailView.Show(selected);
        else                  detailView.Clear();
    }
}

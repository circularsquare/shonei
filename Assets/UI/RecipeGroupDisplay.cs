using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One collapsible workstation group inside the RecipePanel list. Authored as a prefab
// (Assets/Resources/Prefabs/RecipeGroup.prefab) and instantiated by RecipePanel — tweak
// its header layout / icon size / font in the editor, not here.
//
// Prefab structure:
//   RecipeGroup  (VerticalLayoutGroup + ContentSizeFitter Vertical=Preferred)
//     Header     (Image bg + HorizontalLayoutGroup, ~20px, clickable Button)
//       [Indicator +/-][building Icon][Label "name (N)"]
//     Cards      (VerticalLayoutGroup + ContentSizeFitter; rows instantiate here)
//
// Rows are instantiated lazily the first time the group is expanded, then just toggled
// active/inactive — so a collapsed panel costs ~one header per workstation instead of a
// row per recipe. Expand state is owned by RecipePanel (persisted), keyed by the
// workstation tile string; this component only mirrors it.

public class RecipeGroupDisplay : MonoBehaviour {
    [Header("UI Refs")]
    [SerializeField] Button        headerButton;   // whole-header click target (toggles)
    [SerializeField] TMP_Text      indicator;      // +/- expand glyph
    [SerializeField] Image         iconImage;      // building sprite (StructType, not an item)
    [SerializeField] TMP_Text      label;          // "name (N)"
    [SerializeField] RectTransform cardsContainer; // rows instantiate here

    string                       tile;
    List<Recipe>                 recipes; // craft AND processor recipes for this building
    RecipeListRow                rowPrefab; // instantiated once per list row
    readonly List<RecipeListRow> rows = new List<RecipeListRow>();

    bool expanded;
    bool built; // rows instantiated yet?

    TMP_Text newBadge; // pulsing "new" badge after the workstation name; shown if any child is new

    // tileKey persists across saves; st is the resolved StructType (may be null if a
    // recipe's tile isn't a known building — then we show the name with no icon).
    public void Setup(string tileKey, StructType st, List<Recipe> rs,
                      RecipeListRow rowPrefab, bool startExpanded) {
        tile           = tileKey;
        recipes        = rs;
        this.rowPrefab = rowPrefab;

        // Building icon — hidden when the tile isn't a known building (no StructType sprite).
        Sprite spr = st != null ? st.LoadSprite() : null;
        if (spr != null) iconImage.sprite = spr;
        else             iconImage.gameObject.SetActive(false);

        string name = st != null ? st.DisplayName : tile;
        label.text = name + " (" + recipes.Count + ")";

        // Hug the header label so the "new" badge sits right after the name (the prefab flexes the
        // label to fill the header width otherwise).
        var labelLE = label.GetComponent<LayoutElement>();
        if (labelLE != null) labelLE.flexibleWidth = 0;
        if (newBadge == null) newBadge = PulsingText.CreateNewBadge(label.transform.parent);

        headerButton.onClick.AddListener(() => SetExpanded(!expanded, persist: true));

        expanded = startExpanded;
        // Activate before building so rows spawn active (TMP enabled, ItemIcon Awake
        // runs). The panel's Rebuild path settles layout afterwards via LayoutUtil.
        cardsContainer.gameObject.SetActive(expanded);
        if (expanded) BuildRows();
        indicator.text = expanded ? "-" : "+";
    }

    // ── Build ──────────────────────────────────────────────────────────

    void BuildRows() {
        built = true;
        foreach (Recipe r in recipes)
            AddRow("Row_" + r.id, r);
    }

    void AddRow(string name, Recipe r) {
        var row = Instantiate(rowPrefab, cardsContainer, false);
        row.name = name;
        row.Setup(r);
        rows.Add(row);
    }

    // ── Toggle / refresh ───────────────────────────────────────────────

    void SetExpanded(bool exp, bool persist) {
        expanded = exp;
        // Activate before building so rows spawn active and their TMP/fitters are
        // measurable in the same frame.
        cardsContainer.gameObject.SetActive(exp);
        if (exp && !built) BuildRows();
        indicator.text = exp ? "-" : "+";

        if (persist) RecipePanel.instance?.SetGroupExpanded(tile, exp);

        // Settle the whole list bottom-up in one frame so the group opens at full
        // height immediately instead of popping from min-height (see LayoutUtil).
        LayoutUtil.RebuildImmediate(RecipePanel.instance?.recipeListContent as RectTransform);
    }

    // Called by RecipePanel's refresh timer. Rows show name + On/Off, so expanded rows re-sync the
    // allow icon; and every group updates its "new" badges. A recipe loses "new" only once its group
    // is expanded AND its row is actually scrolled into `viewport` — so off-screen rows stay new.
    // The header badge shows whenever any child recipe is still new (even while collapsed — that's
    // the cue that there's something new inside).
    public void RefreshVisibleCards(RectTransform viewport) {
        if (expanded) {
            foreach (var r in rows) {
                if (r == null) continue;
                r.RefreshAllowIcon();
                if (viewport != null && r.gameObject.activeInHierarchy && Overlaps(viewport, (RectTransform)r.transform))
                    RecipePanel.instance?.MarkRecipeSeen(r.RecipeId);
                r.RefreshNew();
            }
        }
        RefreshHeaderNew();
    }

    void RefreshHeaderNew() {
        if (newBadge == null) return;
        bool anyNew = false;
        var rp = RecipePanel.instance;
        if (rp != null && recipes != null)
            foreach (Recipe r in recipes) if (r != null && rp.IsRecipeNew(r.id)) { anyNew = true; break; }
        if (newBadge.gameObject.activeSelf != anyNew) newBadge.gameObject.SetActive(anyNew);
    }

    // World-space overlap test between the scroll viewport and a row — "has the player actually
    // seen this row". Partial overlap counts as seen.
    static readonly Vector3[] _corners = new Vector3[4];
    static Rect WorldRect(RectTransform rt) {
        rt.GetWorldCorners(_corners);
        return new Rect(_corners[0].x, _corners[0].y, _corners[2].x - _corners[0].x, _corners[2].y - _corners[0].y);
    }
    static bool Overlaps(RectTransform a, RectTransform b) => WorldRect(a).Overlaps(WorldRect(b), true);
}

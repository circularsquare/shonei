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
    List<Recipe>                 recipes;
    List<ProcessorRecipe>        processes;
    RecipeListRow                rowPrefab; // instantiated once per list row
    readonly List<RecipeListRow> rows = new List<RecipeListRow>();

    bool expanded;
    bool built; // rows instantiated yet?

    // tileKey persists across saves; st is the resolved StructType (may be null if a
    // recipe's tile isn't a known building — then we show the name with no icon).
    public void Setup(string tileKey, StructType st, List<Recipe> rs,
                      List<ProcessorRecipe> procs, RecipeListRow rowPrefab, bool startExpanded) {
        tile           = tileKey;
        recipes        = rs;
        processes      = procs;
        this.rowPrefab = rowPrefab;

        // Building icon — hidden when the tile isn't a known building (no StructType sprite).
        Sprite spr = st != null ? st.LoadSprite() : null;
        if (spr != null) iconImage.sprite = spr;
        else             iconImage.gameObject.SetActive(false);

        string name = st != null ? st.DisplayName : tile;
        int    count = recipes.Count + (processes?.Count ?? 0);
        label.text = name + " (" + count + ")";

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
            AddRow("Row_" + r.id, r, null);
        if (processes != null)
            foreach (ProcessorRecipe pr in processes)
                AddRow("ProcRow_" + pr.building + "_" + pr.id, null, pr);
    }

    void AddRow(string name, Recipe r, ProcessorRecipe pr) {
        var row = Instantiate(rowPrefab, cardsContainer, false);
        row.name = name;
        row.Setup(r, pr);
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

    // Called by RecipePanel's refresh timer; cheap no-op while collapsed. Rows only show
    // name + On/Off, so they just re-sync the allow icon (quantities live in the detail pane).
    public void RefreshVisibleCards() {
        if (!expanded) return;
        foreach (var r in rows) r.RefreshAllowIcon();
    }
}

using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One compact row in the Recipes panel's left list. Built in code by RecipeGroupDisplay.
// Layout: [output icon][name][inline On/Off icon], with a highlight tint behind it when
// selected. Clicking the row body selects it (RecipePanel shows the detail on the right);
// clicking the On/Off icon toggles the recipe/process allow state in place.
//
// Each row carries one payload: a craft Recipe (incl. the "write a book" proxy) OR a
// ProcessorRecipe — exactly one is non-null. Allow + selection routing go through
// RecipePanel so the craft/process/book logic lives in one place.

[RequireComponent(typeof(RectTransform))]
public class RecipeListRow : MonoBehaviour {
    const float RowHeight = 16f;
    const float IconSize  = 16f;
    const float OnOffSize = 11f; // native size of check/redx — don't scale (keeps pixel art crisp)
    static readonly Color SelectedTint = new Color(1f, 0.95f, 0.4f, 0.55f); // mirrors TradingPanel

    Recipe          recipe;
    ProcessorRecipe process;
    Image           highlight;
    Image           onoffImg;

    public Recipe          RecipeData  => recipe;
    public ProcessorRecipe ProcessData => process;

    static Sprite iconAllowed, iconDisallowed;
    static void EnsureIcons() {
        if (iconAllowed    == null) iconAllowed    = Resources.Load<Sprite>("Sprites/Misc/check");
        if (iconDisallowed == null) iconDisallowed = Resources.Load<Sprite>("Sprites/Misc/redx");
    }

    // Exactly one of r / p is non-null. fontSource supplies the panel font/size/colour.
    public void Setup(Recipe r, ProcessorRecipe p, RecipeDisplay fontSource) {
        recipe = r; process = p;
        EnsureIcons();

        // Row background: transparent, but the click target for "select this recipe".
        var bg = gameObject.AddComponent<Image>();
        bg.color = new Color(1f, 1f, 1f, 0f);
        bg.raycastTarget = true;

        var hlg = gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing                = 4f;
        hlg.padding                = new RectOffset(4, 4, 0, 0);
        // Bottom-align + don't control/expand height: children keep their native pixel
        // height at an integer Y (mirrors ItemDisplay), so the circle/x icon stays crisp
        // instead of being scaled to the row height. Bottom-align is also m5x7-friendly.
        hlg.childAlignment         = TextAnchor.LowerLeft;
        hlg.childControlWidth      = true;
        hlg.childControlHeight     = false;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = false;

        var le = gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = RowHeight;

        var selBtn = gameObject.AddComponent<Button>();
        selBtn.targetGraphic = bg;
        selBtn.transition = Selectable.Transition.None;
        selBtn.onClick.AddListener(() => RecipePanel.instance?.Select(this));

        // Highlight tint behind the content (ignored by the layout group, stretched full).
        var hlGO = new GameObject("Highlight", typeof(RectTransform), typeof(Image));
        hlGO.transform.SetParent(transform, false);
        highlight = hlGO.GetComponent<Image>();
        highlight.color = SelectedTint;
        highlight.raycastTarget = false;
        var hlRt = hlGO.GetComponent<RectTransform>();
        hlRt.anchorMin = Vector2.zero; hlRt.anchorMax = Vector2.one;
        hlRt.offsetMin = Vector2.zero; hlRt.offsetMax = Vector2.zero;
        hlGO.AddComponent<LayoutElement>().ignoreLayout = true;
        hlGO.transform.SetAsFirstSibling(); // draw behind icon/label
        hlGO.SetActive(false);

        // Output icon (locked square so the pixel art stays crisp; non-raycast so clicks
        // fall through to the row select).
        var iconGO = new GameObject("Icon", typeof(RectTransform));
        iconGO.transform.SetParent(transform, false);
        var iconImg = iconGO.AddComponent<Image>();
        iconImg.preserveAspect = true;
        iconImg.raycastTarget  = false;
        var iconLE = iconGO.AddComponent<LayoutElement>();
        iconLE.minWidth = iconLE.preferredWidth = IconSize;
        iconLE.flexibleWidth = 0f;
        ((RectTransform)iconGO.transform).sizeDelta = new Vector2(IconSize, IconSize); // height honoured (childControlHeight off)
        iconGO.AddComponent<ItemIcon>().SetItem(FirstOutputItem());

        // Name, fills the middle.
        var labelGO = new GameObject("Label", typeof(RectTransform));
        labelGO.transform.SetParent(transform, false);
        labelGO.AddComponent<LayoutElement>().flexibleWidth = 1f;
        ((RectTransform)labelGO.transform).sizeDelta = new Vector2(0f, RowHeight);
        var tmp = labelGO.AddComponent<TextMeshProUGUI>();
        tmp.raycastTarget = false;
        if (fontSource != null && fontSource.descText != null) {
            tmp.font     = fontSource.descText.font;
            tmp.fontSize = fontSource.descText.fontSize;
            tmp.color    = fontSource.descText.color;
        }
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Truncate;
        tmp.alignment = TextAlignmentOptions.BottomLeft; // crisp m5x7
        tmp.text = process != null
            ? (string.IsNullOrEmpty(process.description) ? process.building : process.description)
            : (string.IsNullOrEmpty(recipe.description) ? recipe.tile : recipe.description);

        // Inline On/Off icon (its own click target so it doesn't trigger row-select).
        var onoffGO = new GameObject("OnOff", typeof(RectTransform));
        onoffGO.transform.SetParent(transform, false);
        onoffImg = onoffGO.AddComponent<Image>();
        onoffImg.preserveAspect = true;
        onoffImg.raycastTarget  = true;
        var onoffLE = onoffGO.AddComponent<LayoutElement>();
        onoffLE.minWidth = onoffLE.preferredWidth = OnOffSize;
        onoffLE.flexibleWidth = 0f;
        ((RectTransform)onoffGO.transform).sizeDelta = new Vector2(OnOffSize, OnOffSize); // native 11x11, unscaled
        var onoffBtn = onoffGO.AddComponent<Button>();
        onoffBtn.targetGraphic = onoffImg;
        onoffBtn.transition = Selectable.Transition.None;
        onoffBtn.onClick.AddListener(OnClickToggle);

        RefreshAllowIcon();
    }

    Item FirstOutputItem() {
        ItemQuantity[] outs = process != null ? process.outputs : recipe?.outputs;
        return (outs != null && outs.Length > 0) ? outs[0].item : null;
    }

    void OnClickToggle() {
        RecipePanel.instance?.ToggleEntryAllowed(recipe, process);
        RefreshAllowIcon();
    }

    public void SetSelected(bool selected) {
        if (highlight != null) highlight.gameObject.SetActive(selected);
    }

    public void RefreshAllowIcon() {
        bool allowed = RecipePanel.instance == null || RecipePanel.instance.IsEntryAllowed(recipe, process);
        if (onoffImg != null) onoffImg.sprite = allowed ? iconAllowed : iconDisallowed;
    }
}

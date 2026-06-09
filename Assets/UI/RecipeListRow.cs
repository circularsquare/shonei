using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One compact row in the Recipes panel's left list. Authored as a prefab
// (Assets/Resources/Prefabs/RecipeListRow.prefab) and instantiated by
// RecipeGroupDisplay — tweak its layout / style / font in the editor, not here.
//
// Prefab structure (root carries a transparent Image = the row's click target):
//   RecipeListRow  (Image bg, HorizontalLayoutGroup, LayoutElement, Button=select)
//     Highlight    (Image, stretched full, LayoutElement.ignoreLayout; selection tint)
//     Icon         (Image + ItemIcon; output item)
//     Label        (TMP; recipe / process name)
//     OnOff        (Image + Button; allow check / redx, toggles in place)
//
// Each row carries one payload: a craft Recipe (incl. the "write a book" proxy) OR a
// ProcessorRecipe — exactly one is non-null. Allow + selection routing go through
// RecipePanel so the craft/process/book logic lives in one place.

public class RecipeListRow : MonoBehaviour {
    [Header("UI Refs")]
    [SerializeField] Button   selectButton; // root button — click selects the row
    [SerializeField] Image    highlight;    // selection tint, toggled on/off
    [SerializeField] ItemIcon icon;         // output item icon
    [SerializeField] TMP_Text label;        // recipe / process name
    [SerializeField] Image    onoffImg;     // allow check / redx sprite
    [SerializeField] Button   onoffButton;  // toggles allow in place (own click target)

    Recipe          recipe;
    ProcessorRecipe process;

    public Recipe          RecipeData  => recipe;
    public ProcessorRecipe ProcessData => process;

    static Sprite iconAllowed, iconDisallowed;
    static void EnsureIcons() {
        if (iconAllowed    == null) iconAllowed    = Resources.Load<Sprite>("Sprites/Misc/check");
        if (iconDisallowed == null) iconDisallowed = Resources.Load<Sprite>("Sprites/Misc/redx");
    }

    // Exactly one of r / p is non-null. Binds this prefab instance to its payload.
    public void Setup(Recipe r, ProcessorRecipe p) {
        recipe = r; process = p;
        EnsureIcons();

        selectButton.onClick.AddListener(() => RecipePanel.instance?.Select(this));
        onoffButton.onClick.AddListener(OnClickToggle);

        icon.SetItem(FirstOutputItem());
        label.text = process != null
            ? (string.IsNullOrEmpty(process.description) ? process.building : process.description)
            : (string.IsNullOrEmpty(recipe.description) ? recipe.tile : recipe.description);

        highlight.gameObject.SetActive(false);
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

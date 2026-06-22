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
// Each row carries one Recipe — a craft recipe, the "write a book" proxy, or a processor
// (batch-conversion) recipe; they all route allow + selection through RecipePanel by id.

public class RecipeListRow : MonoBehaviour {
    [Header("UI Refs")]
    [SerializeField] Button   selectButton; // root button — click selects the row
    [SerializeField] Image    highlight;    // selection tint, toggled on/off
    [SerializeField] ItemIcon icon;         // output item icon
    [SerializeField] TMP_Text label;        // recipe name
    [SerializeField] Image    onoffImg;     // allow check / redx sprite
    [SerializeField] Button   onoffButton;  // toggles allow in place (own click target)

    Recipe recipe;

    public Recipe RecipeData => recipe;

    static Sprite iconAllowed, iconDisallowed;
    static void EnsureIcons() {
        if (iconAllowed    == null) iconAllowed    = Resources.Load<Sprite>("Sprites/Misc/check");
        if (iconDisallowed == null) iconDisallowed = Resources.Load<Sprite>("Sprites/Misc/redx");
    }

    // Binds this prefab instance to its recipe.
    public void Setup(Recipe r) {
        recipe = r;
        EnsureIcons();

        selectButton.onClick.AddListener(() => RecipePanel.instance?.Select(this));
        onoffButton.onClick.AddListener(OnClickToggle);

        icon.SetItem(FirstOutputItem());
        label.text = string.IsNullOrEmpty(recipe.description) ? recipe.tile : recipe.description;

        highlight.gameObject.SetActive(false);
        RefreshAllowIcon();
    }

    Item FirstOutputItem() {
        ItemQuantity[] outs = recipe?.outputs;
        return (outs != null && outs.Length > 0) ? outs[0].item : null;
    }

    void OnClickToggle() {
        var rp = RecipePanel.instance;
        if (rp != null) rp.SetAllowed(recipe.id, !rp.IsAllowed(recipe.id));
        RefreshAllowIcon();
    }

    public void SetSelected(bool selected) {
        if (highlight != null) highlight.gameObject.SetActive(selected);
    }

    public void RefreshAllowIcon() {
        bool allowed = RecipePanel.instance == null || RecipePanel.instance.IsAllowed(recipe.id);
        if (onoffImg != null) onoffImg.sprite = allowed ? iconAllowed : iconDisallowed;
    }
}

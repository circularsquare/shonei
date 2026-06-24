using UnityEngine;
using UnityEngine.UI;
using TMPro;

// A configurable mouse row: head icon + name + one action button. Built per-mouse as a runtime
// list by StructureInfoView (like BlueprintCostRow rows). Used in three places — housing occupants
// (button = evict), a work flag's roster (button = unassign), and a work flag's available list
// (button = assign) — so the action, its button sprite, and its tooltip are all passed into Setup.
// Clicking the head selects that mouse in the InfoPanel. Setup() is re-called on rebuild, so the
// listener is cleared before re-adding to avoid stacking.
//
// (The field is still named evictButton from its first use, kept so the prefab's serialized
// reference survives; it's the generic action button now.)
public class OccupantRow : MonoBehaviour {
    [SerializeField] MouseHeadIcon headIcon;
    [SerializeField] TextMeshProUGUI nameLabel;
    [SerializeField] Button evictButton;          // the action button (evict / unassign / assign)
    [SerializeField] Image actionIcon;            // optional: the action button's glyph (x / +)
    [SerializeField] Tooltippable actionTip;      // optional: the action button's hover label

    Animal animal;
    System.Action onAction;

    // buttonSprite/tooltip are applied only when their refs are wired (null-safe); onAction runs
    // on click, then should rebuild the owning list.
    public void Setup(Animal a, Sprite buttonSprite, string tooltip, System.Action onAction) {
        animal = a;
        this.onAction = onAction;
        headIcon?.Set(a);
        if (headIcon != null) headIcon.onClick = SelectInInfoPanel;
        if (nameLabel != null) {
            nameLabel.text = a.aName;
            nameLabel.color = Color.black;
        }
        if (actionIcon != null && buttonSprite != null) actionIcon.sprite = buttonSprite;
        if (actionTip != null) actionTip.title = tooltip;
        if (evictButton != null) {
            evictButton.onClick.RemoveAllListeners();
            evictButton.onClick.AddListener(OnClickAction);
        }
    }

    void OnClickAction() {
        if (animal == null) return;
        onAction?.Invoke();
    }

    // Clicking the head selects this mouse in the InfoPanel — same path a world-click takes
    // (SelectionContext for the mouse's tile + the mouse). Clears any storage selection so a
    // stale highlight doesn't linger, matching MouseController.HandleSelectClick.
    void SelectInInfoPanel(Animal a) {
        if (a == null || InfoPanel.instance == null) return;
        Tile t = World.instance != null ? World.instance.GetTileAt((int)a.x, (int)a.y) : null;
        var ctx = SelectionContext.FromTile(t, new System.Collections.Generic.List<Animal> { a });
        InfoPanel.instance.ShowSelection(ctx);
        InventoryController.instance?.SelectInventory(null);
    }
}

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// ── EquipSlotDisplay ───────────────────────────────────────────────────────
// One RPG-style equipment box in the mouse info panel's gear grid. The box is a
// fixed-size frame (root Image) holding an inner icon Image that shows either the
// equipped item's sprite or, when empty, a slot-specific outline placeholder
// (e.g. a hat silhouette). Hovering shows the item name plus, for worn gear, its
// condition; an empty slot's hover shows just the slot name.
//
// A reserved blank cell (bound with a null inventory) draws nothing — no frame, no
// icon, no tooltip — but its GameObject stays active so the grid keeps the cell and
// the other boxes don't reflow.
//
// Authored once as a prefab (frame + inner icon child); AnimalInfoView instantiates
// one per slot into a GridLayoutGroup, binds each via SetSlot, then repaints each tick
// via Refresh. The widget is deliberately slot-agnostic so any equip grid can reuse it.
[RequireComponent(typeof(Image))]
public class EquipSlotDisplay : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler {
    [SerializeField] Image icon;   // inner image: item icon when filled, outline placeholder when empty

    Image     frame;         // root box frame (this GameObject's Image)
    string    label;         // slot name shown on hover when empty ("hat", "tool", ...)
    Sprite    emptySprite;   // outline placeholder drawn when the slot is empty
    Inventory slotInv;       // backing equip inventory; null = reserved blank cell
    bool      showCondition; // worn gear (tool/clothing/hat) → show a condition %; food/book don't
    bool      hidden;        // reserved blank cell: occupies a grid cell but draws nothing
    bool      hovered;

    void Awake() { frame = GetComponent<Image>(); }

    // Bind this box to a slot. A null slotInv marks a reserved blank cell.
    public void SetSlot(string label, Sprite emptySprite, Inventory slotInv, bool showCondition) {
        if (frame == null) frame = GetComponent<Image>();
        this.label = label;
        this.emptySprite = emptySprite;
        this.slotInv = slotInv;
        this.showCondition = showCondition;
        this.hidden = slotInv == null;
        Refresh();
    }

    // Repaint from the current stack. Cheap; called each panel tick so live wear/equip
    // changes show without a rebind.
    public void Refresh() {
        if (frame != null) frame.enabled = !hidden;
        if (icon == null) return;
        if (hidden) {
            icon.enabled = false;
            if (hovered) TooltipSystem.Hide();
            return;
        }
        ItemStack s = Stack();
        Sprite sprite = (s != null && s.item != null && s.quantity > 0) ? s.item.icon : emptySprite;
        icon.sprite  = sprite;
        icon.enabled = sprite != null;
        if (hovered) ShowTooltip();
    }

    ItemStack Stack() => slotInv != null ? slotInv.itemStacks[0] : null;

    // ── Hover tooltip ───────────────────────────────────────────────────────
    public void OnPointerEnter(PointerEventData e) { hovered = true; ShowTooltip(); }
    public void OnPointerExit(PointerEventData e)  { hovered = false; TooltipSystem.Hide(); }
    void OnDisable() { hovered = false; TooltipSystem.Hide(); }

    void ShowTooltip() {
        if (hidden) return;
        ItemStack s = Stack();
        if (s == null || s.item == null || s.quantity == 0) {
            TooltipSystem.Show(label, "");   // empty slot: just the slot name
            return;
        }
        TooltipSystem.Show(s.item.name, Body(s));
    }

    // Worn gear → "condition NN%". Otherwise food shows its amount; a single discrete item
    // (e.g. a book) shows just its name. Concise, no period.
    string Body(ItemStack s) {
        if (showCondition && (s.item.decayRate > 0f || s.item.equipDecayRate > 0f))
            return "condition " + ConditionPct(s) + "%";
        if (!s.item.discrete)
            return ItemStack.FormatQ(s.quantity, s.item) + " / " + ItemStack.FormatQ(s.stackSize, s.item);
        if (s.quantity > s.item.unitFen)
            return "x" + ItemStack.FormatQ(s.quantity, s.item);
        return "";
    }

    // Wear remaining (100% = fresh, 0% = about to break). A discrete item loses a whole unit
    // (unitFen fen) once decayCounter reaches unitFen*maxDecayCount; a non-discrete item loses
    // 1 fen per maxDecayCount. So the wear denominator is the fen-per-unit times maxDecayCount.
    static int ConditionPct(ItemStack s) {
        long step = (s.item.discrete ? (long)s.item.unitFen : 1L) * ItemStack.maxDecayCount;
        float worn = step > 0 ? (float)((double)s.decayCounter / step) : 0f;
        return Mathf.Clamp(Mathf.RoundToInt(100f * (1f - worn)), 0, 100);
    }
}

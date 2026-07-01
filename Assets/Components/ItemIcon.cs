using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// Reusable item icon widget. Attach to a UI Image GameObject.
// The root Image renders a shared backing plate (a slot-cell sprite) so icons stay
// legible against the tan wood frame; the item's own sprite is drawn on a
// runtime-created child Image stacked on top. Call SetItem() to display an item
// and register it for hover tooltips. The icon falls back to the default if the
// item has no dedicated sprite.
[RequireComponent(typeof(Image))]
public class ItemIcon : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler {
    static Sprite backingSprite;

    Image backing;    // root Image: shared slot-cell backing plate
    Image iconImage;  // child Image: the item sprite, drawn over the backing
    Item  item;

    // Optional click callback. If null, clicks are inert (existing call sites stay quiet).
    public System.Action<Item> onClick;

    void Awake() {
        EnsureRefs();
    }

    // Wires the root backing Image and the child icon Image, creating the child on
    // first use. Idempotent: callers may SetItem before Awake (e.g. building into a
    // still-inactive container), so this runs lazily from both entry points.
    void EnsureRefs() {
        if (backing == null) backing = GetComponent<Image>();
        if (backingSprite == null) backingSprite = Resources.Load<Sprite>("Sprites/Items/itembacking");
        backing.sprite = backingSprite;

        if (iconImage != null) return;
        Transform existing = transform.Find("IconSprite");
        if (existing != null) { iconImage = existing.GetComponent<Image>(); return; }

        var go = new GameObject("IconSprite", typeof(RectTransform), typeof(Image));
        go.layer = gameObject.layer;
        var rt = (RectTransform)go.transform;
        rt.SetParent(transform, false);
        rt.anchorMin = Vector2.zero;   // stretch to fill the backing
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        iconImage = go.GetComponent<Image>();
        iconImage.raycastTarget = false;   // tooltip/click ray hits the root backing
        iconImage.preserveAspect = true;
    }

    public void SetItem(Item newItem) {
        EnsureRefs();
        item = newItem;
        if (item == null) { gameObject.SetActive(false); return; }
        iconImage.sprite  = ResolveIcon(item);
        iconImage.enabled = iconImage.sprite != null;
        gameObject.SetActive(true);
    }

    // Returns the sprite to display for an item. Group items with no dedicated
    // sprite (icon == null) fall back to the icon of the leaf child you have the
    // most of globally; if all quantities are zero the first leaf is used.
    static Sprite ResolveIcon(Item item) {
        if (item.icon != null) return item.icon;

        // Group item with no dedicated sprite — find best leaf by global inventory.
        Item best = null;
        int bestQty = -1;
        FindBestLeaf(item, ref best, ref bestQty);
        return best != null ? best.icon : null;
    }

    static void FindBestLeaf(Item group, ref Item best, ref int bestQty) {
        if (group.children == null) return;
        var ginv = GlobalInventory.instance;
        foreach (Item child in group.children) {
            if (child.children != null && child.children.Length > 0) {
                FindBestLeaf(child, ref best, ref bestQty);
            } else {
                int qty = ginv != null ? ginv.Quantity(child.id) : 0;
                if (qty > bestQty) { bestQty = qty; best = child; }
            }
        }
    }

    public void OnPointerEnter(PointerEventData eventData) {
        if (item != null) TooltipSystem.Show(item.name, item.description ?? "");
    }

    public void OnPointerExit(PointerEventData eventData) {
        TooltipSystem.Hide();
    }

    public void OnPointerClick(PointerEventData eventData) {
        if (onClick != null && item != null) onClick(item);
    }

    void OnDisable() {
        TooltipSystem.Hide();
    }
}

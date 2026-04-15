using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// Reusable item icon widget. Attach to a UI Image GameObject.
// Call SetItem() to display the item's icon and register it for hover tooltips.
// The tooltip shows the item name; the icon falls back to the default if the item
// has no dedicated sprite.
[RequireComponent(typeof(Image))]
public class ItemIcon : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler {
    Image image;
    Item item;

    void Awake() {
        image = GetComponent<Image>();
    }

    public void SetItem(Item newItem) {
        item = newItem;
        if (item == null) { gameObject.SetActive(false); return; }
        image.sprite = ResolveIcon(item);
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
        if (item != null) TooltipSystem.Show(item.name, "");
    }

    public void OnPointerExit(PointerEventData eventData) {
        TooltipSystem.Hide();
    }

    void OnDisable() {
        TooltipSystem.Hide();
    }
}

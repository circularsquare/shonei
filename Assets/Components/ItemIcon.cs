using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

/// <summary>
/// Reusable item icon widget. Attach to a UI Image GameObject.
/// Call SetItem() to display the item's icon and register it for hover tooltips.
/// The tooltip shows the item name; the icon falls back to the default if the item
/// has no dedicated sprite.
/// </summary>
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
        image.sprite = item.icon;
        gameObject.SetActive(true);
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

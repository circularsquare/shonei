using UnityEngine;
using TMPro;

/// <summary>
/// Displays a single item stack slot in the StoragePanel compact view.
/// Shows "item: qty/max" or "empty: 0/max" for empty slots.
/// </summary>
public class StorageSlotDisplay : MonoBehaviour {
    public TextMeshProUGUI text;

    public void UpdateSlot(ItemStack stack, int stackSize) {
        if (stack.item == null || stack.quantity == 0)
            text.text = $"empty: 0/{ItemStack.FormatQ(stackSize)}";
        else {
            string spc = stack.resSpace > 0 ? $" (s{ItemStack.FormatQ(stack.resSpace, stack.item.discrete)})" : "";
            text.text = $"{stack.item.name}: {ItemStack.FormatQ(stack.quantity, stack.item.discrete)}/{ItemStack.FormatQ(stackSize)}{spc}";
        }
    }

    // Aggregated row: total qty of one item type across multiple inventories.
    public void UpdateSlot(Item item, int totalQty, int totalCapacity, int totalResSpace = 0) {
        if (item == null)
            text.text = $"empty: 0/{ItemStack.FormatQ(totalCapacity)}";
        else {
            string spc = totalResSpace > 0 ? $" (s{ItemStack.FormatQ(totalResSpace, item.discrete)})" : "";
            text.text = $"{item.name}: {ItemStack.FormatQ(totalQty, item.discrete)}/{ItemStack.FormatQ(totalCapacity)}{spc}";
        }
    }
}

using UnityEngine;
using TMPro;

// Displays a single item stack slot in the StoragePanel compact view.
// Shows "item: qty/max" or "empty: 0/max" for empty slots.
public class StorageSlotDisplay : MonoBehaviour {
    public TextMeshProUGUI text;

    public void UpdateSlot(ItemStack stack, int stackSize) {
        if (stack.item == null || stack.quantity == 0)
            text.text = $"empty: 0/{ItemStack.FormatQ(stackSize)}";
        else {
            string spc = stack.resSpace > 0 ? $" (s{ItemStack.FormatQ(stack.resSpace, stack.item)})" : "";
            // Capacity shown in the item's own unit: for a discrete item FormatQ(stackSize, item)
            // floors stackSize/unitFen — the whole-unit count the slot can actually hold.
            text.text = $"{stack.item.name}: {ItemStack.FormatQ(stack.quantity, stack.item)}/{ItemStack.FormatQ(stackSize, stack.item)}{spc}";
        }
    }

    // Aggregated row: total qty of one item type across multiple inventories.
    public void UpdateSlot(Item item, int totalQty, int totalCapacity, int totalResSpace = 0) {
        if (item == null)
            text.text = $"empty: 0/{ItemStack.FormatQ(totalCapacity)}";
        else {
            string spc = totalResSpace > 0 ? $" (s{ItemStack.FormatQ(totalResSpace, item)})" : "";
            // Aggregate capacity in unit count. FormatQ floors totalCapacity/unitFen — exact when
            // stacks share a size; may very slightly over-count across many small stacks (display-only).
            text.text = $"{item.name}: {ItemStack.FormatQ(totalQty, item)}/{ItemStack.FormatQ(totalCapacity, item)}{spc}";
        }
    }
}

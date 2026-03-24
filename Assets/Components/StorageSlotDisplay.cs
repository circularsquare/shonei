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
        else
            text.text = $"{stack.item.name}: {ItemStack.FormatQ(stack.quantity, stack.item.discrete)}/{ItemStack.FormatQ(stackSize)}";
    }
}

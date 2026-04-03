using UnityEngine;
using TMPro;

/// <summary>
/// Sub-view for InfoPanel that displays tile-specific information:
/// coordinates, tile type, standability, water level, and floor inventory contents.
/// Does NOT show structures — those get their own tabs via StructureInfoView.
/// </summary>
public class TileInfoView : MonoBehaviour {
    [SerializeField] TextMeshProUGUI text;

    private Tile tile;

    public void Show(Tile tile) {
        this.tile = tile;
        gameObject.SetActive(true);
        Refresh();
    }

    public void Hide() {
        gameObject.SetActive(false);
        tile = null;
    }

    public void Refresh() {
        if (tile == null) return;

        var sb = new System.Text.StringBuilder();
        sb.Append("location: " + tile.x + ", " + tile.y);
        sb.Append("\nstandable: " + tile.node.standable + "  neighbors: " + tile.node.neighbors.Count);

        if (tile.type.id != 0) {
            sb.Append("\ntile: " + tile.type.name + "  solid: " + tile.type.solid);
        }

        if (tile.water > 0)
            sb.Append($"\nwater: {tile.water}/{WaterController.WaterMax}");

        // Floor inventory
        if (tile.inv != null) {
            sb.Append("\ninv:");
            foreach (var stack in tile.inv.itemStacks) {
                if (stack.item != null) {
                    string resStr = stack.resAmount > 0 ? " (r" + ItemStack.FormatQ(stack.resAmount, stack.item.discrete) + ")" : "";
                    string spcStr = stack.resSpace > 0 ? " (s" + ItemStack.FormatQ(stack.resSpace, stack.item.discrete) + ")" : "";
                    var stackOrder = WorkOrderManager.instance?.FindOrderForStack(stack);
                    string woStr = stackOrder == null ? "" :
                        stackOrder.type == WorkOrderManager.OrderType.Haul && stackOrder.priority == 1
                            ? $" [wo:Haul! {stackOrder.res.reserved}/{stackOrder.res.capacity}]"
                            : $" [wo:{stackOrder.type} {stackOrder.res.reserved}/{stackOrder.res.capacity}]";
                    sb.Append("\n  " + stack.item.name + " x " + ItemStack.FormatQ(stack.quantity, stack.item.discrete) + resStr + spcStr + woStr);
                }
            }
        }

        text.text = sb.ToString();
    }
}

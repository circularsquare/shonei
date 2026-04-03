using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Attach to the OrderDisplay prefab.
// Inspector refs:
//   label        — TextMeshProUGUI  (shows "from  xQty  @ price")
//   cancelButton — GameObject       (shown only for the player's own orders)
public class OrderDisplay : MonoBehaviour {
    public TextMeshProUGUI label;
    public GameObject      cancelButton;

    long _orderId;

    public void Init(MarketOrder order) {
        _orderId = order.id;
        if (label != null) {
            string nameColor;
            if (order.from == TradingClient.playerName) nameColor = "#bb55dd"; // purple — own order
            else if (order.client_type == "bot")        nameColor = "#55aa55"; // green — bot
            else                                        nameColor = "#5588dd"; // blue — other player
            label.text = $"<color={nameColor}>{order.from}</color>  x{ItemStack.FormatQ(order.quantity)}  @ {order.price / 100f:0.##}";
        }

        bool isOwn = order.from == TradingClient.playerName;
        if (cancelButton != null) cancelButton.SetActive(isOwn);
    }

    // Wire the cancel button's onClick to this.
    public void OnClickCancel() {
        TradingClient.instance?.SendCancel(_orderId);
    }
}

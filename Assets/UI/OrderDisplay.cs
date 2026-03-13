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
        if (label != null)
            label.text = $"{order.from}  x{order.quantity}  @ {order.price}";

        bool isOwn = order.from == TradingClient.playerName;
        if (cancelButton != null) cancelButton.SetActive(isOwn);
    }

    // Wire the cancel button's onClick to this.
    public void OnClickCancel() {
        TradingClient.instance?.SendCancel(_orderId);
    }
}

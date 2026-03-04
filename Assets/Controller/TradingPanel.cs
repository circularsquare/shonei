using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Trading panel — market query, order entry, chat.
//
// Unity setup required:  (see bottom of file for full layout)
//   Assign in Inspector:
//     itemInput        — TMP_InputField  (item name, shared by query + order)
//     orderPrice       — TMP_InputField  (price)
//     orderQty         — TMP_InputField  (quantity)
//     resultList       — Transform       (scroll content for order book)
//     chatList         — Transform       (scroll content for chat + fills)
//     chatInput        — TMP_InputField  (chat message)
//     onlineIndicator  — GameObject      (Image + TMP child)
//
//   Button onClick wiring:
//     TradingToggle button  -> TradingPanel.instance.Toggle()
//     QueryButton           -> TradingPanel.instance.OnClickQuery()
//     BuyButton             -> TradingPanel.instance.SetBuy(true)   [pass bool via UnityEvent]
//     SellButton            -> TradingPanel.instance.SetBuy(false)
//     PlaceOrderButton      -> TradingPanel.instance.OnClickPlaceOrder()
//     ChatSendButton        -> TradingPanel.instance.OnClickSendChat()

public class TradingPanel : MonoBehaviour {
    public static TradingPanel instance;

    [Header("Market Query")]
    public TMP_InputField itemInput;
    public Transform      buysList;
    public Transform      sellsList;

    [Header("Order Entry")]
    public TMP_InputField orderPrice;
    public TMP_InputField orderQty;

    [Header("Chat")]
    public ScrollRect     chatScroll;
    public Transform      chatList;
    public TMP_InputField chatInput;

    [Header("Status")]
    public GameObject onlineIndicator;

    bool _orderIsBuy = true;

    Image            indicatorImage;
    TextMeshProUGUI  indicatorText;
    Sprite           spriteGreen;
    Sprite           spriteRed;

    void Start() {
        if (instance != null) { Debug.LogError("there should only be one TradingPanel"); }
        instance = this;

        spriteGreen = Resources.Load<Sprite>("Sprites/Misc/indicator/green");
        spriteRed   = Resources.Load<Sprite>("Sprites/Misc/indicator/red");

        if (onlineIndicator != null) {
            indicatorImage = onlineIndicator.GetComponentInChildren<Image>();
            indicatorText  = onlineIndicator.GetComponentInChildren<TextMeshProUGUI>();
        }

        if (itemInput != null) itemInput.onSubmit.AddListener(_ => OnClickQuery());
        if (chatInput != null) chatInput.onSubmit.AddListener(_ => OnClickSendChat());

        var client = TradingClient.instance;
        if (client != null) {
            SetIndicator(client.isOnline);
            client.OnConnectionChanged += SetIndicator;
            client.OnMarketResponse    += DisplayMarketBook;
            client.OnFill              += DisplayFill;
            client.OnChat              += DisplayChat;
        } else {
            SetIndicator(false);
        }
    }

    void OnDestroy() {
        var client = TradingClient.instance;
        if (client != null) {
            client.OnConnectionChanged -= SetIndicator;
            client.OnMarketResponse    -= DisplayMarketBook;
            client.OnFill              -= DisplayFill;
            client.OnChat              -= DisplayChat;
        }
    }

    // set connected indicator
    void SetIndicator(bool online) {
        if (indicatorImage) indicatorImage.sprite = online ? spriteGreen : spriteRed;
        if (indicatorText)  indicatorText.text    = online ? "online" : "offline";
    }

    // toggle panel active
    public void Toggle() {
        gameObject.SetActive(!gameObject.activeSelf);
    }

    // -------------------------------------------------------------------------
    // Market query
    // -------------------------------------------------------------------------

    public void OnClickQuery() {
        string item = ItemName();
        if (item.Length == 0) return;
        TradingClient.instance?.QueryMarket(item);
    }

    void DisplayMarketBook(MarketBook book) {
        if (buysList != null) {
            foreach (Transform child in buysList) Destroy(child.gameObject);
            if (book.buys != null)
                foreach (var o in book.buys)
                    AddRow($"{o.from}  x{o.quantity}  @ {o.price}", buysList);
        }
        if (sellsList != null) {
            foreach (Transform child in sellsList) Destroy(child.gameObject);
            if (book.sells != null)
                foreach (var o in book.sells)
                    AddRow($"{o.from}  x{o.quantity}  @ {o.price}", sellsList);
        }
    }

    // -------------------------------------------------------------------------
    // Order entry
    // -------------------------------------------------------------------------

    public void SetBuy(bool isBuy) {
        _orderIsBuy = isBuy;
    }

    public void OnClickPlaceOrder() {
        string item = ItemName();
        if (item.Length == 0) return;
        if (!int.TryParse(orderPrice?.text, out int price) || price <= 0) return;
        if (!int.TryParse(orderQty?.text,   out int qty)   || qty   <= 0) return;
        string side = _orderIsBuy ? "b" : "s";
        TradingClient.instance?.SendOrder(item, side, price, qty);
    }

    // -------------------------------------------------------------------------
    // Chat
    // -------------------------------------------------------------------------

    public void OnClickSendChat() {
        if (chatInput == null) return;
        string text = chatInput.text.Trim();
        if (text.Length == 0) return;
        TradingClient.instance?.SendChat(text);
        chatInput.text = "";
        chatInput.ActivateInputField();
    }

    void DisplayChat(ChatMsg msg) {
        AddChat($"{msg.from}: {msg.text}");
    }

    void DisplayFill(Fill fill) {
        AddChat($"<color=#aaffaa>[fill] {fill.buyer} bought {fill.quantity} {fill.item} from {fill.seller} @ {fill.price}</color>");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    string ItemName() {
        return itemInput != null ? itemInput.text.Trim() : "";
    }

    void AddChat(string text) {
        if (chatList == null) return;
        var go  = new GameObject("ChatRow", typeof(RectTransform));
        go.transform.SetParent(chatList, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text               = text;
        tmp.fontSize           = 16;
        tmp.enableWordWrapping = true;
        // ContentSizeFitter lets TMP compute its own height after VLG sets width.
        // Requires chatList VLG: Control Child Size Width ON, Height OFF.
        var csf = go.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        if (chatList.childCount > 20)
            Destroy(chatList.GetChild(0).gameObject);
        LayoutRebuilder.ForceRebuildLayoutImmediate(chatList as RectTransform);
        if (chatScroll) chatScroll.verticalNormalizedPosition = 0f;
    }

    void AddRow(string text, Transform parent) {
        var go  = new GameObject("Row", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text     = text;
        tmp.fontSize = 16;
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 14;
        le.minHeight       = 14;
    }
}

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
    public GameObject     orderDisplayPrefab;

    [Header("Order Entry")]
    public Button         buyButton;
    public Button         sellButton;
    public TMP_InputField orderPrice;
    public TMP_InputField orderQty;
    public TextMeshProUGUI orderAlert; // assign in inspector; shows validation errors

    [Header("Chat")]
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

        SetBuy(_orderIsBuy);

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
        gameObject.SetActive(false);
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
                foreach (var o in book.buys) SpawnOrder(o, buysList);
        }
        if (sellsList != null) {
            foreach (Transform child in sellsList) Destroy(child.gameObject);
            if (book.sells != null)
                foreach (var o in book.sells) SpawnOrder(o, sellsList);
        }
    }

    void SpawnOrder(MarketOrder order, Transform parent) {
        if (orderDisplayPrefab == null) { AddRow($"{order.from}  x{order.quantity}  @ {order.price}", parent); return; }
        var go = Instantiate(orderDisplayPrefab, parent, false);
        go.GetComponent<OrderDisplay>()?.Init(order);
    }

    // -------------------------------------------------------------------------
    // Order entry
    // -------------------------------------------------------------------------

    static readonly Color ColorBuyActive   = new Color(0.20f, 0.55f, 1.00f); // blue
    static readonly Color ColorSellActive  = new Color(1.00f, 0.35f, 0.35f); // red
    static readonly Color ColorInactive    = new Color(0.80f, 0.80f, 0.80f); // grey

    public void SetBuy(bool isBuy) {
        _orderIsBuy = isBuy;
        if (buyButton  != null) buyButton.image.color  = isBuy  ? ColorBuyActive  : ColorInactive;
        if (sellButton != null) sellButton.image.color = !isBuy ? ColorSellActive : ColorInactive;
    }

    public void OnClickPlaceOrder() {
        string itemName = ItemName();
        if (itemName.Length == 0) { ShowAlert("Enter an item name."); return; }
        if (!int.TryParse(orderPrice?.text, out int price) || price <= 0) { ShowAlert("Enter a valid price."); return; }
        if (!int.TryParse(orderQty?.text,   out int qty)   || qty   <= 0) { ShowAlert("Enter a valid quantity."); return; }

        if (!Db.itemByName.ContainsKey(itemName)) { ShowAlert($"Unknown item: {itemName}"); return; }
        Item item   = Db.itemByName[itemName];
        Item silver = Db.itemByName["silver"];
        Inventory market = TradingClient.FindMarketInventory();
        if (market == null) { ShowAlert("No market building found."); return; }

        if (_orderIsBuy) {
            int silverNeeded = qty * price;
            int silverHave   = market.AvailableQuantity(silver);
            if (silverHave < silverNeeded) { ShowAlert($"Need {silverNeeded} silver in market (have {silverHave})."); return; }
            int spaceForItem = market.GetMarketSpace(item);
            if (spaceForItem < qty) { ShowAlert($"Need {qty} space for {itemName} in market (have {spaceForItem})."); return; }
        } else {
            int itemHave = market.AvailableQuantity(item);
            if (itemHave < qty) { ShowAlert($"Need {qty} {itemName} in market (have {itemHave})."); return; }
            int silverSpace = market.GetMarketSpace(silver);
            int silverIncoming = qty * price;
            if (silverSpace < silverIncoming) { ShowAlert($"Need {silverIncoming} space for silver in market (have {silverSpace})."); return; }
        }

        ShowAlert("");
        string side = _orderIsBuy ? "b" : "s";
        TradingClient.instance?.SendOrder(itemName, side, price, qty);
    }

    void ShowAlert(string msg) {
        if (orderAlert != null) orderAlert.text = msg;
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
        var csf = go.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        if (chatList.childCount > 20)
            Destroy(chatList.GetChild(0).gameObject);
        LayoutRebuilder.ForceRebuildLayoutImmediate(chatList as RectTransform);
    }

    void AddCancelRow(string text, Transform parent, long orderId) {
        var row = new GameObject("Row", typeof(RectTransform));
        row.transform.SetParent(parent, false);
        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 4;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = false;
        var le = row.AddComponent<LayoutElement>();
        le.preferredHeight = 14;
        le.minHeight       = 14;

        // text label
        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.transform.SetParent(row.transform, false);
        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.text     = text;
        tmp.fontSize = 16;
        var labelLe = labelGo.AddComponent<LayoutElement>();
        labelLe.flexibleWidth = 1;

        // cancel button
        var btnGo = new GameObject("CancelBtn", typeof(RectTransform));
        btnGo.transform.SetParent(row.transform, false);
        var img = btnGo.AddComponent<Image>();
        img.color = new Color(1f, 0.35f, 0.35f);
        var btn = btnGo.AddComponent<Button>();
        long captured = orderId;
        btn.onClick.AddListener(() => TradingClient.instance?.SendCancel(captured));
        var btnLe = btnGo.AddComponent<LayoutElement>();
        btnLe.preferredWidth = 14;
        btnLe.minWidth       = 14;

        var xGo = new GameObject("X", typeof(RectTransform));
        xGo.transform.SetParent(btnGo.transform, false);
        var xTmp = xGo.AddComponent<TextMeshProUGUI>();
        xTmp.text      = "×";
        xTmp.fontSize  = 12;
        xTmp.alignment = TextAlignmentOptions.Center;
        var xRect = xGo.GetComponent<RectTransform>();
        xRect.anchorMin = Vector2.zero;
        xRect.anchorMax = Vector2.one;
        xRect.offsetMin = Vector2.zero;
        xRect.offsetMax = Vector2.zero;
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

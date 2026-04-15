using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Trading panel — market query, order entry, chat.
//
// Button onClick wiring:
//   TradingToggle  -> Toggle()
//   QueryButton    -> OnClickQuery()
//   BuyButton      -> SetBuy(true)
//   SellButton     -> SetBuy(false)
//   PlaceOrder     -> OnClickPlaceOrder()
//   ChatSend       -> OnClickSendChat()

public class TradingPanel : MonoBehaviour {
    public static TradingPanel instance { get; protected set; }

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
    public TextMeshProUGUI orderAlert;

    [Header("Market Inventory")]
    public Transform      marketInvContent;
    public GameObject     itemDisplayPrefab;
    private Dictionary<int, GameObject> marketDisplayGos = new Dictionary<int, GameObject>();
    private Inventory     currentMarket;

    [Header("Chat")]
    public Transform      chatList;
    public TMP_InputField chatInput;

    [Header("Merchant Journey")]
    public MerchantJourneyDisplay merchantJourney;

    bool _orderIsBuy   = true;
    bool _orderSideSet = false; // neither buy nor sell selected by default

    void Start() {
        if (instance != null) { Debug.LogError("there should only be one TradingPanel"); }
        instance = this;
        UI.RegisterExclusive(gameObject);

        // Start with neither side selected — both buttons grey
        if (buyButton  != null) buyButton.image.color  = ColorInactive;
        if (sellButton != null) sellButton.image.color = ColorInactive;

        if (itemInput != null) itemInput.onSubmit.AddListener(_ => OnClickQuery());
        if (chatInput != null) chatInput.onSubmit.AddListener(_ => OnClickSendChat());
        if (orderQty != null) orderQty.contentType = TMP_InputField.ContentType.IntegerNumber;

        var client = TradingClient.instance;
        if (client != null) {
            client.OnMarketResponse += DisplayMarketBook;
            client.OnFill           += DisplayFill;
            client.OnChat           += DisplayChat;
        }
        gameObject.SetActive(false);
    }

    void Update() {
        // Tab while typing qty → jump to price field.
        // (Input polling is inherently per-frame — no event form available.)
        if (orderQty != null && orderPrice != null
                && orderQty.isFocused && Input.GetKeyDown(KeyCode.Tab)) {
            orderPrice.ActivateInputField();
            orderPrice.MoveTextEnd(false);
        }
    }

    void OnDestroy() {
        ClearMarketTree();
        var client = TradingClient.instance;
        if (client != null) {
            client.OnMarketResponse -= DisplayMarketBook;
            client.OnFill           -= DisplayFill;
            client.OnChat           -= DisplayChat;
        }
    }

    // toggle panel active — attempts reconnection in background if offline,
    // but always opens so targets/inventory can be viewed while disconnected
    public void Toggle() {
        var client = TradingClient.instance;
        if (client != null && !client.isOnline) client.Connect();
        if (gameObject.activeSelf) gameObject.SetActive(false);
        else {
            UI.OpenExclusive(gameObject);
            PopulateMarketTree();
        }
    }

    // ── Market inventory ItemDisplay tree ──────────────────────────

    // Builds the full collapsible ItemDisplay tree for the market inventory.
    // Follows the same pattern as StoragePanel.PopulateAllowTree.
    void PopulateMarketTree() {
        ClearMarketTree();
        if (marketInvContent == null || itemDisplayPrefab == null) return;

        currentMarket = TradingClient.FindMarketInventory();
        if (currentMarket == null) return;

        RectTransform panelRoot = marketInvContent.GetComponent<RectTransform>();

        foreach (Item item in Db.items) {
            if (item == null) continue;
            if (!currentMarket.ItemTypeCompatible(item)) continue;

            Transform parent = item.parent == null
                ? marketInvContent
                : (marketDisplayGos.ContainsKey(item.parent.id)
                    ? marketDisplayGos[item.parent.id].transform
                    : marketInvContent);

            GameObject go = Instantiate(itemDisplayPrefab, parent);
            go.name = "ItemDisplay_" + item.name;
            marketDisplayGos[item.id] = go;

            bool discovered = InventoryController.instance.discoveredItems.ContainsKey(item.id)
                && InventoryController.instance.discoveredItems[item.id];
            // Market panel: groups are always expanded, so visibility depends only on discovery.
            go.SetActive(discovered);

            ItemDisplay display = go.GetComponent<ItemDisplay>();
            display.item = item;
            display.displayMode = ItemDisplay.DisplayMode.Market;
            display.panelRoot = panelRoot;
            display.targetInventory = currentMarket;
            display.getDisplayGo = id => marketDisplayGos.ContainsKey(id) ? marketDisplayGos[id] : null;
            display.SetDisplayMode(ItemDisplay.DisplayMode.Market);
            display.open = true;

            // Set initial text
            UpdateMarketItemDisplay(display, item);
        }

        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(panelRoot);
    }

    void ClearMarketTree() {
        foreach (var kvp in marketDisplayGos) {
            kvp.Value.SetActive(false);
            Destroy(kvp.Value);
        }
        marketDisplayGos.Clear();
        currentMarket = null;
    }

    // Refreshes quantities and targets on existing ItemDisplay rows.
    // Called from InventoryController.TickUpdate (every 0.2s) while the panel is open —
    // same cadence as StoragePanel.UpdateDisplay. Do NOT call from Update().
    public void UpdateMarketTree() {
        if (currentMarket == null) return;
        foreach (var kvp in marketDisplayGos) {
            ItemDisplay display = kvp.Value.GetComponent<ItemDisplay>();
            if (display == null || display.item == null) continue;
            UpdateMarketItemDisplay(display, display.item);
        }
    }

    void UpdateMarketItemDisplay(ItemDisplay display, Item item) {
        int qty = currentMarket.Quantity(item);
        if (display.itemText != null) display.itemText.text = item.name;
        if (display.quantityText != null)
            display.quantityText.text = ItemStack.FormatQ(qty, item.discrete);
        // Groups don't get meaningful targets in market mode — only leaf items do.
        if (item.IsGroup) return;
        int target = currentMarket.targets != null && currentMarket.targets.ContainsKey(item)
            ? currentMarket.targets[item] : 0;
        display.SetTargetDisplay(target);
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
        if (orderDisplayPrefab == null) { AddRow($"{order.from}  x{ItemStack.FormatQ(order.quantity)}  @ {order.price / 100f:0.##}", parent); return; }
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
        _orderIsBuy   = isBuy;
        _orderSideSet = true;
        if (buyButton  != null) buyButton.image.color  = isBuy  ? ColorBuyActive  : ColorInactive;
        if (sellButton != null) sellButton.image.color = !isBuy ? ColorSellActive : ColorInactive;
    }

    public void OnClickPlaceOrder() {
        if (!_orderSideSet) { ShowAlert("Select Buy or Sell."); return; }
        string itemName = ItemName();
        if (itemName.Length == 0) { ShowAlert("Enter an item name."); return; }

        // Price: entered in liang (e.g. 0.3), converted to fen for wire
        if (!float.TryParse(orderPrice?.text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float priceLiang) || priceLiang <= 0f) {
            ShowAlert("Enter a valid price (e.g. 0.3)."); return;
        }
        int priceFen = Mathf.RoundToInt(priceLiang * 100f);

        // Qty: whole liang entered by user; converted to fen for wire/inventory
        if (!int.TryParse(orderQty?.text, out int qtyLiang) || qtyLiang <= 0) {
            ShowAlert("Enter a whole number quantity (min 1)."); return;
        }
        int qtyFen = qtyLiang * 100;

        if (!Db.itemByName.ContainsKey(itemName)) { ShowAlert($"Unknown item: {itemName}"); return; }
        Item item   = Db.itemByName[itemName];
        Item silver = Db.itemByName["silver"];
        Inventory market = TradingClient.FindMarketInventory();
        if (market == null) { ShowAlert("No market building found."); return; }

        if (_orderIsBuy) {
            int silverNeeded = qtyFen * priceFen / 100;
            int silverHave   = market.AvailableQuantity(silver);
            if (silverHave < silverNeeded) { ShowAlert($"Need {ItemStack.FormatQ(silverNeeded)} silver in market (have {ItemStack.FormatQ(silverHave)})."); return; }
            int spaceForItem = market.GetMarketSpace(item);
            if (spaceForItem < qtyFen) { ShowAlert($"Need {ItemStack.FormatQ(qtyFen)} space for {itemName} in market (have {ItemStack.FormatQ(spaceForItem)})."); return; }
        } else {
            int itemHave = market.AvailableQuantity(item);
            if (itemHave < qtyFen) { ShowAlert($"Need {ItemStack.FormatQ(qtyFen)} {itemName} in market (have {ItemStack.FormatQ(itemHave)})."); return; }
            int silverSpace = market.GetMarketSpace(silver);
            int silverIncoming = qtyFen * priceFen / 100;
            if (silverSpace < silverIncoming) { ShowAlert($"Need {ItemStack.FormatQ(silverIncoming)} space for silver in market (have {ItemStack.FormatQ(silverSpace)})."); return; }
        }

        ShowAlert("");
        string side = _orderIsBuy ? "b" : "s";
        TradingClient.instance?.SendOrder(itemName, side, priceFen, qtyFen);
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

        // Console commands: messages starting with "/" are parsed locally
        if (text.StartsWith("/")) {
            chatInput.text = "";
            chatInput.ActivateInputField();
            HandleCommand(text);
            return;
        }

        var client = TradingClient.instance;
        if (client == null || !client.isOnline) {
            AddChat("<color=#cc3333>not connected to server 3:</color>");
            return;
        }
        client.SendChat(text);
        chatInput.text = "";
        chatInput.ActivateInputField();
    }

    // ── Console commands ─────────────────────────────────────────────────────

    void HandleCommand(string input) {
        // Split on whitespace: ["/give", "oak", "5"] etc.
        string[] parts = input.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLower();

        switch (cmd) {
            case "/give": CmdGive(parts); break;
            default:
                AddChat($"<color=#cc3333>Unknown command: {cmd}</color>");
                break;
        }
    }

    // /give [itemname] [quantity in liang]
    // Produces the item directly into the market inventory.
    void CmdGive(string[] parts) {
        if (parts.Length < 3) {
            AddChat("<color=#cc3333>Usage: /give [item] [quantity]</color>");
            return;
        }

        // Item name may contain spaces — everything between first and last arg is the name.
        // Last arg is always the quantity.
        string qtyStr = parts[parts.Length - 1];
        string itemName = string.Join(" ", parts, 1, parts.Length - 2);

        if (!float.TryParse(qtyStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float qtyLiang) || qtyLiang <= 0f) {
            AddChat("<color=#cc3333>Quantity must be a positive number (in liang).</color>");
            return;
        }
        int qtyFen = Mathf.RoundToInt(qtyLiang * 100f);

        if (!Db.itemByName.ContainsKey(itemName)) {
            AddChat($"<color=#cc3333>Unknown item: {itemName}</color>");
            return;
        }
        Item item = Db.itemByName[itemName];

        Inventory market = TradingClient.FindMarketInventory();
        if (market == null) {
            AddChat("<color=#cc3333>No market building found.</color>");
            return;
        }

        int leftover = market.Produce(item, qtyFen);
        int produced = qtyFen - leftover;
        bool discrete = item.discrete;
        if (produced > 0)
            AddChat($"<color=#aaffaa>Gave {ItemStack.FormatQ(produced, discrete)} {itemName} to market.</color>");
        if (leftover > 0)
            AddChat($"<color=#cc3333>Could not fit {ItemStack.FormatQ(leftover, discrete)} {itemName} — market full.</color>");
    }

    void DisplayChat(ChatMsg msg) {
        AddChat($"{msg.from}: {msg.text}");
    }

    void DisplayFill(Fill fill) {
        bool discrete = Db.itemByName.TryGetValue(fill.item, out Item item) && item.discrete;
        AddChat($"<color=#55aa55>[fill] {fill.buyer} bought {ItemStack.FormatQ(fill.quantity, discrete)} {fill.item} from {fill.seller} @ {fill.price / 100f:0.##}</color>");
        UpdateMarketTree();
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
        tmp.color              = Color.white;
        tmp.richText           = true;
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

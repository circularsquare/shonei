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
//
// Item icon grid (for click-to-query):
//   itemIconGrid    — Transform of a GridLayoutGroup container (scene-wired).
//                     Recommended: ScrollView Content with GridLayoutGroup
//                     (e.g. Cell Size 20×20, Spacing 4×4, Constraint=FixedColumnCount)
//                     and a ContentSizeFitter (Vertical Fit = Preferred Size).
//   itemIconPrefab  — Resources/Prefabs/ItemIcon.prefab.
//   Populated once in Start() with all leaf items in Db.itemsFlat.
//   Click handler fills itemInput and triggers OnClickQuery().

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

    [Header("Item Icon Grid")]
    public Transform      itemIconGrid;     // GridLayoutGroup container
    public GameObject     itemIconPrefab;   // ItemIcon prefab
    private Dictionary<int, GameObject> iconHighlights = new Dictionary<int, GameObject>(); // per-item selection backdrop

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

        if (itemInput != null) {
            itemInput.onSubmit.AddListener(_ => OnClickQuery());
            itemInput.onValueChanged.AddListener(_ => RefreshIconSelection());
        }
        if (chatInput != null) chatInput.onSubmit.AddListener(_ => OnClickSendChat());
        if (orderQty != null) orderQty.contentType = TMP_InputField.ContentType.IntegerNumber;

        var client = TradingClient.instance;
        if (client != null) {
            client.OnMarketResponse += DisplayMarketBook;
            client.OnFill           += DisplayFill;
            client.OnChat           += DisplayChat;
        }
        if (EventFeed.instance != null) EventFeed.instance.OnEntry += HandleFeedEntry;

        PopulateItemIconGrid();

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
        if (EventFeed.instance != null) EventFeed.instance.OnEntry -= HandleFeedEntry;
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

    // Focuses the chat input and seeds it with "/". Entry point for the
    // global "/" shortcut wired in UI.cs. The chatInput field is in the
    // always-active ChatPanel (UI/ChatPanel/ChatBar/ChatInput), wired into
    // TradingPanel via the inspector — so it can be focused without opening
    // the trading panel. The seed is set next frame via a coroutine so we
    // overwrite whatever Unity's input system did with the "/" keystroke
    // this frame (the field may or may not consume Input.inputString
    // depending on when it became selected). The one-frame normalisation
    // guarantees exactly one "/" with the caret past it.
    public void OpenChatInput() {
        if (chatInput == null) return;
        chatInput.ActivateInputField();
        // Coroutine runs on chatInput's GameObject (always-active ChatPanel),
        // not `this` — TradingPanel itself is inactive when closed, and
        // StartCoroutine on an inactive MonoBehaviour silently no-ops.
        chatInput.StartCoroutine(SeedChatSlashNextFrame());
    }

    System.Collections.IEnumerator SeedChatSlashNextFrame() {
        yield return null;
        if (chatInput == null) yield break;
        chatInput.text = "/";
        chatInput.caretPosition = 1;
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
        var discoveredItems = InventoryController.instance?.discoveredItems;
        foreach (var kvp in marketDisplayGos) {
            ItemDisplay display = kvp.Value.GetComponent<ItemDisplay>();
            if (display == null || display.item == null) continue;
            // Re-apply visibility = discovered ∧ no-ancestor-collapsed — items
            // discovered since the tree was built (e.g. via /give) stay hidden
            // otherwise until panel reopen, and we mustn't fight a user's
            // dropdown collapse by re-activating their hidden children.
            bool discovered = discoveredItems != null
                && discoveredItems.TryGetValue(kvp.Key, out bool d) && d;
            bool shouldBeActive = discovered && IsVisibleInMarketTree(display.item);
            if (kvp.Value.activeSelf != shouldBeActive) {
                kvp.Value.SetActive(shouldBeActive);
            }
            UpdateMarketItemDisplay(display, display.item);
        }
    }

    // True if every ancestor of `item` in the market tree is `open`.
    // Mirrors InventoryController.IsVisibleInTree but reads marketDisplayGos.
    bool IsVisibleInMarketTree(Item item) {
        if (item.parent == null) return true;
        if (!marketDisplayGos.TryGetValue(item.parent.id, out GameObject parentGo) || parentGo == null) return true;
        ItemDisplay parentDisplay = parentGo.GetComponent<ItemDisplay>();
        if (parentDisplay == null) return true;
        return parentDisplay.open && IsVisibleInMarketTree(item.parent);
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

    // ── Item icon grid ─────────────────────────────────────────────

    // Selection-highlight tint applied behind the icon when itemInput resolves to it.
    static readonly Color IconSelectedTint = new Color(1f, 0.95f, 0.4f, 0.55f);

    // Builds the click-to-query grid: one ItemIcon per leaf item in Db.itemsFlat,
    // each wrapped in a slot with a hidden Highlight Image drawn behind it.
    // Group items are skipped — they aren't tradable (see SPEC-trading.md
    // "Market targets are leaf-only"). Built once at startup; item set is static.
    void PopulateItemIconGrid() {
        if (itemIconGrid == null || itemIconPrefab == null) return;
        foreach (Transform child in itemIconGrid) Destroy(child.gameObject);
        iconHighlights.Clear();

        foreach (Item item in Db.itemsFlat) {
            if (item == null || item.IsGroup) continue;
            // Skip "none" (placeholder, not a real item) and "silver" (the trade currency,
            // never the traded item — appears on every order automatically).
            if (item.name == "none" || item.name == "silver") continue;

            // Slot wrapper sits in the GridLayoutGroup cell. Children render in order,
            // so Highlight (added first) draws behind Icon (added second).
            GameObject slot = new GameObject("IconSlot_" + item.name, typeof(RectTransform));
            slot.transform.SetParent(itemIconGrid, false);

            GameObject hl = new GameObject("Highlight", typeof(RectTransform), typeof(Image));
            hl.transform.SetParent(slot.transform, false);
            var hlRt = hl.GetComponent<RectTransform>();
            hlRt.anchorMin = Vector2.zero;
            hlRt.anchorMax = Vector2.one;
            hlRt.offsetMin = Vector2.zero;
            hlRt.offsetMax = Vector2.zero;
            var hlImg = hl.GetComponent<Image>();
            hlImg.color = IconSelectedTint;
            hlImg.raycastTarget = false;
            hl.SetActive(false);
            iconHighlights[item.id] = hl;

            GameObject go = Instantiate(itemIconPrefab, slot.transform, false);
            go.name = "ItemIcon_" + item.name;
            ItemIcon icon = go.GetComponent<ItemIcon>();
            if (icon == null) { Debug.LogError($"TradingPanel: itemIconPrefab missing ItemIcon component"); continue; }
            icon.SetItem(item);
            icon.onClick = OnItemIconClicked;
        }

        RefreshIconSelection();
    }

    void OnItemIconClicked(Item item) {
        if (item == null) return;
        if (itemInput != null) itemInput.text = item.name;
        OnClickQuery();
    }

    // Toggles each icon's highlight backdrop based on whether itemInput resolves to that item.
    // Driven by itemInput.onValueChanged so typing and click-to-fill both update in real time.
    void RefreshIconSelection() {
        string sel = itemInput != null ? itemInput.text.Trim() : "";
        Item selected = null;
        if (sel.Length > 0) Db.itemByName.TryGetValue(sel, out selected);
        int selId = selected != null ? selected.id : -1;
        foreach (var kvp in iconHighlights) {
            if (kvp.Value != null) kvp.Value.SetActive(kvp.Key == selId);
        }
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
            EventFeed.instance?.Post("<color=#cc3333>not connected to server 3:</color>", EventFeed.Category.Alert);
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
            case "/rain": CmdRain(parts); break;
            case "/day":  CmdDay(parts);  break;
            case "/wind": CmdWind(parts); break;
            default:
                EventFeed.instance?.Post($"<color=#cc3333>Unknown command: {cmd}</color>", EventFeed.Category.Alert);
                break;
        }
    }

    // /rain                 — toggle weather between rain and clear.
    // /rain [0..1]           — snap humidity to a fixed value. Rain triggers
    //                          when humidity > WeatherSystem.rainThreshold (0.65).
    void CmdRain(string[] parts) {
        if (WeatherSystem.instance == null) {
            EventFeed.instance?.Post("<color=#cc3333>WeatherSystem not initialised.</color>", EventFeed.Category.Alert);
            return;
        }

        if (parts.Length == 1) {
            WeatherSystem.instance.ToggleRain();
            bool now = WeatherSystem.instance.isRaining;
            EventFeed.instance?.Post(
                now ? "<color=#aaccff>Rain started.</color>" : "<color=#aaffaa>Rain stopped.</color>",
                EventFeed.Category.Info);
            return;
        }

        if (parts.Length != 2 || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float h)) {
            EventFeed.instance?.Post("<color=#cc3333>Usage: /rain or /rain [0..1]</color>", EventFeed.Category.Alert);
            return;
        }
        WeatherSystem.instance.SetHumidity(h);
        EventFeed.instance?.Post(
            $"<color=#aaccff>Humidity set to {Mathf.Clamp01(h):F2} (rain triggers above {WeatherSystem.rainThreshold:F2}).</color>",
            EventFeed.Category.Info);
    }

    // /day [number] — jump the world clock to that day-of-year (fractional ok).
    // Preserves the current year count, so /day 5 lands at day 5 of the current
    // year — going backward if needed. Debug-only; rewinding the timer is fine
    // here even though most systems assume monotonic time.
    void CmdDay(string[] parts) {
        if (parts.Length != 2) {
            EventFeed.instance?.Post("<color=#cc3333>Usage: /day [number]</color>", EventFeed.Category.Alert);
            return;
        }
        if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float day)) {
            EventFeed.instance?.Post("<color=#cc3333>Day must be a number.</color>", EventFeed.Category.Alert);
            return;
        }
        if (World.instance == null) {
            EventFeed.instance?.Post("<color=#cc3333>World not initialised.</color>", EventFeed.Category.Alert);
            return;
        }
        // Wrap into [0, daysInYear) so e.g. /day 30 in a 24-day year lands at day 6.
        day = ((day % World.daysInYear) + World.daysInYear) % World.daysInYear;
        float yearLen = World.ticksInDay * World.daysInYear;
        float yearStart = Mathf.Floor(World.instance.timer / yearLen) * yearLen;
        World.instance.timer = yearStart + day * World.ticksInDay;
        EventFeed.instance?.Post(
            $"<color=#aaffaa>Jumped to day {day:F2}/{World.daysInYear}.</color>",
            EventFeed.Category.Info);
    }

    // /wind [value] — snap wind to a fixed value (e.g. 0, 0.5, -1).
    // Positive blows right. Magnitudes >1 are valid but Windmill clamps output
    // to 1; visuals like plant sway scale linearly so big values look extreme.
    void CmdWind(string[] parts) {
        if (parts.Length != 2) {
            EventFeed.instance?.Post("<color=#cc3333>Usage: /wind [value]</color>", EventFeed.Category.Alert);
            return;
        }
        if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value)) {
            EventFeed.instance?.Post("<color=#cc3333>Value must be a number.</color>", EventFeed.Category.Alert);
            return;
        }
        if (WeatherSystem.instance == null) {
            EventFeed.instance?.Post("<color=#cc3333>WeatherSystem not initialised.</color>", EventFeed.Category.Alert);
            return;
        }
        WeatherSystem.instance.SetWind(value);
        EventFeed.instance?.Post($"<color=#aaffaa>Wind set to {value:F2}.</color>", EventFeed.Category.Info);
    }

    // /give [itemname] [quantity in liang]
    // Produces the item directly into the market inventory.
    void CmdGive(string[] parts) {
        if (parts.Length < 3) {
            EventFeed.instance?.Post("<color=#cc3333>Usage: /give [item] [quantity]</color>", EventFeed.Category.Alert);
            return;
        }

        // Item name may contain spaces — everything between first and last arg is the name.
        // Last arg is always the quantity.
        string qtyStr = parts[parts.Length - 1];
        string itemName = string.Join(" ", parts, 1, parts.Length - 2);

        if (!float.TryParse(qtyStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float qtyLiang) || qtyLiang <= 0f) {
            EventFeed.instance?.Post("<color=#cc3333>Quantity must be a positive number (in liang).</color>", EventFeed.Category.Alert);
            return;
        }
        int qtyFen = Mathf.RoundToInt(qtyLiang * 100f);

        if (!Db.itemByName.ContainsKey(itemName)) {
            EventFeed.instance?.Post($"<color=#cc3333>Unknown item: {itemName}</color>", EventFeed.Category.Alert);
            return;
        }
        Item item = Db.itemByName[itemName];

        Inventory market = TradingClient.FindMarketInventory();
        if (market == null) {
            EventFeed.instance?.Post("<color=#cc3333>No market building found.</color>", EventFeed.Category.Alert);
            return;
        }

        int leftover = market.Produce(item, qtyFen);
        int produced = qtyFen - leftover;
        bool discrete = item.discrete;
        if (produced > 0)
            EventFeed.instance?.Post($"<color=#aaffaa>Gave {ItemStack.FormatQ(produced, discrete)} {itemName} to market.</color>", EventFeed.Category.Info);
        if (leftover > 0)
            EventFeed.instance?.Post($"<color=#cc3333>Could not fit {ItemStack.FormatQ(leftover, discrete)} {itemName} — market full.</color>", EventFeed.Category.Alert);
    }

    void DisplayChat(ChatMsg msg) {
        EventFeed.instance?.Post($"{msg.from}: {msg.text}", EventFeed.Category.Chat);
    }

    void DisplayFill(Fill fill) {
        bool discrete = Db.itemByName.TryGetValue(fill.item, out Item item) && item.discrete;
        EventFeed.instance?.Post(
            $"<color=#55aa55>[fill] {fill.buyer} bought {ItemStack.FormatQ(fill.quantity, discrete)} {fill.item} from {fill.seller} @ {fill.price / 100f:0.##}</color>",
            EventFeed.Category.Fill);
        UpdateMarketTree();
    }

    // Renders an entry posted to the EventFeed as a chat row.
    void HandleFeedEntry(EventFeed.Entry e) {
        AddChat(e.text);
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

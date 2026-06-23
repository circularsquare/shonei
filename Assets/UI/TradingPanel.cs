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
    // Built lazily on first Toggle(open) and reused for the panel's lifetime —
    // same pattern as StoragePanel's allow tree. Rebound to the current market
    // inventory on each open so a save-load (which destroys/recreates the market
    // building) is picked up. See SPEC-ui §StoragePanel for the contract.
    private Dictionary<int, GameObject> marketDisplayGos = new Dictionary<int, GameObject>();
    private bool _marketTreeBuilt = false;
    private Inventory     currentMarket;

    [Header("Item Icon Grid")]
    public Transform      itemIconGrid;     // GridLayoutGroup container
    public GameObject     itemIconPrefab;   // ItemIcon prefab
    private Dictionary<int, GameObject> iconHighlights = new Dictionary<int, GameObject>(); // per-item selection backdrop

    [Header("Chat")]
    // The chat log itself (rows + server chat/fill → feed) lives on the always-on
    // ChatLog component on ChatPanel. This panel keeps only the input field, for
    // command entry (OnClickSendChat) and the "/" focus shortcut (OpenChatInput).
    public TMP_InputField chatInput;

    [Header("Merchant Journey")]
    public MerchantJourneyDisplay merchantJourney;

    [Header("Price Graph")]
    public PriceGraphPanel priceGraphPanel;

    bool _orderIsBuy   = true;
    bool _orderSideSet = false; // neither buy nor sell selected by default

    // Item whose price history is currently in the graph. Drives the periodic
    // re-poll and filters out stale price_history_response messages.
    string _queriedItem = "";
    float  _lastHistoryPoll;
    const float PriceHistoryPollSeconds = 25f; // re-poll cadence while the panel is open

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
        // chatInput.onSubmit is wired to OnClickSendChat in the Inspector (persistent), NOT here:
        // the chat bar lives in the always-active ChatPanel, but this Start() only runs once the
        // (authored-inactive) TradingPanel is first opened — so a code listener would leave Enter
        // dead until then. itemInput below is fine code-wired: it only matters while the panel's open.
        if (orderQty != null) orderQty.contentType = TMP_InputField.ContentType.IntegerNumber;

        var client = TradingClient.instance;
        if (client != null) {
            client.OnMarketResponse += DisplayMarketBook;
            client.OnFill           += RefreshMarketOnFill;
            client.OnPriceHistory   += OnPriceHistory;
        }

        PopulateItemIconGrid();

        // Graph stays hidden until a query returns real history (see OnClickQuery).
        priceGraphPanel?.Hide();

        // Do NOT SetActive(false) here. This GameObject is authored inactive in the scene,
        // so Start() doesn't run until the panel's first activation (first Toggle →
        // OpenExclusive). Hiding here would fire on that first open and immediately re-close
        // the panel — the "click the market button twice" bug. The scene's authored-inactive
        // state is what hides it at load; keep the GameObject inactive in the scene.
    }

    void Update() {
        // Tab while typing qty → jump to price field.
        // (Input polling is inherently per-frame — no event form available.)
        if (orderQty != null && orderPrice != null
                && orderQty.isFocused && Input.GetKeyDown(KeyCode.Tab)) {
            orderPrice.ActivateInputField();
            orderPrice.MoveTextEnd(false);
        }

        // Re-poll price history (new minute-cadence samples) and the order book
        // (keeps the graph's live tip fresh) without a manual re-query.
        // Update() only runs while the panel is open; both are no-ops offline.
        if (_queriedItem.Length > 0
                && Time.unscaledTime - _lastHistoryPoll > PriceHistoryPollSeconds) {
            _lastHistoryPoll = Time.unscaledTime;
            QueryHistory(_queriedItem);
            TradingClient.instance?.QueryMarket(_queriedItem);
        }
    }

    void OnDestroy() {
        ClearMarketTree();
        var client = TradingClient.instance;
        if (client != null) {
            client.OnMarketResponse -= DisplayMarketBook;
            client.OnFill           -= RefreshMarketOnFill;
            client.OnPriceHistory   -= OnPriceHistory;
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
            OpenMarketTree();
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

    // Toggle(open) entry point: builds the tree on first call, then rebinds it
    // to the current market inventory (which may differ from last open if the
    // market building was destroyed/rebuilt) and refreshes display.
    void OpenMarketTree() {
        if (marketInvContent == null || itemDisplayPrefab == null) return;
        BuildMarketTreeOnce();
        currentMarket = TradingClient.FindMarketInventory();
        if (currentMarket == null) return;
        // Rebind every cached row to the (possibly new) market inv before refresh.
        foreach (var kvp in marketDisplayGos) {
            ItemDisplay display = kvp.Value.GetComponent<ItemDisplay>();
            if (display != null) display.targetInventory = currentMarket;
        }
        UpdateMarketTree();
        LayoutUtil.RebuildImmediate(marketInvContent as RectTransform);
    }

    // One-shot tree construction. Builds a row for every item in Db.items
    // regardless of ItemTypeCompatible — the per-market filter is applied at
    // refresh time in UpdateMarketTree's visibility computation, so the same
    // cache works if the market's storageClass ever changes. Mirrors
    // StoragePanel.BuildAllowTreeOnce.
    void BuildMarketTreeOnce() {
        if (_marketTreeBuilt) return;
        RectTransform panelRoot = marketInvContent.GetComponent<RectTransform>();

        foreach (Item item in Db.items) {
            if (item == null) continue;

            Transform parent = item.parent == null
                ? marketInvContent
                : (marketDisplayGos.ContainsKey(item.parent.id)
                    ? marketDisplayGos[item.parent.id].transform
                    : marketInvContent);

            GameObject go = Instantiate(itemDisplayPrefab, parent);
            go.name = "ItemDisplay_" + item.name;
            marketDisplayGos[item.id] = go;
            // Start inactive; UpdateMarketTree (called from OpenMarketTree) activates
            // discovered + compatible rows immediately.
            go.SetActive(false);

            ItemDisplay display = go.GetComponent<ItemDisplay>();
            display.item = item;
            display.displayMode = ItemDisplay.DisplayMode.Market;
            display.panelRoot = panelRoot;
            display.getDisplayGo = id => marketDisplayGos.ContainsKey(id) ? marketDisplayGos[id] : null;
            display.SetDisplayMode(ItemDisplay.DisplayMode.Market);
            // Market mode default — Start() also sets this for Market mode, but we
            // preempt so the first UpdateMarketTree's visibility walk is correct.
            display.open = true;

            if (display.itemText != null) display.itemText.text = item.name;
        }

        _marketTreeBuilt = true;
    }

    // OnDestroy cleanup — destroys cached rows. Tree will rebuild on next Toggle
    // if the panel ever survives this (it normally doesn't; called at scene unload).
    void ClearMarketTree() {
        foreach (var kvp in marketDisplayGos) {
            kvp.Value.SetActive(false);
            Destroy(kvp.Value);
        }
        marketDisplayGos.Clear();
        _marketTreeBuilt = false;
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
            // Visibility = compatible ∧ discovered ∧ no-ancestor-collapsed.
            // Compat is checked here (not at build time) so the cached tree works
            // even if the market's storageClass changes. Items discovered since the
            // tree was built (e.g. via /give) appear within one tick, and we mustn't
            // fight a user's dropdown collapse by re-activating their hidden children.
            bool compat = currentMarket.ItemTypeCompatible(display.item);
            bool discovered = discoveredItems != null
                && discoveredItems.TryGetValue(kvp.Key, out bool d) && d;
            bool shouldBeActive = compat && discovered && IsVisibleInMarketTree(display.item);
            if (kvp.Value.activeSelf != shouldBeActive) {
                kvp.Value.SetActive(shouldBeActive);
            }
            display.Refresh();
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
        // Switching items: hide the previous item's graph until this item's
        // history comes back. Re-polls (Update) don't go through here, so the
        // graph doesn't flicker while viewing a single item.
        if (item != _queriedItem) priceGraphPanel?.Hide();
        TradingClient.instance?.QueryMarket(item);
        _queriedItem = item;
        _lastHistoryPoll = Time.unscaledTime;
        QueryHistory(item);
    }

    // Queries price history for an item at the graph's currently-selected range.
    void QueryHistory(string item) {
        if (priceGraphPanel == null) return;
        TradingClient.instance?.QueryPriceHistory(item, priceGraphPanel.RangeSec, priceGraphPanel.BucketSec);
    }

    // Re-queries the current item's history — called when the graph's range
    // changes. Resets the poll timer so the 25 s poll doesn't immediately re-fire.
    public void RequeryHistory() {
        if (_queriedItem.Length == 0) return;
        _lastHistoryPoll = Time.unscaledTime;
        QueryHistory(_queriedItem);
    }

    // Price-history response handler — feeds the graph, ignoring responses for
    // an item other than the one in view, or for a no-longer-selected range.
    void OnPriceHistory(PriceHistoryData data) {
        if (data == null || data.item != _queriedItem) return;
        if (data.rangeSec != 0 && priceGraphPanel != null
                && data.rangeSec != priceGraphPanel.RangeSec) return;
        priceGraphPanel?.SetHistory(data);
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

        // Feed the graph's live tip when this book is for the item in view.
        // buys[0] is the best bid, sells[0] the best ask (server sends them sorted).
        if (book.item == _queriedItem && priceGraphPanel != null) {
            int liveBid = (book.buys  != null && book.buys.Length  > 0) ? book.buys[0].price  : 0;
            int liveAsk = (book.sells != null && book.sells.Length > 0) ? book.sells[0].price : 0;
            priceGraphPanel.SetLivePrice(book.item, liveBid, liveAsk);
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
        if (!_orderSideSet) { ShowAlert("Select buy or sell."); return; }
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

        if (!Db.itemByName.ContainsKey(itemName)) { ShowAlert($"unknown item: {itemName}"); return; }
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
            EventFeed.instance?.Post("<color=#cc3333>not connected to server 3:</color>", EventFeed.Category.Info);
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
            case "/mice": CmdMice(parts); break;
            case "/rain": CmdRain(parts); break;
            case "/day":  CmdDay(parts);  break;
            case "/wind": CmdWind(parts); break;
            case "/timespeed": CmdTimeSpeed(parts); break;
            case "/generate":   CmdGenerate(parts);   break;
            case "/regenerate": CmdRegenerate(parts); break;
            case "/research": CmdResearch(parts); break;
            case "/online": CmdOnline(); break;
            case "/help": case "/commands": CmdHelp(); break;
            default:
                EventFeed.instance?.Post($"<color=#cc3333>Unknown command: {cmd}. Try /help</color>", EventFeed.Category.Info);
                break;
        }
    }

    // /help (alias /commands) — list every chat command, one multi-line feed entry.
    // Keep this list in sync with the HandleCommand switch above. ASCII only: the m5x7
    // pixel font has no em-dash/arrow glyphs, so usage hints use plain hyphens.
    void CmdHelp() {
        EventFeed.instance?.Post(
            "<color=#aaccff>commands:\n" +
            "/help - this list\n" +
            "/give [item] [qty] ([x] [y]) - spawn items\n" +
            "/mice [n] - set population\n" +
            "/rain ([0..1]) - toggle or set rain\n" +
            "/day [n] - jump to day-of-year\n" +
            "/wind [value] - set wind\n" +
            "/timespeed [n] - set time scale\n" +
            "/generate [seed] - new world from seed\n" +
            "/regenerate - rebuild current world\n" +
            "/research ([id]) - unlock all or one tech\n" +
            "/online - players online</color>",
            EventFeed.Category.Info);
    }

    // /online — report how many players the server currently sees connected.
    // The count is pushed by the server on every connect/disconnect; we just read
    // the latest value TradingClient cached. hasOnlineCount distinguishes "not
    // reported yet" (just connected) from a genuine count.
    void CmdOnline() {
        var client = TradingClient.instance;
        if (client == null || !client.isOnline) {
            EventFeed.instance?.Post("<color=#cc3333>not connected to server 3:</color>", EventFeed.Category.Info);
            return;
        }
        if (!client.hasOnlineCount) {
            EventFeed.instance?.Post("<color=#aaaaaa>online count not in yet, try again in a sec</color>", EventFeed.Category.Info);
            return;
        }
        int n = client.OnlinePlayerCount;
        EventFeed.instance?.Post(
            $"<color=#aaffaa>{n} {(n == 1 ? "player" : "players")} online</color>",
            EventFeed.Category.Info);
    }

    // /rain                 — toggle weather between rain and clear.
    // /rain [0..1]           — snap humidity to a fixed value. Rain triggers
    //                          when humidity > WeatherSystem.rainThreshold (0.65).
    void CmdRain(string[] parts) {
        if (WeatherSystem.instance == null) {
            EventFeed.instance?.Post("<color=#cc3333>WeatherSystem not initialised.</color>", EventFeed.Category.Info);
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
            EventFeed.instance?.Post("<color=#cc3333>Usage: /rain or /rain [0..1]</color>", EventFeed.Category.Info);
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
            EventFeed.instance?.Post("<color=#cc3333>Usage: /day [number]</color>", EventFeed.Category.Info);
            return;
        }
        if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float day)) {
            EventFeed.instance?.Post("<color=#cc3333>Day must be a number.</color>", EventFeed.Category.Info);
            return;
        }
        if (World.instance == null) {
            EventFeed.instance?.Post("<color=#cc3333>World not initialised.</color>", EventFeed.Category.Info);
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
            EventFeed.instance?.Post("<color=#cc3333>Usage: /wind [value]</color>", EventFeed.Category.Info);
            return;
        }
        if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value)) {
            EventFeed.instance?.Post("<color=#cc3333>Value must be a number.</color>", EventFeed.Category.Info);
            return;
        }
        if (WeatherSystem.instance == null) {
            EventFeed.instance?.Post("<color=#cc3333>WeatherSystem not initialised.</color>", EventFeed.Category.Info);
            return;
        }
        WeatherSystem.instance.SetWind(value);
        EventFeed.instance?.Post($"<color=#aaffaa>Wind set to {value:F2}.</color>", EventFeed.Category.Info);
    }

    // /timespeed [n] — set Time.timeScale to any value, beyond the 0x/1x/2x buttons.
    // Cheat-only fast-forward (e.g. /timespeed 4). 0 pauses. Negative is rejected
    // (Unity disallows it); an upper cap keeps fixedDeltaTime sane.
    void CmdTimeSpeed(string[] parts) {
        if (parts.Length != 2) {
            EventFeed.instance?.Post("<color=#cc3333>Usage: /timespeed [n]</color>", EventFeed.Category.Info);
            return;
        }
        if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float scale)) {
            EventFeed.instance?.Post("<color=#cc3333>Speed must be a number.</color>", EventFeed.Category.Info);
            return;
        }
        if (scale < 0f || scale > 100f) {
            EventFeed.instance?.Post("<color=#cc3333>Speed must be between 0 and 100.</color>", EventFeed.Category.Info);
            return;
        }
        if (TimeController.instance == null) {
            EventFeed.instance?.Post("<color=#cc3333>TimeController not initialised.</color>", EventFeed.Category.Info);
            return;
        }
        TimeController.instance.SetSpeed(scale);
        EventFeed.instance?.Post($"<color=#aaffaa>Time speed set to {scale:0.##}x.</color>", EventFeed.Category.Info);
    }

    // /generate [seed] — wipe and rebuild the world from the given integer seed.
    // Use a seed from the Ctrl+D debug log to reproduce a specific world.
    void CmdGenerate(string[] parts) {
        if (parts.Length != 2) {
            EventFeed.instance?.Post("<color=#cc3333>Usage: /generate [seed]</color>", EventFeed.Category.Info);
            return;
        }
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int seed) || seed <= 0) {
            EventFeed.instance?.Post("<color=#cc3333>Seed must be a positive whole number.</color>", EventFeed.Category.Info);
            return;
        }
        Regenerate(seed);
    }

    // /regenerate — rebuild the world from its current seed (Rng.worldSeed, which is
    // authoritative on both fresh-gen and loaded-save paths), discarding edits.
    void CmdRegenerate(string[] parts) {
        if (!Rng.IsInitialized) {
            EventFeed.instance?.Post("<color=#cc3333>No world seed yet.</color>", EventFeed.Category.Info);
            return;
        }
        Regenerate(Rng.worldSeed);
    }

    // Shared regen: stash the seed as a one-shot override, then run the same default-world
    // reset path the Save menu uses — SaveSystem.LoadDefault clears the world and calls
    // GenerateDefault, which consumes pendingSeedOverride. Discards the current world
    // without confirmation (debug command, like /give).
    void Regenerate(int seed) {
        if (SaveSystem.instance == null) {
            EventFeed.instance?.Post("<color=#cc3333>SaveSystem not initialised.</color>", EventFeed.Category.Info);
            return;
        }
        WorldController.pendingSeedOverride = seed;
        SaveSystem.instance.LoadDefault();
        EventFeed.instance?.Post($"<color=#aaffaa>World regenerated (seed {seed}).</color>", EventFeed.Category.Info);
    }

    // /give [itemname] [quantity]                        — produce into the market inventory.
    //   quantity is liang for normal items, a whole unit count for discrete items (stools etc).
    // /give [itemname] [quantity] [x] [y]                — spawn as a floor item at tile (x,y)
    //                                                      if the tile is non-solid and has no
    //                                                      existing floor stack.
    void CmdGive(string[] parts) {
        if (parts.Length < 3) {
            EventFeed.instance?.Post("<color=#cc3333>Usage: /give [item] [quantity] (optional: [x] [y])</color>", EventFeed.Category.Info);
            return;
        }

        // Detect the coord form: at least 4 args after /give, last two parse as ints, and
        // the arg before them parses as a float (the quantity). Item names don't end in
        // numbers in this game, so this disambiguates reliably.
        bool coordForm = false;
        int tx = 0, ty = 0;
        int qtyArgIdx = parts.Length - 1;
        if (parts.Length >= 5
            && int.TryParse(parts[parts.Length - 2], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out tx)
            && int.TryParse(parts[parts.Length - 1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out ty)
            && float.TryParse(parts[parts.Length - 3], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _)) {
            coordForm = true;
            qtyArgIdx = parts.Length - 3;
        }

        // Item name may contain spaces — everything between /give and the quantity arg is the name.
        string qtyStr = parts[qtyArgIdx];
        string itemName = string.Join(" ", parts, 1, qtyArgIdx - 1);

        if (!Db.itemByName.ContainsKey(itemName)) {
            EventFeed.instance?.Post($"<color=#cc3333>Unknown item: {itemName}</color>", EventFeed.Category.Info);
            return;
        }
        Item item = Db.itemByName[itemName];

        // Quantity is liang for normal items, a whole unit count for discrete items.
        if (!ItemStack.TryParseQ(qtyStr, item, out int qtyFen) || qtyFen <= 0) {
            string unit = item.discrete ? "a positive whole unit count" : "a positive number in liang";
            EventFeed.instance?.Post($"<color=#cc3333>Quantity must be {unit}.</color>", EventFeed.Category.Info);
            return;
        }

        if (coordForm) {
            if (World.instance == null) {
                EventFeed.instance?.Post("<color=#cc3333>World not initialised.</color>", EventFeed.Category.Info);
                return;
            }
            Tile tile = World.instance.GetTileAt(tx, ty);
            if (tile == null) {
                EventFeed.instance?.Post($"<color=#cc3333>Tile ({tx},{ty}) is out of bounds.</color>", EventFeed.Category.Info);
                return;
            }
            if (tile.type != null && tile.type.solid) {
                EventFeed.instance?.Post($"<color=#cc3333>Tile ({tx},{ty}) is solid - can't drop a floor item there.</color>", EventFeed.Category.Info);
                return;
            }
            if (tile.inv != null && !tile.inv.IsEmpty()) {
                EventFeed.instance?.Post($"<color=#cc3333>Tile ({tx},{ty}) already has a floor stack.</color>", EventFeed.Category.Info);
                return;
            }

            Inventory floor = tile.EnsureFloorInventory();
            int leftoverF = floor.Produce(item, qtyFen);
            int producedF = qtyFen - leftoverF;
            // Mid-air tiles aren't rejected — let the existing fall primitive land the stack
            // on the first standable tile below, so /give in air behaves naturally.
            World.instance.FallIfUnstandable(tx, ty);
            if (producedF > 0)
                EventFeed.instance?.Post($"<color=#aaffaa>Spawned {ItemStack.FormatQ(producedF, item)} {itemName} at ({tx},{ty}).</color>", EventFeed.Category.Info);
            if (leftoverF > 0)
                EventFeed.instance?.Post($"<color=#cc3333>Could not fit {ItemStack.FormatQ(leftoverF, item)} {itemName} on tile ({tx},{ty}).</color>", EventFeed.Category.Info);
            return;
        }

        Inventory market = TradingClient.FindMarketInventory();
        if (market == null) {
            EventFeed.instance?.Post("<color=#cc3333>No market building found.</color>", EventFeed.Category.Info);
            return;
        }

        int leftover = market.Produce(item, qtyFen);
        int produced = qtyFen - leftover;
        if (produced > 0)
            EventFeed.instance?.Post($"<color=#aaffaa>Gave {ItemStack.FormatQ(produced, item)} {itemName} to market.</color>", EventFeed.Category.Info);
        if (leftover > 0)
            EventFeed.instance?.Post($"<color=#cc3333>Could not fit {ItemStack.FormatQ(leftover, item)} {itemName} - market full.</color>", EventFeed.Category.Info);
    }

    // /mice [n] — set the colony population to exactly n (cheat). When n exceeds the
    // current population, newcomers spawn clustered on the mouse nearest the original
    // spawn point; when n is lower, mice are randomly culled. n == current is a no-op.
    void CmdMice(string[] parts) {
        if (parts.Length != 2) {
            EventFeed.instance?.Post("<color=#cc3333>Usage: /mice [n]</color>", EventFeed.Category.Info);
            return;
        }
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int target) || target < 0) {
            EventFeed.instance?.Post("<color=#cc3333>Population must be a whole number >= 0.</color>", EventFeed.Category.Info);
            return;
        }
        AnimalController ac = AnimalController.instance;
        if (ac == null) {
            EventFeed.instance?.Post("<color=#cc3333>AnimalController not initialised.</color>", EventFeed.Category.Info);
            return;
        }

        int current = ac.na;
        if (target == current) {
            EventFeed.instance?.Post($"<color=#aaaaaa>Population already {current}.</color>", EventFeed.Category.Info);
            return;
        }

        if (target > current) {
            // ac.na won't reflect the new mice until they register next frame, so report
            // the expected total from the spawn count rather than re-reading na.
            int spawned = ac.DebugSpawnMice(target - current);
            if (spawned > 0)
                EventFeed.instance?.Post($"<color=#aaffaa>Spawned {spawned} mice (pop {current} -> {current + spawned}).</color>", EventFeed.Category.Info);
            else
                EventFeed.instance?.Post("<color=#cc3333>Couldn't spawn mice (at capacity or no spawn point).</color>", EventFeed.Category.Info);
        } else {
            int removed = ac.DebugRemoveMice(current - target);
            EventFeed.instance?.Post($"<color=#aaffaa>Removed {removed} mice (pop {current} -> {current - removed}).</color>", EventFeed.Category.Info);
        }
    }

    // /research        — fully research every tech (the old "unlock all" button).
    // /research [id]    — fully research just the tech with this id (and its prereqs,
    //                     so the unlock graph stays consistent).
    void CmdResearch(string[] parts) {
        ResearchSystem rs = ResearchSystem.instance;
        if (rs == null) {
            EventFeed.instance?.Post("<color=#cc3333>ResearchSystem not initialised.</color>", EventFeed.Category.Info);
            return;
        }

        if (parts.Length == 1) {
            rs.UnlockAll();
            ResearchPanel.instance?.Refresh();
            EventFeed.instance?.Post("<color=#aaffaa>All tech researched.</color>", EventFeed.Category.Info);
            return;
        }

        if (parts.Length != 2 || !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int id)) {
            EventFeed.instance?.Post("<color=#cc3333>Usage: /research or /research [id]</color>", EventFeed.Category.Info);
            return;
        }

        if (!rs.MaxTech(id)) {
            EventFeed.instance?.Post($"<color=#cc3333>No tech with id {id}.</color>", EventFeed.Category.Info);
            return;
        }
        ResearchPanel.instance?.Refresh();
        string techName = rs.nodeById.TryGetValue(id, out var node) ? node.name : id.ToString();
        EventFeed.instance?.Post($"<color=#aaffaa>Researched tech {id} ({techName}).</color>", EventFeed.Category.Info);
    }

    // A fill changed the market inventory; refresh the holdings tree while the
    // panel is open. The fill's feed message + SFX live on ChatLog (always-on),
    // so they fire even when this panel is closed.
    void RefreshMarketOnFill(Fill fill) {
        UpdateMarketTree();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    string ItemName() {
        return itemInput != null ? itemInput.text.Trim() : "";
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
        tmp.text = text;
        FontConfig.Apply(tmp);
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
        xTmp.text      = "x";  // ASCII only — the m5x7 font option has no non-ASCII glyphs
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
        tmp.text = text;
        FontConfig.Apply(tmp);
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 14;
        le.minHeight       = 14;
    }
}

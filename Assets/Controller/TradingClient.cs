using NativeWebSocket;
using UnityEngine;
using System.Text;
using System.Collections;
using System;

public class TradingClient : MonoBehaviour {
    public static TradingClient instance { get; protected set; }

    WebSocket ws;
    public bool isOnline { get; private set; } = false;
    // Latest player count the server reported (it pushes one on every connect /
    // disconnect). hasOnlineCount stays false until the first report arrives, so
    // a consumer can tell "not known yet" from a genuine count. Both reset on
    // disconnect — a stale count shouldn't outlive the connection it came from.
    public int  OnlinePlayerCount { get; private set; }
    public bool hasOnlineCount    { get; private set; }
    bool isConnecting = false;
    bool hasLoggedConnectError = false;
    bool hasLoggedDisconnect = false;
    // Set when the server kicks us because the same account logged in elsewhere
    // (newest-login-wins). Suppresses the auto-reconnect loop — otherwise we'd
    // immediately reconnect and kick the new session back, ping-ponging forever.
    // Only a fresh world entry (new TradingClient) clears it.
    bool kicked = false;
    // The market identity for this session — the logged-in account's username
    // (set by the menu via Session). In the editor with no login (running Main
    // directly) it falls back to a dev name against the local insecure server.
    public static string playerName =>
        !string.IsNullOrEmpty(Session.Username) ? Session.Username : EditorFallbackName;
#if UNITY_EDITOR
    const string EditorFallbackName = "anita";
#else
    const string EditorFallbackName = "anonymous";
#endif

    // ── Server target ──────────────────────────────────────────────────────
    // Host (prod vs local) comes from MarketServer; identity is the auth token
    // when logged in, else ?name= for the editor/insecure-local path. Resolved
    // fresh each connect so a menu toggle or login takes effect on reconnect.
    static string ResolveWsUrl() {
        string url = MarketServer.WsBase + "/ws";
        if (!string.IsNullOrEmpty(Session.Token))
            return url + "?token=" + Uri.EscapeDataString(Session.Token);
        return url + "?name=" + Uri.EscapeDataString(playerName);
    }

    // Builds must authenticate before connecting; the editor may connect with the
    // dev fallback name so running Main.unity standalone still reaches the market.
    static bool ShouldConnect() {
        if (Session.LoggedIn) return true;
#if UNITY_EDITOR
        return true;
#else
        return false;
#endif
    }

    const float ReconnectInterval = 20f;

    public event Action<bool>             OnConnectionChanged;
    public event Action<MarketBook>       OnMarketResponse;
    public event Action<Fill>             OnFill;
    public event Action<ChatMsg>          OnChat;
    public event Action<PriceHistoryData> OnPriceHistory;
    public event Action<int>              OnOnlineCount;

    public async void Connect() {
        if (isConnecting || isOnline) return;
        if (kicked) return;  // taken over by another device — don't fight it back online
        if (!ShouldConnect()) return;
        // Don't burn reconnect attempts on a token we already know is expired —
        // the player needs to re-login (handled by the menu on next launch).
        if (Session.LoggedIn && !Session.IsTokenValid(Session.Token)) {
            Debug.LogWarning("[market] session token expired — not connecting; re-login required");
            return;
        }
        isConnecting = true;

        string url = ResolveWsUrl();
        Debug.Log("[market] connecting as " + playerName + " to " + MarketServer.WsBase);
#if UNITY_EDITOR
        if (!MarketServer.UseLocal)
            Debug.LogWarning("[market] editor is connected to PRODUCTION — orders here hit the live market");
#endif
        ws = new WebSocket(url);

        ws.OnMessage += (bytes) => {
            string raw = Encoding.UTF8.GetString(bytes);
            HandleMessage(raw);
        };

        ws.OnOpen  += () => { Debug.Log("connected to server"); hasLoggedConnectError = false; hasLoggedDisconnect = false; SetOnline(true); };
        ws.OnError += (e) => {
            if (e == "Unable to connect to the remote server") {
                if (!hasLoggedConnectError) { Debug.Log("ServerError: " + e); hasLoggedConnectError = true; }
            } else {
                Debug.Log("ServerError: " + e);
            }
        };
        ws.OnClose += (e) => { if (!hasLoggedDisconnect) { Debug.Log("disconnected from server"); hasLoggedDisconnect = true; } SetOnline(false); };

        await ws.Connect();
        isConnecting = false;
    }

    void HandleMessage(string raw) {
        Debug.Log("Server: " + raw);
        var env = JsonUtility.FromJson<Envelope>(raw);
        switch (env.type) {
            case "market_response":
                OnMarketResponse?.Invoke(JsonUtility.FromJson<MarketResponseEnvelope>(raw).payload);
                break;
            case "fill":
                var fill = JsonUtility.FromJson<FillEnvelope>(raw).payload;
                ProcessFill(fill);
                OnFill?.Invoke(fill);
                QueryMarket(fill.item);
                break;
            case "chat":
                OnChat?.Invoke(JsonUtility.FromJson<ChatEnvelope>(raw).payload);
                break;
            case "price_history_response":
                OnPriceHistory?.Invoke(JsonUtility.FromJson<PriceHistoryEnvelope>(raw).payload);
                break;
            case "online_count":
                OnlinePlayerCount = JsonUtility.FromJson<OnlineCountEnvelope>(raw).payload.count;
                hasOnlineCount    = true;
                OnOnlineCount?.Invoke(OnlinePlayerCount);
                break;
            case "kick":
                // Same account signed in elsewhere; the server is closing this socket.
                // Latch `kicked` so ReconnectLoop stands down (no kick-war), then surface it.
                kicked = true;
                string kickText = JsonUtility.FromJson<NoticeEnvelope>(raw).payload.text;
                EventFeed.instance?.Post(
                    $"<color=#cc3333>{(string.IsNullOrEmpty(kickText) ? "signed out: account opened on another device" : kickText)}</color>",
                    EventFeed.Category.Alert);
                break;
            case "notice":
                // Informational one-off (e.g. "you displaced an existing session"). No behaviour change.
                string noticeText = JsonUtility.FromJson<NoticeEnvelope>(raw).payload.text;
                if (!string.IsNullOrEmpty(noticeText))
                    EventFeed.instance?.Post($"<color=#cc9933>{noticeText}</color>", EventFeed.Category.Info);
                break;
        }
    }

    public static Inventory FindMarketInventory() {
        foreach (Inventory inv in InventoryController.instance.inventories)
            if (inv.invType == Inventory.InvType.Market) return inv;
        return null;
    }

    static void ProcessFill(Fill fill) {
        Inventory market = FindMarketInventory();
        if (market == null) { Debug.LogError("Fill received but no market inventory found."); return; }

        if (!Db.itemByName.ContainsKey(fill.item)) { Debug.LogError($"Fill: unknown item '{fill.item}'"); return; }
        Item item   = Db.itemByName[fill.item];
        Item silver = Db.itemByName["silver"];
        // both quantity and price are in fen; divide by 100 to get silver in fen
        int  silverAmt = fill.quantity * fill.price / 100;

        if (fill.buyer == playerName) {
            // We bought: pay silver, receive item
            int leftover = market.Produce(silver, -silverAmt);
            if (leftover != 0) Debug.LogError($"Fill: couldn't remove {silverAmt} silver from market (leftover {leftover})");
            leftover = market.Produce(item, fill.quantity);
            if (leftover != 0) Debug.LogError($"Fill: couldn't add {fill.quantity} {fill.item} to market (leftover {leftover})");
        } else if (fill.seller == playerName) {
            // We sold: give item, receive silver
            int leftover = market.Produce(item, -fill.quantity);
            if (leftover != 0) Debug.LogError($"Fill: couldn't remove {fill.quantity} {fill.item} from market (leftover {leftover})");
            leftover = market.Produce(silver, silverAmt);
            if (leftover != 0) Debug.LogError($"Fill: couldn't add {silverAmt} silver to market (leftover {leftover})");
        }
    }

    void SetOnline(bool online) {
        isOnline = online;
        if (!online) { hasOnlineCount = false; OnlinePlayerCount = 0; }
        OnConnectionChanged?.Invoke(online);
    }

    IEnumerator ReconnectLoop() {
        while (true) {
            yield return new WaitForSecondsRealtime(ReconnectInterval);
            if (!isOnline && !isConnecting && !kicked && ShouldConnect()) Connect();
        }
    }

    void Awake() {
        if (instance != null) { Debug.LogError("there should only be one TradingClient"); }
        instance = this;
    }

    void Start() {
        Connect();
        StartCoroutine(ReconnectLoop());
    }

    void Update() {
        #if !UNITY_WEBGL || UNITY_EDITOR
        ws?.DispatchMessageQueue();
        #endif
    }

    public async void QueryMarket(string item) {
        if (!isOnline) return;
        var envelope = $"{{\"type\":\"market_query\",\"payload\":{{\"item\":\"{item}\"}}}}";
        if (!await TrySend(envelope)) return;
    }

    // Requests logged bid/ask price history for an item over the past rangeSec
    // seconds, downsampled to bucketSec-wide buckets (for the price graph).
    public async void QueryPriceHistory(string item, int rangeSec, int bucketSec) {
        if (!isOnline) return;
        var envelope = $"{{\"type\":\"price_history_query\",\"payload\":{{\"item\":\"{item}\",\"rangeSec\":{rangeSec},\"bucketSec\":{bucketSec}}}}}";
        if (!await TrySend(envelope)) return;
    }

    public async void SendChat(string text) {
        if (!isOnline) return;
        var payload = JsonUtility.ToJson(new ChatPayload { text = text });
        var envelope = $"{{\"type\":\"chat\",\"payload\":{payload}}}";
        if (!await TrySend(envelope)) return;
    }

    // qty and price are both in fen
    public async void SendOrder(string item, string side, int priceFen, int qty) {
        if (!isOnline) return;
        var payload = $"{{\"item\":\"{item}\",\"side\":\"{side}\",\"price\":{priceFen},\"quantity\":{qty}}}";
        var envelope = $"{{\"type\":\"order\",\"payload\":{payload}}}";
        if (!await TrySend(envelope)) return;
        QueryMarket(item);
    }

    public async void SendCancel(long id) {
        if (!isOnline) return;
        var envelope = $"{{\"type\":\"cancel_order\",\"payload\":{{\"id\":{id}}}}}";
        if (!await TrySend(envelope)) return;
    }

    // Sends text over the websocket; if the socket was disposed between the
    // isOnline check and the actual send, catches the exception and marks offline.
    async System.Threading.Tasks.Task<bool> TrySend(string text) {
        try {
            await ws.SendText(text);
            return true;
        } catch (ObjectDisposedException) {
            Debug.Log("WebSocket disposed during send — marking offline");
            SetOnline(false);
            return false;
        }
    }

    async void OnDestroy() {
        StopAllCoroutines();
        if (ws != null) await ws.Close();
    }
}

[Serializable] class Envelope               { public string type; }
[Serializable] class MarketResponseEnvelope { public string type; public MarketBook payload; }
[Serializable] class FillEnvelope           { public string type; public Fill     payload; }
[Serializable] class ChatEnvelope           { public string type; public ChatMsg  payload; }
[Serializable] public class MarketBook  { public string item; public MarketOrder[] buys; public MarketOrder[] sells; }
// price and quantity are both in fen (100 fen = 1 liang)
[Serializable] public class MarketOrder { public long id; public string from; public string side; public int price; public int quantity; public string client_type; }
[Serializable] public class Fill        { public string buyer; public string seller; public string item; public int price; public int quantity; }
[Serializable] public class ChatMsg     { public string from; public string text; }
[Serializable] class ChatPayload        { public string text; }
[Serializable] class PriceHistoryEnvelope { public string type; public PriceHistoryData payload; }
[Serializable] class OnlineCountEnvelope   { public string type; public OnlineCount payload; }
[Serializable] public class OnlineCount    { public int count; }
// Server-pushed text shown to a single client: "kick" (account opened elsewhere,
// socket closing) and "notice" (informational) both carry this payload.
[Serializable] class NoticeEnvelope        { public string type; public Notice   payload; }
[Serializable] public class Notice         { public string text; }
// One bid/ask snapshot. Prices are in fen; 0 means no order rested on that side.
// t is unix seconds — a large jump in t between samples marks server downtime.
[Serializable] public class PriceSample      { public long t; public int bid; public int ask; }
// samples are downsampled to bucketSec buckets; startSec/endSec are the time
// window the server measured (axis left/right edges, unix seconds).
[Serializable] public class PriceHistoryData {
    public string item;
    public int    rangeSec;
    public int    bucketSec;
    public long   startSec;
    public long   endSec;
    public PriceSample[] samples;
}

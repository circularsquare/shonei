using NativeWebSocket;
using UnityEngine;
using System.Text;
using System.Collections;
using System;

public class TradingClient : MonoBehaviour {
    public static TradingClient instance { get; protected set; }

    WebSocket ws;
    public bool isOnline { get; private set; } = false;
    bool isConnecting = false;
    bool hasLoggedConnectError = false;
    bool hasLoggedDisconnect = false;
    public const string playerName = "anita";
    const string WsUrl = "ws://127.0.0.1:8082/ws?name=" + playerName;
    const float ReconnectInterval = 20f;

    public event Action<bool>       OnConnectionChanged;
    public event Action<MarketBook> OnMarketResponse;
    public event Action<Fill>       OnFill;
    public event Action<ChatMsg>    OnChat;

    public async void Connect() {
        if (isConnecting || isOnline) return;
        isConnecting = true;

        ws = new WebSocket(WsUrl);

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
        OnConnectionChanged?.Invoke(online);
    }

    IEnumerator ReconnectLoop() {
        while (true) {
            yield return new WaitForSecondsRealtime(ReconnectInterval);
            if (!isOnline && !isConnecting) Connect();
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

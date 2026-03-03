using NativeWebSocket;
using UnityEngine;
using System.Text;
using System.Collections;
using System;

public class TradingClient : MonoBehaviour {
    public static TradingClient instance;

    WebSocket ws;
    public bool isOnline { get; private set; } = false;
    bool isConnecting = false;
    const string WsUrl = "ws://127.0.0.1:8080/ws?name=UnityPlayer";
    const float ReconnectInterval = 20f;

    public event Action<bool> OnConnectionChanged;
    public event Action<MarketBook> OnMarketResponse;

    async void Connect() {
        if (isConnecting || isOnline) return;
        isConnecting = true;

        ws = new WebSocket(WsUrl);

        ws.OnMessage += (bytes) => {
            string raw = Encoding.UTF8.GetString(bytes);
            HandleMessage(raw);
        };

        ws.OnOpen  += () => { Debug.Log("connected to trading server!"); SetOnline(true); };
        ws.OnError += (e) => Debug.Log("ServerError: " + e);
        ws.OnClose += (e) => { Debug.Log("disconnected from server"); SetOnline(false); };

        await ws.Connect();
        isConnecting = false;
    }

    void HandleMessage(string raw) {
        Debug.Log("Server: " + raw);
        var env = JsonUtility.FromJson<Envelope>(raw);
        if (env.type == "market_response") {
            var resp = JsonUtility.FromJson<MarketResponseEnvelope>(raw);
            OnMarketResponse?.Invoke(resp.payload);
        }
        // other types (chat, fill, etc.) can be routed here later
    }

    void SetOnline(bool online) {
        isOnline = online;
        OnConnectionChanged?.Invoke(online);
    }

    IEnumerator ReconnectLoop() {
        while (true) {
            yield return new WaitForSeconds(ReconnectInterval);
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
        await ws.SendText(envelope);
    }

    public async void SendChat(string text) {
        var payload = JsonUtility.ToJson(new ChatPayload { text = text });
        var envelope = $"{{\"type\":\"chat\",\"payload\":{payload}}}";
        await ws.SendText(envelope);
    }

    async void OnDestroy() {
        StopAllCoroutines();
        if (ws != null) await ws.Close();
    }
}

[Serializable] class Envelope              { public string type; }
[Serializable] class MarketResponseEnvelope { public string type; public MarketBook payload; }
[Serializable] public class MarketBook     { public string item; public MarketOrder[] buys; public MarketOrder[] sells; }
[Serializable] public class MarketOrder    { public string from; public string side; public int price; public int quantity; }
[Serializable] class ChatPayload           { public string text; }

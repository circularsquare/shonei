using UnityEngine;
using UnityEngine.UI;
using TMPro;

// ChatLog — always-on renderer for the HUD chat log (bottom-left ChatPanel).
//
// Lives on the always-active ChatPanel, NOT the lazily-activated TradingPanel.
// Both jobs below used to live on TradingPanel, whose GameObject is authored
// inactive — so chat, trade fills, and command feedback stayed invisible until
// the player first opened the market panel. Hosting them here makes the chat log
// work from the moment the world loads.
//
// Two responsibilities:
//   1. Source — turn server chat (OnChat) and trade fills (OnFill) into EventFeed
//      entries (plus the fill SFX). A fill ALSO refreshes the market holdings
//      tree, but that's panel-only and stays on TradingPanel.
//   2. Render — every non-Alert EventFeed entry becomes a chat row. Alert is
//      owned by AlertToast (the transient overlay above this log) and skipped
//      here so error messages don't double-render.
//
// Rows carry a ChatRowFader: opaque for 60s, then fade; focusing the chat input
// reveals the whole backlog (ChatRowFader reads our chatInput).
public class ChatLog : MonoBehaviour {
    public static ChatLog instance { get; private set; }

    // Both children of ChatPanel, assigned in the inspector: the ScrollRect
    // content rows are parented to, and the always-active chat input (exposed so
    // ChatRowFader can reveal the backlog while the player is typing).
    [SerializeField] Transform      chatList;
    public           TMP_InputField chatInput;

    const int MaxRows = 20;  // visible chat rows before the oldest is dropped

    void Awake() {
        if (instance != null) { Debug.LogError("there should only be one ChatLog"); }
        instance = this;
    }

    // Subscribe in Start (not Awake) per SPEC-eventfeed.md — EventFeed/TradingClient
    // set their singletons in Awake, so they're reliably available by Start.
    void Start() {
        var client = TradingClient.instance;
        if (client != null) {
            client.OnChat += DisplayChat;
            client.OnFill += DisplayFill;
        }
        if (EventFeed.instance != null) EventFeed.instance.OnEntry += HandleFeedEntry;
        else Debug.LogError("[ChatLog] EventFeed.instance null at Start — chat log won't render.");
    }

    void OnDestroy() {
        var client = TradingClient.instance;
        if (client != null) {
            client.OnChat -= DisplayChat;
            client.OnFill -= DisplayFill;
        }
        if (EventFeed.instance != null) EventFeed.instance.OnEntry -= HandleFeedEntry;
        if (instance == this) instance = null;
    }

    // ── Server events → EventFeed ─────────────────────────────────

    void DisplayChat(ChatMsg msg) {
        EventFeed.instance?.Post($"{msg.from}: {msg.text}", EventFeed.Category.Chat);
    }

    void DisplayFill(Fill fill) {
        SoundManager.instance?.PlaySFX("trade_fill", 0.5f);
        Db.itemByName.TryGetValue(fill.item, out Item item); // item may be null — FormatQ tolerates it
        EventFeed.instance?.Post(
            $"<color=#55aa55>[fill] {fill.buyer} bought {ItemStack.FormatQ(fill.quantity, item)} {fill.item} from {fill.seller} @ {fill.price / 100f:0.##}</color>",
            EventFeed.Category.Fill);
    }

    // ── EventFeed → chat rows ─────────────────────────────────────

    void HandleFeedEntry(EventFeed.Entry e) {
        if (e.category == EventFeed.Category.Alert) return;  // owned by AlertToast
        AddChat(e.text);
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
        go.AddComponent<ChatRowFader>();   // stale rows fade out; chat-input focus reveals them again
        if (chatList.childCount > MaxRows)
            Destroy(chatList.GetChild(0).gameObject);
        LayoutUtil.RebuildImmediate(chatList as RectTransform);
    }
}

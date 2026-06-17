using System;
using System.Collections.Generic;
using UnityEngine;

// EventFeed — in-game alert/message dispatcher.
//
// Systems call Post() to surface player-facing events (research forgot,
// market errors, trade fills, chat, etc). UIs subscribe to OnEntry to render.
//
// Decoupled from any specific UI: ChatLog (on the always-on ChatPanel) renders
// entries into the HUD chat log and AlertToast renders Alerts as toasts; any
// future overlay can subscribe the same way.
//
// History is capped at HistoryCap entries and cleared by WorldController.ClearWorld
// so a freshly loaded save doesn't show stale alerts from the previous session.
public class EventFeed : MonoBehaviour {
    public static EventFeed instance { get; protected set; }

    public enum Category { Alert, Info, Chat, Fill }

    public struct Entry {
        public string   text;      // rich-text allowed
        public Category category;
        public float    gameTime;  // Time.time at post — pauses with timeScale
    }

    public event Action<Entry> OnEntry;

    private readonly List<Entry> _history = new List<Entry>();
    public IReadOnlyList<Entry> history => _history;

    private const int HistoryCap = 200;

    void Awake() {
        if (instance != null) { Debug.LogError("two EventFeeds!"); }
        instance = this;
        ResearchSystem.OnTechUnlocked  += HandleTechUnlocked;
        ResearchSystem.OnTechForgotten += HandleTechForgotten;
    }

    void OnDestroy() {
        ResearchSystem.OnTechUnlocked  -= HandleTechUnlocked;
        ResearchSystem.OnTechForgotten -= HandleTechForgotten;
        if (instance == this) instance = null;
    }

    // Post a player-facing message. Rich-text colour tags are allowed in `text`
    // and are currently the canonical way to convey severity (red = error, etc.).
    public void Post(string text, Category category = Category.Alert) {
        var e = new Entry { text = text, category = category, gameTime = Time.time };
        _history.Add(e);
        if (_history.Count > HistoryCap) _history.RemoveAt(0);
        OnEntry?.Invoke(e);
    }

    // Drop all history. Called from WorldController.ClearWorld().
    public void Clear() {
        _history.Clear();
    }

    // ── Bindings ──────────────────────────────────────────────

    // Research completing has a sound (SoundManager) but no UI moment of its own —
    // surface it as a toast so passive completions (often while AFK) are noticed.
    private void HandleTechUnlocked(ResearchNodeData node) {
        Post($"<color=#66ccff>[research] Done: {node.name}</color>", Category.Alert);
    }

    private void HandleTechForgotten(ResearchNodeData node) {
        Post($"<color=#ffaa55>[research] Forgot: {node.name}</color>", Category.Alert);
    }
}

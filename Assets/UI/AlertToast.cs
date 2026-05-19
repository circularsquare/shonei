using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// AlertToast — transient on-screen renderer for EventFeed.Category.Alert events.
//
// Subscribes to EventFeed.OnEntry and shows the most recent N alert messages as a
// vertical stack of fading text rows. Designed to sit above ChatPanel in the
// bottom-left of the HUD. The chat list continues to receive every entry as
// persistent history; this toast is the brief, eye-catching surface for errors
// and important notifications (placement failures, breakdowns, etc).
//
// Notes:
//  - Uses Time.unscaledTime so toasts still fade when the game is paused.
//  - Dedupes consecutive identical messages by resetting the existing row's timer,
//    preventing spam from rapid invalid clicks.
//  - Rich-text color tags from the entry text are preserved (TextMeshProUGUI).
//  - Subscribes in Start (not Awake) per SPEC-eventfeed.md to avoid Awake-order races.
public class AlertToast : MonoBehaviour {
    // Container that rows are spawned into. Should have a VerticalLayoutGroup with
    // LowerLeft alignment so new rows appear at the bottom (closest to chat) and
    // older rows push upward. Defaults to `transform` if left null in the inspector.
    [SerializeField] Transform rowContainer;

    // Per-row lifetime before fade begins (seconds, real time).
    const float Lifetime     = 4f;
    const float FadeDuration = 0.5f;
    const int   MaxRows      = 3;

    class Row {
        public GameObject       go;
        public CanvasGroup      cg;
        public string           text;
        public float            spawnUnscaled;
    }

    readonly List<Row> rows = new List<Row>();

    void Start() {
        if (rowContainer == null) rowContainer = transform;
        if (EventFeed.instance == null) {
            Debug.LogError("[AlertToast] EventFeed.instance is null at Start — no alerts will render.");
            return;
        }
        EventFeed.instance.OnEntry += HandleEntry;
    }

    void OnDestroy() {
        if (EventFeed.instance != null) EventFeed.instance.OnEntry -= HandleEntry;
    }

    void HandleEntry(EventFeed.Entry e) {
        if (e.category != EventFeed.Category.Alert) return;

        // Dedupe: if the newest visible row already shows this text, just reset its timer
        // instead of stacking a duplicate row. Keeps rapid repeated clicks from spamming.
        if (rows.Count > 0) {
            Row last = rows[rows.Count - 1];
            if (last.text == e.text) {
                last.spawnUnscaled = Time.unscaledTime;
                last.cg.alpha = 1f;
                return;
            }
        }

        // Cap row count: drop the oldest before spawning a new one.
        while (rows.Count >= MaxRows) {
            Destroy(rows[0].go);
            rows.RemoveAt(0);
        }

        // Row construction follows TradingPanel.AddChat's pattern so visual style
        // stays consistent across the two EventFeed renderers (same default font
        // from TMP Settings, same 16pt, same wrap behaviour).
        GameObject go = new GameObject("ToastRow", typeof(RectTransform));
        go.transform.SetParent(rowContainer, false);
        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text               = e.text;
        tmp.fontSize           = 16;
        tmp.enableWordWrapping = true;
        tmp.color              = Color.white;
        tmp.richText           = true;
        ContentSizeFitter csf = go.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        CanvasGroup cg = go.AddComponent<CanvasGroup>();
        cg.alpha = 1f;

        rows.Add(new Row {
            go            = go,
            cg            = cg,
            text          = e.text,
            spawnUnscaled = Time.unscaledTime,
        });

        if (rowContainer is RectTransform rt) LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
    }

    void Update() {
        if (rows.Count == 0) return;
        float now = Time.unscaledTime;
        // Iterate in reverse so RemoveAt doesn't shift indices we haven't visited.
        for (int i = rows.Count - 1; i >= 0; i--) {
            Row r = rows[i];
            float age = now - r.spawnUnscaled;
            if (age >= Lifetime + FadeDuration) {
                Destroy(r.go);
                rows.RemoveAt(i);
                continue;
            }
            if (age >= Lifetime) {
                r.cg.alpha = 1f - (age - Lifetime) / FadeDuration;
            }
        }
    }
}

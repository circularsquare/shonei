using UnityEngine;

// ChatRowFader — fades a stale chat-log row out, then reveals it again on demand.
//
// Attached to every runtime-built ChatRow by TradingPanel.AddChat. A row stays
// fully opaque for StableSeconds, then fades to transparent over FadeSeconds so
// old messages stop cluttering the HUD. The fade never destroys the row —
// focusing the chat input snaps every row back to full opacity so the player can
// read the whole backlog; releasing focus resumes the age-based fade.
//
// Alpha is recomputed from the row's age every frame rather than stepped, so a
// row fades correctly even if its GameObject spent part of its life inactive
// (chat panel closed). Uses Time.unscaledTime to match AlertToast — the fade
// keeps progressing while the game is paused.
public class ChatRowFader : MonoBehaviour {
    const float StableSeconds = 60f;  // fully-opaque window before the fade starts
    const float FadeSeconds   = 5f;   // fade-out duration once the row is stale

    CanvasGroup cg;
    float       spawnUnscaled;

    void Awake() {
        cg            = gameObject.AddComponent<CanvasGroup>();
        spawnUnscaled = Time.unscaledTime;
    }

    void Update() {
        cg.alpha = ChatInputFocused() ? 1f : AgeAlpha();
    }

    // The whole backlog is revealed while the player is typing in chat.
    bool ChatInputFocused() {
        TradingPanel tp = TradingPanel.instance;
        return tp != null && tp.chatInput != null && tp.chatInput.isFocused;
    }

    // 1 while fresh, ramping down to 0 across the fade window once stale.
    float AgeAlpha() {
        float staleFor = Time.unscaledTime - spawnUnscaled - StableSeconds;
        if (staleFor <= 0f) return 1f;
        return Mathf.Clamp01(1f - staleFor / FadeSeconds);
    }
}

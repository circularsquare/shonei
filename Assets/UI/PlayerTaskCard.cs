using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

// PlayerTaskCard — the persistent on-screen "Tasks" card. A thin view over
// PlayerTaskController: on a throttled tick it shows the current task's title and
// progress, and hides the frame entirely when onboarding is complete (Current ==
// null). Lives in the scene under the UI canvas; the controller that owns task
// state is bootstrapped in code, so this view reads it via the static instance
// rather than a serialized reference.
//
// Self-wires its child widgets by name in Awake (transform "Frame" -> "Content" TMP,
// "Frame" -> CollapsibleHeader) to avoid silent null SerializeField refs. Uses unscaled
// time so it updates while the game is paused (a fresh world pauses after worldgen).
//
// Collapsible: the Frame is a bottom-anchored VerticalLayoutGroup + ContentSizeFitter with
// the header ("task" + dropdown arrow) as sibling 0 and the progress Content below it.
// Clicking the header (its own CollapsibleHeader.IPointerClickHandler) hides the Content, so
// the Frame shrinks to just the header row — which stays pinned at the bottom corner because
// the card grows upward from a bottom pivot. Collapse state persists via SaveSystem (saveKey
// "tasks"). Clicking the Content (not the header) still bubbles to this card's
// OnPointerClick to skip the "complete!" celebration.
public class PlayerTaskCard : MonoBehaviour, IPointerClickHandler {
    public static PlayerTaskCard instance { get; private set; }

    const float CelebrateSeconds = 3f; // how long "complete!" shows before advancing

    GameObject       frame;   // visible woodframe; toggled off when there's no current task
    TextMeshProUGUI  label;   // the Content row (title + progress); excludes the header label
    public CollapsibleHeader header; // the "task" header row; SaveSystem reads/writes open via saveKey
    RectTransform    frameRt;
    string           lastText;
    float            nextCheck;
    bool             celebrating;    // showing "complete!" before stepping to the next task
    float            celebrateUntil; // unscaled time at which the celebration ends

    void Awake() {
        if (instance != null && instance != this) Debug.LogError("two PlayerTaskCards!");
        instance = this;

        frame = transform.Find("Frame")?.gameObject;
        if (frame == null) {
            Debug.LogError("[PlayerTaskCard] missing child 'Frame' — card disabled");
            enabled = false;
            return;
        }
        header = frame.GetComponentInChildren<CollapsibleHeader>(true);
        if (header != null) header.SetTitle("task");
        // Find the progress label specifically under "Content" so we don't grab the
        // header's own TMP. Fallback to any TMP for the pre-restructure scene.
        Transform content = frame.transform.Find("Content");
        label = content != null ? content.GetComponentInChildren<TextMeshProUGUI>(true)
                                : frame.GetComponentInChildren<TextMeshProUGUI>(true);
        frameRt = frame.GetComponent<RectTransform>();
        if (label == null) {
            Debug.LogError("[PlayerTaskCard] no Content TextMeshProUGUI under 'Frame' — card disabled");
            enabled = false;
        }
    }

    // Drives the card on unscaled time so onboarding progresses even while the game
    // is paused (a fresh world pauses after worldgen). Owns the completion →
    // "complete!" celebration → advance flow; the controller just steps the index.
    void Update() {
        if (Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 0.2f;

        PlayerTaskController c = PlayerTaskController.instance;
        PlayerTask t = c?.Current;
        if (t == null) {                       // onboarding complete (or not started)
            if (frame.activeSelf) frame.SetActive(false);
            celebrating = false;
            return;
        }
        if (!frame.activeSelf) frame.SetActive(true);

        if (celebrating) {
            // Hold the "complete!" text, then advance once the timer elapses.
            if (Time.unscaledTime >= celebrateUntil) {
                celebrating = false;
                c.Advance(); // next tick renders the new current task
            }
            return;
        }

        TaskProgress p = t.Progress();
        if (p.Complete) {
            celebrating    = true;
            celebrateUntil = Time.unscaledTime + CelebrateSeconds;
            SetText(t.title + "\n<color=#3B7D3B>complete!</color>");
            return;
        }
        int shown = Mathf.Min(p.current, p.target); // cap display so over-flagging shows 3/3, not 5/3
        SetText(t.title + "\n" + shown + "/" + p.target);
    }

    // Clicking the card while it shows "complete!" skips the rest of the celebration
    // and advances immediately — manual control instead of waiting out CelebrateSeconds.
    // Clicks bubble up from the Frame Image (which must have raycastTarget on) to this
    // handler on the root. No-op when the current task isn't yet complete.
    public void OnPointerClick(PointerEventData eventData) {
        if (!celebrating) return;
        celebrating = false;
        PlayerTaskController.instance?.Advance();
        nextCheck = 0f; // force the next Update to refresh, so the new task shows at once
    }

    // `body` is the title + progress (or "complete!") line(s). The "task" label now
    // lives in the collapsible header, so the Content row shows only the body.
    void SetText(string body) {
        if (body == lastText) return;
        lastText   = body;
        label.text = body;
        if (frameRt != null) LayoutUtil.RebuildImmediate(frameRt);
    }
}

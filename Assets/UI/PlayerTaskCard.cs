using TMPro;
using UnityEngine;

// PlayerTaskCard — the persistent on-screen "Tasks" card. A thin view over
// PlayerTaskController: on a throttled tick it shows the current task's title and
// progress, and hides the frame entirely when onboarding is complete (Current ==
// null). Lives in the scene under the UI canvas; the controller that owns task
// state is bootstrapped in code, so this view reads it via the static instance
// rather than a serialized reference.
//
// Self-wires its child widgets by name in Awake (transform "Frame" -> its TMP) to
// avoid silent null SerializeField refs. Uses unscaled time so it updates while the
// game is paused (a fresh world pauses after worldgen).
public class PlayerTaskCard : MonoBehaviour {
    const float CelebrateSeconds = 3f; // how long "complete!" shows before advancing

    GameObject      frame;   // visible woodframe; toggled off when there's no current task
    TextMeshProUGUI label;
    RectTransform   frameRt;
    string          lastText;
    float           nextCheck;
    bool            celebrating;    // showing "complete!" before stepping to the next task
    float           celebrateUntil; // unscaled time at which the celebration ends

    void Awake() {
        frame = transform.Find("Frame")?.gameObject;
        if (frame == null) {
            Debug.LogError("[PlayerTaskCard] missing child 'Frame' — card disabled");
            enabled = false;
            return;
        }
        label   = frame.GetComponentInChildren<TextMeshProUGUI>(true);
        frameRt = frame.GetComponent<RectTransform>();
        if (label == null) {
            Debug.LogError("[PlayerTaskCard] no TextMeshProUGUI under 'Frame' — card disabled");
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

    // `body` is the title + progress (or "complete!") line(s); a fixed "task:" header
    // is prepended to everything the card shows.
    void SetText(string body) {
        if (body == lastText) return;
        lastText   = body;
        label.text = "task:\n" + body;
        if (frameRt != null) LayoutUtil.RebuildImmediate(frameRt);
    }
}

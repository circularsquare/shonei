using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

// Owns Time.timeScale (Pause/Normal/Fast) AND the render-side frame-rate cap.
// Three caps: active play (60), paused-but-focused (30), backgrounded (5).
// vsync is disabled globally so Application.targetFrameRate is actually honored —
// otherwise high-refresh monitors render uncapped during active play and the
// paused/background drops have no effect on 60 Hz monitors.
// The sim keeps ticking when backgrounded (Application.runInBackground = true,
// set in WorldController) so alt-tabbing doesn't lose game time — the 5 fps cap
// just stops the GPU re-rendering essentially-static frames.
public class TimeController : MonoBehaviour {
    public static TimeController instance { get; protected set; }

    // Last non-zero speed, so Space-to-resume restores 2x if player was at 2x.
    float lastSpeed = 1f;

    // ── Frame-rate caps ──────────────────────────────────────────────────────
    const int activeFps     = 60;
    const int pausedFps     = 30;
    const int backgroundFps = 30;
    bool _isFocused = true;

    void Awake() {
        if (instance != null) { Debug.LogError("there should only be one TimeController"); }
        instance = this;
        QualitySettings.vSyncCount = 0;
        ApplyTargetFrameRate();
    }

    void Update() {
        if (Input.GetKeyDown(KeyCode.Space) && !IsTypingInField()) {
            TogglePause();
        }
    }

    void OnApplicationFocus(bool focused) {
        _isFocused = focused;
        ApplyTargetFrameRate();
    }

    // Recomputes the cap from current state. Called on every transition that
    // could change it (focus, pause toggle, speed change).
    void ApplyTargetFrameRate() {
        // Belt-and-braces: focus events can be flaky on some Windows configs.
        bool focused = _isFocused && Application.isFocused;
        int target = !focused ? backgroundFps
                   : (Time.timeScale > 0f ? activeFps : pausedFps);
        Application.targetFrameRate = target;
    }

    public void SetSpeed(float scale) {
        Time.timeScale = scale;
        Time.fixedDeltaTime = 0.02f * scale;
        if (scale > 0f) lastSpeed = scale;
        ApplyTargetFrameRate();
    }

    public void Pause()       { SetSpeed(0f); }
    public void NormalSpeed() { SetSpeed(1f); }
    public void FastSpeed()   { SetSpeed(2f); }

    public void TogglePause() {
        if (Time.timeScale > 0f) Pause();
        else SetSpeed(lastSpeed);
    }

    // Don't steal Space from text entry (e.g. trading panel, save slot names).
    static bool IsTypingInField() {
        var sel = EventSystem.current?.currentSelectedGameObject;
        if (sel == null) return false;
        return sel.GetComponent<InputField>() != null
            || sel.GetComponent<TMP_InputField>() != null;
    }
}

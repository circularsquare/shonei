using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

public class TimeController : MonoBehaviour {
    public static TimeController instance { get; protected set; }

    // Last non-zero speed, so Space-to-resume restores 2x if player was at 2x.
    float lastSpeed = 1f;

    void Awake() {
        if (instance != null) { Debug.LogError("there should only be one TimeController"); }
        instance = this;
    }

    void Update() {
        if (Input.GetKeyDown(KeyCode.Space) && !IsTypingInField()) {
            TogglePause();
        }
    }

    public void SetSpeed(float scale) {
        Time.timeScale = scale;
        Time.fixedDeltaTime = 0.02f * scale;
        if (scale > 0f) lastSpeed = scale;
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

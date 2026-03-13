using UnityEngine;

public class TimeController : MonoBehaviour {
    public static TimeController instance;

    void Awake() {
        if (instance != null) { Debug.LogError("there should only be one TimeController"); }
        instance = this;
    }

    public void SetSpeed(float scale) {
        Time.timeScale = scale;
        Time.fixedDeltaTime = 0.02f * scale;
    }

    public void Pause()       { SetSpeed(0f); }
    public void NormalSpeed() { SetSpeed(1f); }
    public void FastSpeed()   { SetSpeed(2f); }
}

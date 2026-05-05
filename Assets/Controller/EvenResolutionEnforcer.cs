using UnityEngine;

// Snaps the standalone player's window to even pixel dimensions when the user
// drag-resizes it. Without this, an odd-numbered window size (e.g. 1558×755)
// breaks the PixelPerfectCamera's integer-scale path — the camera falls back
// to fractional sampling and sprites blur. The PPC itself prints a warning in
// development builds but doesn't fix the resolution.
//
// Auto-spawns on first scene load via RuntimeInitializeOnLoadMethod so no
// scene wiring is required. Skipped in the editor (Game view handles its own
// sizing) and in fullscreen modes (resolution is OS-managed, not drag-sized).
public class EvenResolutionEnforcer : MonoBehaviour {
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoSpawn() {
        if (Application.isEditor) return;
        var go = new GameObject("EvenResolutionEnforcer");
        go.AddComponent<EvenResolutionEnforcer>();
        DontDestroyOnLoad(go);
    }

    int lastW, lastH;

    void Start() {
        lastW = Screen.width;
        lastH = Screen.height;
    }

    void Update() {
        if (Screen.fullScreen) return;
        int w = Screen.width;
        int h = Screen.height;
        if (w == lastW && h == lastH) return;
        // `& ~1` clears the low bit — same as floor-to-even but cheaper and
        // (arguably) clearer once you've seen the idiom.
        int evenW = w & ~1;
        int evenH = h & ~1;
        if (evenW != w || evenH != h) {
            Screen.SetResolution(evenW, evenH, Screen.fullScreenMode);
        }
        lastW = evenW;
        lastH = evenH;
    }
}

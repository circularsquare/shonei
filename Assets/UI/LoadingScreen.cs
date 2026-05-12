using System.Diagnostics;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

// Lightweight per-phase load-time reporter. Surfaces a fullscreen overlay
// during the play-mode startup sequence so you can see which phase the load
// is currently sitting in. On phase transitions, only logs a console line
// if the completed phase took longer than SlowPhaseMs — fast phases stay
// silent so the console isn't spammed on normal loads.
//
// Call sites are sprinkled through the startup sequence so the user can spot
// the slow link by reading the console after entering play mode. See
// WorldController.Start, SaveSystem.Load / .PostLoadInit, TileMeshController
// for the instrumented boundaries.
//
// Usage:
//   LoadingScreen.Begin("Allocating world…");
//   ... work ...
//   LoadingScreen.SetPhase("Subscribing callbacks…");
//   ... work ...
//   LoadingScreen.End();
//
// Begin auto-creates the overlay GameObject + Canvas on first call; End hides
// it. All static state is cleared on SubsystemRegistration so Reload-Domain-off
// play sessions don't leak destroyed Unity refs.
public class LoadingScreen : MonoBehaviour {
    const long SlowPhaseMs = 1000; // only log phases that exceed this threshold

    static LoadingScreen instance;

    Canvas    canvas;
    TMP_Text  label;
    Stopwatch totalSw;
    Stopwatch phaseSw;
    string    currentPhase;
    // Mini history shown on the overlay — keeps the last few completed phases
    // visible so the user can see the chain in real time without alt-tabbing
    // to the Console.
    readonly StringBuilder historyText = new();

    public static void Begin(string firstPhase) {
        Ensure();
        instance.totalSw      = Stopwatch.StartNew();
        instance.phaseSw      = Stopwatch.StartNew();
        instance.currentPhase = firstPhase;
        instance.historyText.Clear();
        instance.RefreshLabel();
        instance.canvas.enabled = true;
    }

    public static void SetPhase(string phase) {
        if (instance == null || instance.totalSw == null) return; // Begin was never called
        long phaseMs = instance.phaseSw.ElapsedMilliseconds;
        long totalMs = instance.totalSw.ElapsedMilliseconds;
        if (phaseMs > SlowPhaseMs) Debug.Log($"[Load] slow phase: {instance.currentPhase} — {phaseMs} ms (total {totalMs} ms)");
        // Keep the last 6 completed phases on screen.
        // m5x7 only ships basic ASCII glyphs — anything beyond a-z/0-9/common
        // punctuation falls back and emits a console warning. Stick to plain ASCII.
        instance.historyText.AppendLine($"<color=#888>- {instance.currentPhase}  ({phaseMs} ms)</color>");
        TrimHistory();
        instance.currentPhase = phase;
        instance.phaseSw.Restart();
        instance.RefreshLabel();
    }

    public static void End() {
        if (instance == null || instance.totalSw == null) return;
        long phaseMs = instance.phaseSw.ElapsedMilliseconds;
        long totalMs = instance.totalSw.ElapsedMilliseconds;
        if (phaseMs > SlowPhaseMs) Debug.Log($"[Load] slow phase: {instance.currentPhase} — {phaseMs} ms (total {totalMs} ms)");
        instance.canvas.enabled = false;
        instance.totalSw = null;
    }

    void RefreshLabel() {
        long phaseMs = phaseSw?.ElapsedMilliseconds ?? 0;
        long totalMs = totalSw?.ElapsedMilliseconds ?? 0;
        label.text = $"{historyText}\n<b>{currentPhase}</b>  ({phaseMs} ms)\n\n<size=18><color=#888>{totalMs} ms total</color></size>";
    }

    // Per-frame label refresh so the elapsed-time counter ticks visibly even
    // when no SetPhase call lands for a while. Cheap — just a string format.
    void Update() {
        if (totalSw != null && canvas.enabled) RefreshLabel();
    }

    static void TrimHistory() {
        // Keep the StringBuilder bounded — strip oldest line when >6 entries.
        // Cheap split since this fires once per phase, not per frame.
        var s = instance.historyText.ToString();
        var lines = s.Split('\n');
        int kept = 6;
        if (lines.Length <= kept + 1) return; // +1 for trailing empty after AppendLine
        instance.historyText.Clear();
        for (int i = lines.Length - kept - 1; i < lines.Length; i++) {
            if (i < 0) continue;
            if (i < lines.Length - 1) instance.historyText.AppendLine(lines[i]);
            else                       instance.historyText.Append(lines[i]);
        }
    }

    static void Ensure() {
        if (instance != null) return;
        var go = new GameObject("LoadingScreen");
        DontDestroyOnLoad(go); // surviving scene loads is harmless and avoids null refs mid-load
        instance = go.AddComponent<LoadingScreen>();
        instance.BuildUI();
    }

    void BuildUI() {
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000; // above gameplay UI
        gameObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

        // Dim background that fully covers the camera.
        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(transform, false);
        var bg = bgGO.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.85f);
        var bgRt = bg.rectTransform;
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;
        bg.raycastTarget = false;

        // Label — centred.
        var labelGO = new GameObject("Label");
        labelGO.transform.SetParent(transform, false);
        label = labelGO.AddComponent<TextMeshProUGUI>();
        label.fontSize = 22;
        label.color = Color.white;
        label.alignment = TextAlignmentOptions.Center;
        label.richText = true;
        label.text = "Loading…";
        label.raycastTarget = false;
        var lRt = label.rectTransform;
        lRt.anchorMin = new Vector2(0.5f, 0.5f);
        lRt.anchorMax = new Vector2(0.5f, 0.5f);
        lRt.sizeDelta = new Vector2(800, 400);
        lRt.anchoredPosition = Vector2.zero;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; }
}

using System.Diagnostics;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

// Fullscreen overlay shown during the play-mode startup sequence. Two modes:
//
//   • Simplified (default): just "loading..." and a fill bar that advances as the
//     load progresses. This is what players should see.
//   • Detailed (dev): a per-phase timing breakdown — which phase the load is sitting
//     in and how long each took — for spotting the slow link by reading the overlay
//     or the console. Toggle via the Detailed flag.
//
// Simplified is the default everywhere (players and editor). Press Ctrl+D during a load
// to switch to the detailed breakdown — a dev escape hatch that needs no build flag.
//
// Call sites are sprinkled through the startup sequence — see WorldController.Start,
// SaveSystem.Load / .PostLoadInit, TileMeshController for the instrumented boundaries.
//
// Usage:
//   LoadingScreen.Begin("Allocating world…");
//   ... work ...
//   LoadingScreen.SetPhase("Subscribing callbacks…");
//   ... work ...
//   LoadingScreen.End();
//
// Begin auto-creates the overlay GameObject + Canvas on first call; End hides it. All
// static state is cleared on SubsystemRegistration so Reload-Domain-off play sessions
// don't leak destroyed Unity refs.
public class LoadingScreen : MonoBehaviour {
    const long SlowPhaseMs = 1000; // only log phases that exceed this threshold

    // false → simplified player screen; true → per-phase dev breakdown. Reset to false at
    // the start of every load (see Begin); Ctrl+D opts into detailed for the current load
    // only (see Update), so it never lingers into the next one.
    public static bool Detailed = false;

    static LoadingScreen instance;

    // True while the overlay is up (between Begin and End). Other systems gate on this —
    // the game is paused during a load (see Begin), and autosave skips so a slow/hung load
    // can't persist a half-built world (see SaveSystem.Update).
    public static bool IsActive => instance != null && instance.canvas != null && instance.canvas.enabled;

    Canvas canvas;

    // ── Detailed (dev) UI ──
    GameObject detailedGroup;
    TMP_Text   label;
    // Mini history shown on the overlay — the last few completed phases, so the chain
    // is visible in real time without alt-tabbing to the Console.
    readonly StringBuilder historyText = new();

    // ── Simplified (player) UI ──
    GameObject    simpleGroup;
    RectTransform barFillRt;
    float         trackInnerWidth;

    Stopwatch totalSw;
    Stopwatch phaseSw;
    string    currentPhase;

    // Load progress 0..1. targetProgress advances a fraction of the remaining distance
    // on each phase (robust without knowing the exact phase count); the displayed
    // progress eases toward it in Update, and End snaps both to 100%.
    float progress;
    float targetProgress;

    public static void Begin(string firstPhase) {
        Ensure();
        Detailed = false; // every load starts simplified; Ctrl+D opts in for this load only
        instance.totalSw      = Stopwatch.StartNew();
        instance.phaseSw      = Stopwatch.StartNew();
        instance.currentPhase = firstPhase;
        instance.historyText.Clear();
        instance.progress       = 0f;
        instance.targetProgress = 0f;
        instance.detailedGroup.SetActive(Detailed);
        instance.simpleGroup.SetActive(!Detailed);
        instance.RefreshLabel();
        instance.ApplyBar();
        instance.canvas.enabled = true;
        // Pause the sim while loading: a load that hangs (or just runs across several frames)
        // shouldn't tick the world or fire an autosave. End() deliberately does NOT resume —
        // the post-load speed is path-dependent (new worlds stay paused for the settlement
        // popup; loaded worlds resume in WorldController.Start) — so it's set there, not here.
        TimeController.instance?.Pause();
    }

    public static void SetPhase(string phase) {
        if (instance == null || instance.totalSw == null) return; // Begin was never called
        long phaseMs = instance.phaseSw.ElapsedMilliseconds;
        long totalMs = instance.totalSw.ElapsedMilliseconds;
        if (phaseMs > SlowPhaseMs) Debug.Log($"[Load] slow phase: {instance.currentPhase} — {phaseMs} ms (total {totalMs} ms)");
        // Keep the last 6 completed phases on screen (detailed mode).
        // m5x7 only ships basic ASCII glyphs — anything beyond a-z/0-9/common
        // punctuation falls back and emits a console warning. Stick to plain ASCII.
        instance.historyText.AppendLine($"<color=#888>- {instance.currentPhase}  ({phaseMs} ms)</color>");
        TrimHistory();
        instance.currentPhase = phase;
        instance.phaseSw.Restart();
        // Ease the bar toward full by a fraction of what's left — always moves forward
        // and approaches 1 without a known phase total; End fills the rest.
        instance.targetProgress = Mathf.Lerp(instance.targetProgress, 1f, 0.4f);
        instance.RefreshLabel();
    }

    public static void End() {
        if (instance == null || instance.totalSw == null) return;
        long phaseMs = instance.phaseSw.ElapsedMilliseconds;
        long totalMs = instance.totalSw.ElapsedMilliseconds;
        if (phaseMs > SlowPhaseMs) Debug.Log($"[Load] slow phase: {instance.currentPhase} — {phaseMs} ms (total {totalMs} ms)");
        instance.progress = instance.targetProgress = 1f;
        instance.ApplyBar();
        instance.canvas.enabled = false;
        instance.totalSw = null;
    }

    void RefreshLabel() {
        if (!Detailed || label == null) return;
        long phaseMs = phaseSw?.ElapsedMilliseconds ?? 0;
        long totalMs = totalSw?.ElapsedMilliseconds ?? 0;
        label.text = $"{historyText}\n<b>{currentPhase}</b>  ({phaseMs} ms)\n\n<size=18><color=#888>{totalMs} ms total</color></size>";
    }

    // Size the fill bar to the current progress (left-anchored, so its width = the
    // filled fraction of the track's inner width).
    void ApplyBar() {
        if (barFillRt == null) return;
        barFillRt.sizeDelta = new Vector2(trackInnerWidth * Mathf.Clamp01(progress), barFillRt.sizeDelta.y);
    }

    // Per-frame: tick the detailed timer, and ease the fill bar toward its target so it
    // animates between phases on frames that actually render (the load yields a few).
    void Update() {
        if (totalSw == null || !canvas.enabled) return;
        // Esc during a load bails back to the main menu — an escape hatch if a load is slow
        // or stuck. Nothing is lost (the world is still loading), so unlike the in-game "main
        // menu" button this needs no confirm. Restore normal speed first so the menu (no
        // TimeController of its own) doesn't inherit the loading pause, and End() so the
        // overlay doesn't linger over the menu (LoadingScreen is DontDestroyOnLoad).
        if (Input.GetKeyDown(KeyCode.Escape)) {
            Debug.Log("[LoadingScreen] Esc during load — returning to main menu.");
            Time.timeScale = 1f;
            End();
            SceneManager.LoadScene("Menu");
            return;
        }
        // Ctrl+D reveals the detailed dev breakdown mid-load (and back).
        if (Input.GetKeyDown(KeyCode.D) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            SetDetailed(!Detailed);
        if (Detailed) { RefreshLabel(); return; }
        progress = Mathf.MoveTowards(progress, targetProgress, Time.unscaledDeltaTime * 2.5f);
        ApplyBar();
    }

    void SetDetailed(bool on) {
        Detailed = on;
        detailedGroup.SetActive(on);
        simpleGroup.SetActive(!on);
        RefreshLabel();
        ApplyBar();
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

        BuildDetailedUI();
        BuildSimpleUI();
    }

    // The per-phase timing readout (dev mode) — one centred rich-text label.
    void BuildDetailedUI() {
        detailedGroup = new GameObject("Detailed", typeof(RectTransform));
        detailedGroup.transform.SetParent(transform, false);
        Stretch(detailedGroup);

        var labelGO = new GameObject("Label");
        labelGO.transform.SetParent(detailedGroup.transform, false);
        label = labelGO.AddComponent<TextMeshProUGUI>();
        label.fontSize = 22;
        label.color = Color.white;
        label.alignment = TextAlignmentOptions.Center;
        label.richText = true;
        label.text = "loading...";
        label.raycastTarget = false;
        var lRt = label.rectTransform;
        lRt.anchorMin = new Vector2(0.5f, 0.5f);
        lRt.anchorMax = new Vector2(0.5f, 0.5f);
        lRt.sizeDelta = new Vector2(800, 400);
        lRt.anchoredPosition = Vector2.zero;
    }

    // The player-facing screen — "loading..." over a fill bar.
    void BuildSimpleUI() {
        simpleGroup = new GameObject("Simple", typeof(RectTransform));
        simpleGroup.transform.SetParent(transform, false);
        Stretch(simpleGroup);

        // "loading..." text, just above the bar.
        var textGO = new GameObject("Label");
        textGO.transform.SetParent(simpleGroup.transform, false);
        var t = textGO.AddComponent<TextMeshProUGUI>();
        t.fontSize = 24;
        t.color = Color.white;
        t.alignment = TextAlignmentOptions.Center;
        t.text = "loading...";
        t.raycastTarget = false;
        var tRt = t.rectTransform;
        tRt.anchorMin = tRt.anchorMax = new Vector2(0.5f, 0.5f);
        tRt.sizeDelta = new Vector2(400, 40);
        tRt.anchoredPosition = new Vector2(0, 18);

        // Bar track.
        const float trackWidth = 360f, trackHeight = 16f, pad = 3f;
        trackInnerWidth = trackWidth - pad * 2f;
        var trackGO = new GameObject("BarTrack");
        trackGO.transform.SetParent(simpleGroup.transform, false);
        var track = trackGO.AddComponent<Image>();
        track.sprite = Resources.Load<Sprite>("Sprites/Misc/slider_bg");
        track.type = Image.Type.Sliced;
        track.raycastTarget = false;
        var trRt = track.rectTransform;
        trRt.anchorMin = trRt.anchorMax = new Vector2(0.5f, 0.5f);
        trRt.sizeDelta = new Vector2(trackWidth, trackHeight);
        trRt.anchoredPosition = new Vector2(0, -12);

        // Bar fill — left-anchored; ApplyBar drives its width from progress.
        var fillGO = new GameObject("BarFill");
        fillGO.transform.SetParent(trackGO.transform, false);
        var fill = fillGO.AddComponent<Image>();
        fill.sprite = Resources.Load<Sprite>("Sprites/Misc/slider_fill");
        fill.type = Image.Type.Sliced;
        fill.raycastTarget = false;
        barFillRt = fill.rectTransform;
        barFillRt.anchorMin = barFillRt.anchorMax = new Vector2(0f, 0.5f);
        barFillRt.pivot = new Vector2(0f, 0.5f);
        barFillRt.sizeDelta = new Vector2(0f, trackHeight - pad * 2f);
        barFillRt.anchoredPosition = new Vector2(pad, 0f);
    }

    // Anchor a child RectTransform to fill its parent.
    static void Stretch(GameObject go) {
        var rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; }
}

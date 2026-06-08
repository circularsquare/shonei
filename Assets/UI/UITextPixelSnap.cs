using System.Collections;
using TMPro;
using UnityEngine;

// Runtime UI text styling, driven off TMP's global TEXT_CHANGED event so existing AND
// dynamically-spawned text stay in sync. Two jobs:
//
//   1. Font choice — stamps the player's selected font + size (SettingsManager.uiFontIndex →
//      UIFontOptions entry) onto every overlay UI label. Falls back to FontConfig (the editor-
//      baked default) if the registry is missing. Changing the choice (and startup) triggers a
//      strong full refresh — re-font + clean mesh regen of every label (RefreshAll).
//   2. Pixel snap — snaps each text line's baseline to a whole device pixel so labels share a
//      consistent sub-pixel phase (SDF renders each phase with different blur otherwise). The
//      shift is uniform per line, so glyph shapes are untouched. Gated by FontConfig.pixelSnap.
//      (Canvas.pixelPerfect does NOT snap TMP here — don't use it.)
//
// Per-regen order is "fix font first, else snap": if a text's font is wrong, setting it dirties
// layout and re-fires TEXT_CHANGED next frame, so we bail and snap then, on the corrected mesh.
// World-space text (chat bubbles) is left alone. Self-bootstraps — no scene wiring.
//
// NB: named for the snap, but now also owns the runtime font swap — a rename (e.g.
// UITextRuntimeStyle) would read clearer.
public class UITextPixelSnap : MonoBehaviour {
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap() {
        if (FindObjectOfType<UITextPixelSnap>() != null) return;
        new GameObject("UITextPixelSnap").AddComponent<UITextPixelSnap>();
    }

    static UITextPixelSnap _instance;
    bool _lastSnapEnabled = true;
    int  _lastFontIndex   = -1;
    bool _refreshScheduled;

    void Awake() { _instance = this; }

    void OnEnable() {
        TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);
        if (SettingsManager.instance != null) SettingsManager.instance.OnChanged += OnSettingsChanged;
    }
    void OnDisable() {
        TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);
        if (SettingsManager.instance != null) SettingsManager.instance.OnChanged -= OnSettingsChanged;
    }

    void Start() {
        _lastSnapEnabled = SnapEnabled();
        _lastFontIndex   = SettingsManager.instance != null ? SettingsManager.instance.uiFontIndex : 0;
        // Apply font + snap to any text already built before we subscribed.
        foreach (var t in FindObjectsOfType<TextMeshProUGUI>()) Process(t);
        // Text generated during the chaotic load frames can render with stale/invisible meshes,
        // and a partial font mix garbles. Once the UI settles, do strong full regen passes.
        StartCoroutine(InitialRefresh());
    }

    IEnumerator InitialRefresh() {
        yield return null;                                 // let the first build happen
        RefreshAll();
        yield return new WaitForSecondsRealtime(0.5f);     // catch slightly-later builds + settle
        RefreshAll();
    }

    void OnTextChanged(UnityEngine.Object obj) {
        Process(obj as TextMeshProUGUI);
    }

    static void Process(TextMeshProUGUI t) {
        if (t == null) return;
        var c = t.canvas;
        if (c == null || c.renderMode != RenderMode.ScreenSpaceOverlay) return;   // overlay UI only
        if (ApplyFont(t)) {
            // A font actually changed — this is freshly-built/baked content (e.g. a panel's rows
            // built on open) being swapped onto the chosen font. A lone swap can leave a transient
            // two-font mix that garbles, so schedule a debounced strong refresh to clean the UI.
            if (_instance != null) _instance.ScheduleRefresh();
            return;
        }
        Snap(t);
    }

    // Debounced: coalesces a burst of swaps (a whole panel's worth) into one strong refresh,
    // a couple frames later (once the content has finished building).
    void ScheduleRefresh() {
        if (_refreshScheduled) return;
        _refreshScheduled = true;
        StartCoroutine(DelayedRefresh());
    }
    IEnumerator DelayedRefresh() {
        yield return null;
        yield return null;
        _refreshScheduled = false;
        RefreshAll();
    }

    // ── Runtime font choice ──────────────────────────────────────────────────

    void OnSettingsChanged() {
        var sm = SettingsManager.instance;
        if (sm == null || sm.uiFontIndex == _lastFontIndex) return;
        _lastFontIndex = sm.uiFontIndex;
        RefreshAll();
    }

    // Stamp the chosen font/size onto one label. Returns true if it changed something (which
    // queues a regen). TMP's setters no-op when the value is unchanged, so this is cheap.
    static bool ApplyFont(TextMeshProUGUI t) {
        var font = ChosenFont();
        if (font == null) return false;
        float size = ChosenSize();
        bool changed = false;
        if (t.font != font)                         { t.font = font;     changed = true; }
        if (!Mathf.Approximately(t.fontSize, size)) { t.fontSize = size; changed = true; }
        return changed;
    }

    static UIFontOptions.Entry ChosenEntry() {
        var opt = UIFontOptions.instance;
        var sm  = SettingsManager.instance;
        if (opt == null || sm == null) return null;
        var e = opt.Get(sm.uiFontIndex);
        return (e != null && e.font != null) ? e : null;
    }
    static TMP_FontAsset ChosenFont() {
        var e = ChosenEntry();
        if (e != null) return e.font;
        var cfg = FontConfig.instance;          // fallback: editor-baked default
        return cfg != null ? cfg.font : null;
    }
    static float ChosenSize() {
        var e = ChosenEntry();
        if (e != null) return e.size;
        var cfg = FontConfig.instance;
        return cfg != null ? cfg.fontSize : 16f;
    }

    // Strong full-UI refresh: stamp the chosen font/size onto every overlay label + input field
    // (incl. inactive) AND force a clean mesh regen, then rebuild layouts. Used on font-choice
    // change and at startup. The ForceMeshUpdate is what fixes startup garble — text generated
    // during load can have stale/invisible meshes and a partial two-font mix; a clean regen with
    // a single font resolves both. Heavy, but only runs on rare events (startup, font switch).
    void RefreshAll() {
        var font = ChosenFont();
        if (font == null) { Debug.LogError("[UITextPixelSnap] No font to apply (registry + FontConfig both empty)."); return; }
        float size = ChosenSize();

        foreach (var o in Resources.FindObjectsOfTypeAll(typeof(TextMeshProUGUI))) {
            var t = (TextMeshProUGUI)o;
            if (!t.gameObject.scene.IsValid() || t.hideFlags != HideFlags.None) continue;
            var canvas = t.GetComponentInParent<Canvas>(true);   // cached .canvas is null while inactive
            if (canvas == null || canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
            if (t.font != font)                         t.font = font;
            if (!Mathf.Approximately(t.fontSize, size)) t.fontSize = size;
            t.ForceMeshUpdate(true, false);                      // clean regen even if inactive
        }
        // Input fields re-apply m_GlobalFontAsset to their child text, so keep it in sync too.
        foreach (var o in Resources.FindObjectsOfTypeAll(typeof(TMP_InputField))) {
            var f = (TMP_InputField)o;
            if (!f.gameObject.scene.IsValid() || f.hideFlags != HideFlags.None) continue;
            if (f.fontAsset != font) f.fontAsset = font;
        }

        var ui = FindObjectOfType<UI>();
        var rootRect = ui != null ? ui.GetComponent<RectTransform>() : null;
        if (rootRect != null) LayoutUtil.RebuildImmediate(rootRect);
    }

    // ── Pixel snap ───────────────────────────────────────────────────────────

    static bool SnapEnabled() {
        var cfg = FontConfig.instance;
        return cfg == null || cfg.pixelSnap;   // default on if config missing
    }

    // Toggling FontConfig.pixelSnap re-renders all overlay text so the change applies live.
    void LateUpdate() {
        bool en = SnapEnabled();
        if (en == _lastSnapEnabled) return;
        _lastSnapEnabled = en;
        ForceRerenderAll();
    }

    static void ForceRerenderAll() {
        foreach (var t in FindObjectsOfType<TextMeshProUGUI>()) {
            var c = t.canvas;
            if (c != null && c.renderMode == RenderMode.ScreenSpaceOverlay) t.ForceMeshUpdate();
        }
    }

    static void Snap(TextMeshProUGUI t) {
        if (!SnapEnabled()) return;
        var info = t.textInfo;
        if (info == null || info.characterCount == 0 || info.lineCount == 0) return;

        float sy = t.rectTransform.lossyScale.y;
        if (sy < 0.0001f) return;
        float originY = t.rectTransform.position.y;

        bool changed = false;
        for (int li = 0; li < info.lineCount; li++) {
            var line = info.lineInfo[li];
            int fvc = line.firstVisibleCharacterIndex;
            if (fvc < 0 || fvc >= info.characterCount) continue;

            float baseLocalY      = info.characterInfo[fvc].baseLine;
            float deviceBaselineY = originY + baseLocalY * sy;
            float delta           = Mathf.Round(deviceBaselineY) - deviceBaselineY;
            if (Mathf.Abs(delta) < 0.001f) continue;
            float deltaLocal      = delta / sy;

            for (int ci = line.firstCharacterIndex; ci <= line.lastCharacterIndex; ci++) {
                var ch = info.characterInfo[ci];
                if (!ch.isVisible) continue;
                var verts = info.meshInfo[ch.materialReferenceIndex].vertices;
                int vi = ch.vertexIndex;
                verts[vi + 0].y += deltaLocal;
                verts[vi + 1].y += deltaLocal;
                verts[vi + 2].y += deltaLocal;
                verts[vi + 3].y += deltaLocal;
            }
            changed = true;
        }
        if (changed) t.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);
    }
}

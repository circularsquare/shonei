using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

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
// Per-regen order is "fix font first, else snap": if a label's font is wrong, we set it and
// regenerate the mesh synchronously in the same frame (see Process) so it's born in the chosen
// font with no flicker; the nested TEXT_CHANGED then re-enters with the font correct and snaps.
// World-space text (chat bubbles) is left alone. Self-bootstraps — no scene wiring.
public class UITextRuntimeStyle : MonoBehaviour {
    // The styler is a per-scene object: it subscribes to the scene's SettingsManager and
    // styles that scene's text. It is NOT DontDestroyOnLoad, so a single-mode scene load
    // destroys it — we re-create one in each newly loaded scene. AfterSceneLoad seeds the
    // first scene; sceneLoaded covers every load after (e.g. Menu -> Main). Without this,
    // loading the game from the menu left the game scene with no styler: text rendered in
    // its authored font mix and the options font switch had no live listener.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap() {
        EnsureInstance();
        // -= then += guarantees exactly one subscription even with Reload Domain off, where
        // this static event subscription would otherwise persist and stack across play sessions.
        SceneManager.sceneLoaded -= OnSceneLoadedStatic;
        SceneManager.sceneLoaded += OnSceneLoadedStatic;
    }

    static void OnSceneLoadedStatic(Scene scene, LoadSceneMode mode) => EnsureInstance();

    static void EnsureInstance() {
        if (FindObjectOfType<UITextRuntimeStyle>() != null) return;
        new GameObject("UITextRuntimeStyle").AddComponent<UITextRuntimeStyle>();
    }

    static bool _applyingFont;          // re-entrancy guard for the inline regen in Process
    bool _lastSnapEnabled = true;
    int  _lastFontIndex   = -1;

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
            // Font was wrong — this is freshly-built content (e.g. a panel's rows on open) born in
            // the prefab-baked font. TEXT_CHANGED fires during the canvas pre-render pass, AFTER the
            // (wrong-font) mesh is committed to the CanvasRenderer but BEFORE the GPU draws this
            // frame, so regenerating synchronously here replaces it with the chosen font in-frame —
            // killing the one-frame flicker AND any lingering two-font mix without a deferred pass.
            // The guard stops the nested TEXT_CHANGED (from ForceMeshUpdate) from re-forcing.
            if (!_applyingFont) {
                _applyingFont = true;
                t.ForceMeshUpdate();
                _applyingFont = false;
            }
            return;
        }
        Snap(t);
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
        if (font == null) { Debug.LogError("[UITextRuntimeStyle] No font to apply (registry + FontConfig both empty)."); return; }
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

using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Full-screen options panel — volume sliders + graphics toggles + keyboard reference.
//
// Reads/writes via SettingsManager.instance — this script is just the UI shell.
// Set up as an exclusive panel: at most one of {Trading, Recipe, Research,
// GlobalHappiness, Options} may be visible at a time (see UI.RegisterExclusive).
//
// Unity setup:
//   OptionsToggle button -> OptionsPanel.instance.Toggle()
//
//   RectTransform for full-screen with margin:
//     Anchor Min=(0,0)  Anchor Max=(1,1)  Left=Right=Top=Bottom=20
//
//   Inspector wiring — drag scene UI elements onto these slots:
//     masterSlider   — Slider, range 0-1
//     sfxSlider      — Slider, range 0-1
//     ambientSlider  — Slider, range 0-1
//     fpsDropdown    — TMP_Dropdown, options exactly: "30", "60", "120", "144", "Unlimited"
//     vsyncToggle    — Toggle
//     lightingToggle — Toggle
//     closeButton    — Button (optional; an X in the corner)
//     controlsText   — TMP_Text (optional); content is auto-filled at Awake.
//                      Relies on m5x7's monospace metrics for column alignment.
public class OptionsPanel : MonoBehaviour {
    public static OptionsPanel instance { get; protected set; }

    [Header("Audio")]
    [SerializeField] Slider masterSlider;
    [SerializeField] Slider sfxSlider;
    [SerializeField] Slider ambientSlider;

    [Header("Graphics")]
    [SerializeField] TMP_Dropdown fpsDropdown;
    [SerializeField] Toggle       vsyncToggle;
    [SerializeField] Toggle       lightingToggle;

    [Header("Misc")]
    [SerializeField] Button closeButton;

    [Header("Controls")]
    [SerializeField] TMP_Text controlsText;

    // Keyboard / mouse reference shown in the panel. Grouped sections separated by a
    // blank `(null, null)` row. Keep order ~stable: general → camera → build → selection.
    // Excludes dev-only shortcuts (Ctrl+D audit, Ctrl+Shift+D instant-deconstruct,
    // Ctrl+T cursor light) — those are debug aids, not player-facing controls.
    static readonly (string keys, string action)[] ControlsRows = {
        ("space",       "pause / resume"),
        ("esc",         "close panel / cancel mode"),
        ("f1",          "toggle ui"),
        (null,          null),
        ("scroll",      "zoom"),
        ("right drag",  "pan camera"),
        (null,          null),
        ("f",           "mirror (build mode)"),
        ("r",           "rotate (build mode)"),
        ("q / e",       "cycle shape (build mode)"),
        ("shift+lmb",   "place more after building (build mode)"),
        (null,          null),
        ("lmb drag",    "drag-select / paint harvest"),
        ("ctrl+lmb",    "add to / remove from selection"),
        ("shift+lmb",   "copy filter (on storage tile)"),
        ("shift+rmb",   "paste filter (on storage tile)"),
    };

    // Dropdown index → fps value. 0 = unlimited (no cap).
    static readonly int[] FpsOptions = { 30, 60, 120, 144, 0 };

    // Set during RefreshFromSettings to suppress the onValueChanged callbacks
    // we'd otherwise fire just by writing the slider/toggle values.
    bool suppressCallbacks;

    void Awake() {
        if (instance != null) { Debug.LogError("two OptionsPanels!"); }
        instance = this;
        UI.RegisterExclusive(gameObject);
        WireUp();
        if (controlsText != null) controlsText.text = BuildControlsText();
    }

    // Builds a column-aligned reference block from ControlsRows. Pads the keys column
    // to the widest key length so the action column lines up — works because the UI
    // uses m5x7, a monospace font (variable-width fonts would still render OK, just
    // with looser alignment).
    static string BuildControlsText() {
        int maxKey = 0;
        foreach (var row in ControlsRows)
            if (row.keys != null && row.keys.Length > maxKey)
                maxKey = row.keys.Length;
        var sb = new System.Text.StringBuilder(ControlsRows.Length * 32);
        for (int i = 0; i < ControlsRows.Length; i++) {
            var row = ControlsRows[i];
            if (row.keys == null) sb.AppendLine();
            else sb.Append(row.keys.PadRight(maxKey + 3)).AppendLine(row.action);
        }
        return sb.ToString().TrimEnd();
    }

    void OnEnable() {
        // Re-sync UI to current settings every time the panel opens. Cheap,
        // and means external mutations (e.g. a hotkey toggling vsync) show up.
        RefreshFromSettings();
    }

    public void Toggle() {
        if (gameObject.activeSelf) gameObject.SetActive(false);
        else UI.OpenExclusive(gameObject);
    }

    void WireUp() {
        if (masterSlider   != null) masterSlider.onValueChanged.AddListener(OnMaster);
        if (sfxSlider      != null) sfxSlider.onValueChanged.AddListener(OnSfx);
        if (ambientSlider  != null) ambientSlider.onValueChanged.AddListener(OnAmbient);
        if (fpsDropdown    != null) fpsDropdown.onValueChanged.AddListener(OnFpsIndex);
        if (vsyncToggle    != null) vsyncToggle.onValueChanged.AddListener(OnVsync);
        if (lightingToggle != null) lightingToggle.onValueChanged.AddListener(OnLighting);
        if (closeButton    != null) closeButton.onClick.AddListener(() => gameObject.SetActive(false));
    }

    void RefreshFromSettings() {
        var s = SettingsManager.instance;
        if (s == null) {
            Debug.LogError("[OptionsPanel] SettingsManager.instance is null — add a SettingsManager to the scene.");
            return;
        }
        suppressCallbacks = true;
        if (masterSlider   != null) masterSlider.value   = s.masterVolume;
        if (sfxSlider      != null) sfxSlider.value      = s.sfxVolume;
        if (ambientSlider  != null) ambientSlider.value  = s.ambientVolume;
        if (fpsDropdown    != null) fpsDropdown.value    = FpsValueToIndex(s.targetFps);
        if (vsyncToggle    != null) vsyncToggle.isOn     = s.vsyncEnabled;
        if (lightingToggle != null) lightingToggle.isOn  = s.lightingEnabled;
        suppressCallbacks = false;
    }

    static int FpsValueToIndex(int fps) {
        for (int i = 0; i < FpsOptions.Length; i++)
            if (FpsOptions[i] == fps) return i;
        // Unknown stored value — show "Unlimited" as least surprising fallback.
        Debug.Log($"[OptionsPanel] No dropdown option matches stored fps={fps}; falling back to Unlimited.");
        return FpsOptions.Length - 1;
    }

    // ── Callbacks ────────────────────────────────────────────────────────────

    void OnMaster(float v)   { if (!suppressCallbacks) SettingsManager.instance?.SetMasterVolume(v); }
    void OnSfx(float v)      { if (!suppressCallbacks) SettingsManager.instance?.SetSfxVolume(v); }
    void OnAmbient(float v)  { if (!suppressCallbacks) SettingsManager.instance?.SetAmbientVolume(v); }
    void OnVsync(bool v)     { if (!suppressCallbacks) SettingsManager.instance?.SetVsync(v); }
    void OnLighting(bool v)  { if (!suppressCallbacks) SettingsManager.instance?.SetLighting(v); }

    void OnFpsIndex(int i) {
        if (suppressCallbacks) return;
        if (i < 0 || i >= FpsOptions.Length) {
            Debug.LogError($"[OptionsPanel] FPS dropdown index out of range: {i}");
            return;
        }
        SettingsManager.instance?.SetTargetFps(FpsOptions[i]);
    }
}

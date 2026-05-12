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
    //
    // `keys` is an array of chip labels: each entry renders as its own framed chip.
    // Use multiple entries for combo inputs ("shift" + "lmb" → two chips); use a
    // single multi-word entry for compound concepts that read as one thing
    // ("lmb drag", "q / e", "right drag").
    static readonly (string[] keys, string action)[] ControlsRows = {
        (new[] { "space" },        "pause / resume"),
        (new[] { "esc" },          "close panel / cancel mode"),
        (new[] { "f1" },           "toggle ui"),
        (null,                     null),
        (new[] { "scroll" },       "zoom"),
        (new[] { "right drag" },   "pan camera"),
        (null,                     null),
        (new[] { "f" },            "mirror (build mode)"),
        (new[] { "r" },            "rotate (build mode)"),
        (new[] { "q / e" },        "cycle shape (build mode)"),
        (new[] { "shift", "lmb" }, "place more after building (build mode)"),
        (null,                     null),
        (new[] { "lmb drag" },     "drag-select / paint harvest"),
        (new[] { "ctrl", "lmb" },  "add to / remove from selection"),
        (new[] { "shift", "lmb" }, "copy filter (on storage tile)"),
        (new[] { "shift", "rmb" }, "paste filter (on storage tile)"),
    };

    // Layout sizes for the controls section. Tweak here, not at call sites.
    const float KeysColumnWidth = 84f;   // fits the widest combo: "shift" + "lmb" ≈ 50px, with headroom for chip padding
    const float RowHeight       = 18f;   // chip is 16; +2 vertical breathing room
    const int   ChipPadX        = 3;     // sliced 2px border + 1px inset around the label
    const float DividerHeight   = 2f;
    const float SectionGap      = 6f;

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
        if (controlsText != null) BuildControlsSection(controlsText);
    }

    // Replaces the legacy single-TMP_Text controls block with a richer column:
    //   [divider] [Controls label] [row …] [row …] …
    // Reuses the existing GameObject (the one wired into `controlsText` in the
    // inspector) so no scene rewiring is needed — the TMP_Text component on it
    // is destroyed and the GameObject is converted into a VLG container.
    // Each row has a fixed-width keys column on the left (multiple framed chips)
    // and a non-wrapping action TMP on the right, so action text never wraps onto
    // the next line. Font is inherited from the original TMP_Text so the panel
    // stays consistent if the project ever swaps m5x7 for another asset.
    void BuildControlsSection(TMP_Text marker) {
        GameObject host = marker.gameObject;
        TMP_FontAsset font = marker.font;
        int uiLayer = host.layer;
        Destroy(marker);

        var vlg = host.GetComponent<VerticalLayoutGroup>();
        if (vlg == null) vlg = host.AddComponent<VerticalLayoutGroup>();
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.spacing                = 2;
        vlg.childAlignment         = TextAnchor.UpperLeft;

        var fitter = host.GetComponent<ContentSizeFitter>();
        if (fitter == null) fitter = host.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        Sprite dividerSprite  = Resources.Load<Sprite>("Sprites/Misc/divider");
        Sprite woodSprite     = Resources.Load<Sprite>("Sprites/Misc/woodframe");
        if (dividerSprite == null) Debug.LogError("[OptionsPanel] Missing Sprites/Misc/divider");
        if (woodSprite    == null) Debug.LogError("[OptionsPanel] Missing Sprites/Misc/woodframe");

        AddDivider(host.transform, dividerSprite, uiLayer);
        AddSectionLabel(host.transform, "Controls", font, uiLayer);

        foreach (var row in ControlsRows) {
            if (row.keys == null) {
                AddSpacer(host.transform, SectionGap, uiLayer);
                continue;
            }
            AddControlsRow(host.transform, row.keys, row.action, font, woodSprite, uiLayer);
        }
    }

    static GameObject NewUI(string name, Transform parent, int uiLayer) {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = uiLayer;
        go.transform.SetParent(parent, false);
        return go;
    }

    static void AddDivider(Transform parent, Sprite sprite, int uiLayer) {
        var go = NewUI("Divider", parent, uiLayer);
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.type   = Image.Type.Sliced;
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = DividerHeight;
        le.minHeight       = DividerHeight;
    }

    static void AddSectionLabel(Transform parent, string text, TMP_FontAsset font, int uiLayer) {
        var go = NewUI("SectionLabel", parent, uiLayer);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.color     = Color.black;
        tmp.fontSize  = 16;
        if (font != null) tmp.font = font;
        tmp.alignment = TextAlignmentOptions.Left;
        tmp.enableWordWrapping = false;
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 16;
    }

    static void AddSpacer(Transform parent, float h, int uiLayer) {
        var go = NewUI("Spacer", parent, uiLayer);
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = h;
        le.minHeight       = h;
    }

    static void AddControlsRow(Transform parent, string[] keys, string action,
                               TMP_FontAsset font, Sprite woodSprite, int uiLayer) {
        var row = NewUI("Row", parent, uiLayer);
        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.childControlWidth      = true;
        hlg.childControlHeight     = true;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = false;
        hlg.spacing                = 6;
        hlg.childAlignment         = TextAnchor.MiddleLeft;
        var rowLe = row.AddComponent<LayoutElement>();
        rowLe.preferredHeight = RowHeight;
        rowLe.minHeight       = RowHeight;

        // Fixed-width keys column so action texts align across rows. Inside it,
        // chips lay out left-to-right with a small gap. Setting childForceExpandWidth=false
        // on the inner HLG lets chips size to their text via per-chip ContentSizeFitter.
        var keysGo = NewUI("Keys", row.transform, uiLayer);
        var keysHlg = keysGo.AddComponent<HorizontalLayoutGroup>();
        keysHlg.childControlWidth      = true;
        keysHlg.childControlHeight     = true;
        keysHlg.childForceExpandWidth  = false;
        keysHlg.childForceExpandHeight = false;
        keysHlg.spacing                = 3;
        keysHlg.childAlignment         = TextAnchor.MiddleLeft;
        var keysLe = keysGo.AddComponent<LayoutElement>();
        keysLe.preferredWidth = KeysColumnWidth;
        keysLe.minWidth       = KeysColumnWidth;

        foreach (var k in keys) AddKeyChip(keysGo.transform, k, font, woodSprite, uiLayer);

        // Action text — single line, no wrap, takes the rest of the row width.
        var actGo = NewUI("Action", row.transform, uiLayer);
        var actTmp = actGo.AddComponent<TextMeshProUGUI>();
        actTmp.text      = action;
        actTmp.color     = Color.black;
        actTmp.fontSize  = 16;
        if (font != null) actTmp.font = font;
        actTmp.alignment          = TextAlignmentOptions.Left;
        actTmp.enableWordWrapping = false;
        actTmp.overflowMode       = TextOverflowModes.Overflow;
        var actLe = actGo.AddComponent<LayoutElement>();
        actLe.flexibleWidth    = 1;
        actLe.preferredHeight  = 16;
    }

    static void AddKeyChip(Transform parent, string label, TMP_FontAsset font,
                           Sprite woodSprite, int uiLayer) {
        var go = NewUI("Chip_" + label, parent, uiLayer);
        var img = go.AddComponent<Image>();
        img.sprite = woodSprite;
        img.type   = Image.Type.Sliced;

        var hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(ChipPadX, ChipPadX, 0, 0);
        hlg.childControlWidth      = true;
        hlg.childControlHeight     = true;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = false;
        hlg.childAlignment         = TextAnchor.MiddleCenter;

        var chipFit = go.AddComponent<ContentSizeFitter>();
        chipFit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 16;
        le.minHeight       = 16;

        var lbl = NewUI("Label", go.transform, uiLayer);
        var tmp = lbl.AddComponent<TextMeshProUGUI>();
        tmp.text      = label;
        tmp.color     = Color.black;
        tmp.fontSize  = 16;
        if (font != null) tmp.font = font;
        tmp.alignment          = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.overflowMode       = TextOverflowModes.Overflow;
        var lblLe = lbl.AddComponent<LayoutElement>();
        lblLe.preferredHeight = 14;
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

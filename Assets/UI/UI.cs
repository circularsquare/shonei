using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using System;
using System.Linq;

public class UI : MonoBehaviour {
    public static UI instance {get; protected set;}
    public static World world {get; protected set;}
    public static Db db {get; protected set;}

    [SerializeField] GameObject chatWindow;

    // Cached root Canvas components for F1 hide-UI toggle. Populated lazily on first press.
    // We toggle Canvas.enabled rather than GameObject.SetActive so panel hierarchies keep
    // their internal state (selections, open/collapsed groups, etc.) untouched.
    Canvas[] _allCanvases;
    bool[]   _wasEnabled; // pre-hide snapshot so restore doesn't resurrect canvases that were already off (e.g. LoadingScreen post-End, closed exclusive panels)
    bool _uiHidden;

    // Exclusive panels — at most one may be visible at a time.
    // Call RegisterExclusive() in Awake/Start; call OpenExclusive() instead of SetActive(true).
    static readonly List<GameObject> exclusivePanels = new List<GameObject>();

    // Fires on every Play Mode entry, including when Domain Reload is disabled.
    // Without this, a second Play press would accumulate destroyed GameObjects in the
    // list, and OpenExclusive's p.activeSelf check would throw MissingReferenceException.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStaticsForPlayMode() {
        exclusivePanels.Clear();
    }

    public static void RegisterExclusive(GameObject panel) {
        if (!exclusivePanels.Contains(panel))
            exclusivePanels.Add(panel);
    }

    // Close all other exclusive panels, then open this one.
    public static void OpenExclusive(GameObject panel) {
        foreach (var p in exclusivePanels)
            if (p != panel && p.activeSelf)
                p.SetActive(false);
        panel.SetActive(true);
    }

    void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one ui controller");}
        instance = this;
        if (chatWindow != null) chatWindow.SetActive(true);
    }
    void StartLate(){
        if (world == null){
            // world = WorldController.instance.world;
            // db = Db.instance; // right now this is just used to check if the db is finished loading
            // moved this to InventoryController
        } 
    }
    void Update() {
        if (world == null){
            StartLate();
        }
        // F1 toggles all UI visibility (every root Canvas). Useful for screenshots / observing
        // the world without HUD overlay. Disabling a Canvas also stops it from intercepting
        // mouse input, so clicks pass through to MouseController normally.
        if (Input.GetKeyDown(KeyCode.F1)) {
            ToggleUIVisible();
        }

        // "/" anywhere outside an input field opens the trading panel chat,
        // focuses the field, and seeds it with "/". See TradingPanel.OpenChatInput.
        if (Input.GetKeyDown(KeyCode.Slash) && !IsTypingInField()) {
            TradingPanel.instance?.OpenChatInput();
        }

        // LMB on world mirrors the Esc chain (steps 1–3): SaveMenu → BuildPanel
        // sub-panel → exclusive panel, first match wins, so a single click never
        // collapses two layers. The handler is non-consuming — control falls
        // through to MouseController this frame, so a Select-mode click both
        // closes a panel AND selects the clicked tile (matches Esc step 4 semantics
        // for non-tool clicks). Esc-step-4-equivalent (exit non-Select mode) lives
        // in MouseController's Build branch instead, so the no-structType case can
        // also seed a Select drag-start on the same press.
        if (Input.GetMouseButtonDown(0) && !EventSystem.current.IsPointerOverGameObject()) {
            if (SaveMenuPanel.instance != null && SaveMenuPanel.instance.gameObject.activeSelf) {
                SaveMenuPanel.instance.gameObject.SetActive(false);
            }
            else if (BuildPanel.instance != null && BuildPanel.instance.IsSubPanelOpen) {
                BuildPanel.instance.CloseSubPanel();
            }
            else {
                foreach (var p in exclusivePanels)
                    if (p.activeSelf) { p.SetActive(false); break; }
            }
        }

        // Esc dismisses one layer of UI per press, in priority order. Centralised here
        // (rather than each panel handling its own Esc) so a single press never triggers
        // two layers in the same frame — e.g. closing a build subpanel AND leaving Build
        // mode would feel like Esc "skipped" a step.
        if (Input.GetKeyDown(KeyCode.Escape)) {
            // 1. save menu (most modal — full-screen overlay)
            if (SaveMenuPanel.instance != null && SaveMenuPanel.instance.gameObject.activeSelf) {
                SaveMenuPanel.instance.gameObject.SetActive(false);
                return;
            }
            // 2. open build category subpanel
            if (BuildPanel.instance != null && BuildPanel.instance.IsSubPanelOpen) {
                BuildPanel.instance.CloseSubPanel();
                return;
            }
            // 3. any open exclusive panel (TradingPanel / RecipePanel / ResearchPanel / GlobalHappinessPanel)
            foreach (var p in exclusivePanels) {
                if (p.activeSelf) { p.SetActive(false); return; }
            }
            // 4. fall back to leaving non-Select mouse mode
            var mc = MouseController.instance;
            if (mc != null && mc.mouseMode != MouseController.MouseMode.Select)
                mc.SetModeSelect();
        }
    }

    // True if the keyboard focus is currently on a text input field, so global
    // shortcuts (Space pause, "/" chat) should not hijack the keystroke.
    static bool IsTypingInField() {
        var sel = EventSystem.current?.currentSelectedGameObject;
        if (sel == null) return false;
        return sel.GetComponent<InputField>() != null
            || sel.GetComponent<TMP_InputField>() != null;
    }

    // Flips visibility of every Canvas in the scene. On hide we snapshot the current
    // enabled-state of each root canvas; on restore we put each one back to exactly
    // what it was before — never blanket-enable, because that would resurrect
    // canvases that were intentionally off (LoadingScreen after End(), closed
    // exclusive panels, etc.).
    void ToggleUIVisible() {
        if (!_uiHidden) {
            // Hide: re-scan (canvases may have been added/removed at runtime), snapshot, disable.
            _allCanvases = FindObjectsOfType<Canvas>(includeInactive: false);
            _wasEnabled  = new bool[_allCanvases.Length];
            for (int i = 0; i < _allCanvases.Length; i++) {
                var c = _allCanvases[i];
                if (c == null) continue;
                _wasEnabled[i] = c.enabled;
                // Only toggle root canvases — nested canvases inherit enabled state from
                // their parent's render hierarchy, and flipping them independently would
                // leave child canvases visible after the parent is hidden.
                if (c.isRootCanvas) c.enabled = false;
            }
            _uiHidden = true;
        } else {
            // Restore: only re-enable canvases that were enabled at hide time.
            if (_allCanvases != null) {
                for (int i = 0; i < _allCanvases.Length; i++) {
                    var c = _allCanvases[i];
                    if (c == null) continue;
                    if (c.isRootCanvas) c.enabled = _wasEnabled[i];
                }
            }
            _uiHidden = false;
        }
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class UI : MonoBehaviour {
    public static UI instance {get; protected set;}
    public static World world {get; protected set;}
    public static Db db {get; protected set;}

    [SerializeField] GameObject chatWindow;

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
        // Close whichever exclusive panel is open when the user clicks outside UI
        if (Input.GetMouseButtonDown(0) && !EventSystem.current.IsPointerOverGameObject()) {
            foreach (var p in exclusivePanels)
                if (p.activeSelf) { p.SetActive(false); break; }
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


}

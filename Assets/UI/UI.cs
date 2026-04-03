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
    }


}

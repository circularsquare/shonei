using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.U2D;

// note: this is not really a controller. it just manages mouse input

public class MouseController : MonoBehaviour {
    public enum MouseMode {Select, Build, Remove};
    public MouseMode mouseMode = MouseMode.Select;
    public GameObject cursorHighlight;
    Vector3 prevPosition;

    public World world;
    public static MouseController instance;
    PixelPerfectCamera ppc;

    void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one mouse controller");}
        instance = this;
        ppc = Camera.main.GetComponent<PixelPerfectCamera>();
    }

    // wanna chagne the building process to only be able to make Buildings, not tiles. 
    // dirt buildings will instantly just turn into dirt tiles after theyre made.
    void Update() {
        if (EventSystem.current.IsPointerOverGameObject()){
            if (Input.GetMouseButtonDown(0) && mouseMode == MouseMode.Build)
                SetModeSelect();
            return;
        }
        if (world == null){
            world = WorldController.instance.world;
        }

        // zoom
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll != 0f && ppc != null) {
            int[] zoomLevels = {8, 12, 16, 24 }; // in PPU
            int idx = System.Array.IndexOf(zoomLevels, ppc.assetsPPU);
            if (idx == -1) idx = System.Array.BinarySearch(zoomLevels, ppc.assetsPPU);
            if (idx < 0) idx = ~idx; // nearest index if not found
            idx = Mathf.Clamp(idx + (scroll > 0 ? 1 : -1), 0, zoomLevels.Length - 1);
            ppc.assetsPPU = zoomLevels[idx];
        }

        // draggin world around
        Vector3 currPosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        currPosition.z = 1f;
        if (Input.GetMouseButtonDown(1)){ 
            prevPosition = Input.mousePosition;
            // if (mouseMode == MouseMode.Build) SetModeSelect();             // cancels build mode on right click
        }
        if (Input.GetMouseButton(1)) {
            Vector3 currScreenPosition = Input.mousePosition;
            Vector3 delta = Camera.main.ScreenToWorldPoint(prevPosition) 
                        - Camera.main.ScreenToWorldPoint(currScreenPosition);
            delta.z = 0f;
            Camera.main.transform.Translate(delta);
            prevPosition = currScreenPosition;
        }

        // set cursorHighlight if in build/remove mode
        Tile tileAt = WorldController.instance.world.GetTileAt(currPosition.x, currPosition.y);
        if (tileAt == null){ cursorHighlight.SetActive(false);}
        else if ((mouseMode == MouseMode.Build) || (mouseMode == MouseMode.Remove)){
            cursorHighlight.SetActive(true);
            cursorHighlight.transform.position = new Vector3(tileAt.x, tileAt.y, 1);
        }
        if (mouseMode == MouseMode.Select){
            cursorHighlight.SetActive(false);
        }


        // register click
        if (Input.GetMouseButtonDown(0)) {
            Vector3 clickPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            if (mouseMode == MouseMode.Select){ // display info
                RaycastHit2D hit = Physics2D.Raycast(clickPos, Vector2.zero);
                if (hit.collider != null) {
                    InfoPanel.instance.ShowInfo(hit.collider); // clicked on animal
                } else if (tileAt != null) {
                    InfoPanel.instance.ShowInfo(tileAt); // clicked on tile
                    if (tileAt.inv != null && (tileAt.inv.invType == Inventory.InvType.Storage
                                            || tileAt.inv.invType == Inventory.InvType.Market)) {
                        InventoryController.instance.SelectInventory(tileAt.inv);  // select inventory if storage
                    } else {
                        InventoryController.instance.SelectInventory(null); // deselect inventory (show global)
                    }
                }

            } else if (mouseMode == MouseMode.Build) {
                if (BuildPanel.instance.PlaceBlueprint(tileAt)) {
                    mouseMode = MouseMode.Select;
                }
            } else if (mouseMode == MouseMode.Remove) {
                BuildPanel.instance.Remove(tileAt);
            }
        }
    }

    public void SetModeBuild() {
        mouseMode = MouseMode.Build;
    }
    public void SetModeRemove() {
        mouseMode = MouseMode.Remove;
        if (BuildPanel.instance != null){
            BuildPanel.instance.structType = null;
            BuildPanel.instance.CloseSubPanel();
        }
    }
    public void SetModeSelect() {
        mouseMode = MouseMode.Select;
        if (BuildPanel.instance != null){
            BuildPanel.instance.structType = null;
            BuildPanel.instance.CloseSubPanel();
        }
    }
}

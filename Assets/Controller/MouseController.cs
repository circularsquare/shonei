using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Reflection;

// note: this is not really a controller. it just manages mouse input

public class MouseController : MonoBehaviour {
    public enum MouseMode {Select, Build, Remove};
    public MouseMode mouseMode = MouseMode.Select;
    public GameObject cursorHighlight;
    SpriteRenderer cursorHighlightSr;
    Sprite cursorHighlightDefaultSprite;
    Vector3 prevPosition;

    public World world;
    public static MouseController instance;
    Component ppcComponent;
    PropertyInfo ppcAssetsPPU;

    void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one mouse controller");}
        instance = this;
        cursorHighlightSr = cursorHighlight.GetComponent<SpriteRenderer>();
        cursorHighlightDefaultSprite = cursorHighlightSr.sprite;
        foreach (var c in Camera.main.GetComponents<Component>()) {
            var prop = c.GetType().GetProperty("assetsPPU");
            if (prop != null) { ppcComponent = c; ppcAssetsPPU = prop; break; }
        }
    }

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
        if (scroll != 0f && ppcAssetsPPU != null) {
            int[] zoomLevels = {8, 12, 16, 24 }; // in PPU
            int current = (int)ppcAssetsPPU.GetValue(ppcComponent);
            int idx = System.Array.IndexOf(zoomLevels, current);
            if (idx == -1) idx = System.Array.BinarySearch(zoomLevels, current);
            if (idx < 0) idx = ~idx;
            idx = Mathf.Clamp(idx + (scroll > 0 ? 1 : -1), 0, zoomLevels.Length - 1);
            ppcAssetsPPU.SetValue(ppcComponent, zoomLevels[idx]);
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
        StructType st = BuildPanel.instance != null ? BuildPanel.instance.structType : null;

        // For multi-tile buildings, offset anchor so the building is centered on the mouse
        Tile anchorTile = tileAt;
        if (tileAt != null && st != null && st.nx > 1) {
            int offsetX = (st.nx - 1) / 2;
            anchorTile = WorldController.instance.world.GetTileAt(tileAt.x - offsetX, tileAt.y);
        }

        if (tileAt == null){ cursorHighlight.SetActive(false); }
        else if ((mouseMode == MouseMode.Build) || (mouseMode == MouseMode.Remove)){
            cursorHighlight.SetActive(true);
            if (mouseMode == MouseMode.Build && st != null && anchorTile != null) {
                Sprite buildSprite = st.LoadSprite();
                cursorHighlightSr.sprite = buildSprite != null ? buildSprite : cursorHighlightDefaultSprite;
                cursorHighlightSr.color = buildSprite != null ? new Color(1f, 1f, 1f, 0.3f) : Color.white;
                float visualX = anchorTile.x + (st.nx - 1) / 2.0f;
                cursorHighlight.transform.position = new Vector3(visualX, anchorTile.y, -1);
            } else {
                cursorHighlight.transform.position = new Vector3(tileAt.x, tileAt.y, -1);
                cursorHighlightSr.sprite = cursorHighlightDefaultSprite;
                cursorHighlightSr.color = Color.white;
            }
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
                Tile placeTile = anchorTile ?? tileAt;
                if (BuildPanel.instance.PlaceBlueprint(placeTile) && !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) {
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

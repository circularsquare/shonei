using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// note: this is not really a controller. it just manages mouse input

public class MouseController : MonoBehaviour
{
    public enum MouseMode {Select, Build, Destroy};
    public MouseMode mouseMode = MouseMode.Select;
    public GameObject cursorHighlight;
    Vector3 prevPosition;

    public World world;
    public static MouseController instance;

    void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one mouse controller");}
        instance = this;        
    }



    // wanna chagne the building process to only be able to make Buildings, not tiles. 
    // dirt buildings will instantly just turn into dirt tiles after theyre made.
    void Update() {
        if (EventSystem.current.IsPointerOverGameObject()){
            // this only checks if it's over ui elements.
            return;
        }
        if (world == null){
            world = WorldController.instance.world;
        }

        // draggin world around
        // is buggy if you drag off a UI element        
        Vector3 currPosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        currPosition.z = 1f;
        if (Input.GetMouseButton(1)){ // right click. drag to move around in game
            Camera.main.transform.Translate(prevPosition - currPosition);
        }
        if (Input.GetMouseButtonDown(1)){
            prevPosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            prevPosition.z = 1f;
        }

        // set cursorHighlight if in build/destroy mode
        Tile tileAt = WorldController.instance.world.GetTileAt(currPosition.x, currPosition.y);
        if (tileAt == null){ cursorHighlight.SetActive(false);}
        else if ((mouseMode == MouseMode.Build) || (mouseMode == MouseMode.Destroy)){
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
                    if (tileAt.inv != null && tileAt.inv.invType == Inventory.InvType.Storage) {
                        InventoryController.instance.SelectInventory(tileAt.inv);  // select inventory if storage
                    } else {
                        InventoryController.instance.SelectInventory(null); // deselect inventory (show global)
                    }
                }

            } else if (mouseMode == MouseMode.Build) {
                BuildPanel.instance.Construct(tileAt);
            } else if (mouseMode == MouseMode.Destroy) {
                BuildPanel.instance.Destroy(tileAt);
            }
        }

        
    }

    public void SetModeBuild() {
        mouseMode = MouseMode.Build;
    }
    public void SetModeDestroy() {
        mouseMode = MouseMode.Destroy;
        if (BuildPanel.instance != null){
            BuildPanel.instance.bt = null;
        }
    }
    public void SetModeSelect() {
        mouseMode = MouseMode.Select;
        if (BuildPanel.instance != null){
            BuildPanel.instance.bt = null;
        }
    }
}

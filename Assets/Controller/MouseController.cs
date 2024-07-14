using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// note: this is not really a controller. it just manages mouse input

public class MouseController : MonoBehaviour
{
    public Tile.TileType buildModeTile;
    public GameObject cursorHighlight;
    Vector3 prevPosition;

    public World world;
    public Inventory inventory;
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
            return;
        }
        if (world == null){
            world = WorldController.instance.world;
            inventory = InventoryController.instance.inventory;
        }

        // draggin world around
        Vector3 currPosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        currPosition.z = 1f;
        if (Input.GetMouseButton(1)){ // right or middle click
            Camera.main.transform.Translate(prevPosition - currPosition);
        }
        // for some reason quill18 didnt need the "if mouse button down" part??
        if (Input.GetMouseButtonDown(1)){
            prevPosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            prevPosition.z = 1f;
        }

        Tile tileAt = WorldController.instance.world.GetTileAt(currPosition.x, currPosition.y);


        // ok i think i should
        // abolish mousecontroller
        // make TileType a full object instead of just an enum
        // have click interaction in buildmenu
        // have separate functions to construct tile or construct building 
        // check costs / consume costs
        // if buildingType.isTile, 
        //     call the current tile updating thing
        // else 
        //     set the tile to Structure
        //     instantiate a Building on that tile (with coordinates int x y equal to that of the Tile)
            


        if (tileAt == null){ cursorHighlight.SetActive(false);}
        else{
            cursorHighlight.SetActive(true);
            cursorHighlight.transform.position = new Vector3(tileAt.x, tileAt.y, 1);
            // click to switch tile type
            if (Input.GetMouseButtonDown(0)) {
                Vector3 clickPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
                if (tileAt != null && tileAt.Type == Tile.TileType.Empty) {
                    tileAt.Type = buildModeTile;
                    // if (buildModeTile == Tile.TileType.Structure){
                    //     BuildMenu.Construct(BuildMenu.instance.bt, x = 0, y = 0);
                    // }
                } else if (buildModeTile == Tile.TileType.Empty){
                    tileAt.Type = buildModeTile; // destroy 
                }
            }
        }
    }

    public void SetModeBuild() {
        buildModeTile = Tile.TileType.Soil;
    }
    public void SetModeDestroy() {
        buildModeTile = Tile.TileType.Empty;
    }
    public void HarvestWood(){
        inventory.AddItem(1, 2);
    }
}

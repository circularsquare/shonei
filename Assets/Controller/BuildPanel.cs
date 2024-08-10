using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class BuildPanel : MonoBehaviour {

    public GameObject buildButtonPrefab; // UI button prefab for each building
    public GameObject textDisplayPrefab;
    public static BuildPanel instance;
    public BuildingType bt;

    private void Start(){
        if (instance != null) {
            Debug.LogError("there should only be one build panel");}
        instance = this;        


        // Create a button for each building and add it to the build menu
        foreach (BuildingType building in Db.buildingTypes){
            if (building != null){
                GameObject buttonGo = Instantiate(buildButtonPrefab, transform);
                foreach (ItemQuantity iq in building.costs) {
                    GameObject costDisplay = Instantiate(textDisplayPrefab, buttonGo.transform);
                    costDisplay.GetComponent<TMPro.TextMeshProUGUI>().text = Db.items[iq.id].name + ": " + iq.quantity.ToString();
                    costDisplay.name = "CostDisplay" + Db.items[iq.id].name;
                }
                buttonGo.GetComponent<Button>().onClick.AddListener(() => SetBuildingType(building));  // is this right??
                buttonGo.GetComponentInChildren<TMPro.TextMeshProUGUI>().text = building.name;
            }
        }
    }
    public void SetBuildingType(BuildingType bt){
        this.bt = bt;
        MouseController.instance.mouseMode = MouseController.MouseMode.Build;
    }

    // mousecontroller handles the mouse stuff. and calls build here.

    public bool Construct(Tile tile){
        if (bt != null && GlobalInventory.instance.SufficientResources(bt.costs) && tile.type.id == 0){
            if (bt.isTile){
                if (Db.tileTypeByName.ContainsKey(bt.name)){
                    tile.type = Db.tileTypeByName[bt.name];
                    GlobalInventory.instance.AddItems(bt.costs, true);
                }
            }
            if (!bt.isTile){
                if (tile.building != null){
                    Debug.Log("theres already a building here!");
                    return false;
                } else {
                    Building building;
                    building = new Building(bt, tile.x, tile.y); 
                    tile.building = building;
                    GlobalInventory.instance.AddItems(bt.costs, true);
                }
            }
            return true;
        }
        return false;
    }
    public bool Destroy(Tile tile){
        if (tile.type != Db.tileTypes[0]){
            if (tile.building != null){
                tile.building = null;
                return true; // destroyed structure
            }
            tile.type = Db.tileTypes[0]; 
            return true; // destroyed tile
        }
        return false;
    }

    private void Update()
    {
        // // If the player is holding the build button and there is a selected building, start building the building
        // if (Input.GetMouseButton(0) && isBuilding)
        // {
        //     BuildingType buildingType = Db.buildingTypes[selectedBuildingIndex];
        //     if (Inventory.instance.SufficientResources(buildingType.costs))
        //     {
        //         Vector3 mousePos = Input.mousePosition;
        //         Ray ray = Camera.main.ScreenPointToRay(mousePos);
        //         RaycastHit hit;
        //         if (Physics.Raycast(ray, out hit))
        //         {
        //             if (hit.collider.tag == "BuildLocation")
        //             {
        //                 Inventory.instance.AddItems(buildingType.costs);
        //                 //World.Instance.AddBuilding(building, hit.point);
        //                 //Player.Instance.RemoveResources(buildingType.resources, buildingType.cost);
        //             }
        //         }
        //     }
        // }
    }

}
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class BuildMenu : MonoBehaviour {

    public GameObject buildButtonPrefab; // UI button prefab for each building
    public GameObject textDisplayPrefab;
    public BuildMenu instance;
    public BuildingType bt;

    private void Start(){
        if (instance != null) {
            Debug.LogError("there should only be one buildmenu");}
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
                buttonGo.GetComponent<Button>().onClick.AddListener(() => SelectBuilding(building));
                buttonGo.GetComponentInChildren<TMPro.TextMeshProUGUI>().text = building.name;
            }

        }
    }

    // mousecontroller handles the mouse stuff. and calls build here.

    public bool Construct(BuildingType bt, int x, int y){
        if (Inventory.instance.SufficientResources(bt.costs)){
            Inventory.instance.AddItems(bt.costs);
            return true;
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

    private void SelectBuilding(BuildingType bt)
    {
        this.bt = bt;
        if (bt.isTile == true){
            if (bt.name == "stone"){
                MouseController.instance.buildModeTile = Tile.TileType.Stone;
            } else{
                MouseController.instance.buildModeTile = Tile.TileType.Soil;
            }
            
        } else {
            MouseController.instance.buildModeTile = Tile.TileType.Structure;
        }

    }
}
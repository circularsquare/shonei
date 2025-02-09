using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class BuildPanel : MonoBehaviour {

    public GameObject buildDisplayPrefab; // UI button prefab for each building
    public GameObject textDisplayPrefab;
    public static BuildPanel instance;
    public BuildingType bt;

    private void Start(){
        if (instance != null) {
            Debug.LogError("there should only be one build panel");}
        instance = this;        

        // add building buttons
        foreach (BuildingType building in Db.buildingTypes){
            if (building != null){
                GameObject buildDisplayGo = Instantiate(buildDisplayPrefab, transform);
                foreach (ItemQuantity iq in building.costs) { 
                    GameObject costDisplay = Instantiate(textDisplayPrefab, buildDisplayGo.transform);
                    costDisplay.GetComponent<TMPro.TextMeshProUGUI>().text = iq.item.name + ": " + iq.quantity.ToString();
                    costDisplay.name = "CostDisplay_" + iq.item.name;
                }
                buildDisplayGo.transform.GetChild(0).GetComponent<Button>().onClick.AddListener(() => SetBuildingType(building));  // is this right??
                GameObject buildingTextGo = buildDisplayGo.transform.Find("BuildingButton/TextBuildingName").gameObject;
                buildingTextGo.GetComponent<TMPro.TextMeshProUGUI>().text = building.name;
                buildDisplayGo.name = "BuildDisplay_" + building.name;
            }
        }

        // foreach (PlantType plantType in Db.plantTypes){
        //     if (plantType != null){
        //         GameObject buildDisplayGo = Instantiate(buildDisplayPrefab, transform);
        //         foreach (ItemQuantity iq in plantType.costs) { 
        //             GameObject costDisplay = Instantiate(textDisplayPrefab, buildDisplayGo.transform);
        //             costDisplay.GetComponent<TMPro.TextMeshProUGUI>().text = iq.item.name + ": " + iq.quantity.ToString();
        //             costDisplay.name = "CostDisplay_" + iq.item.name;
        //         }
        //         // buildDisplayGo.transform.GetChild(0).GetComponent<Button>().onClick.AddListener(() => SetBuildingType(plantType));  
        //         GameObject buildingTextGo = buildDisplayGo.transform.Find("BuildingButton/TextBuildingName").gameObject;
        //         buildingTextGo.GetComponent<TMPro.TextMeshProUGUI>().text = plantType.name;
        //     }
        // }
    }
    public void SetBuildingType(BuildingType bt){
        this.bt = bt;
        MouseController.instance.SetModeBuild();    
    }

    // mousecontroller handles the mouse stuff. and calls build here.

    public bool Construct(Tile tile){
        if (bt != null && tile.type.id == 0){ // && GlobalInventory.instance.SufficientResources(bt.costs)
            if (bt.isTile){
                if (Db.tileTypeByName.ContainsKey(bt.name)){
                    tile.type = Db.tileTypeByName[bt.name];
                    GlobalInventory.instance.AddItems(bt.costs, true);
                }
            }
            if (!bt.isTile){
                if ((tile.building != null) || (tile.blueprint != null)){
                    Debug.Log("theres already a building or blueprint here!");
                    return false;
                } else {
                    Blueprint blueprint = new Blueprint(bt, tile.x, tile.y);
                    tile.blueprint = blueprint;                   
                    return true;
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
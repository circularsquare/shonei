using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class BuildPanel : MonoBehaviour {
    public GameObject buildDisplayPrefab; // UI button prefab for each building
    public GameObject textDisplayPrefab;
    public static BuildPanel instance;
    public StructType structType;

    private void Start(){
        if (instance != null) {
            Debug.LogError("there should only be one build panel");}
        instance = this;        

        // add building buttons
        foreach (StructType structType in Db.structTypes){
            if (structType != null){
                GameObject buildDisplayGo = Instantiate(buildDisplayPrefab, transform);
                foreach (ItemQuantity iq in structType.costs) { 
                    GameObject costDisplay = Instantiate(textDisplayPrefab, buildDisplayGo.transform);
                    costDisplay.GetComponent<TMPro.TextMeshProUGUI>().text = iq.item.name + ": " + iq.quantity.ToString();
                    costDisplay.name = "CostDisplay_" + iq.item.name;
                }
                buildDisplayGo.transform.GetChild(0).GetComponent<Button>().onClick.AddListener(() => SetStructType(structType));  // is this right??
                GameObject buildingTextGo = buildDisplayGo.transform.Find("BuildingButton/TextBuildingName").gameObject;
                buildingTextGo.GetComponent<TMPro.TextMeshProUGUI>().text = structType.name;
                buildDisplayGo.name = "BuildDisplay_" + structType.name;
            }
        }

    }
    public void SetStructType(StructType st){
        this.structType = st;
        MouseController.instance.SetModeBuild();    
    }

    // mousecontroller handles the mouse stuff. and calls build here.

    public bool PlaceBlueprint(Tile tile){ // tile must be empty (id 0), or blueprint is to mine tile
        if (structType == null) return false;
        if (tile.GetBlueprintAt(structType.depth) != null) return false;
        if (tile.type.id != 0 && structType.name != "empty") return false; // special case: mine tile

        // Check for existing structure at the same depth slot
        if (!structType.isTile){
            if ((structType.isPlant || structType.depth == "b") && tile.building != null) return false;
            if (structType.depth == "m" && tile.mStruct != null) return false;
            if (structType.depth == "f" && tile.fStruct != null) return false;
        }

        Blueprint blueprint = new Blueprint(structType, tile.x, tile.y);
        return true;
    }

    public bool Destroy(Tile tile){ // DOESNT WORK
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

    private void Update(){
    }

}
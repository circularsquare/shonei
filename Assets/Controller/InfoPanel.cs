using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class InfoPanel : MonoBehaviour {
    public GameObject textDisplayPrefab;
    public static InfoPanel instance;
    public GameObject textDisplayGo;
    public object obj;

    public enum InfoMode {
        Inactive,
        Tile, 
        Building,
        Blueprint,
        Animal, 
        Plant,
    }
    public InfoMode infoMode = InfoMode.Inactive;

    public void Start(){
        if (instance != null) {
            Debug.LogError("there should only be one " + this.GetType().ToString());}
        instance = this;        
        ShowInfo(null); // initialize as inactive
        textDisplayGo = Instantiate(textDisplayPrefab, this.transform);
    }

    public void ShowInfo(object obj){
        this.obj = obj;
        UpdateInfo();
    }
    public void UpdateInfo(){
        // todo: make it so if you click again it cycles possible targets somehow?
        if (obj == null){
            Deselect();
            return;
        }
        if (obj is Collider2D){
            Collider2D collider = obj as Collider2D;
            if (collider.gameObject.GetComponent<Animal>() != null){
                infoMode = InfoMode.Animal;
                gameObject.SetActive(true);
                Animal ani = collider.gameObject.GetComponent<Animal>();
                string displayText = ("animal: " + ani.aName + 
                "\n state: " + ani.state.ToString() + 
                "\n job: " + ani.job.name +
                "\n inventory: " + ani.inv.ToString() + 
                "\n location: " + ani.go.transform.position.ToString() + 
                "\n fullness: " + ani.eating.Fullness().ToString("F2"));
                textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = displayText;
            }
        }

        else if (obj is Tile){
            Tile tile = obj as Tile;
            string displayText = "";
            if (tile.building != null){
                if (tile.building is Plant){
                    infoMode = InfoMode.Plant;
                    gameObject.SetActive(true);
                    displayText =  ( "plant: " + tile.building.buildingType.name + 
                        "\n location: " + tile.x.ToString() + ", " + tile.y.ToString() + 
                        "\n growth: " + (tile.building as Plant).growthStage);
                } else {
                    infoMode = InfoMode.Building;
                    gameObject.SetActive(true);
                    displayText =  ( "building: " + tile.building.buildingType.name + 
                        "\n location: " + tile.x.ToString() + ", " + tile.y.ToString());
                }
            } else if (tile.blueprint != null){
                infoMode = InfoMode.Blueprint;
                gameObject.SetActive(true);
                displayText =  ( "blueprint: " + tile.blueprint.buildingType.name + 
                    "\n location: " + tile.x.ToString() + ", " + tile.y.ToString() + 
                    "\n progress: " + tile.blueprint.GetProgress());
            } else {
                infoMode = InfoMode.Tile;
                gameObject.SetActive(true);
                displayText = "tile: " + tile.type.name;            
            }
            if (tile.inv != null){
                displayText += "\n inventory: " + tile.inv.ToString();
            }
            textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = displayText;
            gameObject.SetActive(true);
        }
        else{ Deselect(); }
    }

    public void Deselect(){
        infoMode = InfoMode.Inactive;
        gameObject.SetActive(false);
    }

}
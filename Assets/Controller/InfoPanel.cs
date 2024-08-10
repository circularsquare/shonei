using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class InfoPanel : MonoBehaviour {
    public GameObject textDisplayPrefab;
    public static InfoPanel instance;
    public GameObject textDisplayGo;

    public enum InfoMode {
        Inactive,
        Tile, 
        Building,
        Animal
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
        if (obj == null){
            Deselect();
            return;
        }

        // todo: make it so if you click again it cycles possible targets somehow?
        if (obj is Collider2D){
            Collider2D collider = obj as Collider2D;
            if (collider.gameObject.GetComponent<Animal>() != null){
                infoMode = InfoMode.Animal;
                gameObject.SetActive(true);
                Animal ani = collider.gameObject.GetComponent<Animal>();
                string displayText = ("animal: " + ani.aName + 
                "\n state: " + ani.state.ToString() + 
                "\n job: " + ani.job.name +
                "\n inventory: " + ani.inventory.ToString() + 
                "\n locationxy: " + ani.x.ToString() + ", " + ani.y.ToString() +
                "\n location: " + ani.go.transform.position.ToString());
                textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = displayText;
            }
        }

        else if (obj is Tile){
            Tile tile = obj as Tile;
            if (tile.building != null){
                infoMode = InfoMode.Building;
                gameObject.SetActive(true);
                textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = (
                    "building: " + tile.building.buildingType.name + 
                    "\n location: " + tile.x.ToString() + ", " + tile.y.ToString());
            } else {
                infoMode = InfoMode.Tile;
                gameObject.SetActive(true);
                textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = "tile: " + tile.type.name;            
            }

            gameObject.SetActive(true);
        }
        else{ Deselect(); }
    }

    public void Deselect(){
        infoMode = InfoMode.Inactive;
        gameObject.SetActive(false);
    }

}
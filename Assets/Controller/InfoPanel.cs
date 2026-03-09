using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class InfoPanel : MonoBehaviour {
    public GameObject textDisplayPrefab;
    public static InfoPanel instance;
    public GameObject textDisplayGo;
    public object obj;
    public GameObject animalHighlight;    // assign in Inspector; follows selected animal
    public GameObject tileHighlight;      // assign in Inspector; overlays selected tile
    [SerializeField] Button priorityUpButton;    // assign in Inspector
    [SerializeField] Button priorityDownButton;  // assign in Inspector
    private Animal selectedAnimal;

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
        //textDisplayGo = Instantiate(textDisplayPrefab, this.transform);
        if (priorityUpButton != null)   priorityUpButton.onClick.AddListener(() => ChangeBlueprintPriority(1));
        if (priorityDownButton != null) priorityDownButton.onClick.AddListener(() => ChangeBlueprintPriority(-1));
    }

    void ChangeBlueprintPriority(int delta) {
        if (obj is Tile tile) {
            Blueprint bp = tile.GetAnyBlueprint();
            if (bp != null) {
                bp.priority = Mathf.Max(0, bp.priority + delta);
                UpdateInfo();
            }
        }
    }

    void SetPriorityButtonsVisible(bool visible) {
        if (priorityUpButton != null)   priorityUpButton.gameObject.SetActive(visible);
        if (priorityDownButton != null) priorityDownButton.gameObject.SetActive(visible);
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
        SetPriorityButtonsVisible(false); // hide by default; shown only for blueprints below
        if (obj is Collider2D){
            Collider2D collider = obj as Collider2D;
            selectedAnimal = collider.gameObject.GetComponent<Animal>();
            if (selectedAnimal != null){
                infoMode = InfoMode.Animal;
                gameObject.SetActive(true);
                Animal ani = selectedAnimal;
                if (animalHighlight != null) animalHighlight.SetActive(true);
                if (tileHighlight != null) tileHighlight.SetActive(false);
                static string FormatSlot(Inventory slot) {
                    var s = slot.itemStacks[0];
                    return s.item != null ? s.item.name + " " + ItemStack.FormatQ(s.quantity, s.item.discrete) : "empty";
                }
                string displayText = (
                    "animal: " + ani.aName +
                    "\n state: " + ani.state.ToString() +
                    "\n job: " + ani.job.name +
                    "\n [food] " + FormatSlot(ani.foodSlotInv) +
                    "\n [tool] " + FormatSlot(ani.toolSlotInv) +
                    "\n inventory: " + ani.inv.ToString());
                if (ani.task != null){
                    displayText += "\n task: " + ani.task.ToString();}
                if (ani.task?.currentObjective != null){
                    displayText += "\n objective " + ani.task.currentObjective.ToString();}
                if (ani.task is CraftTask craftTask){
                    displayText += "\n recipe: " + craftTask.recipe?.description;
                }
                displayText += (
                    "\n location: " + ani.go.transform.position.ToString() +
                    "\n efficiency: " + ani.efficiency.ToString("F2") +
                    "\n fullness: " + ani.eating.Fullness().ToString("F2") +
                    "\n eep: " + ani.eeping.Eepness().ToString("F2") +
                    "\n happiness: " + ani.happiness.ToString());
                textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = displayText;
            }
        }

        else if (obj is Tile){
            selectedAnimal = null;
            Tile tile = obj as Tile;
            if (animalHighlight != null) animalHighlight.SetActive(false);
            if (tileHighlight != null) {
                tileHighlight.SetActive(true);
                tileHighlight.transform.position = new Vector3(tile.x, tile.y, -1);
            }
            string displayText = "";
            if (tile.building != null){
                if (tile.building is Plant){
                    infoMode = InfoMode.Plant;
                    gameObject.SetActive(true);
                    displayText =  ( "plant: " + tile.building.structType.name + 
                        "\n location: " + tile.x.ToString() + ", " + tile.y.ToString() + 
                        "\n growth: " + (tile.building as Plant).growthStage + 
                        "\n reserved: " + tile.building.res.reserved + "/" + tile.building.res.capacity + 
                        "\n standability: " + tile.node.standable.ToString() + 
                        "\n num neighbors: " + tile.node.neighbors.Count);
                } else {
                    infoMode = InfoMode.Building;
                    gameObject.SetActive(true);
                    displayText = "building: " + tile.building.structType.name +
                        "\n location: " + tile.x.ToString() + ", " + tile.y.ToString() +
                        "\n reserved: " + tile.building.res.reserved + "/" + tile.building.res.capacity +
                        "\n standability: " + tile.node.standable.ToString() +
                        "\n num neighbors: " + tile.node.neighbors.Count;
                    if (tile.building is Building bldg && bldg.structType.depleteAt > 0)
                        displayText += "\n uses: " + bldg.uses + "/" + bldg.structType.depleteAt;
                }
            } else if (tile.GetAnyBlueprint() != null){
                Blueprint blueprint = tile.GetAnyBlueprint();
                infoMode = InfoMode.Blueprint;
                gameObject.SetActive(true);
                SetPriorityButtonsVisible(true);
                displayText =  ( "blueprint: " + blueprint.structType.name +
                    "\n location: " + tile.x.ToString() + ", " + tile.y.ToString() +
                    "\n priority: " + blueprint.priority +
                    "\n progress: " + blueprint.GetProgress());
            } else {
                infoMode = InfoMode.Tile;
                gameObject.SetActive(true);
                displayText = ("tile: " + tile.type.name + 
                    "\n location: " + tile.x.ToString() + ", " + tile.y.ToString() +
                    "\n standability: " + tile.node.standable.ToString() + 
                    "\n num neighbors: " + tile.node.neighbors.Count);            
            }
            if (tile.inv != null){
                displayText += "\n inventory: " + tile.inv.ToString();
            }
            textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = displayText;
            gameObject.SetActive(true);
        }
        else{ selectedAnimal = null; Deselect(); }
    }

    public void Deselect(){
        infoMode = InfoMode.Inactive;
        gameObject.SetActive(false);
        SetPriorityButtonsVisible(false);
        if (animalHighlight != null) animalHighlight.SetActive(false);
        if (tileHighlight != null) tileHighlight.SetActive(false);
    }

    void Update(){
        if (infoMode == InfoMode.Animal && selectedAnimal != null && animalHighlight != null)
            animalHighlight.transform.position = selectedAnimal.go.transform.position + new Vector3(0, 0.6f, -1);
    }

}
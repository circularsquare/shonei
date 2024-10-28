using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Linq;

// this is the script for a singular row job display prefab, not the whole job panel
public class ItemDisplay : MonoBehaviour
{
    public Item item;
    public GameObject toggleGo;
    public bool open = true;


    public void Start(){
        item = Db.itemByName[gameObject.name.Split('_')[1]];
    }

    public void OnClickDropdown(){
        if (item == null || item.children.Length == 0){ return; } // don't toggle if no children
        open = !open;
        foreach (Item child in item.children){
            if (InventoryController.instance.discoveredItems[child.id]){  // don't toggle if undiscovered
                InventoryController.instance.itemDisplayGos[child.id].SetActive(open);
            } 
        }
    }
    public void OnClickTargetUp(){ // this should only work for globalinv? idk
        InventoryController.instance.itemTargets[item.id] *= 2;
    }
    public void OnClickTargetDown(){ // this should only work for globalinv? idk
        InventoryController.instance.itemTargets[item.id] /= 2;
    }

    public void LoadAllowed(){ 
        bool allowed = false;
        if (InventoryController.instance.selectedInventory != null){
            allowed = InventoryController.instance.selectedInventory.allowed[item.id];
        }
        if (allowed != toggleGo.GetComponent<Toggle>().isOn){
            toggleGo.GetComponent<Toggle>().SetIsOnWithoutNotify(allowed);}
    }
    public void SetAllowed(bool allowed){GetComponent<Toggle>().isOn = allowed;}
    
    public void OnClickAllow(){ //  allow item in inv
        if (InventoryController.instance.selectedInventory == null){return;}
        if (InventoryController.instance.selectedInventory != null){
            InventoryController.instance.selectedInventory.ToggleAllowItem(item);
        }
    }
}

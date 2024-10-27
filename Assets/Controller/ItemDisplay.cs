using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// this is the script for a singular row job display prefab, not the whole job panel
public class ItemDisplay : MonoBehaviour
{
    public Item item;
    public GameObject toggleGo;


    public void Start(){
        item = Db.itemByName[gameObject.name.Split('_')[1]];
    }
    public bool open = false;
    public void OnClickDropdown(){
        Debug.Log("dropdown");
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

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using System;
using System.Linq;


public class InventoryController : MonoBehaviour
{
    public static InventoryController instance {get; protected set;}
    public GlobalInventory globalInventory;
    public GameObject panelInventory;
    public GameObject itemDisplay; // prefab 
    private World world;
    public Inventory selectedInventory; // if u click on a drawer, allows u to set what its assigned to
    public Dictionary<int, bool> discoveredItems;
    public Dictionary<int, GameObject> itemDisplayGos; // keyed by itemid
    public Dictionary<int, int> targets;

    void Start(){    
        if (instance != null) {
            Debug.LogError("there should only be one inv controller");}
        instance = this;        
        globalInventory = new GlobalInventory();
        discoveredItems = Db.itemsFlat.ToDictionary(i => i.id, i => false); // default no items discovered
        itemDisplayGos = Db.itemsFlat.ToDictionary(i => i.id, i => default(GameObject));
        targets = Db.itemsFlat.ToDictionary(i => i.id, i => 1000);
    }

    public void TickUpdate(){
        if (world == null){
            world = WorldController.instance.world;
            panelInventory = GetComponent<Transform>().gameObject;
            foreach (Item item in Db.items){
                AddItemDisplay(item);
            }
        }
        UpdateItemsDisplay();
    }

    void AddItemDisplay(Item item){
        if (item == null){return;}

        GameObject itemDisplayGo;
        if (item.parent == null){
            itemDisplayGo = Instantiate(itemDisplay, panelInventory.transform);
        } else { // WARNING parent must be initiated beore child
            itemDisplayGo = Instantiate(itemDisplay, itemDisplayGos[item.parent.id].transform);
        }

        itemDisplayGos[item.id] = itemDisplayGo;
        itemDisplayGo.name = "ItemDisplay_" + item.name;
        itemDisplayGo.SetActive(discoveredItems[item.id]);
        UpdateItemDisplay(item);    
    }
    bool HaveAnyOfChildren(Item item){ // this is a temporary fix while items are not actually their parents!
        if (globalInventory.Quantity(item.id) != 0){
            return true;
        }
        if (item.children != null){
            foreach (Item child in item.children){
                if (HaveAnyOfChildren(child)){
                    return true;
                }
            }
        }
        return false;
    }
    void UpdateItemDisplay(Item item){
        if (item == null){return;}
        // TODO: add thing at the top that indicates ur looking at a specific inventory
        if (HaveAnyOfChildren(item)){ 
            GameObject itemDisplayGo = itemDisplayGos[item.id];
            if (itemDisplayGo == null){Debug.LogError("itemdisplaygo not found: " + item.name);return;}

            if (discoveredItems[item.id] == false){
                discoveredItems[item.id] = true;
                itemDisplayGo.SetActive(discoveredItems[item.id]);
                RectTransform rectTransform = GetComponent<RectTransform>();
                LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
            }

            string text;
            if (selectedInventory != null){text = item.name + ": " + selectedInventory.Quantity(item).ToString();}
            else {text = item.name + ": " + globalInventory.Quantity(item.id).ToString();}
            Transform textGo = itemDisplayGo.transform.Find("HorizontalLayout/TextItem");
            if (textGo != null){textGo.gameObject.GetComponent<TMPro.TextMeshProUGUI>().text = text;}

            text = "/" + targets[item.id].ToString();
            Transform textTargetGo = itemDisplayGo.transform.Find("HorizontalLayout/TextItemTarget");
            if (textTargetGo != null){textTargetGo.gameObject.GetComponent<TMPro.TextMeshProUGUI>().text = text;}

            ItemDisplay itemDisplay = itemDisplayGo.GetComponent<ItemDisplay>();
            itemDisplay.LoadAllowed();

            if (item.parent != null){
                UpdateItemDisplay(item.parent);
            }
        }
    }
    public void UpdateItemsDisplay(){ foreach (Item item in Db.itemsFlat){ UpdateItemDisplay(item); } }
    public void SelectInventory(Inventory inv){
        selectedInventory = inv; 
        UpdateItemsDisplay();
        if (inv != null && inv.invType == Inventory.InvType.Storage){
            MenuPanel.instance.SetActivePanel(MenuPanel.instance.panels[0]); // object ref not sent to instance
        }
    }
}

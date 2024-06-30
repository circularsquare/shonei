using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using System;



// eventually, would like to have local inventorys (stockpiles) 
// and a fake global inventory that simply tracks the sum of the local inventories 
// for sidebar display purposes.

public class InventoryController : MonoBehaviour
{
    public static InventoryController instance {get; protected set;}
    public Inventory inventory;
    public GameObject inv_go;
    public GameObject panelInv;
    public GameObject itemCount; // prefab 
    private World world;

    void Start()
    {    
        if (instance != null) {
            Debug.LogError("there should only be one inv controller");}
        instance = this;        
        inventory = new Inventory();
        inv_go = new GameObject();
        inv_go.name = "Inventory0";
        inv_go.transform.position = new Vector3(100, 100, 0);
    }

    void Update()
    {
        if (world == null){
            world = WorldController.instance.world;
            inventory.RegisterCbInventoryChanged(OnInventoryChanged);
            inv_go.transform.SetParent(this.transform, true);
            SpriteRenderer inv_sr = inv_go.AddComponent<SpriteRenderer>();
            panelInv = UI.instance.transform.Find("PanelInventory").gameObject;
            foreach (Item item in Db.items){
                addItemCountDisplay(item);
            }
        }
    }

    void addItemCountDisplay(Item item){
        if (item != null && inventory.GetAmount(item.id) != 0){
            GameObject itemCountGo = Instantiate(itemCount, panelInv.transform);
            itemCountGo.GetComponent<TMPro.TextMeshProUGUI>().text = item.name + ": " + inventory.GetAmount(item.id).ToString();
            itemCountGo.name = "ItemCount_" + item.name;
        }
    }
    void updateItemCountDisplay(Item item){
        if (item != null && inventory.GetAmount(item.id) != 0){
            Transform itemCountTransform = panelInv.transform.Find("ItemCount_" + item.name);
            if (itemCountTransform == null){
                addItemCountDisplay(item);
            } else {
                itemCountTransform.gameObject.GetComponent<TMPro.TextMeshProUGUI>().text = item.name + ": " + inventory.GetAmount(item.id).ToString();
            }
        }
    }
    void addItemCounts(){
    }

    // updaes the gameobjects sprite and display when the inv data is changed,
    // should be smarter about which things it updates. not all of them.
    // or do it all at once.
    // as is, this is a pretty beefy callback for 10000 items. n^2? every second
    void OnInventoryChanged(Inventory inv_data) {
        // inv_go.GetComponent<SpriteRenderer>().sprite = null;
        foreach(Item item in Db.items){
            updateItemCountDisplay(item);
        }            
    }
}

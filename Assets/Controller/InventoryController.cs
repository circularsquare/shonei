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
    public GlobalInventory globalInventory;
    public GameObject gInvGo;
    public GameObject panelInv;
    public GameObject itemCount; // prefab 
    private World world;

    void Start()
    {    
        if (instance != null) {
            Debug.LogError("there should only be one inv controller");}
        instance = this;        
        globalInventory = new GlobalInventory();
        gInvGo = new GameObject();
        gInvGo.name = "GlobalInventory";
        gInvGo.transform.position = new Vector3(100, 100, 0);
    }

    void Update()
    {
        if (world == null){
            world = WorldController.instance.world;
            globalInventory.RegisterCbInventoryChanged(OnGlobalInventoryChanged);
            gInvGo.transform.SetParent(this.transform, true);
            SpriteRenderer gInvSr = gInvGo.AddComponent<SpriteRenderer>();
            panelInv = UI.instance.transform.Find("InventoryPanel").gameObject;
            foreach (Item item in Db.items){
                addItemCountDisplay(item);
            }
        }
    }

    void addItemCountDisplay(Item item){
        if (item != null && globalInventory.Quantity(item.id) != 0){
            GameObject itemCountGo = Instantiate(itemCount, panelInv.transform);
            itemCountGo.GetComponent<TMPro.TextMeshProUGUI>().text = item.name + ": " + globalInventory.Quantity(item.id).ToString();
            itemCountGo.name = "ItemCount_" + item.name;
        }
    }
    void updateItemCountDisplay(Item item){
        if (item != null && globalInventory.Quantity(item.id) != 0){
            Transform itemCountTransform = panelInv.transform.Find("ItemCount_" + item.name);
            if (itemCountTransform == null){
                addItemCountDisplay(item);
            } else {
                itemCountTransform.gameObject.GetComponent<TMPro.TextMeshProUGUI>().text = item.name + ": " + globalInventory.Quantity(item.id).ToString();
            }
        }
    }
    void addItemCounts(){
    }

    // updaes the gameobjects sprite and display when the inv data is changed,
    // should be smarter about which things it updates. not all of them.
    // or do it all at once.
    // as is, this is a pretty beefy callback for 10000 items. n^2? every second
    void OnGlobalInventoryChanged(GlobalInventory inv_data) {
        // inv_go.GetComponent<SpriteRenderer>().sprite = null;
        foreach(Item item in Db.items){
            updateItemCountDisplay(item);
        }            
    }
}

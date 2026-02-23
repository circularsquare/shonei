using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;
using System.Runtime.Serialization;


// this class holds mostly permanent attributes about an item in general.
// items don't have unique attributes. they are like resources. 
// if you want unique attributes make something else.
public class Item {
    public int id {get; set;}
    public string name {get; set;}
    public Item[] children {get; set;}
    public float decayRate{get; set;}   
    public Item parent; 
    

    public bool IsDiscovered(){
        if (InventoryController.instance != null){
            return InventoryController.instance.discoveredItems[id];
        }
        return false;
    }
    // inventories are the ones with gameobjects and sprites, not items.
}



// for stuff like input costs.
public class ItemQuantity {
    public int id {get; set;}
    public int quantity {get; set;}
    public Item item;
    public ItemQuantity(){}
    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        item = Db.items[id];
    }

    public ItemQuantity(int id, int quantity){
        this.id = id;
        this.item = Db.items[id];
        this.quantity = quantity;
    }
    public ItemQuantity(Item item, int quantity){
        this.id = item.id;
        this.item = item;
        this.quantity = quantity;
    }
    public ItemQuantity(string name, int quantity){
        this.id = Db.iidByName[name];
        this.item = Db.itemByName[name];
        this.quantity = quantity;
    }
    public override string ToString(){
        return item.name + ": " + quantity.ToString();}
    public string ItemName(){
        return item.name;
    }
}

public class ItemNameQuantity {
    public string name {get; set;}
    public int quantity {get; set;}
}
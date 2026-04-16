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
    public float foodValue {get; set;}  // 0 = not edible; >0 = nutrition restored per unit eaten
    public string happinessNeed {get; set;} // which happiness satisfaction eating this food grants (e.g. "wheat", "fruit"); null = none
    public bool discrete {get; set;}    // true = stored/moved in whole-liang (100 fen) multiples only
    public bool isLiquid {get; set;}    // true = liquid (water, soymilk, etc.); used to restrict which inventory types can hold it
    // Optional per-liquid tint (#RRGGBB) used by WaterController when this liquid is rendered in a
    // decorative zone (tank/fountain). Absent/invalid → shader falls back to its default water blue.
    public string liquidColorHex {get; set;}
    [Newtonsoft.Json.JsonIgnore]
    public Color32 liquidColor;         // parsed from liquidColorHex; alpha=0 when unset, alpha=255 flags "tint active" in the tint texture
    public Item parent;
    // Loaded at startup by Db. Falls back to Sprites/Items/split/default/icon if no item-specific icon exists.
    public Sprite icon;

    // Group items are wildcards for recipe inputs / building costs and are never physical
    // (see SPEC-trading). Only leaf items exist in inventories and on market targets.
    public bool IsGroup => children != null && children.Length > 0;

    public bool IsDiscovered(){
        if (InventoryController.instance != null){
            return InventoryController.instance.discoveredItems[id];
        }
        return false;
    }

    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        if (!string.IsNullOrEmpty(liquidColorHex)) {
            if (ColorUtility.TryParseHtmlString(liquidColorHex, out Color c)) {
                liquidColor = c;
                liquidColor.a = 255; // alpha flags "tint active" in the tint texture
            } else {
                Debug.LogError($"Item '{name}': invalid liquidColorHex '{liquidColorHex}'");
            }
        }
    }
    // inventories are the ones with gameobjects and sprites, not items.
}



// for stuff like input costs.
public class ItemQuantity {
    public int id {get; set;}
    public int quantity {get; set;}
    public float chance = 1f;
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
        return item.name + ": " + ItemStack.FormatQ(quantity, item.discrete);}
    public string ItemName(){
        return item.name;
    }
}

public class ItemNameQuantity {
    public string name {get; set;}
    public float quantity {get; set;} // authored in liang; converted to fen (×100) on use
    public float chance {get; set;} = 1f;
}
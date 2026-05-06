using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;
using System.Runtime.Serialization;


// Physical category for items. Governs which storage inventories accept the item
// (see Inventory.ItemTypeCompatible). Extend here as new categories are added.
// JSON uses lowercase string ("default", "liquid", "book") — Newtonsoft maps case-insensitively.
public enum ItemClass { Default, Liquid, Book }

// this class holds mostly permanent attributes about an item in general.
// items don't have unique attributes. they are like resources.
// if you want unique attributes make something else.
public class Item {
    public int id {get; set;}
    public string name {get; set;}
    public Item[] children {get; set;}
    public bool defaultOpen {get; set;} // group items only: start expanded in inventory trees by default (e.g. "food"). Groups without this start collapsed.
    public float decayRate{get; set;}
    public float foodValue {get; set;}  // 0 = not edible; >0 = nutrition restored per unit eaten
    public string happinessNeed {get; set;} // which happiness satisfaction eating this food grants (e.g. "wheat", "fruit"); null = none
    public bool discrete {get; set;}    // true = stored/moved in whole-liang (100 fen) multiples only
    public ItemClass itemClass {get; set;} = ItemClass.Default; // Default = solid goods; Liquid = water/soymilk/etc.; Book = tech & fiction books. Storage inventories accept only items matching their storageClass.
    // Initial value seeded into InventoryController.targets for this item's id, in liang.
    // Lower for byproducts (acorn, sawdust) so the "outputs over target" gate can actually
    // trigger on multi-product plants without forcing the player to manually retune. Books
    // ignore this field — itemClass==Book overrides to 1 liang in DefaultTargetFen.
    public int defaultTarget {get; set;} = 100;
    // Resolved default target in fen — single source of truth shared by InventoryController.Start
    // (initial seed) and SaveSystem.Gather (delta-vs-default skip on save).
    public int DefaultTargetFen => itemClass == ItemClass.Book ? 100 : defaultTarget * 100;
    public bool isLiquid => itemClass == ItemClass.Liquid; // convenience — lets WaterController and similar liquid-specific code stay readable
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
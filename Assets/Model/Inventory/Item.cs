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
    public bool discrete {get; set;}    // true = stored/moved in whole-unit (unitFen) multiples only
    // Weight of one whole unit, in liang (JSON-authored). Only meaningful when discrete; 0 = unset
    // and resolves to 1 liang. Lets a discrete item (stool, statue) cost more than 1 liang per unit,
    // so it takes up proportionally more storage space — weight and bulk are deliberately one number.
    public float unitWeight {get; set;}
    // Fen per whole unit — the discrete step size. Non-discrete items return 100 but it is never
    // read for them. Computed, not cached: the unitWeight cascade in Db runs after deserialization.
    public int unitFen => discrete ? (unitWeight > 0 ? ItemStack.LiangToFen(unitWeight) : 100) : 100;
    public bool startDiscovered {get; set;} // true = revealed in inventory/storage trees from game start, no research or production needed (e.g. water, drawn from ponds without research)
    // Multiplier applied when this item is equipped in the tool slot. 1.0 = no bonus (treated
    // as "no tool"). >1 = a usable tool. Higher-tier metals (bronze > copper > stone) get
    // larger values. Read by ModifierSystem; meaningless on non-tool items where it stays 1.
    public float workEfficiency {get; set;} = 1f;
    // Wear rate applied while equipped on an animal that is currently working. Same
    // per-year units as `decayRate` — but only ticks during HandleWorking, so an idle
    // or sleeping mouse doesn't wear its tools/clothes. Deterministic (shares ItemStack's
    // decayCounter with passive decay). 0 = no wear from use; `decayRate` still applies
    // passively whenever this item is anywhere in an inventory.
    public float equipDecayRate {get; set;} = 0f;
    public ItemClass itemClass {get; set;} = ItemClass.Default; // Default = solid goods; Liquid = water/soymilk/etc.; Book = tech & fiction books. Storage inventories accept only items matching their storageClass.
    // Initial value seeded into InventoryController.targets for this item's id. In liang for normal
    // items; in whole-unit count for discrete items (resolved via unitFen, like recipe quantities).
    // Lower for byproducts (acorn, sawdust) so the "outputs over target" gate can actually
    // trigger on multi-product plants without forcing the player to manually retune. Books
    // ignore this field — itemClass==Book overrides to 1 liang in DefaultTargetFen.
    public int defaultTarget {get; set;} = 100;
    // Resolved default target in fen — single source of truth shared by InventoryController.Start
    // (initial seed) and SaveSystem.Gather (delta-vs-default skip on save).
    public int DefaultTargetFen => itemClass == ItemClass.Book ? 100 : (discrete ? defaultTarget * unitFen : defaultTarget * 100);
    public bool isLiquid => itemClass == ItemClass.Liquid; // convenience — lets WaterController and similar liquid-specific code stay readable
    // Optional per-liquid tint (#RRGGBB) used by WaterController when this liquid is rendered in a
    // decorative zone (tank/fountain). Absent/invalid → shader falls back to its default water blue.
    public string liquidColorHex {get; set;}
    [Newtonsoft.Json.JsonIgnore]
    public Color32 liquidColor;         // parsed from liquidColorHex; alpha=0 when unset, alpha=255 flags "tint active" in the tint texture

    // ── Furnishing fields ─────────────────────────────────────────────────────
    // Optional: marks this item (or its descendants, via Db inheritance) as installable
    // into a house's FurnishingSlots. The slot's `slotNames[i]` is matched against this
    // string to decide which items fit. Null = not a furnishing.
    public string furnishingSlot {get; set;}
    // Flat happiness bonus granted to every resident of the house while this item is
    // installed in a slot. Added directly to Happiness.score via furnishingScore.
    public float furnishingHappiness {get; set;}
    // Fixed lifetime in in-game days. Counts down on each tick while installed; on 0-cross
    // the slot empties. Independent of decayRate (which governs food/cloth-as-clothing wear).
    public float furnishingLifetimeDays {get; set;}
    // Optional name of a sprite under Resources/Sprites/Buildings/furnishings/ to overlay
    // on the house tile while this item is installed. Null = no visual.
    public string furnishingSprite {get; set;}
    // Amount of this item consumed to install it as one furnishing, authored in liang.
    // 0 = unset → defaults to one whole unit (discrete) or 1 liang (non-discrete).
    public float furnishingCost {get; set;}

    // Resolved install cost in fen — what SupplyFurnishingTask delivers into a slot, and the size
    // FurnishingSlots needs a slot to be. A discrete item installs as whole units, so the cost is
    // floored to a unitFen multiple; an authored cost below one unit floors to 0, and Db validation
    // logs an error (the item becomes un-installable).
    public int furnishingCostFen {
        get {
            int raw = furnishingCost > 0f ? ItemStack.LiangToFen(furnishingCost)
                                          : (discrete ? unitFen : 100);
            return discrete ? (raw / unitFen) * unitFen : raw;
        }
    }

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

    // Raw constructors: `quantity` is fen, already converted. For a value authored in JSON — liang
    // for normal items, a whole-unit count for discrete items — use the ItemNameQuantity constructor
    // below instead; it does the discrete-aware conversion. These take pre-converted fen only.
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
    // Builds an ItemQuantity from an authored ItemNameQuantity — the single chokepoint for
    // JSON-quantity → fen conversion. The authored quantity is liang for normal items, but a whole
    // unit count for discrete items (× unitFen), so a recipe can say { "stool": 1 } for one stool.
    public ItemQuantity(ItemNameQuantity src){
        this.id = Db.iidByName[src.name];
        this.item = Db.itemByName[src.name];
        if (item.discrete){
            if (Math.Abs(src.quantity - Math.Round(src.quantity)) > 0.0001f)
                Debug.LogWarning($"ItemQuantity: discrete item '{src.name}' authored with fractional quantity {src.quantity} — rounding to whole units.");
            this.quantity = (int)Math.Round(src.quantity) * item.unitFen;
        } else {
            this.quantity = ItemStack.LiangToFen(src.quantity);
        }
        this.chance = src.chance;
    }
    public override string ToString(){
        return item.name + ": " + ItemStack.FormatQ(quantity, item);}
    public string ItemName(){
        return item.name;
    }
}

public class ItemNameQuantity {
    public string name {get; set;}
    public float quantity {get; set;} // authored in liang; converted to fen (×100) on use
    public float chance {get; set;} = 1f;
}
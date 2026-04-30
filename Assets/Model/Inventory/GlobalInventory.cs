using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System;

// World-total item counter. Updated by Inventory.Produce on every physical item
// mutation; queried by UI/targets logic and by task layers that score by global
// availability (e.g. recipe picking). Group items sum their leaf descendants.
public class GlobalInventory {
    public static GlobalInventory instance {get; protected set;}
    public Dictionary<int, int> itemAmounts {get; protected set;}
    public Dictionary<int, int> itemCapacities {get; protected set;}
    Action<GlobalInventory> cbInventoryChanged;

    public GlobalInventory() { // this is instantiated by invcontroller
        if (instance != null) {
            Debug.LogError("there should only be one global inv");}
        instance = this;  

        itemAmounts = Db.itemsFlat.ToDictionary(i => i.id, i => 0);
    }

    public void AddItem(Item item, int quantity){
        // Group-item check is handled upstream in Inventory.AddItem — no need to re-log here.
        if (item.children != null && item.children.Length > 0) return;
        AddItem(item.id, quantity);
    }
    public void AddItem(int iid, int quantity){
        // itemAmounts is seeded from Db.itemsFlat at construction — any id not present
        // is an unknown / out-of-range id, not a missing leaf. Reject rather than create
        // a phantom entry (used to silently accept e.g. AddItem(999, 7)).
        if (!itemAmounts.ContainsKey(iid)){
            Debug.LogError($"GlobalInventory.AddItem: unknown item id {iid} (quantity={quantity}); rejecting.");
            return;
        }
        itemAmounts[iid] += quantity;
        if (cbInventoryChanged != null){
            cbInventoryChanged(this); } // make sure to add this callback thing wherever inv is changed
    }
    public void AddItems(ItemQuantity[] iqs, bool negate = false){
        if (negate){
            foreach (ItemQuantity iq in iqs){
                AddItem(iq.item.id, -iq.quantity);
            }
        } else {
            foreach (ItemQuantity iq in iqs){
                AddItem(iq.item.id, iq.quantity);
            }
        }
    }

    // Group-aware: routes through Quantity(Item) so callers using a name get the same
    // group-summing behaviour as the Item overload. Logs and returns 0 if the name is unknown.
    public int Quantity(string name){
        if (!Db.iidByName.TryGetValue(name, out int iid)){
            Debug.LogError($"GlobalInventory.Quantity: unknown item name '{name}'.");
            return 0;
        }
        return Quantity(iid);
    }
    // Group-aware: routes through Quantity(Item) so group ids sum their leaf descendants
    // instead of returning 0. Logs and returns 0 if the id doesn't resolve to an item.
    public int Quantity(int iid){
        if (iid < 0 || iid >= Db.items.Length || Db.items[iid] == null){
            Debug.LogError($"GlobalInventory.Quantity: unknown item id {iid}.");
            return 0;
        }
        return Quantity(Db.items[iid]);
    }
    // Group-aware: sums leaf descendants if item has children, otherwise exact lookup
    // against itemAmounts. The other overloads delegate here.
    public int Quantity(Item item){
        if (item.children == null){
            return itemAmounts.TryGetValue(item.id, out int amt) ? amt : 0;
        }
        int total = 0;
        foreach (Item child in item.children)
            total += Quantity(child);
        return total;
    }

    public bool SufficientResources(ItemQuantity[] iqs){
        foreach (ItemQuantity iq in iqs){
            if (Quantity(iq.item) < iq.quantity) return false;
        }
        return true;
    }

    public void RegisterCbInventoryChanged(Action<GlobalInventory> callback){
        cbInventoryChanged += callback;}
    public void UnregisterCbInventoryChanged(Action<GlobalInventory> callback){
        cbInventoryChanged -= callback;}
} 

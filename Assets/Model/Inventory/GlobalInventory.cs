using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System;
using TMPro;

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
        if (!itemAmounts.ContainsKey(iid)){
            itemAmounts.Add(iid, 0);
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

    public int Quantity(string name){
        return Quantity(Db.iidByName[name]);
    }
    // Exact lookup by id — always returns 0 for group items since they never physically exist.
    // Prefer Quantity(Item) for any call that may involve group items.
    public int Quantity(int iid){
        if (itemAmounts.ContainsKey(iid)){
            return itemAmounts[iid];
        } else {return 0;}
    }
    // Group-aware: sums leaf descendants if item has children, otherwise exact.
    public int Quantity(Item item){
        if (item.children == null) return Quantity(item.id);
        int total = 0;
        foreach (Item child in item.children)
            total += Quantity(child);
        return total;
    }

    public bool SufficientResources(ItemQuantity[] iqs){
        bool sufficient = true;
        foreach (ItemQuantity iq in iqs){
            if (Quantity(iq.item) < iq.quantity){
                sufficient = false;
            }
        }
        return sufficient;
    }

    public void CalculateCapacities(){
        // need to allocate capacities for inventories.
    }

    public void RegisterCbInventoryChanged(Action<GlobalInventory> callback){
        cbInventoryChanged += callback;}
    public void UnregisterCbInventoryChanged(Action<GlobalInventory> callback){
        cbInventoryChanged -= callback;}
} 

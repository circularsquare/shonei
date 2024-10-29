using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System;
using TMPro;

// this is global inventory
public class GlobalInventory 
{
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


    public void AddItem(ItemQuantity iq){
        AddItem(iq.item.id, iq.quantity);
    }
    public void AddItem(string name, int quantity){
        if (Db.iidByName.ContainsKey(name)){
            AddItem(Db.iidByName[name], quantity);
        } else {
            Debug.LogError("item name doesn't exist in iid dictionary");
        }
    }
    public void AddItem(Item item, int quantity){
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
    public int Quantity(int iid){
        if (itemAmounts.ContainsKey(iid)){
            return itemAmounts[iid];
        } else {return 0;}
    }

    public bool SufficientResources(ItemQuantity[] iqs){
        bool sufficient = true;
        foreach (ItemQuantity iq in iqs){
            if (Quantity(iq.item.id) < iq.quantity){
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

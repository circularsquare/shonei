using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using TMPro;

// this is global inventory
public class GlobalInventory 
{
    public static GlobalInventory instance {get; protected set;}
    public Dictionary<int, int> itemAmounts {get; protected set;}
    Action<GlobalInventory> cbInventoryChanged;

    public GlobalInventory() {
        itemAmounts = new Dictionary<int, int>();

        if (instance != null) {
            Debug.LogError("there should only be one global inv");}
        instance = this;  
    }

    public void AddItem(ItemQuantity iq){
        AddItem(iq.id, iq.quantity);
    }
    public void AddItem(string name, int amount){
        if (Db.iidByName.ContainsKey(name)){
            AddItem(Db.iidByName[name], amount);
        } else {
            Debug.LogError("item name doesn't exist in iid dictionary");
        }
        
    }
    public void AddItem(int iid, int amount){
        if (!itemAmounts.ContainsKey(iid)){
            itemAmounts.Add(iid, amount);
        }
        itemAmounts[iid] += amount;

        if (cbInventoryChanged != null){
            cbInventoryChanged(this); } // make sure to add this callback thing wherever inv is changed
    }
    public void AddItems(ItemQuantity[] iqs, bool negate = false){
        if (negate){
            foreach (ItemQuantity iq in iqs){
                AddItem(iq.id, -iq.quantity);
            }
        } else {
            foreach (ItemQuantity iq in iqs){
                AddItem(iq.id, iq.quantity);
            }
        }

    }

    public float GetAmount(string name){
        return GetAmount(Db.iidByName[name]);
    }
    public float GetAmount(int iid){
        if (itemAmounts.ContainsKey(iid)){
            return itemAmounts[iid];
        } else {return 0;}
    }

    public bool SufficientResources(ItemQuantity[] iqs){
        bool sufficient = true;
        foreach (ItemQuantity iq in iqs){
            if (GetAmount(iq.id) < iq.quantity){
                sufficient = false;
            }
        }
        return sufficient;
    }


    public void RegisterCbInventoryChanged(Action<GlobalInventory> callback){
        cbInventoryChanged += callback;}
    public void UnregisterCbInventoryChanged(Action<GlobalInventory> callback){
        cbInventoryChanged -= callback;}
} 

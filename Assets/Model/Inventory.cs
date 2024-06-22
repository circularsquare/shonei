using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using TMPro;

public class Inventory // should make a separate inventory game object?
{
    public Dictionary<int, int> itemAmounts {get; protected set;}
    Action<Inventory> cbInventoryChanged;

    public Inventory() {
        itemAmounts = new Dictionary<int, int>();
    }

    public void AddItem(string name, int amount){
        AddItem(Db.iidByName[name], amount);
    }
    public void AddItem(int iid, int amount){
        if (!itemAmounts.ContainsKey(iid)){
            itemAmounts.Add(iid, amount);
        }
        itemAmounts[iid] += amount;

        if (cbInventoryChanged != null){
            cbInventoryChanged(this); } // make sure to add this callback thing wherever inv is changed
    }
    public float GetAmount(string name){
        return GetAmount(Db.iidByName[name]);
    }
    public float GetAmount(int iid){
        if (itemAmounts.ContainsKey(iid)){
            return itemAmounts[iid];
        } else {return 0;}
    }



    public void RegisterCbInventoryChanged(Action<Inventory> callback){
        cbInventoryChanged += callback;}
    public void UnregisterCbInventoryChanged(Action<Inventory> callback){
        cbInventoryChanged -= callback;}
} 

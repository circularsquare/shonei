using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class Inventory
{
    public int nStacks;
    public int stackSize; 
    public ItemStack[] itemStacks;
    public enum InvType {Floor, Storage, Animal};
    public InvType invType;

    public GameObject go;

    public Inventory(int n = 1, int stackSize = 20, InvType invType = InvType.Floor, int x = 0, int y = 0) {
        nStacks = n;
        this.stackSize = stackSize;
        this.invType = invType;
        itemStacks = new ItemStack[nStacks];
        for (int i = 0; i < nStacks; i++){
            itemStacks[i] = new ItemStack(null, 0, stackSize);
        }

        if (invType == InvType.Floor || invType == InvType.Storage){
            go = new GameObject();
            go.transform.position = new Vector3(x, y, 0);
            go.transform.SetParent(WorldController.instance.transform, true);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 10;
            sr.sprite = Resources.Load<Sprite>("Sprites/Inventory/" + invType.ToString());
        }

    }

    public int AddItem(Item item, int quantity){
        for (int i = 0; i < nStacks; i++){
            int? result = itemStacks[i].AddItem(item, quantity);
            if (result == null){ continue; }
            if (result.Value == 0){ continue; } // successfully added item to a stack
            else {quantity = result.Value;} // set quantity to remaining size to get off and keep trying
        }
        UpdateSprite(); // this is a bit wasteful right now.
        return quantity; // leftover size
    }
    public int AddItem(string name, int quantity){return(AddItem(Db.itemByName[name], quantity));}
    public int TakeItem(Item item, int quantity){return AddItem(item, -quantity);}
    public int TakeItem(string name, int quantity){return AddItem(name, -quantity);}
    public int MoveItemTo(Inventory otherInv, Item item, int quantity){
        int taken = quantity + TakeItem(item, quantity);
        int overFill = otherInv.AddItem(item, taken);
        if (overFill > 0){
            AddItem(item, overFill); // return the item if recipient is full.
        }
        return taken - overFill;
    }
    public int MoveItemTo(Inventory otherInv, string name, int quantity){return MoveItemTo(otherInv, Db.itemByName[name], quantity);}

    public override string ToString(){
        string s = "inventory \n";
        foreach (ItemStack stack in itemStacks){
            if (stack != null){
                s += stack.ToString();
            }  
        }
        return s;
    }
    public bool ContainsItem(Item item){
        if (item == null){ return !IsEmpty(); }
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item == item && stack.quantity > 0){
                return true;
            }
        }
        return false;
    }
    public int GetItemAmount(Item item){
        int amount = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item == item){
                amount += stack.quantity;
            }
        }
        return amount;
    }
    public bool HasSpaceForItem(Item item){
        foreach (ItemStack stack in itemStacks){
            if (stack == null || stack.item == null || (stack.item == item && stack.quantity < stackSize)){
                return true;
            }
        }
        return false;
    }
    public bool IsEmpty(){
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.quantity > 0){
                return false; }}
        return true;
    }

    public void UpdateSprite(){
        if (invType == InvType.Animal){return;} // animal invs don't have game objects.
        if (IsEmpty()) {
            go.name = "InventoryEmpty";
            go.GetComponent<SpriteRenderer>().sprite = null;
            return;
        } 
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item != null){
                go.name = "Inventory" + stack.item.name;
                go.GetComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>("Sprites/items/" + stack.item.name);
                return;
            }
        }
    }


}
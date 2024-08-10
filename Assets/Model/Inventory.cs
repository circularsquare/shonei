using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class Inventory
{
    public int nStacks;
    public int stackSize; 
    public ItemStack[] itemStacks;

    public Inventory(int n = 4, int x = 0, int y = 0) {
        nStacks = n;
        stackSize = 10;
        itemStacks = new ItemStack[nStacks];
        for (int i = 0; i < nStacks; i++){
            itemStacks[i] = new ItemStack(null, 0, x, y);
        }
    }

    public int AddItem(Item item, int quantity){
        for (int i = 0; i < nStacks; i++){
            int? result = itemStacks[i].AddItem(item, quantity);
            if (result == null){ continue; }
            int resulti = result.Value;
            if (resulti == 0){ return 0; } // successfully added item to a stack
            else {quantity = resulti;} // set quantity to remaining size to get off and keep trying
        }
        return quantity; // leftover size
    }
    public int AddItem(string name, int quantity){
        return(AddItem(Db.itemByName[name], quantity));
    }

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
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item == item){
                return true;
            }
        }
        return false;
    }
    public bool HasSpaceForItem(Item item){
        foreach (ItemStack stack in itemStacks){
            if (stack == null || stack.item == null || (stack.item == item && stack.quantity < stackSize)){
                return true;
            }
        }
        return false;
    }

}
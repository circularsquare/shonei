using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class ItemStack 
{
    public bool isComposite{get; set;}
    public Item item { get; set; }
    public int quantity { get; set; } //if i want some things to be floats... have a display multiplier?
                                        // so some things can then be like 0.001 but in reality are just small?
    public int stackSize = 100;

    public ItemStack(Item item, int quantity = 0, int stackSize = 100)
    {
        this.item = item;
        this.quantity = quantity;
        this.stackSize = stackSize;
        if (item != null){
            isComposite = item.isComposite;
        }        
    }
    public int? AddItem(Item item, int quantity){
        if (this.item == null || this.quantity == 0){ // add to empty stack
            this.item = item;
            this.quantity = Math.Min(stackSize, quantity);
            return Math.Max(0, (quantity - stackSize));
        }
        else if (item != this.item){ // not the same item
            return null; 
        } else if (this.quantity + quantity > stackSize){
            int sizeOver = this.quantity + quantity - stackSize;
            this.quantity = stackSize;
            return sizeOver; // overflow (3 if still have 3 to deposit)
        } else if (this.quantity + quantity < 0){
            int sizeUnder = this.quantity + quantity - 0;
            this.quantity = 0;
            this.item = null;
            return sizeUnder; // underflow (-3 if still need 3 more)
        } else {
            this.quantity += quantity; // add to stack
            return 0;
        } 
    }

    public bool ContainsItem(Item iitem){
        return (item == iitem && quantity > 0);
    }
    public bool HasSpaceForItem(Item iitem){
        return (item == iitem && quantity < stackSize); 
    }


    public override string ToString(){
        if (item != null){
            return item.name + " x " + quantity.ToString() + "\n";
        } 
        return "";
    }

}
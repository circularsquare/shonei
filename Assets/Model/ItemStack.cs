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
    public int decayCounter; // increments from 0 to maxdecaycount, when reaches it, it decays 1 item.
    public static int maxDecayCount = 1000000;
    public int stackSize = 100;
    public Inventory inv;

    public ItemStack(Inventory inv, Item item, int quantity = 0, int stackSize = 100){
        this.item = item;
        this.quantity = quantity;
        this.stackSize = stackSize;
        this.inv = inv;
        decayCounter = 0;
    }

    public void Decay(float time = 1f){
        if (item != null && quantity > 0){
            float decayedQuantity = (float)quantity * (item.decayRate * time / (float)(Db.ticksInDay*Db.daysInYear));
            // ^ number near 0
            decayCounter += (int)(decayedQuantity * maxDecayCount);
            int amountToDecay = decayCounter / maxDecayCount;
            if (amountToDecay > 0){
                //Debug.Log("decayed! " + item.name + " x " + amountToDecay);
                inv.Produce(item, -amountToDecay);
                decayCounter -= amountToDecay * maxDecayCount;
            }
        }
    }
    public int? AddItem(Item item, int quantity){
        if (this.item == null || this.quantity == 0){ // add to empty stack
            this.item = item; }
        if (item != this.item){ // item slot occupied by different item. go next
            return null; }
        if (this.quantity + quantity > stackSize){
            int sizeOver = this.quantity + quantity - stackSize;
            this.quantity = stackSize;
            //Debug.Log("this has " + this.quantity + " plus " + quantity + " and stack size is " + stackSize);
            return sizeOver; // overflow (3 if still have 3 to deposit)
        } else if (this.quantity + quantity <= 0){ // <= 0 because want to null out stack
            int sizeUnder = this.quantity + quantity - 0;
            this.quantity = 0;
            this.item = null;
            //Debug.Log("underflow, this has " + this.quantity + " plus " + quantity + " and stack size is " + stackSize);
            return sizeUnder; // underflow (-3 if still need 3 more)
        } else {
            this.quantity += quantity; // add to stack
            return 0;
        } 
    }
    public bool Empty(){ return (item == null || quantity == 0); }

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
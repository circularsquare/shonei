using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class ItemStack {
    public bool isComposite{get; set;}
    public Item item { get; set; }
    public int quantity { get; set; } //if i want some things to be floats... have a display multiplier?
                                        // so some things can then be like 0.001 but in reality are just small? 
    public int decayCounter; // increments from 0 to maxdecaycount, when reaches it, it decays 1 item.
    public static int maxDecayCount = 1000000;  
    public int stackSize = 100;
    public Inventory inv;
    public int resAmount = 0;    // how much of this stack is reserved by pending tasks
    public float resTime = 0f;   // Time.time when last Reserve() was called (for staleness)

    // Formats a fen quantity as a liang string (e.g. 250 → "2.5", 200 → "2", 5 → "0.05")
    public static string FormatQ(int fen, bool discrete = false){
        if (discrete) return (fen / 100).ToString();
        if (fen % 100 == 0) return (fen / 100).ToString(); // exact integer, no decimals
        float val = fen / 100f;
        return Math.Abs(val) switch{
            >= 9.6f => val.ToString("0.#"),
            >= 0.96f => val.ToString("0.#"),
            _ => val.ToString("0.##")
        };
    }
    public static string FormatQ(ItemQuantity iq) => FormatQ(iq.quantity, iq.item.discrete);

    public ItemStack(Inventory inv, Item item, int quantity = 0, int stackSize = 100){
        this.item = item;
        this.quantity = quantity;
        this.stackSize = stackSize;
        this.inv = inv;
        decayCounter = 0;
    }

    public void Decay(float time = 1f){
        if (item != null && quantity > 0 && item.decayRate != 0){
            float decayedQuantity = (float)quantity * (item.decayRate * time / (float)(World.ticksInDay*World.daysInYear));
            // ^ number near 0
            decayCounter += (int)(decayedQuantity * maxDecayCount);
            int amountToDecay = decayCounter / maxDecayCount;
            if (item.discrete) amountToDecay = (amountToDecay / 100) * 100; // only decay whole items
            if (amountToDecay > 0){
                // Debug.Log("decayed! " + item.name + " x " + amountToDecay);
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
        if (item.discrete && quantity % 100 != 0)
            Debug.LogWarning($"Discrete item '{item.name}': non-whole-liang quantity {quantity} fen passed to AddItem");
        if (this.quantity + quantity > stackSize){
            int sizeOver = this.quantity + quantity - stackSize;
            this.quantity = stackSize;
            return sizeOver; // overflow (3 if still have 3 to deposit)
        } else if (this.quantity + quantity <= 0){ // <= 0 because want to null out stack
            int sizeUnder = this.quantity + quantity - 0;
            this.quantity = 0;
            this.item = null;
            resAmount = 0;
            return sizeUnder; // underflow (-3 if still need 3 more)
        } else {
            this.quantity += quantity; // add to stack
            if (resAmount > this.quantity) resAmount = this.quantity;
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


    public bool Available() => resAmount < quantity;
    public int Reserve(int n) {
        int amount = Math.Min(n, quantity - resAmount);
        if (amount <= 0) return 0;
        resAmount += amount;
        resTime = Time.time;
        return amount;
    }
    public bool Reserve() {
        if (!Available()) return false;
        resAmount++;
        resTime = Time.time;
        return true;
    }
    public void Unreserve(int n = 1) {
        if (resAmount < n) { Debug.LogError("ItemStack.Unreserve: underflow!"); resAmount = 0; return; }
        resAmount -= n;
    }
    public bool ExpireIfStale(float maxAge) {
        if (resAmount > 0 && Time.time - resTime > maxAge) {
            Debug.LogWarning($"Cleared stale ItemStack reservation (held {Time.time - resTime:F0}s)");
            resAmount = 0;
            return true;
        }
        return false;
    }

    public override string ToString(){
        if (item != null){
            string resStr = resAmount > 0 ? " (r" + FormatQ(resAmount, item.discrete) + ")" : "";
            return item.name + " x " + FormatQ(quantity, item.discrete) + resStr + "\n";
        }
        return "";
    }

}
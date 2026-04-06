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
    public int resAmount = 0;    // how much of this stack is reserved by pending tasks (source reservation)
    public float resTime = 0f;   // Time.time when last Reserve() was called (for staleness)
    public Task resTask;         // task that made the source reservation (for validity checking + logging)
    public int resSpace = 0;     // space reserved for incoming deliveries (destination reservation)
    public Item resSpaceItem;    // which item the space reservation is for (needed for empty stacks)
    public float resSpaceTime = 0f; // Time.time when last ReserveSpace() was called (for staleness)
    public Task resSpaceTask;    // task that made the space reservation (for validity checking + logging)

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
        if (item.discrete && quantity % 100 != 0){
            Debug.LogWarning($"Discrete item '{item.name}': non-whole-liang quantity {quantity} fen passed to AddItem");
            return 0; // discard the fractional item entirely
        }
        if (this.quantity + quantity > stackSize){
            int sizeOver = this.quantity + quantity - stackSize;
            this.quantity = stackSize;
            resSpace = 0; // stack is full, no space to reserve
            resSpaceItem = null;
            return sizeOver; // overflow (3 if still have 3 to deposit)
        } else if (this.quantity + quantity <= 0){ // <= 0 because want to null out stack
            int sizeUnder = this.quantity + quantity - 0;
            this.quantity = 0;
            this.item = null;
            resAmount = 0;
            resSpace = 0;
            resSpaceItem = null;
            return sizeUnder; // underflow (-3 if still need 3 more)
        } else {
            this.quantity += quantity; // add to stack
            if (resAmount > this.quantity) resAmount = this.quantity;
            // Clamp resSpace: someone else may have added items, reducing available space
            int maxResSpace = stackSize - this.quantity;
            if (resSpace > maxResSpace) resSpace = maxResSpace;
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


    // ── Source reservations (resAmount) ─────────────────────────
    public bool Available() => resAmount < quantity;
    public int Reserve(int n, Task by = null) {
        int amount = Math.Min(n, quantity - resAmount);
        if (amount <= 0) return 0;
        resAmount += amount;
        resTime = Time.time;
        resTask = by;
        return amount;
    }
    public bool Reserve(Task by = null) {
        if (!Available()) return false;
        resAmount++;
        resTime = Time.time;
        resTask = by;
        return true;
    }
    public void Unreserve(int n = 1) {
        if (resAmount < n) { Debug.LogError($"ItemStack.Unreserve: underflow! item={item?.name ?? "null"} resAmount={FormatQ(resAmount)} n={FormatQ(n)}"); resAmount = 0; resTask = null; return; }
        resAmount -= n;
        if (resAmount == 0) resTask = null;
    }

    // ── Destination reservations (resSpace) ──────────────────
    // How much free space is available for `item`, accounting for resSpace.
    public int FreeSpace(Item forItem) {
        if (item != null && item == forItem)
            return Math.Max(0, stackSize - quantity - resSpace);
        if (item == null || quantity == 0) {
            // Empty stack: available if unclaimed or claimed by the same item
            if (resSpaceItem == null || resSpaceItem == forItem)
                return Math.Max(0, stackSize - resSpace);
            return 0; // claimed by a different item
        }
        return 0; // occupied by a different item
    }
    // Reserves up to `n` units of free space for incoming `item`. Returns amount reserved.
    public int ReserveSpace(Item forItem, int n, Task by = null) {
        int free = FreeSpace(forItem);
        int amount = Math.Min(n, free);
        if (amount <= 0) return 0;
        resSpace += amount;
        resSpaceTime = Time.time;
        resSpaceTask = by;
        if (item == null && quantity == 0) resSpaceItem = forItem;
        return amount;
    }
    public void UnreserveSpace(int n) {
        if (resSpace < n) { Debug.LogError($"ItemStack.UnreserveSpace: underflow! resSpace={resSpace}, n={n}"); resSpace = 0; resSpaceItem = null; resSpaceTask = null; return; }
        resSpace -= n;
        if (resSpace == 0) { resSpaceItem = null; resSpaceTask = null; }
    }

    // ── Staleness expiry ─────────────────────────────────────
    // A reservation is only expired if BOTH conditions are met:
    //   1. Held longer than maxAge (time-based safety net)
    //   2. The task that made the reservation is no longer the animal's active task
    // This prevents false-positive expiry on legitimately long tasks (e.g. market journeys).
    private static bool TaskStillActive(Task t) => t != null && t.animal.task == t;

    public bool ExpireIfStale(float maxAge) {
        bool expired = false;
        if (resAmount > 0 && Time.time - resTime > maxAge && !TaskStillActive(resTask)) {
            string itemName = item?.name ?? "null";
            string by = resTask != null ? $" by {resTask.animal.aName}" : "";
            Debug.LogWarning($"Cleared stale ItemStack source reservation{by}: item={itemName} resAmount={FormatQ(resAmount)} qty={FormatQ(quantity)} inv={inv.invType} ({inv.x},{inv.y}) held={Time.time - resTime:F0}s");
            resAmount = 0;
            resTask = null;
            expired = true;
        }
        if (resSpace > 0 && Time.time - resSpaceTime > maxAge && !TaskStillActive(resSpaceTask)) {
            string itemName = resSpaceItem?.name ?? item?.name ?? "null";
            string by = resSpaceTask != null ? $" by {resSpaceTask.animal.aName}" : "";
            Debug.LogWarning($"Cleared stale ItemStack space reservation{by}: item={itemName} resSpace={FormatQ(resSpace)} qty={FormatQ(quantity)} inv={inv.invType} ({inv.x},{inv.y}) held={Time.time - resSpaceTime:F0}s");
            resSpace = 0;
            resSpaceItem = null;
            resSpaceTask = null;
            expired = true;
        }
        return expired;
    }

    public override string ToString(){
        if (item != null){
            string resStr = resAmount > 0 ? " (r" + FormatQ(resAmount, item.discrete) + ")" : "";
            string spcStr = resSpace > 0 ? " (s" + FormatQ(resSpace, item.discrete) + ")" : "";
            return item.name + " x " + FormatQ(quantity, item.discrete) + resStr + spcStr + "\n";
        }
        if (resSpace > 0 && resSpaceItem != null){
            return "(reserved for " + resSpaceItem.name + " s" + FormatQ(resSpace, resSpaceItem.discrete) + ")\n";
        }
        return "";
    }

}
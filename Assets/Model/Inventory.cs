using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;

public class Inventory{
    public int nStacks;
    public int stackSize; 
    public ItemStack[] itemStacks;
    public enum InvType {Floor, Storage, Animal, Market};
    public InvType invType;
    public Dictionary<int, bool> allowed;
    public string displayName = "storage";
    public GameObject go;
    private GameObject[] stackGos; // per-stack sprites for multi-stack storage (e.g. drawer)

    private static readonly Vector2[] quarterOffsets = {
        new Vector2(-0.25f,  0.25f), // top-left
        new Vector2( 0.25f,  0.25f), // top-right
        new Vector2(-0.25f, -0.25f), // bottom-left
        new Vector2( 0.25f, -0.25f), // bottom-right
    };

    public GlobalInventory ginv;

    // target quantity per item for the market; merchants aim to keep inventory at these levels.
    // null on non-market inventories.
    public Dictionary<Item, int> targets;
    // reserved incoming space per item; used when placing orders to guarantee room for incoming goods.
    public Dictionary<Item, Reservable> incomingRes;

    public void SetMarket() {
        invType = InvType.Market;
        targets = Db.itemsFlat.ToDictionary(i => i, i => 0);
        incomingRes = Db.itemsFlat.ToDictionary(i => i, i => new Reservable(9999));
    }

    // Returns unreserved space in this market inventory for the given item.
    public int GetMarketSpace(Item item) {
        int space = 0;
        foreach (ItemStack stack in itemStacks) {
            if (stack.item == null) space += stackSize;
            else if (stack.item == item && stack.quantity < stackSize) space += stackSize - stack.quantity;
        }
        return space - incomingRes[item].reserved;
    }

    public Inventory(int n = 1, int stackSize = 1000, InvType invType = InvType.Floor, int x = 0, int y = 0) {
        nStacks = n;
        this.stackSize = stackSize;
        this.invType = invType;
        itemStacks = new ItemStack[nStacks];
        for (int i = 0; i < nStacks; i++){
            itemStacks[i] = new ItemStack(this, null, 0, stackSize);
        }

        allowed = Db.itemsFlat.ToDictionary(i => i.id, i => true); // default all items allowed

        if (invType == InvType.Storage && nStacks > 1){
            // Multi-stack storage (drawer): one sprite per stack slot in a 2x2 grid
            stackGos = new GameObject[nStacks];
            for (int i = 0; i < nStacks && i < quarterOffsets.Length; i++){
                stackGos[i] = new GameObject("InventoryStack_" + i);
                stackGos[i].transform.position = new Vector3(x + quarterOffsets[i].x, y + quarterOffsets[i].y, 0);
                stackGos[i].transform.SetParent(WorldController.instance.transform, true);
                SpriteRenderer sr = stackGos[i].AddComponent<SpriteRenderer>();
                sr.sortingOrder = 30;
            }
        } else if (invType == InvType.Floor || invType == InvType.Storage){
            go = new GameObject();
            go.transform.position = new Vector3(x, y, 0);
            go.transform.SetParent(WorldController.instance.transform, true);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            if (invType == InvType.Floor) {sr.sortingOrder = 70;}
            else {sr.sortingOrder = 30;}
            sr.sprite = Resources.Load<Sprite>("Sprites/Inventory/" + invType.ToString());
        }

        InventoryController.instance.inventories.Add(this);
        ginv = GlobalInventory.instance;
    }
    public void Destroy(){
        if (go != null){GameObject.Destroy(go); go = null;}
        if (stackGos != null){
            foreach (GameObject sgo in stackGos){ if (sgo != null) GameObject.Destroy(sgo); }
            stackGos = null;
        }
        InventoryController.instance.inventories.Remove(this);
    }
    const int   ReservationExpireInterval = 120; // ticks between expiry sweeps per inventory
    const float ReservationMaxAge         = 60f; // seconds before a reservation is considered stale
    int _expireTick = 0;

    public void TickUpdate(){
        Decay();
        if (++_expireTick >= ReservationExpireInterval) {
            _expireTick = 0;
            foreach (ItemStack stack in itemStacks)
                stack?.res.ExpireIfStale(ReservationMaxAge);
        }
    }
    public void Decay(float time = 1f){
        float invTypeMult = 1f;
        if (invType == InvType.Floor){ invTypeMult = 5f; }
        if (invType == InvType.Storage){ invTypeMult = 1f; }
        if (invType == InvType.Animal){ invTypeMult = 1f; }
        for (int i = 0; i < nStacks; i++){
            itemStacks[i].Decay(invTypeMult * time);
        }
    }

    // =========================
    // ----- MOVING ITEMS ------
    // =========================

    // returns leftover size 
    private int AddItem(Item item, int quantity){
        if (item == null) {Debug.LogError("tried adding null item"); return quantity;}
        if (allowed[item.id] == false && quantity > 0){ 
            Debug.Log("tried adding unallowed item to inventory");
            return quantity;
        } 
        for (int i = 0; i < nStacks; i++){
            int? result = itemStacks[i].AddItem(item, quantity);
            // should probably just check if the itemstack is the right item instead of using this null thing.
            if (result == null){ continue; } // item slot occupied by different item. go next    
            quantity = result.Value; //set quantity to remaining size to get off
            if (quantity == 0){ break; }  // successfully added all items. stop.
        }
        UpdateSprite(); // this is a bit wasteful right now.
        return quantity; // leftover size
    }

    public int MoveItemTo(Inventory otherInv, Item item, int quantity){
        int taken = quantity + AddItem(item, -quantity);
        int overFill = otherInv.AddItem(item, taken);
        if (overFill > 0){
            AddItem(item, overFill); // return the item if recipient is full.
            // Debug.Log("moved " + taken + " from " + invType.ToString() + " to " + otherInv.invType.ToString()+ " and added back " + overFill);
        }
        return taken - overFill;
    }
    public int MoveItemTo(Inventory otherInv, string name, int quantity){return MoveItemTo(otherInv, Db.itemByName[name], quantity);}
    
    // adds to ginv. returns leftover size.
    public int Produce(Item item, int quantity = 1){
        int produced = quantity - AddItem(item, quantity);
        ginv.AddItem(item, produced);
        //Debug.Log("produced" + item.name + produced.ToString());
        return quantity - produced;
    }

    // =========================
    // ---- GETTING INFO -----
    // =========================
    public int Quantity(Item item){
        int amount = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item == item){
                amount += stack.quantity;
            }
        }
        return amount;
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
    public bool ContainsAvailableItem(Item item){
        if (item == null){ return !IsEmpty(); }
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item == item && stack.quantity > 0  && stack.res.Available()){
                return true;
            }
        }
        return false;
    }
    public bool ContainsItem(ItemQuantity iq, int n = 1){ return (Quantity(iq.item) >= iq.quantity*n);}
    public bool ContainsItems(ItemQuantity[] iqs, int n = 1){
        bool sufficient = true;
        foreach (ItemQuantity iq in iqs){
            if (Quantity(iq.item) < iq.quantity * n){
                sufficient = false;
            }
        }
        return sufficient;
    }
    public Item GetMostItem(){
        int most = 0;
        Item mostItem = null;
        foreach (ItemStack stack in itemStacks){
            if (stack.quantity > most){
                most = stack.quantity;
                mostItem = stack.item;
            }
        }
        return mostItem;
    }
    public ItemStack GetItemToHaul(){   // returns null if nothing, or item if something need to haul
        if (invType == InvType.Market) return null;
        foreach (ItemStack stack in itemStacks){
            if (!stack.Empty() && stack.res.Available() &&
                (allowed[stack.item.id] == false || invType == InvType.Floor)){
                return stack;
            }
        }
        return null;
    }
    public bool HasItemToHaul(Item item){ // if null, finds any item to haul
        if (invType == InvType.Market) return false;
        foreach (ItemStack stack in itemStacks){
            if ((item == null || stack.item == item) && stack.quantity > 0 && stack.res.Available() &&
                (allowed[stack.item.id] == false || invType == InvType.Floor)){
                return true;
            }
        }
        return false;
    }
    // How much space is available for item in this inventory (allowed Storage/Animal only).
    // Counts both empty stacks (any item could fill them) and partially-filled stacks of the same item.
    public int GetStorageForItem(Item item){
        if (invType == InvType.Market) return 0;
        if (allowed[item.id] == false || invType == InvType.Floor){return 0;}
        int space = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack.item == null){
                space += stackSize;
            } else if (stack.item == item && stack.quantity < stackSize){
                space += stackSize - stack.quantity;
            }
        }
        return space;
    }
    // Quantity not reserved by any task (usable for order placement checks).
    public int AvailableQuantity(Item item){
        int total = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack.item == item) total += Math.Max(0, stack.quantity - stack.res.reserved);
        }
        return total;
    }
    // Unlike GetStorageForItem, only checks stacks already holding this item (no empty stacks).
    // Use to top up an existing stack without claiming a new slot.
    public bool HasSpaceForItem(Item item){
        if (invType == InvType.Market) return false;
        foreach (ItemStack stack in itemStacks){
            if (stack.item == item && stack.quantity < stackSize){
                return true;
            }
        }
        return false;
    }
    public int GetSpace(){ // only coutns empty stacks
        int amount = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack.quantity == 0){
                amount += stack.stackSize;
            }
        }
        return amount;
    }
    public int GetFreeStacks() {
        int amount = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack.quantity == 0){
                amount += 1;
            }
        }
        return amount;
    }
    public ItemStack GetItemStack(Item item){
        ItemStack best = null;
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item == item && stack.quantity > 0 && stack.res.Available()){
                if (best == null || stack.quantity > best.quantity){
                    best = stack;
                }
            }
        }
        return best;
    }
    public bool HasDisallowedItem(){
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item != null && allowed[stack.item.id] == false){
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
    public List<Item> GetItemsList(){
        List<Item> items = new List<Item>();
        foreach (ItemStack stack in itemStacks) {
            if (stack != null && stack.item != null && stack.quantity > 0) {
                items.Add(stack.item);
            }
        }
        return items;
    }


    // =========================
    // ------ OTHER ------------
    // =========================

    public void Restack(){
        var restackedInventory = new ItemStack[itemStacks.Length];
        int index = 0;
        foreach (var stack in itemStacks){
            if (stack.quantity == 0) continue;

            var matchingStack = restackedInventory.FirstOrDefault(s => s != null && s.item == stack.item);
            if (matchingStack != null){
                int spaceAvailable = matchingStack.stackSize - matchingStack.quantity;
                int quantityToAdd = Math.Min(spaceAvailable, stack.quantity);

                matchingStack.quantity += quantityToAdd;
                stack.quantity -= quantityToAdd;

                if (stack.quantity > 0){
                    restackedInventory[index++] = new ItemStack(this, stack.item, stack.quantity, stackSize);
                }
            }
            else {
                restackedInventory[index++] = new ItemStack(this, stack.item, stack.quantity, stackSize);
            }
        }
        itemStacks = restackedInventory;
    }

    public void AllowItem(Item item){allowed[item.id] = true;}
    public void DisallowItem(Item item){allowed[item.id] = false;}
    public void ToggleAllowItem(Item item){allowed[item.id] = !allowed[item.id];
    Debug.Log("toggled " + item.name + " to " + allowed[item.id]);}

    public enum ItemSpriteType { Icon, Floor, Storage }

    public void UpdateSprite(){
        if (invType == InvType.Animal || invType == InvType.Market){return;}
        if (stackGos != null){
            // Multi-stack storage (drawer): update each slot independently
            for (int i = 0; i < nStacks && i < stackGos.Length; i++){
                if (stackGos[i] == null) continue;
                ItemStack stack = itemStacks[i];
                SpriteRenderer sr = stackGos[i].GetComponent<SpriteRenderer>();
                if (stack == null || stack.Empty()){
                    stackGos[i].name = "inventorystack_empty";
                    sr.sprite = null;
                    continue;
                }
                string sName = stack.item.name;
                Sprite sSprite = Resources.Load<Sprite>($"Sprites/Items/{sName}/qmid");
                sSprite ??= Resources.Load<Sprite>("Sprites/Items/defaultq");
                stackGos[i].name = "inventorystack_" + sName;
                sr.sprite = sSprite;
            }
            return;
        }
        if (IsEmpty()) {
            go.name = "inventory_empty";
            go.GetComponent<SpriteRenderer>().sprite = null;
            return;
        }
        Item mostItem = null;
        int mostAmount = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item != null && stack.quantity > mostAmount){
                mostItem = stack.item;
                mostAmount = stack.quantity;
            }
        }
        String iName = mostItem.name;
        Sprite sprite;
        if (invType == InvType.Floor) {
            sprite = Resources.Load<Sprite>($"Sprites/Items/{iName}/floor");
        } else if (invType == InvType.Storage){
            sprite = Resources.Load<Sprite>($"Sprites/Items/{iName}/smid");
        } else {
            sprite = Resources.Load<Sprite>($"Sprites/Items/{iName}/icon");
        }
        sprite ??= Resources.Load<Sprite>($"Sprites/Items/{iName}/icon");
        sprite ??= Resources.Load<Sprite>("Sprites/Items/default");
        go.name = "inventory_" + mostItem.name;
        go.GetComponent<SpriteRenderer>().sprite = sprite;
    }

    public override string ToString(){
        string str = "";
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.quantity > 0){
                str += stack.ToString();
            }
        }
        return str;
    }


}
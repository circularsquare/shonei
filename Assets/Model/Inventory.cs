using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;

public class Inventory{
    public int nStacks;
    public int stackSize; 
    public ItemStack[] itemStacks;
    public enum InvType {Floor, Storage, Animal, Market, Equip, Blueprint, Liquid};

    // Returns true if this item type is physically compatible with this inventory type.
    // This is a hard constraint checked before the per-item allowed[] dict.
    // Expand here as new item/inventory categories are added (e.g. gas, cold storage).
    public static bool ItemTypeCompatible(InvType invType, Item item) {
        if (invType == InvType.Storage && item.isLiquid)  return false; // dry storage can't hold liquids
        if (invType == InvType.Liquid  && !item.isLiquid) return false; // liquid storage can't hold solids
        return true;
    }
    public InvType invType;
    public int x, y;
    public Dictionary<int, bool> allowed;
    public bool locked = false; // when true, no items accepted and all existing items are treated as needing haul-out
    public string displayName;
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


    // Returns unreserved space in this market inventory for the given item.
    public int GetMarketSpace(Item item) {
        int space = 0;
        foreach (ItemStack stack in itemStacks) {
            if (stack.item == null) space += stackSize;
            else if (stack.item == item && stack.quantity < stackSize) space += stackSize - stack.quantity;
        }
        return space - incomingRes[item].reserved;
    }

    public Inventory(int n = 1, int stackSize = 2500, InvType invType = InvType.Floor, int x = 0, int y = 0) {
        nStacks = n;
        this.stackSize = stackSize;
        this.invType = invType;
        this.x = x;
        this.y = y;
        displayName = invType.ToString().ToLower();
        itemStacks = new ItemStack[nStacks];
        for (int i = 0; i < nStacks; i++){
            itemStacks[i] = new ItemStack(this, null, 0, stackSize);
        }

        if      (invType == InvType.Storage) allowed = Db.itemsFlat.ToDictionary(i => i.id, i => false);         // all disallowed by default; user enables per-item
        else if (invType == InvType.Liquid)  allowed = Db.itemsFlat.ToDictionary(i => i.id, i => i.isLiquid);   // liquid items allowed, solids disallowed (evicted if somehow placed)
        else                                 allowed = Db.itemsFlat.ToDictionary(i => i.id, i => true);
        

        if ((invType == InvType.Storage || invType == InvType.Liquid) && nStacks > 1){
            // Multi-stack storage (drawer/tank): one sprite per stack slot in a 2x2 grid
            stackGos = new GameObject[nStacks];
            for (int i = 0; i < nStacks && i < quarterOffsets.Length; i++){
                stackGos[i] = new GameObject("InventoryStack_" + i);
                stackGos[i].transform.position = new Vector3(x + quarterOffsets[i].x, y + quarterOffsets[i].y, 0);
                stackGos[i].transform.SetParent(InventoryController.instance.transform, true);
                SpriteRenderer sr = stackGos[i].AddComponent<SpriteRenderer>();
                sr.sortingOrder = 30;
            }
        } else if (invType == InvType.Floor || invType == InvType.Storage || invType == InvType.Liquid){
            go = new GameObject();
            go.transform.position = new Vector3(x, y, 0);
            go.transform.SetParent(InventoryController.instance.transform, true);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            if (invType == InvType.Floor) {sr.sortingOrder = 70;}
            else {sr.sortingOrder = 30;}
            sr.sprite = Resources.Load<Sprite>("Sprites/Inventory/" + invType.ToString());
        }

        if (invType == InvType.Market) {
            targets = Db.itemsFlat.ToDictionary(i => i, i => 0);
            incomingRes = Db.itemsFlat.ToDictionary(i => i, i => new Reservable(9999));
        }

        InventoryController.instance.AddInventory(this);
        ginv = GlobalInventory.instance;
    }
    public void Destroy(){
        // Eagerly remove haul orders for floor and storage stacks, then zero quantities as a safety net
        // for PruneStaleHauls (covers other inv types and any orders that slip through).
        if (invType == InvType.Floor || invType == InvType.Storage || invType == InvType.Liquid)
            foreach (ItemStack stack in itemStacks)
                WorkOrderManager.instance?.RemoveHaulForStack(stack);
        foreach (ItemStack stack in itemStacks) { stack.quantity = 0; stack.resAmount = 0; }
        if (go != null){GameObject.Destroy(go); go = null;}
        if (stackGos != null){
            foreach (GameObject sgo in stackGos){ if (sgo != null) GameObject.Destroy(sgo); }
            stackGos = null;
        }
        InventoryController.instance.RemoveInventory(this);
    }
    const int   ReservationExpireInterval = 120; // ticks between expiry sweeps per inventory
    const float ReservationMaxAge         = 60f; // seconds before a reservation is considered stale
    int _expireTick = 0;

    public void TickUpdate(){
        Decay();
        if (++_expireTick >= ReservationExpireInterval) {
            _expireTick = 0;
            foreach (ItemStack stack in itemStacks)
                stack?.ExpireIfStale(ReservationMaxAge);
        }
    }
    public void Decay(float time = 1f){
        float invTypeMult = invType switch {
            InvType.Floor      => 5f,
            InvType.Market     => 0f,
            InvType.Animal     => 0f,
            InvType.Equip      => 1f,
            InvType.Blueprint  => 0f,
            _                  => 1f
        };
        if (invTypeMult == 0f) return;
        for (int i = 0; i < nStacks; i++){
            itemStacks[i].Decay(invTypeMult * time);
        }
    }

    // =========================
    // ----- MOVING ITEMS ------
    // =========================

    // returns leftover size
    private int AddItem(Item item, int quantity, bool force = false){
        if (item == null) {Debug.LogError("tried adding null item"); return quantity;}
        if (item.children != null && item.children.Length > 0) {
            Debug.LogError($"Inventory.AddItem: '{item.name}' is a group item and cannot be added to inventories. Only leaf items may exist in inventories.");
            return quantity;
        }
        if (!force && !ItemTypeCompatible(invType, item) && quantity > 0){
            Debug.Log($"tried adding type-incompatible item {item.name} (isLiquid={item.isLiquid}) to {invType} '{displayName}' at ({x},{y})");
            return quantity;
        }
        if (!force && (locked || allowed[item.id] == false) && quantity > 0){
            string reason = locked ? "locked" : "disallowed";
            Debug.Log($"tried adding {reason} item {item.name} to {invType} '{displayName}' at ({x},{y})");
            return quantity;
        }
        // Build iteration order for consolidation:
        // 1. Existing stacks holding this item, fullest first (top off rather than spreading to a new slot)
        // 2. Empty stacks in original order (overflow into fresh slots as needed)
        // Stacks occupied by a different item are skipped — ItemStack.AddItem returns null for them anyway.
        var matchingIndices = new List<(int idx, int qty)>();
        var emptyIndices = new List<int>();
        for (int i = 0; i < nStacks; i++) {
            if (itemStacks[i].item == item) matchingIndices.Add((i, itemStacks[i].quantity));
            else if (itemStacks[i].item == null) emptyIndices.Add(i);
        }
        matchingIndices.Sort((a, b) => b.qty.CompareTo(a.qty)); // fullest first

        var sortedIndices = new List<int>(nStacks);
        foreach (var (idx, _) in matchingIndices) sortedIndices.Add(idx);
        sortedIndices.AddRange(emptyIndices);

        foreach (int i in sortedIndices){
            Item prevItem = itemStacks[i].item;
            int? result = itemStacks[i].AddItem(item, quantity);
            if (result == null){ continue; } // shouldn't happen with the pre-filter, but guard anyway
            // Floor haul side effects: remove order when stack empties, register when items arrive.
            if (invType == InvType.Floor) {
                if (prevItem != null && itemStacks[i].item == null)
                    WorkOrderManager.instance?.RemoveHaulForStack(itemStacks[i]);
                else if (quantity > 0 && itemStacks[i].quantity > 0)
                    WorkOrderManager.instance?.RegisterHaul(itemStacks[i]);
            }
            // Storage/Liquid eviction side effects.
            if (invType == InvType.Storage || invType == InvType.Liquid) {
                if (prevItem != null && itemStacks[i].item == null)
                    WorkOrderManager.instance?.RemoveHaulForStack(itemStacks[i]); // stack emptied — order no longer needed
                else if (force && allowed[item.id] == false && itemStacks[i].quantity > 0)
                    WorkOrderManager.instance?.RegisterStorageEvictionHaul(itemStacks[i]); // disallowed item force-added
            }
            quantity = result.Value; //set quantity to remaining size to get off
            if (quantity == 0){ break; }  // successfully added all items. stop.
        }
        UpdateSprite(); // this is a bit wasteful right now.
        return quantity; // leftover size
    }

    public int MoveItemTo(Inventory otherInv, Item item, int quantity){
        // Group items (e.g. "wood") can't exist as physical stacks — resolve to the best available leaf.
        if (item.children != null) {
            ItemStack stack = GetItemStack(item);
            if (stack == null) return quantity; // nothing available
            item = stack.item;
        }
        int taken = quantity + AddItem(item, -quantity);
        int overFill = otherInv.AddItem(item, taken);
        if (overFill > 0){
            // Force the return so disallowed/locked source invs don't silently eat the leftover.
            // Items came from this inventory, so there is always room — LogError if not.
            int stillLost = AddItem(item, overFill, force: true);
            if (stillLost > 0)
                Debug.LogError($"MoveItemTo: {stillLost} fen of {item.name} lost returning to {invType} inv at ({x},{y}) — source had no room!");
        }
        int moved = taken - overFill;
        if (otherInv.invType == InvType.Market && moved > 0)
            WorkOrderManager.instance?.UpdateMarketOrders(otherInv);
        if (invType == InvType.Market && moved > 0)
            WorkOrderManager.instance?.UpdateMarketOrders(this);
        return moved;
    }
    public int MoveItemTo(Inventory otherInv, string name, int quantity){return MoveItemTo(otherInv, Db.itemByName[name], quantity);}

    // Like MoveItemTo but bypasses the allowed filter on the destination — use when items must not be lost
    // (e.g. migrating a floor inventory into a newly-placed storage building).
    // Haulers will eventually move the item out once they notice it is disallowed.
    // Do NOT use with market inventories — it skips UpdateMarketOrders.
    public int ForceMoveItemTo(Inventory otherInv, Item item, int quantity){
        int taken = quantity + AddItem(item, -quantity);
        int overFill = otherInv.AddItem(item, taken, force: true);
        if (overFill > 0){
            int stillLost = AddItem(item, overFill, force: true);
            if (stillLost > 0)
                Debug.LogError($"ForceMoveItemTo: {stillLost} fen of {item.name} lost returning to {invType} inv at ({x},{y}) — source had no room!");
        }
        return taken - overFill;
    }
    
    // adds to ginv. returns leftover size.
    public int Produce(Item item, int quantity = 1){
        int produced = quantity - AddItem(item, quantity);
        ginv.AddItem(item, produced);
        //Debug.Log("produced" + item.name + produced.ToString());
        if (invType == InvType.Market)
            WorkOrderManager.instance?.UpdateMarketOrders(this);
        return quantity - produced;
    }

    // =========================
    // ---- GETTING INFO -----
    // =========================
    // Returns true if `candidate` is `query` itself or any leaf descendant of it.
    // Used so that group items (e.g. "wood") act as wildcards matching any child (e.g. "oak", "pine").
    static bool MatchesItem(Item candidate, Item query) {
        if (candidate == query) return true;
        if (query.children == null) return false;
        foreach (Item child in query.children)
            if (MatchesItem(candidate, child)) return true;
        return false;
    }

    public int Quantity(Item item){
        int amount = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item != null && MatchesItem(stack.item, item)){
                amount += stack.quantity;
            }
        }
        return amount;
    }
    public bool ContainsAvailableItem(Item item){
        if (item == null){ return !IsEmpty(); }
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item != null && stack.quantity > 0 && stack.Available() && MatchesItem(stack.item, item)){
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
        if (invType == InvType.Market || invType == InvType.Blueprint) return null;
        foreach (ItemStack stack in itemStacks){
            if (!stack.Empty() && stack.Available() &&
                (locked || allowed[stack.item.id] == false || invType == InvType.Floor)){
                return stack;
            }
        }
        return null;
    }
    public bool HasItemToHaul(Item item){ // if null, finds any item to haul
        if (invType == InvType.Market || invType == InvType.Blueprint) return false;
        foreach (ItemStack stack in itemStacks){
            if ((item == null || stack.item == item) && stack.quantity > 0 && stack.Available() &&
                (locked || allowed[stack.item.id] == false || invType == InvType.Floor)){
                return true;
            }
        }
        return false;
    }
    // How much space is available for item in this inventory (allowed Storage/Animal only).
    // Counts both empty stacks (any item could fill them) and partially-filled stacks of the same item.
    public int GetStorageForItem(Item item){
        if (invType == InvType.Market || invType == InvType.Blueprint) return 0;
        if (!ItemTypeCompatible(invType, item)) return 0;
        if (locked || allowed[item.id] == false || invType == InvType.Floor){return 0;}
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
            if (stack.item != null && MatchesItem(stack.item, item)) total += Math.Max(0, stack.quantity - stack.resAmount);
        }
        return total;
    }
    // Space in an existing partial stack of `item` (for floor consolidation).
    public int GetMergeSpace(Item item) {
        if (!ItemTypeCompatible(invType, item)) return 0;
        if (locked || allowed[item.id] == false) return 0;
        foreach (ItemStack stack in itemStacks)
            if (stack.item == item && stack.quantity < stackSize)
                return stackSize - stack.quantity;
        return 0;
    }
    // Unlike GetStorageForItem, only checks stacks already holding this item (no empty stacks).
    // Use to top up an existing stack without claiming a new slot.
    public bool HasSpaceForItem(Item item){
        if (locked || invType == InvType.Market || invType == InvType.Blueprint) return false;
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
    public ItemStack GetItemStack(Item item){
        ItemStack best = null;
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item != null && stack.quantity > 0 && stack.Available() && MatchesItem(stack.item, item)){
                if (best == null || stack.quantity < best.quantity){ // prefer smallest — drains thin stacks first
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

    public void AllowItem(Item item){
        allowed[item.id] = true;
        // Remove any pending eviction haul orders — item is welcome here again.
        if (invType == InvType.Storage || invType == InvType.Liquid)
            foreach (ItemStack stack in itemStacks)
                if (stack.item == item)
                    WorkOrderManager.instance?.RemoveHaulForStack(stack);
    }
    public void DisallowItem(Item item){
        allowed[item.id] = false;
        // Register eviction hauls for any stacks of this item already in this inventory.
        if (invType == InvType.Storage || invType == InvType.Liquid)
            foreach (ItemStack stack in itemStacks)
                if (stack.item == item && stack.quantity > 0)
                    WorkOrderManager.instance?.RegisterStorageEvictionHaul(stack);
    }
    public void ToggleAllowItem(Item item){
        allowed[item.id] = !allowed[item.id];
        if (invType == InvType.Storage || invType == InvType.Liquid) {
            foreach (ItemStack stack in itemStacks) {
                if (stack.item != item) continue;
                if (allowed[item.id] == false && stack.quantity > 0)
                    WorkOrderManager.instance?.RegisterStorageEvictionHaul(stack);
                else
                    WorkOrderManager.instance?.RemoveHaulForStack(stack);
            }
        }
    }
    /// <summary>Toggles item and all its descendant leaf items together.</summary>
    public void ToggleAllowItemWithChildren(Item item){
        bool newState = !allowed[item.id];
        SetAllowRecursive(item, newState);
    }
    void SetAllowRecursive(Item item, bool state){
        if (state) AllowItem(item);
        else       DisallowItem(item);
        if (item.children != null)
            foreach (Item child in item.children)
                SetAllowRecursive(child, state);
    }
    /// <summary>Allow all compatible items in this inventory.</summary>
    public void AllowAll(){
        foreach (Item item in Db.itemsFlat) {
            if (item == null) continue;
            if (!ItemTypeCompatible(invType, item)) continue;
            AllowItem(item);
        }
    }
    /// <summary>Disallow all items in this inventory.</summary>
    public void DenyAll(){
        foreach (Item item in Db.itemsFlat) {
            if (item == null) continue;
            DisallowItem(item);
        }
    }
    /// <summary>Copies allowed state from another inventory's allowed dictionary.</summary>
    public void PasteAllowed(Dictionary<int, bool> source){
        foreach (var kvp in source) {
            if (!allowed.ContainsKey(kvp.Key)) continue;
            if (kvp.Value) AllowItem(Db.items[kvp.Key]);
            else           DisallowItem(Db.items[kvp.Key]);
        }
    }

    public enum ItemSpriteType { Icon, Floor, Storage }

    public void UpdateSprite(){
        if (invType == InvType.Animal || invType == InvType.Market || invType == InvType.Equip || invType == InvType.Blueprint) return;
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
                string sName = stack.item.name.Trim().Replace(" ", "");
                float qFill = stack.quantity / (float)stack.stackSize;
                string qVariant = qFill >= 0.75f ? "qhigh" : qFill < 0.2f ? "qlow" : "qmid";
                Sprite sSprite  = Resources.Load<Sprite>($"Sprites/Items/{sName}/{qVariant}");
                sSprite ??= Resources.Load<Sprite>($"Sprites/Items/{sName}/qmid");
                sSprite ??= Resources.Load<Sprite>($"Sprites/Items/default/{qVariant}");
                sSprite ??= Resources.Load<Sprite>("Sprites/Items/default/qmid");
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
        String iName = mostItem.name.Trim().Replace(" ", "");
        float fill = mostAmount / (float)stackSize;
        Sprite sprite;
        if (invType == InvType.Floor) {
            sprite = Resources.Load<Sprite>($"Sprites/Items/{iName}/floor");
        } else if (invType == InvType.Storage || invType == InvType.Liquid) {
            string sVariant = fill >= 0.75f ? "shigh" : fill < 0.2f ? "slow" : "smid";
            sprite  = Resources.Load<Sprite>($"Sprites/Items/{iName}/{sVariant}");
            sprite ??= Resources.Load<Sprite>($"Sprites/Items/{iName}/smid");
            sprite ??= Resources.Load<Sprite>($"Sprites/Items/default/{sVariant}");
            sprite ??= Resources.Load<Sprite>("Sprites/Items/default/smid");
        } else {
            sprite = Resources.Load<Sprite>($"Sprites/Items/{iName}/icon");
        }
        sprite ??= Resources.Load<Sprite>($"Sprites/Items/{iName}/icon");
        sprite ??= Resources.Load<Sprite>("Sprites/Items/default/icon");
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
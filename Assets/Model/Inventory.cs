using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;

public class Inventory
{
    public int nStacks;
    public int stackSize; 
    public ItemStack[] itemStacks;
    public enum InvType {Floor, Storage, Animal};
    public InvType invType;
    public Dictionary<int, bool> allowed;
    public GameObject go;

    public GlobalInventory ginv;

    public Inventory(int n = 1, int stackSize = 20, InvType invType = InvType.Floor, int x = 0, int y = 0) {
        nStacks = n;
        this.stackSize = stackSize;
        this.invType = invType;
        itemStacks = new ItemStack[nStacks];
        for (int i = 0; i < nStacks; i++){
            itemStacks[i] = new ItemStack(this, null, 0, stackSize);
        }

        allowed = Db.itemsFlat.ToDictionary(i => i.id, i => true); // default all items allowed


        if (invType == InvType.Floor || invType == InvType.Storage){
            go = new GameObject();
            go.transform.position = new Vector3(x, y, 0);
            go.transform.SetParent(WorldController.instance.transform, true);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 30;

            sr.sprite = Resources.Load<Sprite>("Sprites/Inventory/" + invType.ToString());
        }

        InventoryController.instance.inventories.Add(this);
        ginv = GlobalInventory.instance;
    }
    public void Destroy(){
        if (go != null){GameObject.Destroy(go); go = null;}
        InventoryController.instance.inventories.Remove(this);
    }
    public void TickUpdate(){
        Decay();
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
    public int AddItem(Item item, int quantity){
        if (item == null) {Debug.LogError("tried adding null item"); return quantity;}
        if (allowed[item.id] == false && quantity > 0){  // allowed is not implemented yet... for limiting inventories to certian types of resource
            Debug.Log("tried adding unallowed item to inventory");
            return quantity;
        } // don't add if not allowed
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
    public int AddItem(string name, int quantity){return(AddItem(Db.itemByName[name], quantity));}
    public int TakeItem(Item item, int quantity){
        return AddItem(item, -quantity);
    }
    public int TakeItem(string name, int quantity){return AddItem(name, -quantity);}
    public int MoveItemTo(Inventory otherInv, Item item, int quantity){
        int taken = quantity + TakeItem(item, quantity);
        int overFill = otherInv.AddItem(item, taken);
        if (overFill > 0){
            AddItem(item, overFill); // return the item if recipient is full.
            // Debug.Log("moved " + taken + " from " + invType.ToString() + " to " + otherInv.invType.ToString()+ " and added back " + overFill);
        }
        return taken - overFill;
    }
    public int MoveItemTo(Inventory otherInv, string name, int quantity){return MoveItemTo(otherInv, Db.itemByName[name], quantity);}

    // adds to ginv too
    // returns LEFTOVER size
    public int Produce(Item item, int quantity = 1){
        int produced = quantity - AddItem(item, quantity);
        ginv.AddItem(item, produced);
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
    public ItemStack GetItemToHaul(){   // returns null if nothing, or item if something need to haul
        foreach (ItemStack stack in itemStacks){
            if (!stack.Empty() && stack.res.Available() &&
                (allowed[stack.item.id] == false || invType == InvType.Floor)){
                return stack;
            }
        }
        return null;
    }
    public bool HasItemToHaul(Item item){ // if null, finds any item to haul
        foreach (ItemStack stack in itemStacks){
            if ((item == null || stack.item == item) && stack.quantity > 0 && stack.res.Available() &&
                (allowed[stack.item.id] == false || invType == InvType.Floor)){
                return true;
            }
        }
        return false;
    }
    public int GetStorageForItem(Item item){
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
    public bool HasStorageForItem(Item item){return (GetStorageForItem(item) > 0);}
    public bool HasSpaceForItem(Item item){
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

    public void UpdateSprite(){
        if (invType == InvType.Animal){return;} // animal invs don't have game objects.
        if (IsEmpty()) {
            go.name = "InventoryEmpty";
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
        go.name = "Inventory" + mostItem.name;
        Sprite sprite = Resources.Load<Sprite>("Sprites/Items/" + mostItem.name);
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Items/default");
        }
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
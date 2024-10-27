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

    public Inventory(int n = 1, int stackSize = 20, InvType invType = InvType.Floor, int x = 0, int y = 0) {
        nStacks = n;
        this.stackSize = stackSize;
        this.invType = invType;
        itemStacks = new ItemStack[nStacks];
        for (int i = 0; i < nStacks; i++){
            itemStacks[i] = new ItemStack(null, 0, stackSize);
        }

        allowed = Db.itemsFlat.ToDictionary(i => i.id, i => true); // default all items allowed


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
        if (allowed[item.id] == false && quantity > 0){ 
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
        if (quantity != 0){Debug.Log("quantity left is " + quantity);}
        return quantity; // leftover size
    }
    public int AddItem(string name, int quantity){return(AddItem(Db.itemByName[name], quantity));}
    public void AddItems(ItemQuantity[] iqs, bool negate = false){
        int negateNum = 1;
        if (negate){ negateNum = -1; }
        foreach (ItemQuantity iq in iqs){
            if (AddItem(iq.item, negateNum * iq.quantity) != 0){
                Debug.LogError("failed to add items!" + iq.item.ToString());
            } // this shoudl be using stacks????
        }
    }
    public int TakeItem(Item item, int quantity){return AddItem(item, -quantity);}
    public int TakeItem(string name, int quantity){return AddItem(name, -quantity);}
    public int MoveItemTo(Inventory otherInv, Item item, int quantity){
        int taken = quantity + TakeItem(item, quantity);
        int overFill = otherInv.AddItem(item, taken);
        if (overFill > 0){
            AddItem(item, overFill); // return the item if recipient is full.
        }
        Debug.Log("moved " + taken + " from " + invType.ToString() + " to " + otherInv.invType.ToString()+ " addedback " + overFill);

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
    public bool ContainsItem(ItemQuantity iq){ return (Quantity(iq.item) >= iq.quantity);}
    public bool ContainsItems(ItemQuantity[] iqs){
        bool sufficient = true;
        foreach (ItemQuantity iq in iqs){
            if (Quantity(iq.item) < iq.quantity){
                sufficient = false;
            }
        }
        return sufficient;
    }
    public int Quantity(Item item){
        int amount = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item == item){
                amount += stack.quantity;
            }
        }
        return amount;
    }
    public bool HasSpaceForItem(Item item){
        if (allowed[item.id] == false){return false;}
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


}
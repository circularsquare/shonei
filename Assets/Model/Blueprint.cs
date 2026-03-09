using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class Blueprint {
    public GameObject go;
    public int x;
    public int y;
    public StructType structType;
    public Sprite sprite;
    public Tile tile; // not really sure how this will work for multi-tile buildings...

    public Inventory inv;  // holds delivered materials; not tracked by InventoryController
    public ItemQuantity[] costs;
    public float constructionCost;
    public float constructionProgress = 0f;
    public enum BlueprintState { Receiving, Constructing, Deconstructing}
    public BlueprintState state = BlueprintState.Receiving;
    public Reservable res;
    public bool cancelled = false;
    public int priority = 0;

    public Blueprint(StructType structType, int x, int y){
        this.structType = structType;
        this.x = x;
        this.y = y;
        this.tile = World.instance.GetTileAt(x, y);
        tile.SetBlueprintAt(structType.depth, this);
        res = new Reservable(1);

        if (structType.constructionCost == 0f){
            constructionCost = 2f; // default
        } else {
            constructionCost = structType.constructionCost;
        }

        go = new GameObject();
        go.transform.position = new Vector3(x, y, 0);
        go.transform.SetParent(WorldController.instance.transform, true);
        go.name = "blueprint_" + structType.name;

        sprite = Resources.Load<Sprite>("Sprites/Buildings/" + structType.name.Replace(" ", ""));
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Buildings/default");}
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = 100;
        sr.sprite = sprite;
        sr.color = new Color(0.8f, 0.9f, 1f, 0.5f); // blueprint half alpha
        if (state == BlueprintState.Deconstructing) {
            sr.color = new Color(1f, 0.3f, 0.3f, 0.8f);
        }

        costs = structType.costs;
        // One stack per cost item, capacity capped to exactly that item's cost quantity.
        inv = new Inventory(Math.Max(1, costs.Length), 0, Inventory.InvType.Animal, x, y);
        for (int i = 0; i < costs.Length; i++)
            inv.itemStacks[i].stackSize = costs[i].quantity;
        InventoryController.instance.inventories.Remove(inv);

        if (structType.costs.Length == 0){ state = BlueprintState.Constructing; }
    }
    public static Blueprint CreateDeconstructBlueprint(Tile tile) {
        Structure structure = tile.building ?? tile.mStruct ?? tile.fStruct;
        if (structure == null) return null;
        Blueprint bp = new Blueprint(structure.structType, tile.x, tile.y);
        bp.state = BlueprintState.Deconstructing;
        bp.go.GetComponent<SpriteRenderer>().color = new Color(1f, 0.3f, 0.3f, 0.5f);
        return bp;
    }

    public bool IsFullyDelivered() {
        foreach (var cost in costs)
            if (inv.Quantity(cost.item) < cost.quantity) return false;
        return true;
    }

    public bool ReceiveConstruction(float progress){ // returns whether you just finished
        if (state == BlueprintState.Receiving) { Debug.LogError("Blueprint is not in Constructing state"); return true;}
        constructionProgress += progress;
        if (constructionProgress >= constructionCost){
            if (state == BlueprintState.Constructing) {
                Complete();
                return true;
            } else if (state == BlueprintState.Deconstructing) {
                Deconstruct();
                return true;
            }
        }
        return false;
    }

    public void Complete(){
        // Consume delivered materials — removes them from globalInv now that they're used up
        foreach (var cost in costs)
            inv.Produce(cost.item, -inv.Quantity(cost.item));
        StructController.instance.Construct(structType, tile);
        tile.SetBlueprintAt(structType.depth, null);
        GameObject.Destroy(go);
    }
    public void Deconstruct() {
        foreach (ItemQuantity cost in costs) {
            int amount = Mathf.FloorToInt(cost.quantity / 2f);
            if (amount > 0) {
                tile.EnsureFloorInventory().Produce(cost.item, amount);
                // TODO: this wont actually work if multiple items need to be dropped
            }
        }
        // destroy the building
        if (tile.building != null) { tile.building.Destroy(); tile.building = null; }
        else if (tile.mStruct != null) { tile.mStruct.Destroy(); tile.mStruct = null; }
        else if (tile.fStruct != null) { tile.fStruct.Destroy(); tile.fStruct = null; }
        // remove blueprint
        tile.SetBlueprintAt(structType.depth, null);
        GameObject.Destroy(go);
    }

    public void Destroy() {
        cancelled = true;
        tile.SetBlueprintAt(structType.depth, null);
        GameObject.Destroy(go);
    }

    public string GetProgress(){ // for display string
        string progress = "";
        for (int i = 0; i < costs.Length; i++) {
            progress += costs[i].item.name + " " + ItemStack.FormatQ(inv.Quantity(costs[i].item)) + "/" + ItemStack.FormatQ(costs[i].quantity);
        }
        if (state == BlueprintState.Constructing){
            progress += " (" + constructionProgress.ToString() + "/" + constructionCost.ToString() + ")";
        }
        return progress;
    }
}

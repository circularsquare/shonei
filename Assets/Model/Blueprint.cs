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

    public ItemQuantity[] deliveredResources;
    public ItemQuantity[] costs;
    public float constructionCost;
    public float constructionProgress = 0f; 
    public enum BlueprintState { Receiving, Constructing, Deconstructing}
    public BlueprintState state = BlueprintState.Receiving;
    public Reservable res;

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

        deliveredResources = new ItemQuantity[structType.costs.Length];
        for (int i = 0; i < structType.costs.Length; i++){
            deliveredResources[i] = new ItemQuantity(structType.costs[i].item, 0);
        }
        costs = structType.costs;

        if (structType.costs.Length == 0){ state = BlueprintState.Constructing; }
        // register callback to update sprite?
    }
    public static Blueprint CreateDeconstructBlueprint(Tile tile) {
        Structure structure = tile.building ?? tile.mStruct ?? tile.fStruct;
        if (structure == null) return null;
        Blueprint bp = new Blueprint(structure.structType, tile.x, tile.y);
        bp.state = BlueprintState.Deconstructing;
        bp.go.GetComponent<SpriteRenderer>().color = new Color(1f, 0.3f, 0.3f, 0.5f);
        return bp;
    }

    public int ReceiveResource(Item item, int quantity){
        if (state != BlueprintState.Receiving) { Debug.LogError("Blueprint is not in Receiving state"); return 0;}
        // this maybe should be using itemstacks instead of item quantitys.     
        int delivered = 0;
        for (int i = 0; i < deliveredResources.Length; i++) {
            if (deliveredResources[i].item == item) {
                delivered = Math.Min(quantity, costs[i].quantity - deliveredResources[i].quantity);
                deliveredResources[i].quantity += delivered;
                break;
            }
        }
        // check if construction is complete 
        for (int i = 0; i < deliveredResources.Length; i++) {
            if (deliveredResources[i].quantity < costs[i].quantity) {
                break;
            }
            if (i == deliveredResources.Length - 1) { 
                state = BlueprintState.Constructing;
            }
        }
        return delivered;
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
        StructController.instance.Construct(structType, tile);
        // delete blueprint
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
        tile.SetBlueprintAt(structType.depth, null);
        GameObject.Destroy(go);
    }

    public string GetProgress(){ // for display string
        string progress = "";
        for (int i = 0; i < deliveredResources.Length; i++) {
            progress += (deliveredResources[i].ItemName() 
                + deliveredResources[i].quantity.ToString() 
                + "/" + costs[i].quantity.ToString());
        }
        if (state == BlueprintState.Constructing){
            progress += " (" + constructionProgress.ToString() + "/" + constructionCost.ToString() + ")";
        }
        return progress;
    }
}
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

    public Inventory inv;  // holds delivered materials; InvType.Blueprint keeps it out of haul/consolidate searches
    public ItemQuantity[] costs;
    public float constructionCost;
    public float constructionProgress = 0f;
    public enum BlueprintState { Receiving, Constructing, Deconstructing}
    public BlueprintState state = BlueprintState.Receiving;
    public bool cancelled = false;
    public int priority = 0;
    // Items to give to the completing animal after construction/deconstruction finishes.
    // Set by StructController.Construct (mining output) or Deconstruct (refunded materials).
    public List<ItemQuantity> pendingOutput;

    public Blueprint(StructType structType, int x, int y, bool autoRegister = true){
        this.structType = structType;
        this.x = x;
        this.y = y;
        this.tile = World.instance.GetTileAt(x, y);
        tile.SetBlueprintAt(structType.depth, this);

        if (structType.constructionCost == 0f){
            constructionCost = 2f; // default
        } else {
            constructionCost = structType.constructionCost;
        }

        go = new GameObject();
        float visualX = structType.nx > 1 ? x + (structType.nx - 1) / 2.0f : x;
        go.transform.position = structType.depth == 3
            ? new Vector3(x, y - 1f/8f, 0)
            : new Vector3(visualX, y, 0);
        go.transform.SetParent(StructController.instance.transform, true);
        go.name = "blueprint_" + structType.name;

        sprite = structType.LoadSprite() ?? Resources.Load<Sprite>("Sprites/Buildings/default");
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = 100;
        sr.sprite = sprite;
        sr.color = new Color(0.8f, 0.9f, 1f, 0.5f); // blueprint half alpha

        costs = structType.costs;
        // One stack per cost item, capacity capped to exactly that item's cost quantity.
        inv = new Inventory(Math.Max(1, costs.Length), 0, Inventory.InvType.Blueprint, x, y);
        for (int i = 0; i < costs.Length; i++)
            inv.itemStacks[i].stackSize = costs[i].quantity;

        if (autoRegister) {
            if (structType.costs.Length == 0) {
                state = BlueprintState.Constructing;
                WorkOrderManager.instance?.RegisterConstruct(this);
            } else {
                WorkOrderManager.instance?.RegisterSupplyBlueprint(this);
            }
        }
        StructController.instance.AddBlueprint(this);
    }
    public void RefreshColor() {
        var color = state == BlueprintState.Deconstructing
            ? new Color(1f, 0.3f, 0.3f, 0.5f)
            : new Color(0.8f, 0.9f, 1f, 0.5f);
        go.GetComponent<SpriteRenderer>().color = color;
    }

    public static Blueprint CreateDeconstructBlueprint(Tile tile) {
        Structure structure = tile.structs[0] ?? tile.structs[1] ?? tile.structs[2] ?? tile.structs[3];
        if (structure == null) return null;
        Blueprint bp = new Blueprint(structure.structType, tile.x, tile.y, autoRegister: false);
        bp.state = BlueprintState.Deconstructing;
        bp.RefreshColor();
        WorkOrderManager.instance?.RegisterDeconstruct(bp);
        if (tile.building?.storage != null)
            tile.building.storage.locked = true;
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
        StructController.instance.RemoveBlueprint(this);
        WorkOrderManager.instance?.RemoveForBlueprint(this);
        // Consume delivered materials — removes them from globalInv now that they're used up
        foreach (var cost in costs)
            inv.Produce(cost.item, -inv.Quantity(cost.item));
        // Capture tile products before Construct() changes the tile type
        if (structType.isTile && structType.name == "empty" && tile.type.products != null)
            pendingOutput = new List<ItemQuantity>(tile.type.products);
        StructController.instance.Construct(structType, tile);
        tile.SetBlueprintAt(structType.depth, null);
        GameObject.Destroy(go);
        if (InfoPanel.instance?.obj == tile) InfoPanel.instance.UpdateInfo();
    }
    public void Deconstruct() {
        StructController.instance.RemoveBlueprint(this);
        WorkOrderManager.instance?.RemoveForBlueprint(this);
        pendingOutput = new List<ItemQuantity>(); // given in asm.handleworking
        foreach (ItemQuantity cost in costs) {
            int amount = Mathf.FloorToInt(cost.quantity / 2f);
            if (amount > 0)
                pendingOutput.Add(new ItemQuantity(cost.item, amount));
        }
        // destroy the building
        for (int i = 0; i < 4; i++) { if (tile.structs[i] != null) { tile.structs[i].Destroy(); break; } }
        // remove blueprint
        tile.SetBlueprintAt(structType.depth, null);
        GameObject.Destroy(go);
        if (InfoPanel.instance?.obj == tile) InfoPanel.instance.UpdateInfo();
    }

    // Returns true if this is a deconstruct blueprint on a storage building and the storage still has items.
    // Deconstruction must wait until the storage is emptied by haulers.
    public bool StorageNeedsEmptying() {
        return state == BlueprintState.Deconstructing
            && tile.building?.storage != null
            && !tile.building.storage.IsEmpty();
    }

    // Returns true if completing this blueprint would cause items on the tile(s) above to lose
    // standability and fall. Uses the same logic as Navigation.GetStandability().
    public bool WouldCauseItemsFall() {
        World world = World.instance;
        for (int i = 0; i < structType.nx; i++) {
            int bx = tile.x + i, by = tile.y;
            Tile above = world.GetTileAt(bx, by + 1);
            if (above == null || above.inv == null || above.inv.IsEmpty()) continue;
            if (!world.graph.nodes[bx, by + 1].standable) continue;

            Tile tileBelow = world.GetTileAt(bx, by);

            bool solidTileAfter = structType.isTile
                ? Db.tileTypeByName[structType.name].solid
                : tileBelow.type.solid;

            bool anySolidTopAfter = false;
            for (int d = 0; d < 4; d++) {
                bool solidTop = structType.depth == d
                    ? (state == BlueprintState.Constructing && structType.solidTop)
                    : (tileBelow.structs[d] != null && tileBelow.structs[d].structType.solidTop);
                if (solidTop) { anySolidTopAfter = true; break; }
            }

            bool ladderSupport = above.HasLadder() || tileBelow.HasLadder();

            if (!(solidTileAfter || anySolidTopAfter || ladderSupport))
                return true;
        }
        return false;
    }

    public void Destroy() {
        StructController.instance.RemoveBlueprint(this);
        WorkOrderManager.instance?.RemoveForBlueprint(this);
        cancelled = true;
        if (state == BlueprintState.Deconstructing && tile.building?.storage != null)
            tile.building.storage.locked = false;
        tile.SetBlueprintAt(structType.depth, null);
        GameObject.Destroy(go);
    }

    public string GetProgress(){ // for display string
        string progress = "";
        for (int i = 0; i < costs.Length; i++) {
            progress += costs[i].item.name + " " + ItemStack.FormatQ(inv.Quantity(costs[i].item), costs[i].item.discrete) + "/" + ItemStack.FormatQ(costs[i]);
        }
        if (state == BlueprintState.Constructing){
            progress += " (" + constructionProgress.ToString() + "/" + constructionCost.ToString() + ")";
        }
        return progress;
    }
}

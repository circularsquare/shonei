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
    public bool disabled = false;
    public int priority = 0;
    // Whether this blueprint (and the structure it builds) is horizontally mirrored.
    public bool mirrored = false;
    // Items to give to the completing animal after construction/deconstruction finishes.
    // Set by StructController.Construct (mining output) or Deconstruct (refunded materials).
    public List<ItemQuantity> pendingOutput;

    public Blueprint(StructType structType, int x, int y, bool mirrored = false, bool autoRegister = true){
        this.structType = structType;
        this.x = x;
        this.y = y;
        this.mirrored = mirrored;
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

        Sprite loadedSprite = structType.LoadSprite();
        sprite = loadedSprite ?? Resources.Load<Sprite>("Sprites/Buildings/default");
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = 100;
        sr.sprite = sprite;
        sr.flipX = mirrored;
        sr.color = new Color(0.8f, 0.9f, 1f, 0.5f); // blueprint half alpha
        if (loadedSprite == null) {
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.size = new Vector2(structType.nx, Mathf.Max(1, structType.ny));
        }

        // Deep-copy costs so LockGroupCostsAfterDelivery only affects this blueprint,
        // not every blueprint sharing the same StructType.
        costs = new ItemQuantity[structType.costs.Length];
        for (int i = 0; i < costs.Length; i++) {
            var src = structType.costs[i];
            costs[i] = new ItemQuantity(src.item, src.quantity);
        }
        // One stack per cost item, capacity capped to exactly that item's cost quantity.
        inv = new Inventory(Math.Max(1, costs.Length), 0, Inventory.InvType.Blueprint, x, y);
        for (int i = 0; i < costs.Length; i++)
            inv.itemStacks[i].stackSize = costs[i].quantity;

        StructController.instance.AddBlueprint(this);
        if (autoRegister) {
            if (costs.Length == 0) {
                state = BlueprintState.Constructing;
                if (!IsSuspended())
                    WorkOrderManager.instance?.RegisterConstruct(this);
            } else if (!IsSuspended()) {
                WorkOrderManager.instance?.RegisterSupplyBlueprint(this);
            }
            // If suspended, no order is registered — RegisterOrdersIfUnsuspended()
            // will pick it up when the support below is built.
            RefreshColor(); // apply suspended tint if placed without solid support below
        }
        // For autoRegister: false (load path), RestoreBlueprint calls RefreshColor() separately
        // after restoring inventory and state, so we don't register stale orders here.
    }
    /// <summary>
    /// Disable or re-enable this blueprint. Removes or re-registers WOM orders accordingly.
    /// </summary>
    public void SetDisabled(bool value) {
        disabled = value;
        RefreshColor();
        if (disabled)
            WorkOrderManager.instance?.RemoveForBlueprint(this);
        else
            RegisterOrdersIfUnsuspended();
    }

    public void RefreshColor() {
        Color color;
        if (disabled)
            color = new Color(0.6f, 0.55f, 0.5f, 0.4f); // warm grey: disabled
        else if (state == BlueprintState.Deconstructing)
            color = new Color(1f, 0.3f, 0.3f, 0.5f);
        else if (IsSuspended())
            color = new Color(0.6f, 0.6f, 0.7f, 0.4f); // greyed-out: waiting for support below
        else
            color = new Color(0.8f, 0.9f, 1f, 0.5f);
        go.GetComponent<SpriteRenderer>().color = color;
        // When support below is built, register orders for newly-unsuspended blueprints.
        RegisterOrdersIfUnsuspended();
    }

    /// <summary>
    /// If this blueprint is not suspended and has no work order yet, register one.
    /// Called from RefreshColor() when the structure below completes.
    /// </summary>
    public void RegisterOrdersIfUnsuspended() {
        if (IsSuspended() || cancelled || disabled) return;
        if (state == BlueprintState.Receiving) {
            // After save/load, LockGroupCostsAfterDelivery() is not re-run so cost.item reverts to
            // the group ("wood"). IsFullyDelivered() uses MatchesItem so it still works correctly.
            // If the blueprint is already fully supplied, heal the state rather than registering
            // a supply order that would immediately fail in SupplyBlueprintTask.Initialize().
            if (IsFullyDelivered()) {
                state = BlueprintState.Constructing;
                WorkOrderManager.instance?.RegisterConstruct(this);
            } else {
                WorkOrderManager.instance?.RegisterSupplyBlueprint(this);
            }
        } else if (state == BlueprintState.Constructing)
            WorkOrderManager.instance?.RegisterConstruct(this);
    }

    /// <summary>
    /// True when this blueprint is waiting for world conditions to be met before it can be worked on.
    /// Suspended blueprints are placed validly but mice should not supply or construct them yet.
    ///
    /// If the StructType has tileRequirements, those drive the suspension check (only dynamic
    /// world-state flags are tested: mustBeStandable and mustHaveWater). This lets buildings like
    /// the pump declare their own preconditions rather than relying on standability as a proxy.
    ///
    /// Otherwise: suspended when any tile in the building footprint lacks solid ground below it.
    /// </summary>
    public bool IsSuspended() {
        if (structType.isTile || structType.name == "empty" || structType.requiredTileName != null)
            return false;

        if (structType.tileRequirements != null) {
            foreach (TileRequirement req in structType.tileRequirements) {
                int effectiveDx = mirrored ? (structType.nx - 1 - req.dx) : req.dx;
                Tile t = World.instance.GetTileAt(tile.x + effectiveDx, tile.y + req.dy);
                if (t == null) return true;
                if (req.mustBeStandable && !World.instance.graph.nodes[t.x, t.y].standable) return true;
                if (req.mustHaveWater && t.water == 0) return true;
            }
            return false;
        }

        for (int i = 0; i < structType.nx; i++) {
            Node node = World.instance.graph.nodes[tile.x + i, tile.y];
            if (node != null && !node.standable) return true;
        }
        return false;
    }

    public static Blueprint CreateDeconstructBlueprint(Tile tile) {
        Structure structure = tile.structs[0] ?? tile.structs[1] ?? tile.structs[2] ?? tile.structs[3];
        if (structure == null) return null;
        Blueprint bp = new Blueprint(structure.structType, tile.x, tile.y, structure.mirrored, autoRegister: false);
        bp.state = BlueprintState.Deconstructing;
        bp.RefreshColor();
        WorkOrderManager.instance?.RegisterDeconstruct(bp);
        if (tile.building?.storage != null)
            tile.building.storage.locked = true;
        return bp;
    }

    // Locks each group cost (e.g. "wood") to the specific leaf first delivered (e.g. "pine").
    // Prevents future haulers from bringing a mismatched type that won't fit the occupied slot.
    // No-op if all costs are already leaves.
    public void LockGroupCostsAfterDelivery() {
        foreach (ItemQuantity cost in costs) {
            if (cost.item.children == null) continue; // already locked to a leaf
            foreach (ItemStack stack in inv.itemStacks) {
                if (stack.item == null) continue;
                // Walk up the delivered item's ancestry to see if it belongs to this group cost.
                for (Item cur = stack.item.parent; cur != null; cur = cur.parent) {
                    if (cur != cost.item) continue;
                    cost.item = stack.item;
                    cost.id   = stack.item.id;
                    break;
                }
                if (cost.item.children == null) break; // locked — move on to next cost
            }
        }
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
        // Consume delivered materials — removes them from globalInv now that they're used up.
        // Use the actual stack items (not cost.item) because group costs (e.g. "wood") are only locked
        // to their delivered leaf ("pine") in-memory; after a save/load cost.item reverts to the group
        // and Produce("wood", ...) would fail the group-item guard in AddItem.
        foreach (var stack in inv.itemStacks)
            if (stack.item != null && stack.quantity > 0)
                inv.Produce(stack.item, -stack.quantity);
        // Capture tile products before Construct() changes the tile type
        if (structType.isTile && structType.name == "empty" && tile.type.products != null)
            pendingOutput = new List<ItemQuantity>(tile.type.products);
        StructController.instance.Construct(structType, tile, mirrored);
        tile.SetBlueprintAt(structType.depth, null);
        GameObject.Destroy(go);
        if (InfoPanel.instance?.obj == tile) {
            // Auto-select the newly constructed structure if one was placed (non-tile blueprints only).
            Structure newStructure = structType.isTile ? null : tile.structs[structType.depth];
            InfoPanel.instance.RebuildSelection(newStructure);
        }
    }
    public void Deconstruct() {
        StructController.instance.RemoveBlueprint(this);
        WorkOrderManager.instance?.RemoveForBlueprint(this);
        pendingOutput = new List<ItemQuantity>(); // given in asm.handleworking
        foreach (ItemQuantity cost in costs) {
            int amount = Mathf.FloorToInt(cost.quantity / 2f);
            if (amount <= 0) continue;
            // Resolve group costs (e.g. "wood") to the actual leaf item that was delivered (e.g. "pine").
            // cost.item reverts to the group after save/load, so check the inv stack directly.
            Item item = cost.item;
            if (item.children != null) {
                ItemStack delivered = inv.GetItemStack(item);
                if (delivered == null) continue; // nothing was delivered for this cost
                item = delivered.item;
            }
            pendingOutput.Add(new ItemQuantity(item, amount));
        }
        // destroy the building
        for (int i = 0; i < 4; i++) { if (tile.structs[i] != null) { tile.structs[i].Destroy(); break; } }
        // remove blueprint
        tile.SetBlueprintAt(structType.depth, null);
        GameObject.Destroy(go);
        if (InfoPanel.instance?.obj == tile) InfoPanel.instance.RebuildSelection();
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

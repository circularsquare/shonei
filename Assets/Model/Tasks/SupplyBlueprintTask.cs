using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Hauls one outstanding cost item to a blueprint that's in the Receiving state.
// Spawned from WOM SupplyBlueprint orders. Handles one cost slot per task; the animal
// re-picks for the next slot afterward.
//
// Queue: [optional Fetch(cost)] → Go(adjacent) → DeliverToBlueprint.
// Reserves: source ItemStack (when the animal doesn't already carry the cost item).
// For group-item costs (e.g. "wood") commits to the most-abundant leaf up-front so the
// blueprint isn't locked to a scarce leaf by accident of delivery order.
public class SupplyBlueprintTask : Task {
    Blueprint blueprint;
    ItemQuantity iq;
    public SupplyBlueprintTask(Animal animal, Blueprint bp) : base(animal){
        this.blueprint = bp;
    }
    public override bool Initialize() {
        if (blueprint == null || blueprint.cancelled) return false;
        if (blueprint.state != Blueprint.BlueprintState.Receiving) return false;
        Path standPath = blueprint.structType.isTile
            ? animal.nav.PathStrictlyAdjacent(blueprint.tile)
            : animal.nav.PathToOrAdjacentBlueprint(blueprint);
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;
        for (int i = 0; i < blueprint.costs.Length; i++) {
            int needed = blueprint.costs[i].quantity - blueprint.inv.Quantity(blueprint.costs[i].item);
            if (needed <= 0) continue;
            Item costItem = blueprint.costs[i].item;
            iq = new ItemQuantity(costItem, needed);
            if (!animal.inv.ContainsItem(iq)) {
                // For group-item costs (e.g. "wood"), commit to a single concrete leaf before
                // pathfinding (the most over-target type, nearest preferred — see ResolveConsumeLeaf).
                // This prevents the animal from collecting a mix of leaf types that would then lock
                // the blueprint to whichever leaf happens to be delivered first.
                Item supplyItem = ResolveConsumeLeaf(costItem, excludeLeafIds: blueprint.disallowedLeaves);
                (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(supplyItem);
                if (itemPath == null) continue; // can't find this item — try next cost slot
                iq = new ItemQuantity(supplyItem, needed);
                FetchAndReserve(iq, itemPath.tile, stack);
            }
            objectives.AddLast(new GoObjective(this, standPath.tile));
            objectives.AddLast(new DeliverToBlueprintObjective(this, iq, blueprint));
            return true;
        }
        return false;
    }
}

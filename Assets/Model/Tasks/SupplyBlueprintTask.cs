using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

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
                // For group-item costs (e.g. "wood"), commit to the specific leaf with the most
                // global inventory before pathfinding. This prevents the animal from collecting a
                // mix of leaf types that would then lock the blueprint to whichever leaf happens to
                // be delivered first — potentially a scarce one (e.g. 2 oak over 20 pine).
                Item supplyItem = PickSupplyLeaf(costItem);
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

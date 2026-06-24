using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Repairs a structure's condition by fetching repair materials, walking to the work
// tile, and gradually ticking condition back up over several ticks. Registered as a
// WOM Maintenance order at priority 2; only menders accept it.
//
// Material cost: for every cost item in structType.costs,
//     needed = ceil(cost.quantity × RepairCostFraction × repairAmount)
// So a 0.40 repair of a foundry consumes 10% of its full build cost per item. All
// cost items are required — the mender won't start a repair it can't fully supply.
//
// A single task restores at most MaxRepairPerTask (0.40) condition. A fully-broken
// structure (condition=0) therefore needs three visits to reach 1.0 (0→0.40→0.80→1.0,
// with the last visit capped to the remaining 0.20).
public class MaintenanceTask : Task {
    public readonly Structure target;
    public readonly float startCondition;
    public readonly float repairAmount;           // how much condition this task aims to restore
    public float targetCondition => Mathf.Min(1f, startCondition + repairAmount);

    public MaintenanceTask(Animal animal, Structure target) : base(animal) {
        this.target = target;
        this.startCondition = target != null ? target.condition : 0f;
        this.repairAmount = target != null
            ? Mathf.Min(Structure.MaxRepairPerTask, 1f - target.condition)
            : 0f;
    }

    public override bool Initialize() {
        if (animal.job.name != "mender") return false;
        if (target == null || target.go == null) return false;
        if (!target.NeedsMaintenance) return false;
        if (repairAmount <= 0f) return false;
        if (target.structType.costs == null || target.structType.costs.Length == 0) return false;

        // Resolve where the mender stands to repair. Structures with an interior layer
        // (burrows, doored housing) are mended from inside: the mender enters through the
        // door and stands on an interior node, exactly as occupants do to sleep (see EepTask).
        // Their work tile sits in solid ground with no standable neighbour, so outside-adjacency
        // pathing can never reach them. Everything else is mended from a standable tile adjacent
        // to its work tile. Interior nodes are edged together, so any one is reachable iff all are.
        Node interiorTarget = (target.interiorNodes != null && target.interiorNodes.Length > 0)
            ? target.interiorNodes[0]
            : null;
        Path standPath = interiorTarget != null
            ? animal.nav.PathTo(interiorTarget)
            : animal.nav.PathToOrAdjacent(target.workTile ?? target.tile);
        if (!animal.nav.WithinWorkRange(standPath)) return false;

        // Fetch each cost item. Fail if any single item is unavailable — partial visits
        // are wasteful (animal walks then can't finish) and leaves reserved materials stranded.
        foreach (ItemQuantity cost in target.structType.costs) {
            int needed = Mathf.CeilToInt(cost.quantity * Structure.RepairCostFraction * repairAmount);
            if (needed <= 0) continue;

            Item costItem = cost.item;
            // Repair is an "always allowed" use (like construction, continued) — the "consume" flag
            // does not block it, so no IsConsumptionDisabled check here.
            Item supplyItem = ResolveConsumeLeaf(costItem); // group cost → concrete leaf (surplus × nearness)
            (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(supplyItem);
            if (itemPath == null || stack == null) return false;
            int available = stack.quantity - stack.resAmount;
            if (available < needed) return false;

            ItemQuantity iq = new ItemQuantity(supplyItem, needed);
            FetchAndReserve(iq, itemPath.tile, stack);
        }

        objectives.AddLast(interiorTarget != null
            ? new GoObjective(this, interiorTarget)
            : new GoObjective(this, standPath.tile));
        objectives.AddLast(new MaintenanceObjective(this));
        return true;
    }
}

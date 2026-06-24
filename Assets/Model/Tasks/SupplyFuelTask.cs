using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Hauls fuel items to a building's internal fuel inventory (torch wood, foundry fuel, etc.).
// Registered as a standing SupplyBuilding order; isActive suppresses it when fuel >= target.
public class SupplyFuelTask : Task {
    private readonly Building building;
    public SupplyFuelTask(Animal animal, Building building) : base(animal) {
        this.building = building;
    }
    public override bool Initialize() {
        var fuel = building?.reservoir;
        if (fuel == null) return false;
        int needed = fuel.capacity - fuel.Quantity();
        if (needed <= 0) return false;
        Path standPath = animal.nav.PathToOrAdjacent(building.tile);
        if (!animal.nav.WithinWorkRange(standPath)) return false;
        // Pick the concrete leaf to deliver. Restricted reservoir: resolve its fuelItem (group→leaf,
        // surplus×nearness, biased toward the leaf already stocked so we top up rather than stall).
        // Any-fuel reservoir (fuelItem null): top up whatever leaf is already stocked, else PickFuel
        // by global surplus. Single slot → never mix; a new fuel type is only chosen when empty.
        Item fuelLeaf = fuel.fuelItem != null
            ? ResolveConsumeLeaf(fuel.fuelItem, fuel.inv.HeldLeafMatching(fuel.fuelItem))
            : (fuel.HeldLeaf() ?? GlobalInventory.instance.PickFuel());
        if (fuelLeaf == null) return false;
        // Burning is a gated "consume" channel. PickFuel already skips protected fuel, but the
        // restricted-reservoir leaf fuelItem and the HeldLeaf() top-up branch bypass it — so
        // re-check here to honour the flag uniformly across all three paths.
        if (InventoryController.instance != null && InventoryController.instance.IsConsumptionDisabled(fuelLeaf)) return false;
        (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(fuelLeaf);
        if (itemPath == null) return false;
        int available = stack.quantity - stack.resAmount;
        int qty = Math.Min(needed, available);
        if (qty <= 0) return false;
        // Reserve destination space on fuel inventory
        int spaceReserved = ReserveSpace(fuel.inv, fuelLeaf, qty);
        if (spaceReserved <= 0) return false;
        qty = Math.Min(qty, spaceReserved);
        if (!MeetsHaulMinimum(qty, available)) return false; // de minimis
        ItemQuantity iq = new ItemQuantity(fuelLeaf, qty);
        FetchAndReserve(iq, itemPath.tile, stack);
        objectives.AddLast(new GoObjective(this, standPath.tile));
        objectives.AddLast(new DeliverToInventoryObjective(this, iq, fuel.inv));
        return true;
    }
}

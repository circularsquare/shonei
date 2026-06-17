using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Hauls fuel items to a building's internal fuel inventory (torch wood, furnace coal, etc.).
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
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;
        // A group fuel item (e.g. "wood") resolves to a concrete leaf — surplus × nearness, biased
        // toward the leaf already in the (single-type) reservoir so we top up rather than stall.
        Item fuelLeaf = ResolveConsumeLeaf(fuel.fuelItem, fuel.inv.HeldLeafMatching(fuel.fuelItem));
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

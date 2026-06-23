using System;
using System.Collections.Generic;

// Hauls a target-consistent ore to a foundry's intake inventory (which Foundry.Tick sweeps into a
// melt chunk). Registered as a standing FeedFoundry order; isActive suppresses it when the foundry
// is full or has no cast target. Single-item fetch→deliver, modelled on SupplyFuelTask — the cast
// target (not an allow-set) decides which ore is wanted, and ChooseFeedOre balances the feed.
public class FeedFoundryTask : Task {
    private readonly Foundry foundry;

    public FeedFoundryTask(Animal animal, Building building) : base(animal) {
        this.foundry = building as Foundry;
    }

    public override bool Initialize() {
        if (foundry == null || !foundry.HasRoom()) return false;
        Item target = foundry.TargetBar();
        if (target == null) return false; // nothing wanted right now
        HashSet<int> consistent = Foundry.ConsistentMoltens(target);
        Item ore = foundry.ChooseFeedOre(consistent);
        if (ore == null) return false; // no consistent ore in stock

        Path standPath = animal.nav.PathToOrAdjacent(foundry.tile);
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;
        (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(ore);
        if (itemPath == null) return false;

        int available = stack.quantity - stack.resAmount;
        int room = foundry.capacityFen - foundry.CurrentFen();
        int qty = Math.Min(available, room);
        if (qty <= 0) return false;
        int spaceReserved = ReserveSpace(foundry.intake, ore, qty);
        if (spaceReserved <= 0) return false;
        qty = Math.Min(qty, spaceReserved);
        if (!MeetsHaulMinimum(qty, available)) return false; // de minimis

        ItemQuantity iq = new ItemQuantity(ore, qty);
        FetchAndReserve(iq, itemPath.tile, stack);
        objectives.AddLast(new GoObjective(this, standPath.tile));
        objectives.AddLast(new DeliverToInventoryObjective(this, iq, foundry.intake));
        return true;
    }
}

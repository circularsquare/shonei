using System;
using UnityEngine;

// Hauls a single furnishing item from the floor/storage into an empty slot on a house's
// FurnishingSlots. Registered as a standing SupplyFurnishing WOM order on the building;
// isActive suppresses it while no slot is haulable. Mirrors SupplyFuelTask's shape:
//   1. Pick the first empty slot whose name has matching items globally.
//   2. Find a path to a stack of one of those items.
//   3. Reserve 1 fen on the source stack and 1 fen of space in the target slot.
//   4. Queue Go → Deliver. On Complete(), notify the slot so happiness + visuals refresh.
public class SupplyFurnishingTask : Task {
    private readonly Building building;
    private int slotIndex = -1;

    public SupplyFurnishingTask(Animal animal, Building building) : base(animal) {
        this.building = building;
    }

    public override bool Initialize() {
        var fs = building?.furnishingSlots;
        if (fs == null) return false;

        // Pick a slot. We can't just call FindAnyHaulableSlotIndex once and trust it —
        // we also need a reachable item stack. Loop slots until one yields a path.
        for (int i = 0; i < fs.SlotCount; i++) {
            if (!fs.IsEmpty(i)) continue;
            // Skip if another hauler already reserved this slot's space.
            if (fs.slotInvs[i].itemStacks[0].resSpace > 0) continue;
            if (!Db.itemsByFurnishingSlot.TryGetValue(fs.slotNames[i], out var candidates)) continue;

            foreach (Item candidate in candidates) {
                if (GlobalInventory.instance.Quantity(candidate) <= 0) continue;
                (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(candidate);
                if (itemPath == null) continue;

                Path standPath = animal.nav.PathToOrAdjacent(building.tile);
                if (standPath == null) continue;
                if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) continue;

                int available = stack.quantity - stack.resAmount;
                if (available <= 0) continue;
                int qty = Math.Min(1, available); // a slot holds exactly 1 fen

                int spaceReserved = ReserveSpace(fs.slotInvs[i], candidate, qty);
                if (spaceReserved <= 0) continue;
                qty = Math.Min(qty, spaceReserved);
                if (qty <= 0) { UndoLastSpaceReservation(); continue; }

                ItemQuantity iq = new ItemQuantity(candidate, qty);
                FetchAndReserve(iq, itemPath.tile, stack);
                objectives.AddLast(new GoObjective(this, standPath.tile));
                objectives.AddLast(new DeliverToInventoryObjective(this, iq, fs.slotInvs[i]));
                slotIndex = i;
                return true;
            }
        }
        return false;
    }

    public override void Complete() {
        // Fire NotifyInstalled BEFORE base.Complete — base.Complete may advance to the next
        // objective (if any) or finalize the task; the slot already received the item via
        // DeliverToInventoryObjective. Order doesn't actually matter functionally here, but
        // matching the SupplyFuelTask convention (no override) would require manual fire elsewhere.
        if (slotIndex >= 0 && objectives.Count == 0) {
            // Only fire on the final Complete (no more queued objectives). Delivery is the
            // last objective, so once it Complete()s and the queue is empty, the install is done.
            building?.furnishingSlots?.NotifyInstalled(slotIndex);
        }
        base.Complete();
    }
}

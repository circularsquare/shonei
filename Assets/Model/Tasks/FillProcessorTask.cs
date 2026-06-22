using System;
using System.Collections.Generic;

// Load a building's Processor with a batch: pick the recipe (scored, at fill time), then deliver
// its inputs — plus the committed fuel leaf — into the inputBuffer, and start the batch.
// Modelled on SupplyFuelTask (deliver-into-building) but handles a multi-item recipe + fuel and
// drives the processor state machine.
//
// State handling: registered as a standing FillProcessor order whose isActive gates it to the
// Empty state. Initialize chooses the batch recipe (if not already chosen on a resumed partial)
// and flips Empty→Filling so the order self-suppresses. The fill is *resumable* — only the
// missing remainder per input/fuel is fetched, so a fill aborted after a partial delivery can be
// topped up by a later task (which keeps the same recipe, already set on the processor).
//   Complete:  inputs + fuel now loaded → Filling→Working (progress reset); else → Empty (re-arm).
//   Cleanup:   safety net — if still Filling (task failed before Complete), drop to Empty.
public class FillProcessorTask : Task {
    private readonly Building building;

    public FillProcessorTask(Animal animal, Building building) : base(animal) {
        this.building = building;
    }

    public override bool Initialize() {
        Processor proc = building?.processor;
        if (proc == null || proc.state != Processor.State.Empty) return false;

        // Choose the batch recipe (scored among the building's processor recipes) unless a prior
        // partial fill already committed one — then top that same recipe up.
        if (proc.recipe == null) {
            Recipe chosen = animal.PickProcessorRecipe(building);
            if (chosen == null) return false; // nothing makeable / all disabled right now
            proc.SetBatchRecipe(chosen);
        }

        // The tile the worker stands on to deposit (PathToOrAdjacent, like SupplyFuelTask).
        Path standPath = animal.nav.PathToOrAdjacent(building.tile);
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;

        // Gather a delivery (reserving source + buffer space) for each input's missing remainder, then
        // fetch them CLOSEST-FIRST rather than in authoring order — so a nearby water isn't skipped to
        // walk to a far herb first. An input with no reachable stock is skipped (a later fill tops up).
        var deliveries = new List<Delivery>();
        foreach (ItemQuantity input in proc.inputs) {
            // missing is measured against the group (input.item) so any already-buffered leaf counts;
            // the gather then commits to a concrete leaf (surplus × nearness) for the new delivery.
            int missing = input.quantity - proc.inputBuffer.Quantity(input.item);
            if (missing <= 0) continue;
            Item inputLeaf = ResolveConsumeLeaf(input.item, proc.inputBuffer.HeldLeafMatching(input.item));
            TryGather(inputLeaf, missing, proc, deliveries);
        }
        // Fuel: the processor committed to a concrete fuel leaf when the recipe was chosen; haul the
        // missing remainder into the buffer like any input (Tap drains it with the rest).
        if (proc.batchFuelItem != null) {
            int missingFuel = proc.batchFuelFen - proc.inputBuffer.Quantity(proc.batchFuelItem);
            if (missingFuel > 0) TryGather(proc.batchFuelItem, missingFuel, proc, deliveries);
        }
        if (deliveries.Count == 0) return false; // nothing haulable right now — leave state Empty

        // Fetch closest-first; the deliver leg is one trip to the building regardless of order.
        var order = NearestFetchOrder(animal.x, animal.y, deliveries.ConvertAll(d => d.src));
        foreach (int i in order) {
            Delivery d = deliveries[i];
            objectives.AddLast(new FetchObjective(this, d.iq, d.src, sourceInv: d.srcInv, sourceLimit: d.reserved));
        }
        objectives.AddLast(new GoObjective(this, standPath.tile));
        foreach (int i in order)
            objectives.AddLast(new DeliverToInventoryObjective(this, deliveries[i].iq, proc.inputBuffer));

        proc.state = Processor.State.Filling;
        return true;
    }

    // A committed delivery: the leaf+qty to haul, where it's coming from, and the reservations made.
    private struct Delivery {
        public ItemQuantity iq;
        public Tile         src;
        public Inventory    srcInv;
        public int          reserved;
    }

    // Reserves a source stack + buffer space for `leaf` (capped to the missing amount and what's
    // reachable) and records the delivery. No-op (skip) when nothing can be fetched.
    private void TryGather(Item leaf, int missing, Processor proc, List<Delivery> deliveries) {
        (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(leaf);
        if (itemPath == null) return;
        int available = stack.quantity - stack.resAmount;
        int qty = Math.Min(missing, available);
        if (qty <= 0) return;
        int spaceReserved = ReserveSpace(proc.inputBuffer, leaf, qty);
        if (spaceReserved <= 0) return;
        qty = Math.Min(qty, spaceReserved);
        int reserved = ReserveStack(stack, qty);
        if (reserved <= 0) return;
        qty = Math.Min(qty, reserved);
        deliveries.Add(new Delivery {
            iq       = new ItemQuantity(leaf, qty),
            src      = itemPath.tile,
            srcInv   = stack.inv,
            reserved = reserved,
        });
    }

    public override void Complete() {
        Processor proc = building.processor;
        if (proc.BatchLoaded()) {
            proc.state = Processor.State.Working;
            proc.progress = 0f;
        } else {
            // Partial fill — re-arm the FillProcessor order so a later task tops up the rest (it
            // keeps proc.recipe, so the same batch resumes).
            proc.state = Processor.State.Empty;
        }
        base.Complete();
    }

    public override void Cleanup() {
        // Safety net: if the task failed before Complete resolved the state, drop back to Empty so
        // the FillProcessor order re-arms. No-op on the success path.
        if (building?.processor != null && building.processor.state == Processor.State.Filling)
            building.processor.state = Processor.State.Empty;
        base.Cleanup();
    }
}

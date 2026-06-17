using System;
using System.Collections.Generic;

// Cook task: load a building's Processor with its declared inputs, then start fermentation.
// Modelled on SupplyFuelTask (deliver-into-building) but handles a multi-item recipe and
// drives the processor state machine.
//
// State handling: registered as a standing FillProcessor order whose isActive gates it to
// the Empty state. Initialize flips Empty→Filling so the order self-suppresses and no
// second cook joins. The fill is *resumable* — only the missing remainder per input is
// fetched, so a fill aborted after a partial delivery can be topped up by a later task.
//   Complete:  inputs now complete → Filling→Working (progress reset); else → Empty (re-arm).
//   Cleanup:   safety net — if still Filling (task failed before Complete), drop to Empty.
public class FillProcessorTask : Task {
    private readonly Building building;

    public FillProcessorTask(Animal animal, Building building) : base(animal) {
        this.building = building;
    }

    public override bool Initialize() {
        Processor proc = building?.processor;
        if (proc == null || proc.state != Processor.State.Empty) return false;

        // The tile the cook stands on to deposit (PathToOrAdjacent, like SupplyFuelTask).
        Path standPath = animal.nav.PathToOrAdjacent(building.tile);
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;

        // For each declared input, fetch only the missing remainder. An input with no
        // reachable stock is skipped — this fill loads what it can; a later fill tops up
        // the rest (which is why the order re-arms to Empty on an incomplete Complete).
        var deliveries = new List<ItemQuantity>();
        foreach (ItemQuantity input in proc.inputs) {
            // missing is measured against the group (input.item) so any already-buffered leaf counts;
            // the fetch then commits to a concrete leaf (surplus × nearness) for the new delivery.
            int missing = input.quantity - proc.inputBuffer.Quantity(input.item);
            if (missing <= 0) continue;
            // Bias toward the leaf already buffered for this input so a partial fill tops up in
            // kind rather than committing to a type that won't fit the occupied slot.
            Item inputLeaf = ResolveConsumeLeaf(input.item, proc.inputBuffer.HeldLeafMatching(input.item));
            (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(inputLeaf);
            if (itemPath == null) continue;
            int available = stack.quantity - stack.resAmount;
            int qty = Math.Min(missing, available);
            if (qty <= 0) continue;
            int spaceReserved = ReserveSpace(proc.inputBuffer, inputLeaf, qty);
            if (spaceReserved <= 0) continue;
            qty = Math.Min(qty, spaceReserved);
            ItemQuantity iq = new ItemQuantity(inputLeaf, qty);
            FetchAndReserve(iq, itemPath.tile, stack);
            deliveries.Add(iq);
        }
        if (deliveries.Count == 0) return false; // nothing haulable right now — leave state Empty

        objectives.AddLast(new GoObjective(this, standPath.tile));
        foreach (ItemQuantity iq in deliveries)
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, proc.inputBuffer));

        proc.state = Processor.State.Filling;
        return true;
    }

    public override void Complete() {
        Processor proc = building.processor;
        if (proc.InputsComplete()) {
            proc.state = Processor.State.Working;
            proc.progress = 0f;
        } else {
            // Partial fill — re-arm the FillProcessor order so a later task tops up the rest.
            proc.state = Processor.State.Empty;
        }
        base.Complete();
    }

    public override void Cleanup() {
        // Safety net: if the task failed before Complete resolved the state, drop back to
        // Empty so the FillProcessor order re-arms. No-op on the success path — Complete
        // has already moved the state to Working or Empty.
        if (building?.processor != null && building.processor.state == Processor.State.Filling)
            building.processor.state = Processor.State.Empty;
        base.Cleanup();
    }
}

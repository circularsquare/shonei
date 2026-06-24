using System;
using System.Collections.Generic;

// Pours a foundry's castable molten into its output, then registers eviction hauls so the result gets
// carried to storage. Registered as a standing CastFoundry order, active while there's anything to cast.
// Two cases:
//   - Plain bar drain: leftover / inconsistent molten pours straight to bars (CastAll, whole pool).
//   - Molded (tool) cast: when the cast target is a tool (molten + clay mold + plank → tools), the smith
//     FETCHES enough molds + planks to pour the WHOLE castable batch in one trip (bounded by molten in
//     the pool + output-buffer room + extras in stock), then casts them all in Complete. The bar drain
//     still runs afterwards for any non-target leftover molten.
// Mirrors TapProcessorTask: no reservation on the foundry, so the building.go liveness recheck in
// Complete guards against a deconstruct mid-walk.
public class CastFoundryTask : Task {
    private readonly Foundry foundry;
    private Recipe moldedCast; // the molded target cast whose extras we fetched; null = bar-drain only

    public CastFoundryTask(Animal animal, Building building) : base(animal) {
        this.foundry = building as Foundry;
    }

    public override bool Initialize() {
        if (foundry == null || !foundry.HasCastableMolten()) return false;
        Path standPath = animal.nav.PathToOrAdjacent(foundry.tile);
        if (!animal.nav.WithinWorkRange(standPath)) return false;

        // Molded (tool) cast: gather enough extras (clay mold + plank) to pour the whole castable batch in
        // ONE trip, not one tool at a time. The molten is already in the pool; only the solid extras are
        // fetched. Pass 1 caps the batch by each extra's reach (carried + nearest in-stock stack); pass 2
        // fetches the shortfall for that many tools.
        Recipe targetCast = Foundry.CastRecipeForBar(foundry.TargetBar());
        if (Foundry.IsMoldedCast(targetCast) && foundry.MoldedTargetCastable(targetCast)) {
            int units = foundry.MoldedUnitsCastable(targetCast); // molten + output-buffer room
            var srcs = new List<(Item leaf, ItemQuantity input, Tile tile, ItemStack stack)>();
            for (int i = 1; i < targetCast.inputs.Length && units > 0; i++) {
                ItemQuantity input = targetCast.inputs[i];
                if (input.item == null || input.quantity <= 0) continue;
                int carriedUnits = animal.inv.Quantity(input.item) / input.quantity;
                Item leaf = ResolveConsumeLeaf(input.item); // planks is a group → concrete plank type
                (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(leaf);
                int stockUnits = itemPath != null ? (stack.quantity - stack.resAmount) / input.quantity : 0;
                units = Math.Min(units, carriedUnits + stockUnits); // can't cast more than this extra allows
                if (itemPath != null) srcs.Add((leaf, input, itemPath.tile, stack));
            }
            if (units > 0) {
                foreach (var s in srcs) {
                    int toFetch = s.input.quantity * units - animal.inv.Quantity(s.input.item);
                    if (toFetch > 0) FetchAndReserve(new ItemQuantity(s.leaf, toFetch), s.tile, s.stack);
                }
                moldedCast = targetCast;
            }
        }

        objectives.AddLast(new GoObjective(this, standPath.tile));
        return true;
    }

    public override void Complete() {
        if (foundry != null && foundry.go != null) {
            // Molded (tool) cast: pour as many tools as the carried extras cover (CastMolten re-caps by the
            // molten + output room that remain), consuming exactly that many molds + planks from our inv.
            if (moldedCast != null) {
                int units = CarriedUnits(moldedCast);
                if (units > 0) {
                    int cast = foundry.CastMolten(moldedCast, units);
                    for (int i = 1; cast > 0 && i < moldedCast.inputs.Length; i++) {
                        ItemQuantity iq = moldedCast.inputs[i];
                        if (iq.item != null) animal.Consume(iq.item, iq.quantity * cast);
                    }
                }
            }
            foundry.CastAll(); // drain leftover / inconsistent molten to bars (skips the molded-target molten)
            var wom = WorkOrderManager.instance;
            if (wom != null)
                foreach (ItemStack s in foundry.output.itemStacks)
                    if (s.item != null && s.quantity > 0)
                        wom.RegisterFoundryOutputHaul(s); // haulers OR smiths clear it (single-slot output can't block casting)
        }
        base.Complete();
    }

    // Min over the molded cast's extras of how many whole units we can cover with what we currently carry.
    int CarriedUnits(Recipe cast) {
        int units = int.MaxValue;
        for (int i = 1; i < cast.inputs.Length; i++) {
            ItemQuantity iq = cast.inputs[i];
            if (iq.item == null || iq.quantity <= 0) continue;
            units = Math.Min(units, animal.inv.Quantity(iq.item) / iq.quantity);
        }
        return units == int.MaxValue ? 0 : units;
    }
}

using System.Collections.Generic;
using UnityEngine;

// Consume-in-place: a mouse walks to a stocked tonic (wherever it sits — cauldron, tank, or floor),
// drinks one dose, and gains that tonic's timed buff. Mirrors DrinkTask (rice wine), but instead of
// granting a leisure need it applies a BuffSet effect read from the item's buff fields. The target
// tonic is chosen by Animal.ChooseTonic and passed in.
public class DrinkTonicTask : Task {
    public override bool IsWork => false;

    private const int FullDose = 100; // 1 liang — a full dose grants the tonic's full buff duration
    private const int MinDose  = 20;  // 0.2 liang — below this it's not worth a trip / a meaningful buff

    private readonly Item tonic;
    private Inventory tonicInv;
    private int drinkAmount; // what we'll actually drink (a partial stack → a proportional buff)

    public DrinkTonicTask(Animal animal, Item tonic) : base(animal) {
        this.tonic = tonic;
    }

    public override bool Initialize() {
        if (tonic == null) return false;
        (Path path, ItemStack stack) = animal.nav.FindPathItemStack(tonic);
        if (path == null) return false;
        // Drink up to a full dose, but settle for whatever a partial stack holds (≥ MinDose) — so a
        // sub-liang remnant isn't wasted; the buff duration then scales with the amount drunk.
        int available = stack.quantity - stack.resAmount;
        drinkAmount = System.Math.Min(FullDose, available);
        if (drinkAmount < MinDose) return false;
        if (ReserveStack(stack, drinkAmount) < drinkAmount) return false;
        tonicInv = stack.inv;
        objectives.AddLast(new GoObjective(this, path.tile));
        return true;
    }

    public override void Complete() {
        tonicInv.Produce(tonic, -drinkAmount);
        if (tonic.buffEffect.HasValue) {
            // Full buffDuration for a full dose; a partial dose grants proportionally less TIME (the
            // magnitude is unchanged — you still feel the full effect, just for fewer days).
            float doseFraction  = drinkAmount / (float)FullDose;
            float durationSeconds = tonic.buffDuration * World.ticksInDay * doseFraction; // buffDuration in in-game days
            animal.buffs.Apply(tonic.buffEffect.Value, tonic.buffMagnitude, durationSeconds);
            // Temperature buffs feed the cached comfort range — refresh it now so the effect is felt
            // immediately rather than waiting for the next 10-tick SlowUpdate.
            animal.happiness.UpdateComfortRange(animal);
        }
        base.Complete();
    }
}

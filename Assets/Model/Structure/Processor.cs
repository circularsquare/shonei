using UnityEngine;

// Optional component of a Building: a passive timed converter. A worker loads the
// declared inputs (FillProcessorTask), they transform unattended over `processDays`
// in-game days — optionally temperature-scaled — and a worker then taps the finished
// batch (TapProcessorTask), yielding the outputs into a haulable inventory.
//
// Non-null only when StructType.hasProcessor. Sibling component to Workstation /
// Reservoir — pure composition, so one building can be a workstation AND a processor
// AND a leisure spot (the brewery is exactly that). Generalizable: the brewery
// (rice + water + yeast → rice wine) is the first user; vinegar / soy-sauce / compost
// buildings reuse it via JSON alone, no new code.
//
// Lifecycle:  Empty → Filling → Working → Ready → Tapped → Empty
//   Empty    no inputs; a Fill order is open.
//   Filling  a worker has claimed the load and is delivering inputs.
//   Working  inputs complete + locked in inputBuffer; `progress` advances each tick.
//   Ready    progress complete; a Tap order is open.
//   Tapped   tapped — inputBuffer drained, `output` holds the result: haulable + drinkable.
//   → Empty  once `output` is emptied (drunk down / hauled off).
public class Processor {
    public enum State { Empty, Filling, Working, Ready, Tapped }

    public State state = State.Empty;
    public float progress;          // in-game days elapsed while Working; clamped to [0, processDays]

    // Mixed-class load buffer. Reservoir-typed so it accepts any item class together
    // (rice + water + yeast), never decays, and is never a haul source — exactly the
    // semantics an opaque internal buffer needs.
    public Inventory inputBuffer;
    // The finished batch. Storage-typed (liquid) so haul orders, storage eviction and
    // DrinkTask all route through existing inventory machinery once the batch is Tapped.
    public Inventory output;

    // ── Config (resolved from the StructType in StructType.OnDeserialized) ──────
    // inputs/outputs are public readonly — the fill/tap tasks iterate them. Immutable.
    public readonly ItemQuantity[] inputs;
    public readonly ItemQuantity[] outputs;
    public readonly float processDays;      // base duration at full (rate 1.0) speed
    private readonly float? tempMin;        // null = not temperature-scaled (constant full rate)
    private readonly float? tempIdeal;
    public readonly bool autoTap;           // schema stub — not acted on yet; manual tap only

    public Processor(StructType st, int tileX, int tileY, int parentSortingOrder) {
        inputs      = st.processorInputs;
        outputs     = st.processorOutputs;
        processDays = st.processDays;
        tempMin     = st.processTempMin;
        tempIdeal   = st.processTempIdeal;
        autoTap     = st.autoTap;

        // inputBuffer: one slot per distinct input, each sized to hold its full quantity.
        // Reservoir type → accepts mixed item classes, no decay, not haulable.
        int bufStackSize = 100;
        foreach (ItemQuantity iq in inputs) bufStackSize = Mathf.Max(bufStackSize, iq.quantity);
        inputBuffer = new Inventory(Mathf.Max(1, inputs.Length), bufStackSize,
                                    Inventory.InvType.Reservoir, tileX, tileY);
        inputBuffer.displayName = st.name + "_inputs";

        // output: holds the finished batch. Liquid Storage so it haul/drink-routes normally.
        int outStackSize = 100;
        foreach (ItemQuantity oq in outputs) outStackSize = Mathf.Max(outStackSize, oq.quantity);
        output = new Inventory(Mathf.Max(1, outputs.Length), outStackSize,
                               Inventory.InvType.Storage, tileX, tileY,
                               storageClass: ItemClass.Liquid, parentSortingOrder: parentSortingOrder);
        output.displayName = st.name + "_output";
    }

    // True while ambient temperature affects the fermentation rate.
    public bool TempScaled => tempMin.HasValue && tempIdeal.HasValue;

    // Fermentation speed multiplier at the given ambient temperature: 1.0 when not
    // temperature-scaled, otherwise a linear ramp from 0 at tempMin up to 1 at tempIdeal
    // (InverseLerp clamps to [0, 1]). A high-temperature falloff is deliberately not
    // modelled yet — see the rice-wine plan's deferred list.
    public float Rate(float ambientTemp) {
        if (!TempScaled) return 1f;
        return Mathf.InverseLerp(tempMin.Value, tempIdeal.Value, ambientTemp);
    }

    // Does inputBuffer currently hold every declared input in full?
    public bool InputsComplete() {
        foreach (ItemQuantity iq in inputs)
            if (inputBuffer.Quantity(iq.item) < iq.quantity) return false;
        return true;
    }

    // Advances the processor each in-game time-step. Called every tick while the processor
    // exists. `dtDays` is the elapsed in-game days; `ambientTemp` drives temperature scaling.
    //   Working: progress accrues; on completion → Ready (awaiting a tap).
    //   Tapped:  once `output` has been fully drained (drunk down / hauled off) → Empty,
    //            re-arming the FillProcessor order for the next batch.
    public void Tick(float dtDays, float ambientTemp) {
        if (state == State.Working) {
            progress += Rate(ambientTemp) * dtDays;
            if (progress >= processDays) {
                progress = processDays;
                state = State.Ready;
            }
        } else if (state == State.Tapped && output.IsEmpty()) {
            state = State.Empty;
        }
    }

    // Converts a finished batch: consumes everything in inputBuffer and produces the
    // configured outputs into `output`, then enters Tapped. Called by TapProcessorTask
    // once it has confirmed the building is still alive. Negative Produce drains the
    // inputs (and decrements the global inventory) — the fermentation consumed them.
    public void Tap() {
        foreach (ItemStack s in inputBuffer.itemStacks)
            if (s.item != null && s.quantity > 0)
                inputBuffer.Produce(s.item, -s.quantity);
        foreach (ItemQuantity oq in outputs)
            output.Produce(oq.item, oq.quantity);
        state = State.Tapped;
    }

    // Drops both inventories' contents onto the floor at `here` — called on deconstruct
    // so a tank's loaded ingredients / finished wine aren't silently lost. Mirrors
    // Reservoir.DropToFloor.
    public void DropToFloor(Tile here) {
        DropInv(inputBuffer, here);
        DropInv(output, here);
    }

    static void DropInv(Inventory inv, Tile here) {
        if (inv.IsEmpty() || here == null) return;
        foreach (ItemStack s in inv.itemStacks) {
            if (s.item == null || s.quantity == 0) continue;
            int qty = s.quantity;
            inv.Produce(s.item, -qty);
            World.instance.ProduceAtTile(s.item, qty, here);
        }
    }

    // Destroys both internal inventories (and their backing GameObjects). Call on deconstruct.
    public void Destroy() {
        inputBuffer.Destroy(reason: "processor destroyed");
        output.Destroy(reason: "processor destroyed");
    }
}

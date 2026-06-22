using System.Collections.Generic;
using UnityEngine;

// Optional component of a Building: a batch converter. A worker loads the chosen recipe's
// inputs (FillProcessorTask), they transform over the recipe's `duration`, and the finished
// batch is tapped into a haulable inventory. Two modes, chosen by StructType.processorTended:
//   UNTENDED (brewery): Working advances passively in Processor.Tick — elapsed in-game seconds
//                       scaled by ambient temperature — then a worker taps the Ready batch.
//   TENDED   (cauldron): Working advances by a worker's labour (WorkProcessorTask), which
//                        auto-taps the batch the instant the labour completes.
//
// Multi-recipe: a building's Processor can run any Recipe with tile==buildingName and a
// `duration` (Db.GetProcessorRecipes). The recipe for each batch is scored & chosen at fill
// time (Animal.PickProcessorRecipe) and assigned via SetBatchRecipe; the buffers are sized
// once (ctor) to the largest recipe so no per-batch reallocation is needed.
//
// Non-null only when StructType.hasProcessor. Sibling component to Workstation / Reservoir —
// pure composition, so one building can be a workstation AND a processor (the brewery crafts
// yeast AND ferments rice wine). Generalizable: vinegar / soy-sauce / compost buildings reuse
// it via JSON alone (a building + Recipes with a duration), no new code.
//
// Lifecycle:  Empty → Filling → Working → (Ready →) Tapped → Empty
//   Empty    no batch; a Fill order is open.
//   Filling  a worker has claimed a batch (recipe chosen) and is delivering inputs (+ fuel).
//   Working  inputs locked in inputBuffer; the batch advances (passive Tick, or worker labour).
//   Ready    untended only — progress complete; a Tap order is open.
//   Tapped   inputBuffer drained, `output` holds the result: haulable + drinkable.
//   → Empty  once `output` is emptied (drunk down / hauled off), re-arming the next batch.
public class Processor {
    public enum State { Empty, Filling, Working, Ready, Tapped }

    public State state = State.Empty;
    public float progress;          // seconds elapsed/laboured while Working; clamped to [0, duration]
    public readonly bool tended;    // true = worker-tended Working; false = passive ferment

    // Local-heat mode (foundry): the building's own heat — stoked by fuel burned in StructController
    // (Reservoir.Burn → AddFuelHeat), bleeding off toward ambient each tick — drives the advance rate
    // in place of ambient weather. A completed melt discretely draws recipe.heatCost. See SPEC-systems.
    public readonly bool localHeat;
    public float heat;              // stored thermal charge, in "degrees above ambient"; 0 = cold (ambient)
    public float temperature;       // cached absolute reading (HeatToTemperature) — read by the InfoPanel

    // Mixed-class load buffer. Reservoir-typed so it accepts any item class together
    // (rice + water + yeast + fuel), never decays, and is never a haul source.
    public Inventory inputBuffer;
    // The finished batch. Storage-typed (liquid) so haul orders, storage eviction and
    // DrinkTask all route through existing inventory machinery once the batch is Tapped.
    public Inventory output;

    // ── Config ──────────────────────────────────────────────────────────────
    // Every Recipe this building's Processor can run (each with tile==buildingName + a duration).
    // The recipe for the current batch is chosen per-fill via SetBatchRecipe; null when Empty.
    public readonly List<Recipe> recipes;
    public Recipe recipe { get; private set; }   // current batch's recipe, or null when Empty

    // Fuel committed for the current batch (recipe.fuelCost > 0). The fill task hauls this into
    // inputBuffer like any input; Tap drains it with the rest. null = this recipe burns no fuel.
    public Item batchFuelItem { get; private set; }
    public int  batchFuelFen  { get; private set; }

    // Forwarding accessors so the fill/tap tasks and the InfoPanel read the batch's recipe.
    public ItemQuantity[] inputs  => recipe?.inputs;
    public ItemQuantity[] outputs => recipe?.outputs;
    public float duration         => recipe?.duration ?? 0f;

    // capacityFen: the pot's liquid capacity (fen). 0 → sized to one batch's output (reads full when
    // holding a batch); a larger value (cauldron's 10 liang) makes one batch read partially full and
    // lets `output` buffer more.
    public Processor(List<Recipe> recipes, bool tended, bool localHeat, int capacityFen, int tileX, int tileY, int parentSortingOrder) {
        this.recipes = recipes;
        this.tended  = tended;
        this.localHeat = localHeat;
        string buildingName = (recipes != null && recipes.Count > 0) ? recipes[0].tile : "processor";

        // Size the buffers ONCE to the largest recipe so a batch can switch recipes without
        // reallocating GameObject-backed inventories. inputBuffer needs a slot per distinct
        // input plus one for fuel; output a slot per distinct output.
        int maxInputSlots = 1, maxOutputSlots = 1, inStack = 100, outStack = 100;
        bool anyFuel = false;
        bool anyOutput = false, allOutputsLiquid = true; // → the output pot's ItemClass
        if (recipes != null)
            foreach (Recipe r in recipes) {
                maxInputSlots  = Mathf.Max(maxInputSlots,  r.inputs.Length);
                maxOutputSlots = Mathf.Max(maxOutputSlots, r.outputs.Length);
                foreach (ItemQuantity iq in r.inputs)  inStack  = Mathf.Max(inStack,  iq.quantity);
                foreach (ItemQuantity oq in r.outputs) {
                    outStack = Mathf.Max(outStack, oq.quantity);
                    if (oq.item != null) { anyOutput = true; if (!oq.item.isLiquid) allOutputsLiquid = false; }
                }
                if (r.fuelCost > 0f) { anyFuel = true; inStack = Mathf.Max(inStack, Mathf.RoundToInt(r.fuelCost * 100f)); }
            }
        if (anyFuel) maxInputSlots += 1; // reserve a buffer slot for the fuel leaf
        outStack = Mathf.Max(outStack, capacityFen); // a roomier pot than one batch → partial-fill look
        // The output pot enforces an EXACT item-class match (Inventory.ItemTypeCompatible), so its
        // class MUST match what the recipes produce or Tap silently drops the batch: Liquid for
        // brewed/fermented liquids (wine, tonics), Default for solid output (foundry bars, glass).
        ItemClass outClass = (anyOutput && allOutputsLiquid) ? ItemClass.Liquid : ItemClass.Default;

        // inputBuffer: Reservoir type → accepts mixed item classes, no decay, not haulable.
        inputBuffer = new Inventory(maxInputSlots, inStack, Inventory.InvType.Reservoir, tileX, tileY);
        inputBuffer.displayName = buildingName + "_inputs";

        // output: Storage so it haul/drink-routes normally; class derived above. Storage starts
        // all-disallowed; explicitly allow EVERY recipe's outputs so Tap can deposit whichever batch ran.
        output = new Inventory(maxOutputSlots, outStack, Inventory.InvType.Storage, tileX, tileY,
                               storageClass: outClass, parentSortingOrder: parentSortingOrder);
        output.displayName = buildingName + "_output";
        if (recipes != null)
            foreach (Recipe r in recipes)
                foreach (ItemQuantity oq in r.outputs)
                    if (oq.item != null) output.AllowItem(oq.item);
    }

    // Commits this batch to a recipe (chosen by the fill scorer) and picks its fuel leaf if any.
    // Called by FillProcessorTask before it builds the delivery list. Resilient to being called
    // again on a resumed fill (same recipe) — it only re-picks fuel if none is committed yet.
    public void SetBatchRecipe(Recipe r) {
        recipe = r;
        progress = 0f;
        if (r != null && r.fuelCost > 0f) {
            if (batchFuelItem == null) {
                Item fuel = GlobalInventory.instance?.PickFuel();
                if (fuel != null) {
                    batchFuelItem = fuel;
                    batchFuelFen  = Mathf.RoundToInt(r.fuelCost * 100f / fuel.fuelValue);
                }
            }
        } else {
            batchFuelItem = null;
            batchFuelFen  = 0;
        }
    }

    // Restore-only (SaveSystem): re-bind the batch's recipe + its fuel commitment WITHOUT
    // touching progress or re-picking fuel — the buffer already holds the saved contents, so the
    // committed fuel leaf is whatever fuel item it carries.
    public void RestoreBatch(Recipe r) {
        recipe = r;
        batchFuelItem = null;
        batchFuelFen = 0;
        if (r != null && r.fuelCost > 0f) {
            foreach (ItemStack s in inputBuffer.itemStacks)
                if (s?.item != null && s.item.fuelValue > 0f) { batchFuelItem = s.item; break; }
            if (batchFuelItem != null)
                batchFuelFen = Mathf.RoundToInt(r.fuelCost * 100f / batchFuelItem.fuelValue);
        }
    }

    // True while ambient temperature affects the (untended) advance rate.
    public bool TempScaled => recipe != null && recipe.processTempMin.HasValue && recipe.processTempIdeal.HasValue;

    // Advance-rate multiplier at the given ambient temperature: 1.0 when not temperature-scaled,
    // otherwise a linear ramp from 0 at processTempMin up to 1 at processTempIdeal (clamped).
    public float Rate(float ambientTemp) {
        if (!TempScaled) return 1f;
        return Mathf.InverseLerp(recipe.processTempMin.Value, recipe.processTempIdeal.Value, ambientTemp);
    }

    // ── Local heat (foundry) ──────────────────────────────────────────────────
    // First-pass tuning constants — expect a playtest pass to settle the curve. `heat` is stored as
    // "degrees above ambient"; the temperature shown + rate-gated is ambient + heat. Gameplay knobs
    // are JSON-authored (building fuelBurnRate, recipe processTempMin/Ideal + heatCost); these scale
    // the burn→heat and cooling response.
    public const float HeatPerFuelEnergy    = 100f;   // heat (deg) gained per unit of burned fuel energy (liang × fuelValue)
    public const float HeatRetentionPerTick = 0.998f; // fraction of heat kept each 0.2s tick (slow cool toward ambient)
    public const float TempPerHeat          = 1f;     // heat→temperature scale; 1 = heat is literally degrees-above-ambient

    // Single tunable conversion point between internal heat charge and the absolute temperature the
    // player sees (InfoPanel) and the rate gate reads. Linear for now; can curve later without touching callers.
    public static float HeatToTemperature(float heat, float ambient) => ambient + heat * TempPerHeat;

    // Converts fuel the reservoir burned this tick into stored heat. Called by StructController right
    // after Reservoir.Burn so the heat lands BEFORE this tick's rate check. energy = liang × fuelValue
    // (so coal's higher fuelValue yields more heat per liang, matching its slower burn).
    public void AddFuelHeat(int fenBurned, Item fuel) {
        if (!localHeat || fenBurned <= 0 || fuel == null) return;
        heat += fenBurned / 100f * fuel.fuelValue * HeatPerFuelEnergy;
    }

    // Does inputBuffer hold every declared input in full?
    public bool InputsComplete() {
        if (recipe == null) return false;
        foreach (ItemQuantity iq in recipe.inputs)
            if (inputBuffer.Quantity(iq.item) < iq.quantity) return false;
        return true;
    }

    // Inputs AND the committed fuel are both loaded — the batch may start Working.
    public bool BatchLoaded() =>
        InputsComplete() && (batchFuelItem == null || inputBuffer.Quantity(batchFuelItem) >= batchFuelFen);

    // The pot's liquid capacity (fen) — the denominator for the visual fill, so the level reflects
    // the ACTUAL liquid volume in the pot, not "recipe complete = full". Sized in the ctor.
    public int PotCapacity => output.stackSize * output.nStacks;

    // ── Decorative-zone rendering ──────────────────────────────────────────
    // Maps processor state to how full its _w liquid zone draws (0..1, bottom-up) and its tint.
    // The fill always tracks the real liquid volume against the pot's capacity (so 5 liang in a
    // 10-liang pot reads half, not full): the input liquid while loading/working (water → blue,
    // tinted by processColor mid-batch), the output liquid once Ready/Tapped (tonic → its colour,
    // draining as it's hauled off). A tint with alpha 0 falls through to the shader's default blue.
    public void GetVisualFill(out float fillFraction, out Color32 tint) {
        int cap = PotCapacity;
        switch (state) {
            case State.Filling: fillFraction = LiquidVolume(LiquidInBuffer(), cap); tint = FirstLiquidColor(inputs);        break;
            case State.Working: fillFraction = LiquidVolume(LiquidInBuffer(), cap); tint = recipe?.processColor ?? default; break;
            case State.Ready:   fillFraction = LiquidVolume(OutputHeld(),    cap); tint = FirstLiquidColor(outputs);        break;
            case State.Tapped:  fillFraction = LiquidVolume(OutputHeld(),    cap); tint = FirstLiquidColor(outputs);        break;
            default:            fillFraction = 0f;                                 tint = default;                          break; // Empty
        }
        fillFraction = Mathf.Clamp01(fillFraction);
    }

    private static float LiquidVolume(int held, int capacity) => capacity > 0 ? held / (float)capacity : 0f;

    // Fen of LIQUID-class inputs currently buffered (water, not the herb/rice/fuel) — the visible
    // liquid level while loading and working. Rises as water is delivered, then holds during Working.
    private int LiquidInBuffer() {
        if (recipe == null) return 0;
        int liquid = 0;
        foreach (ItemQuantity iq in recipe.inputs)
            if (iq.item != null && iq.item.isLiquid) liquid += inputBuffer.Quantity(iq.item);
        return liquid;
    }

    // Fen of finished liquid still in the output inventory (falls to 0 as it's hauled off / drunk).
    private int OutputHeld() {
        int held = 0;
        foreach (ItemStack s in output.itemStacks)
            if (s != null) held += s.quantity;
        return held;
    }

    // Colour of the first liquid-class item among a set of declared input/output quantities.
    // Returns alpha 0 (the shader's default-blue fallback) when none are liquid (or null set).
    private static Color32 FirstLiquidColor(ItemQuantity[] set) {
        if (set == null) return default;
        foreach (ItemQuantity iq in set)
            if (iq.item != null && iq.item.isLiquid) return iq.item.liquidColor;
        return default;
    }

    // Advances the processor each in-game time-step (called every 0.2 s from StructController).
    // `dtSeconds` is the elapsed real/in-game seconds; `ambientTemp` drives temperature scaling.
    //   Working (UNTENDED only): progress accrues; on completion → Ready (awaiting a tap). A
    //            tended processor's Working is driven by its worker, so Tick leaves it alone.
    //   Tapped:  once `output` is fully drained → Empty, re-arming the Fill order.
    public void Tick(float dtSeconds, float ambientTemp) {
        // Local-heat (foundry): cool toward ambient each tick (fuel re-adds heat in StructController
        // before this call). The cached temperature drives the rate gate AND the InfoPanel readout.
        if (localHeat) heat *= HeatRetentionPerTick;
        float effTemp = localHeat ? HeatToTemperature(heat, ambientTemp) : ambientTemp;
        temperature = effTemp;

        if (state == State.Working && !tended) {
            progress += Rate(effTemp) * dtSeconds;
            if (progress >= duration) {
                progress = duration;
                state = State.Ready;
                // Latent heat: a completed melt discretely draws recipe.heatCost from the pool — the
                // dominant, intentional fuel cost of smelting (kept from being negligible). Below-min
                // temp afterwards freezes the next batch until the foundry is re-stoked.
                if (localHeat && recipe != null) heat = Mathf.Max(0f, heat - recipe.heatCost);
            }
        } else if (state == State.Tapped && output.IsEmpty()) {
            state = State.Empty;
            recipe = null;
            batchFuelItem = null;
            batchFuelFen = 0;
        }
    }

    // Converts a finished batch: drains everything in inputBuffer (inputs + fuel, decrementing
    // the global inventory — the conversion consumed them) and produces the recipe's outputs into
    // `output`, then enters Tapped. Called by TapProcessorTask (untended) or WorkProcessorTask
    // (tended auto-tap) once the building is confirmed alive.
    public void Tap() {
        foreach (ItemStack s in inputBuffer.itemStacks)
            if (s.item != null && s.quantity > 0)
                inputBuffer.Produce(s.item, -s.quantity);
        if (recipe != null)
            foreach (ItemQuantity oq in recipe.outputs) {
                // Fermented/brewed output bypasses Animal.Produce, so tally it here too (food chart, etc.).
                StatsTracker.instance?.NoteProduced(oq.item, oq.quantity);
                output.Produce(oq.item, oq.quantity);
            }
        state = State.Tapped;
    }

    // Drops both inventories' contents onto the floor at `here` — called on deconstruct so a
    // loaded batch / finished liquid isn't silently lost. Mirrors Reservoir.DropToFloor.
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

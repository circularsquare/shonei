using System;
using System.Collections.Generic;
using UnityEngine;

// The foundry: a continuous melt pool, NOT a batch Processor. Smiths deposit ore as discrete
// CHUNKS (one per deposit, ~1.9 liang); each chunk melts independently at a rate driven by the
// foundry's own heat, and a fully-melted chunk pours its metal into a shared MOLTEN POOL. Molten
// metals AUTO-ALLOY in the pool (copper + tin → bronze) when the cast target calls for the alloy,
// and the smith casts molten metal into bars. Inspired by Tinkers' Construct's smeltery.
//
// Heat (degrees above ambient) is stoked by fuel burned in StructController and bleeds toward
// ambient each tick; temperature = ambient + heat drives the melt rate. Melting draws LATENT HEAT
// as it happens (per liang melted), so a big cold dump stalls — emergent "don't overload" pressure.
//
// The melt + alloy simulation is split into STATIC steppers (StepMelt / StepAlloy) operating on
// plain data (a chunk list + a Dictionary molten pool), so they're unit-testable without a live
// Building / Inventory / World. Tick() just runs them over this foundry's own state.
//
// See SPEC-systems §Foundry. Replaces the foundry's old Processor (localHeat) mode.
public class Foundry : Building {
    // ── Melt-pool state ─────────────────────────────────────────────────────
    // One chunk per smith deposit, tracked separately (not merged) so each melts on its own clock.
    // moltenPool maps molten-metal item id → fen accumulated; bars are cast from it into `output`
    // (Phase 4). A plain Dictionary (not an Inventory) keeps the melt/alloy core pure & testable.
    public readonly List<MeltChunk> chunks = new List<MeltChunk>();
    public readonly Dictionary<int, int> moltenPool = new Dictionary<int, int>();

    public float heat;          // stored thermal charge, degrees above ambient; 0 = cold (ambient)
    public float temperature;   // cached absolute reading (ambient + heat); read by the InfoPanel
    public readonly int capacityFen;  // ceiling on total (intake + unmelted chunks + molten pool)

    // ── Inventories ─────────────────────────────────────────────────────────
    // intake: ore delivered by FeedFoundryTask (Reservoir → mixed classes, no decay, not a haul
    //   source). Tick sweeps it into chunks each frame (one chunk per ore type present). output:
    //   the cast bars (Storage → haul-routes normally); CastFoundryTask registers eviction hauls.
    public readonly Inventory intake;
    public readonly Inventory output;

    // ── Cast target ─────────────────────────────────────────────────────────
    // What bar the foundry aims to produce. Drives feeding (which ores to haul) and auto-alloy
    // gating (only alloys consistent with the target fire); casting itself pours ANY castable molten
    // (so leftover/inconsistent metal drains out as bars). Auto scores cast recipes vs production
    // targets; Manual pins a chosen bar. See SPEC-systems §Foundry.
    public enum CastMode { Auto, Manual }
    public CastMode castMode = CastMode.Auto;
    public int manualTargetBarId;   // bar item id when Manual; 0 = none chosen

    // ── Heat tuning (moved from Processor; the foundry is the only local-heat structure) ──
    // Playtest-tunable. `heat` is stored as degrees above ambient. HeatPerFuelEnergy sets BOTH the
    // rise speed (deg/tick from burning) and the equilibrium ceiling; HeatRetentionPerTick sets the
    // cooldown + caps the ceiling (ceiling ≈ gain/(1−retention)). Bumped 4× for a faster rise, with a
    // touch more cooling so the ceiling lands ~800–850° (well above the 600 melt-ideal) not ~1600°.
    public const float HeatPerFuelEnergy    = 400f;   // heat (deg) per unit burned fuel energy (liang × fuelValue)
    public const float HeatRetentionPerTick = 0.996f; // fraction of heat kept each 0.2s tick (cools toward ambient)
    public const float TempPerHeat          = 1f;     // heat→temperature scale; 1 = heat is literally degrees-above-ambient

    public Foundry(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) {
        capacityFen = Mathf.Max(1, st.foundryCapacityLiang) * 100;

        // The inventory tile (mirrors Processor): processorTileX/Y reused as generic inventory-tile geometry.
        Tile invTile = World.instance.GetTileAt(
            x + (mirrored ? (st.nx - 1 - st.processorTileX) : st.processorTileX),
            y + st.processorTileY);
        int stack = Mathf.Max(100, capacityFen);

        int oreSlots = Mathf.Max(3, Db.foundryMeltRecipes?.Count ?? 3);
        intake = new Inventory(oreSlots, stack, Inventory.InvType.Reservoir, invTile.x, invTile.y);
        intake.displayName = st.name + "_intake";

        // output: a single-slot (crate-like) Default Storage — bars are solid. It holds ONE bar type
        // at a time; CastAll / HasCastableMolten respect available room, so a second bar type just
        // waits for the first to be hauled out. Allow every cast recipe's output bar.
        var casts = Db.GetFoundryCastRecipes();
        output = new Inventory(1, stack, Inventory.InvType.Storage, invTile.x, invTile.y,
                               storageClass: ItemClass.Default, parentSortingOrder: sr.sortingOrder);
        output.displayName = st.name + "_output";
        if (casts != null)
            foreach (Recipe r in casts)
                if (r.outputs.Length > 0 && r.outputs[0].item != null) output.AllowItem(r.outputs[0].item);
    }

    // Single conversion point between heat charge and the absolute temperature shown / rate-gated.
    public static float HeatToTemperature(float heat, float ambient) => ambient + heat * TempPerHeat;

    // Converts fuel the reservoir burned this tick into stored heat (called by StructController after
    // Reservoir.Burn, BEFORE Tick, so the heat lands the frame it's gated on). energy = liang × fuelValue.
    public void AddFuelHeat(int fenBurned, Item fuel) {
        if (fenBurned <= 0 || fuel == null) return;
        heat += fenBurned / 100f * fuel.fuelValue * HeatPerFuelEnergy;
    }

    // Adds a deposited chunk of `ore` (fen) to the melt queue. Used by the feed task (Phase 4).
    public void Deposit(Item ore, int fen) {
        if (ore == null || fen <= 0) return;
        chunks.Add(new MeltChunk(ore, fen));
    }

    // Per-tick advance (called from StructController each 0.2s; ambient = weather temperature).
    public void Tick(float dtSeconds, float ambientTemp) {
        heat *= HeatRetentionPerTick;
        temperature = HeatToTemperature(heat, ambientTemp);
        SweepIntake();
        StepMelt(chunks, moltenPool, ref heat, temperature, dtSeconds);
        // Auto-alloy only fires for moltens consistent with the current cast target (so target=copper
        // doesn't eat stray tin into bronze). null target → empty set → no alloying.
        HashSet<int> consistent = ConsistentMoltens(TargetBar());
        StepAlloy(moltenPool, consistent.Contains);
    }

    // Drains delivered ore out of `intake` into new chunks — one chunk per ore type present this tick
    // (the smith's deposit becomes a melting chunk). Melt time is size-independent, so the exact
    // chunk boundary only affects heat-drain granularity.
    void SweepIntake() {
        foreach (ItemStack s in intake.itemStacks) {
            if (s.item == null || s.quantity <= 0) continue;
            int qty = s.quantity;
            Item ore = s.item;
            intake.Produce(ore, -qty);
            Deposit(ore, qty);
        }
    }

    // Total fen sitting in the molten pool / awaiting melt — for capacity checks (Phase 4) and the
    // visual fill (Phase 5).
    public int MoltenFen() { int t = 0; foreach (KeyValuePair<int, int> kv in moltenPool) t += kv.Value; return t; }
    public int ChunkFen()  { int t = 0; foreach (MeltChunk c in chunks) t += c.fen; return t; }
    public int IntakeFen() { int t = 0; foreach (ItemStack s in intake.itemStacks) if (s.item != null) t += s.quantity; return t; }
    // Everything occupying the pot: ore waiting to melt (intake + chunks) + accumulated molten.
    public int CurrentFen() => IntakeFen() + ChunkFen() + MoltenFen();
    public bool HasRoom() => CurrentFen() < capacityFen;

    // ── Recipe lookups ────────────────────────────────────────────────────────
    public static Recipe CastRecipeForBar(Item bar) {
        var casts = Db.GetFoundryCastRecipes();
        if (bar == null || casts == null) return null;
        foreach (Recipe r in casts) if (r.outputs.Length > 0 && r.outputs[0].item == bar) return r;
        return null;
    }
    public static Recipe CastRecipeForMolten(int moltenId) {
        var casts = Db.GetFoundryCastRecipes();
        if (casts == null) return null;
        foreach (Recipe r in casts) if (r.inputs.Length > 0 && r.inputs[0].item != null && r.inputs[0].item.id == moltenId) return r;
        return null;
    }

    // ── Cast target & consistency ───────────────────────────────────────────────
    // The bar the foundry is currently aiming for (Manual: the pinned bar; Auto: scored). May be null.
    public Item TargetBar() {
        if (castMode == CastMode.Manual)
            return manualTargetBarId != 0 ? Db.items[manualTargetBarId] : null;
        return AutoTargetBar();
    }

    // Autoselect: among cast recipes whose full ore chain is sourceable, pick the bar most under its
    // production target. Recipe.Score doesn't fit here (cast inputs are transient molten, ~always 0 →
    // would score 0), so we score the OUTPUT bar's scarcity directly via SurplusRatio.
    Item AutoTargetBar() {
        var casts = Db.GetFoundryCastRecipes();
        if (casts == null) return null;
        var targets = InventoryController.instance?.targets;
        Recipe best = null; float bestNeed = float.NegativeInfinity;
        foreach (Recipe r in casts) {
            if (!r.IsEligibleForPicking()) continue;
            Item bar = r.outputs[0].item;
            if (bar == null || !OreChainSourceable(bar)) continue;
            int target = (targets != null && targets.TryGetValue(bar.id, out int t)) ? t : 0;
            int have = InventoryController.instance != null ? InventoryController.instance.TotalAvailableQuantity(bar) : 0;
            float need = -Recipe.SurplusRatio(have, target); // lower surplus → more needed
            if (need > bestNeed) { bestNeed = need; best = r; }
        }
        return best?.outputs[0].item;
    }

    // The molten metals consistent with a target bar: the bar's molten input, plus (if that molten is
    // an alloy product) the alloy's component moltens. Everything else is inconsistent (pours out).
    public static HashSet<int> ConsistentMoltens(Item targetBar) {
        var set = new HashSet<int>();
        Recipe cast = CastRecipeForBar(targetBar);
        if (cast == null || cast.inputs.Length == 0 || cast.inputs[0].item == null) return set;
        int tm = cast.inputs[0].item.id;
        set.Add(tm);
        var alloys = Db.GetFoundryAlloyRecipes();
        if (alloys != null)
            foreach (Recipe a in alloys)
                if (a.outputs.Length > 0 && a.outputs[0].item != null && a.outputs[0].item.id == tm)
                    foreach (ItemQuantity iq in a.inputs) if (iq.item != null) set.Add(iq.item.id);
        return set;
    }

    // Is every ore-derived molten the target needs sourceable from current stock? (For bronze, BOTH
    // copper-ore and tin-ore must be available; the alloy product itself isn't ore-derived, so skip it.)
    public static bool OreChainSourceable(Item targetBar) {
        HashSet<int> set = ConsistentMoltens(targetBar);
        if (set.Count == 0) return false;
        var melts = Db.foundryMeltRecipes;
        var ic = InventoryController.instance;
        foreach (int moltenId in set) {
            bool oreDerived = false, sourceable = false;
            foreach (Recipe m in melts) {
                if (m.outputs[0].item.id != moltenId) continue;
                oreDerived = true;
                if (ic == null || ic.TotalAvailableQuantity(m.inputs[0].item) >= m.inputs[0].quantity) { sourceable = true; break; }
            }
            if (oreDerived && !sourceable) return false;
        }
        return true;
    }

    // Picks the consistent ore to feed next: among ores whose molten is consistent with the target and
    // that have free stock, the one whose metal is least-represented in the foundry — balances the
    // copper/tin feed for an alloy. Null = nothing sourceable.
    public Item ChooseFeedOre(HashSet<int> consistentMoltens) {
        var ic = InventoryController.instance;
        if (ic == null) return null;
        Item bestOre = null; int leastInFoundry = int.MaxValue;
        foreach (Recipe m in Db.foundryMeltRecipes) {
            int moltenId = m.outputs[0].item.id;
            if (!consistentMoltens.Contains(moltenId)) continue;
            Item ore = m.inputs[0].item;
            if (ic.TotalAvailableQuantity(ore) < m.inputs[0].quantity) continue;
            int inFoundry = moltenPool.GetValueOrDefault(moltenId) + ChunkFenForMolten(moltenId);
            if (inFoundry < leastInFoundry) { leastInFoundry = inFoundry; bestOre = ore; }
        }
        return bestOre;
    }

    int ChunkFenForMolten(int moltenId) {
        int t = 0;
        foreach (MeltChunk c in chunks) {
            Recipe m = Db.GetFoundryMeltRecipe(c.ore);
            if (m != null && m.outputs[0].item.id == moltenId) t += c.fen;
        }
        return t;
    }

    // ── Casting ─────────────────────────────────────────────────────────────────
    // Any molten with at least one bar's worth that has a cast recipe AND room in `output` for the
    // resulting bar. The room check keeps the single-slot output from busy-looping the cast order when
    // it's already full / occupied by another bar type — that metal waits until the slot frees.
    public bool HasCastableMolten() {
        foreach (KeyValuePair<int, int> kv in moltenPool) {
            Recipe cast = CastRecipeForMolten(kv.Key);
            if (cast == null || kv.Value < cast.inputs[0].quantity) continue;
            if (output.GetStorageForItem(cast.outputs[0].item) >= cast.outputs[0].quantity) return true;
        }
        return false;
    }

    // Pours castable molten into bars in `output` — the target metal AND any inconsistent leftover (so
    // molten can't strand). Bounded by `output` room (single slot → one bar type at a time; the rest
    // stays molten until the slot frees). Sub-bar dregs remain molten. Returns fen of bars produced.
    public int CastAll() {
        int produced = 0;
        foreach (int moltenId in new List<int>(moltenPool.Keys)) {
            Recipe cast = CastRecipeForMolten(moltenId);
            if (cast == null) continue;
            int perIn = cast.inputs[0].quantity, perOut = cast.outputs[0].quantity;
            Item bar = cast.outputs[0].item;
            int unitsByMolten = moltenPool[moltenId] / perIn;
            int unitsByRoom = perOut > 0 ? output.GetStorageForItem(bar) / perOut : unitsByMolten;
            int units = Math.Min(unitsByMolten, unitsByRoom);
            if (units <= 0) continue; // no molten, or output full / holding another bar type
            PoolAdd(moltenPool, moltenId, -units * perIn);
            int barFen = perOut * units;
            StatsTracker.instance?.NoteProduced(bar, barFen);
            output.Produce(bar, barFen);
            produced += barFen;
        }
        return produced;
    }

    // ── Visual: decorative molten glow ───────────────────────────────────────────
    // Renders the firebox glow through the shared liquid-zone path (WaterController, via the foundry's
    // `_w` mask). Fill rises with the pot's contents (melting ore + molten pool); tint is the dominant
    // metal's liquid colour, so a copper melt glows orange, tin paler, etc. surfaceRow off — it's a
    // firebox glow, not a shimmering pond surface.
    public override bool TryGetDisplayLiquid(out float fillFraction, out Color32 tint, out bool surfaceRow) {
        surfaceRow = false;
        int contents = ChunkFen() + MoltenFen();
        if (contents <= 0 || capacityFen <= 0) { fillFraction = 0f; tint = default; return false; }
        // Visible whenever the firebox holds metal, rising toward full as it fills (0.4 floor so a
        // small load still reads). Playtest-tunable.
        fillFraction = Mathf.Clamp01(Mathf.Max(0.4f, contents / (float)capacityFen));
        tint = DominantMeltColor();
        return true;
    }

    // The liquid colour of the metal most-represented in the pot — molten pool + what the melting
    // chunks will become — so the glow matches what's being smelted before any metal finishes melting.
    Color32 DominantMeltColor() {
        var tally = new Dictionary<int, int>();
        foreach (KeyValuePair<int, int> kv in moltenPool) tally[kv.Key] = tally.GetValueOrDefault(kv.Key) + kv.Value;
        foreach (MeltChunk c in chunks) {
            Recipe m = Db.GetFoundryMeltRecipe(c.ore);
            if (m != null) { int id = m.outputs[0].item.id; tally[id] = tally.GetValueOrDefault(id) + c.fen; }
        }
        int bestFen = 0, bestId = 0;
        foreach (KeyValuePair<int, int> kv in tally) if (kv.Value > bestFen) { bestFen = kv.Value; bestId = kv.Key; }
        return bestFen > 0 ? Db.items[bestId].liquidColor : default;
    }

    // ── Teardown (overrides Building.Destroy) ─────────────────────────────────────
    public override void Destroy() {
        WorkOrderManager.instance?.RemoveFoundryOrders(this);
        if (!WorldController.isClearing) DropToFloor(tile);
        intake.Destroy(reason: "foundry destroyed");
        output.Destroy(reason: "foundry destroyed");
        base.Destroy();
    }

    // Drops contents on deconstruct: cast remaining molten to bars first (so it isn't silently lost),
    // then spill intake + output + the raw ore in unmelted chunks to the floor.
    public void DropToFloor(Tile here) {
        CastAll();
        DropInv(intake, here);
        DropInv(output, here);
        foreach (MeltChunk c in chunks)
            if (c.ore != null && c.fen > 0) World.instance.ProduceAtTile(c.ore, c.fen, here);
        chunks.Clear();
        moltenPool.Clear(); // sub-bar molten dregs are lost on deconstruct
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

    // ── Melt + alloy simulation (pure static core — unit-tested in FoundryTests) ──────────────
    // One melt step over every chunk. A chunk gains meltProgress proportional to
    //   rate = (temperature − meltTempMin) / (meltTempIdeal − meltTempMin)
    // UNCLAMPED below min (so a cold pool re-solidifies chunks at a negative rate) and capped at ±1.
    // Melt time is INDEPENDENT of chunk size (meltDuration is per-chunk). A chunk that reaches full
    // progress pours its metal into `pool` and is removed. Heat is drawn per LIANG melted this tick
    // (latent heat), so heat drain DOES scale with chunk size. Mutates chunks, pool, and heat.
    public static void StepMelt(List<MeltChunk> chunks, Dictionary<int, int> pool, ref float heat, float temperature, float dt) {
        for (int i = chunks.Count - 1; i >= 0; i--) {
            MeltChunk chunk = chunks[i];
            Recipe r = Db.GetFoundryMeltRecipe(chunk.ore);
            if (r == null) continue; // not meltable — shouldn't have been deposited; leave it
            float denom = r.meltTempIdeal - r.meltTempMin;
            float rate = denom > 0f ? (temperature - r.meltTempMin) / denom
                                    : (temperature >= r.meltTempMin ? 1f : -1f);
            rate = Mathf.Clamp(rate, -1f, 1f);
            float before = chunk.meltProgress;
            chunk.meltProgress = Mathf.Clamp01(before + rate * dt / r.meltDuration);
            float gained = chunk.meltProgress - before;
            if (gained > 0f) {
                // A full chunk costs (fen/100) × meltHeatCost; charge the fraction melted this tick.
                heat -= chunk.fen / 100f * r.meltHeatCost * gained;
                if (heat < 0f) heat = 0f;
            }
            if (chunk.meltProgress >= 1f) {
                Item molten = r.outputs[0].item;
                int yieldFen = Mathf.RoundToInt(chunk.fen * (r.outputs[0].quantity / (float)r.inputs[0].quantity));
                PoolAdd(pool, molten.id, yieldFen);
                chunks.RemoveAt(i);
            }
        }
    }

    // Greedy auto-alloy. For each alloy recipe whose output is enabled (cast-target gating), convert
    // as many whole ratio-units as the pool's molten inputs allow into the alloy molten metal.
    // Leftover surplus of an input stays molten. Mutates pool only.
    public static void StepAlloy(Dictionary<int, int> pool, Func<int, bool> alloyOutputEnabled) {
        List<Recipe> alloys = Db.GetFoundryAlloyRecipes();
        if (alloys == null) return;
        foreach (Recipe r in alloys) {
            Item outMolten = r.outputs[0].item;
            if (alloyOutputEnabled != null && !alloyOutputEnabled(outMolten.id)) continue;
            int units = int.MaxValue;
            foreach (ItemQuantity iq in r.inputs)
                units = Math.Min(units, PoolGet(pool, iq.item.id) / iq.quantity);
            if (units <= 0) continue;
            foreach (ItemQuantity iq in r.inputs) PoolAdd(pool, iq.item.id, -iq.quantity * units);
            PoolAdd(pool, outMolten.id, r.outputs[0].quantity * units);
        }
    }

    static int PoolGet(Dictionary<int, int> pool, int id) => pool.GetValueOrDefault(id);

    // Adds `fen` (may be negative) to a molten id; drops the entry when it hits zero so the pool
    // never carries empty slots.
    static void PoolAdd(Dictionary<int, int> pool, int id, int fen) {
        int v = pool.GetValueOrDefault(id) + fen;
        if (v <= 0) pool.Remove(id);
        else pool[id] = v;
    }
}

// One ore deposit melting in the foundry. `ore` is the solid being melted (an ore, or later a bar
// to remelt); `fen` its amount; `meltProgress` ramps [0,1] (1 = fully melted into the pool). Melt
// time is size-independent, so a big chunk and a small chunk reach 1 in the same wall-clock.
public class MeltChunk {
    public Item ore;
    public int fen;
    public float meltProgress;
    public MeltChunk(Item ore, int fen) { this.ore = ore; this.fen = fen; }
}

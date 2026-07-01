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
    public readonly int capacityFen;  // metal-equivalent ceiling (molten + ore×yield); see MetalEquivFen

    // ── Inventories ─────────────────────────────────────────────────────────
    // intake: ore delivered by FeedFoundryTask (Reservoir → mixed classes, no decay, not a haul
    //   source). Tick sweeps it into chunks each frame (one chunk per ore type present). output:
    //   the cast bars (Storage → haul-routes normally); CastFoundryTask registers eviction hauls.
    public readonly Inventory intake;
    public readonly Inventory output;

    // In-hearth content visual (ore chunk + cast bar child sprites). Refreshed from Tick.
    FoundryVisuals visuals;

    // ── Cast target ─────────────────────────────────────────────────────────
    // What bar the foundry aims to produce. Drives feeding (which ores to haul) and auto-alloy
    // gating (only alloys consistent with the target fire); casting itself pours ANY castable molten
    // (so leftover/inconsistent metal drains out as bars). Auto scores cast recipes vs production
    // targets; Manual pins a chosen bar. See SPEC-systems §Foundry.
    public enum CastMode { Auto, Manual }
    public CastMode castMode = CastMode.Auto;
    public int manualTargetBarId;   // bar item id when Manual; 0 = none chosen

    // ── Heat tuning (moved from Processor; the foundry is the only local-heat structure) ──
    // Playtest-tunable. `heat` is stored as degrees above ambient. HeatPerFuelEnergy sets the rise
    // speed (deg/tick from burning); HeatRetentionPerTick sets the cooldown; MaxTemperature is a HARD
    // ceiling (heat simply stops rising once temperature hits it). At 2.5 fen/tick of pine the gain is
    // 2.5/100 × 1 × 128 = 3.2°/tick; the asymptotic idle ceiling (≈ gain/(1−retention) ≈ 800°) sits
    // below the 1000° hard cap, so the cap is a safety bound. Under melt load the latent heat
    // (meltHeatCost = 120°/liang, drawn as ore melts) pulls the working temperature well below that — a
    // full 10-liang melt settles ~390° (≈33% of the 600° ideal rate), so overloading the pot slows it.
    public const float HeatPerFuelEnergy    = 128f;   // heat (deg) per unit burned fuel energy (liang × fuelValue)
    public const float HeatRetentionPerTick = 0.996f; // fraction of heat kept each 0.2s tick (cools toward ambient)
    public const float MaxTemperature       = 1000f;  // hard ceiling: temperature never climbs past this
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

        // The cast bar is drawn by FoundryVisuals on the front dish (authored pixel spot), so suppress
        // the output inventory's own centred storage sprite — otherwise it double-draws at the inv tile.
        if (output.go != null) {
            SpriteRenderer osr = output.go.GetComponent<SpriteRenderer>();
            if (osr != null) osr.enabled = false;
        }

        // Heat-gated firebox light + fire art (foundry_f): the LightSource drives the flame's emission
        // brightness, its opacity (fades to show the dull-red firebox when cold), and the cast light —
        // all from HeatGlow01(). Day or night; no fuel/sun/worker gating. Mirrors Building's light setup
        // but with heatGated instead of isLightSource/lightWhileCrafting.
        var ls = go.AddComponent<LightSource>();
        ls.baseIntensity  = st.lightIntensity;
        ls.outerRadius    = st.lightOuterRadius;
        ls.innerRadius    = st.lightInnerRadius;
        ls.centerFlatten  = st.lightCenterFlatten;
        ls.flickerAmount  = st.lightFlicker;
        ls.emissionMult   = st.emissionStrength;
        ls.flickerPhase   = x * 0.37f + y * 0.71f; // decorrelate neighbours, deterministic
        ls.lightColor     = new Color(1f, 0.5f, 0.18f); // warm orange firebox
        ls.building       = this;
        ls.heatGated      = true;
        ls.glow01Provider = HeatGlow01;
        ls.isLit          = false; // Update sets it from heat on the first frame

        // In-hearth content sprites (ore + cast bar).
        visuals = go.AddComponent<FoundryVisuals>();
        visuals.Init(this);
    }

    // Firebox fire strength, 0 (cold) → 1 (blazing), from stored heat. Drives the foundry_f glow's
    // opacity + emission brightness + the cast light (LightSource.heatGated). Reaches full glow well
    // below the heat ceiling (~800–850) so a working foundry blazes, then fades as a spent pot cools.
    const float FireGlowReferenceHeat = 300f; // heat (deg above ambient) at which the firebox glows full
    public float HeatGlow01() => Mathf.Clamp01(heat / FireGlowReferenceHeat);

    // The ore most-represented by fen among melting chunks — what the hearth visual shows. Null if empty.
    public Item DominantChunkOre() {
        Item best = null; int bestFen = 0;
        var tally = new Dictionary<Item, int>();
        foreach (MeltChunk c in chunks) {
            if (c.ore == null) continue;
            int v = tally.GetValueOrDefault(c.ore) + c.fen;
            tally[c.ore] = v;
            if (v > bestFen) { bestFen = v; best = c.ore; }
        }
        return best;
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
        // Hard cap: temperature stops climbing past MaxTemperature (fuel heat above it is wasted).
        float maxHeat = (MaxTemperature - ambientTemp) / TempPerHeat;
        if (heat > maxHeat) heat = maxHeat;
        temperature = HeatToTemperature(heat, ambientTemp);
        SweepIntake();
        StepMelt(chunks, moltenPool, ref heat, temperature, dtSeconds);
        // Auto-alloy only fires for moltens consistent with the current cast target (so target=copper
        // doesn't eat stray tin into bronze). null target → empty set → no alloying.
        HashSet<int> consistent = ConsistentMoltens(TargetBar());
        StepAlloy(moltenPool, consistent.Contains);
        visuals?.Refresh();
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

    // Metal-equivalent fen of `moltenId` currently committed to the foundry: the molten pool plus
    // every chunk / intake ore that will melt into it (ore fen × yield). EXCLUDES alloy products
    // (molten bronze doesn't count toward copper/tin — its components were already spent forming it).
    // Drives both feed-ratio balancing and the "is this molten obtainable" cast-hold check.
    public int MoltenCommittedFen(int moltenId) {
        int t = moltenPool.GetValueOrDefault(moltenId);
        foreach (MeltChunk c in chunks) {
            Recipe m = Db.GetFoundryMeltRecipe(c.ore);
            if (m != null && m.outputs[0].item.id == moltenId) t += Mathf.RoundToInt(c.fen * MeltYield(c.ore));
        }
        foreach (ItemStack s in intake.itemStacks) {
            if (s.item == null || s.quantity <= 0) continue;
            Recipe m = Db.GetFoundryMeltRecipe(s.item);
            if (m != null && m.outputs[0].item.id == moltenId) t += Mathf.RoundToInt(s.quantity * MeltYield(s.item));
        }
        return t;
    }

    // Fen of molten produced per fen of this ore (melt recipe out/in). 1.0 if 1:1 or unknown.
    static float MeltYield(Item ore) {
        Recipe m = Db.GetFoundryMeltRecipe(ore);
        if (m == null || m.inputs.Length == 0 || m.inputs[0].quantity <= 0 || m.outputs.Length == 0) return 1f;
        return m.outputs[0].quantity / (float)m.inputs[0].quantity;
    }

    // Occupancy measured in MOLTEN-METAL equivalent: accumulated molten + each ore (intake + chunks)
    // counted as the metal it will become (ore_fen × yield). capacityFen is this metal-equivalent
    // ceiling, so at a 2:1 melt yield 10 liang molten OR 20 liang of ore both read "full".
    public int MetalEquivFen() {
        int t = MoltenFen();
        foreach (MeltChunk c in chunks)
            if (c.ore != null) t += Mathf.RoundToInt(c.fen * MeltYield(c.ore));
        foreach (ItemStack s in intake.itemStacks)
            if (s.item != null && s.quantity > 0) t += Mathf.RoundToInt(s.quantity * MeltYield(s.item));
        return t;
    }
    public bool HasRoom() => MetalEquivFen() < capacityFen;

    // How many fen of `ore` can still be fed before hitting the metal-equivalent ceiling (converts the
    // remaining metal headroom back into ore fen via the yield). Used by FeedFoundryTask.
    // Bounded by TWO ceilings: the overall metal capacity, AND the ore's molten SHARE of capacity for
    // the current target (so an alloy's components stay balanced — e.g. tin can't claim more than half
    // the pot for bronze, leaving room for copper). Pure-metal targets have share 1.0 → share cap is a
    // no-op. Without the share cap a single feed task could fill the whole pot with one ore.
    public int RoomForOreFen(Item ore) {
        float yield = MeltYield(ore);
        if (yield <= 0f) return 0;
        int metalRoom = capacityFen - MetalEquivFen();
        if (metalRoom <= 0) return 0;

        Recipe melt = Db.GetFoundryMeltRecipe(ore);
        if (melt != null && melt.outputs.Length > 0 && melt.outputs[0].item != null) {
            int moltenId = melt.outputs[0].item.id;
            var shares = TargetMoltenShares(TargetBar());
            if (shares.TryGetValue(moltenId, out float share)) {
                int shareRoom = Mathf.FloorToInt(share * capacityFen) - MoltenCommittedFen(moltenId);
                metalRoom = Mathf.Min(metalRoom, shareRoom);
            }
        }
        return metalRoom <= 0 ? 0 : Mathf.FloorToInt(metalRoom / yield);
    }

    // ── Recipe lookups ────────────────────────────────────────────────────────
    public static Recipe CastRecipeForBar(Item bar) {
        var casts = Db.GetFoundryCastRecipes();
        if (bar == null || casts == null) return null;
        foreach (Recipe r in casts) if (r.outputs.Length > 0 && r.outputs[0].item == bar) return r;
        return null;
    }
    // The plain BAR cast recipe for a molten — exactly ONE input (the molten itself). Used to DRAIN
    // leftover / inconsistent molten into bars. A MOLDED cast (molten + clay mold + plank → tool) has
    // >1 input and is deliberately NOT returned here: molded casts fire only toward the chosen target
    // (the smith brings the mold + plank), never as automatic drainage.
    public static Recipe CastBarRecipeForMolten(int moltenId) {
        var casts = Db.GetFoundryCastRecipes();
        if (casts == null) return null;
        foreach (Recipe r in casts)
            if (r.inputs.Length == 1 && r.inputs[0].item != null && r.inputs[0].item.id == moltenId) return r;
        return null;
    }

    // A cast recipe is "molded" when it needs more than the molten — extra solid inputs (clay mold,
    // plank) the smith must bring. Distinguishes a tool cast from a plain bar cast, data-driven (no
    // "is-tool" flag).
    public static bool IsMoldedCast(Recipe cast) => cast != null && cast.inputs.Length > 1;

    // Are a molded cast's EXTRA (non-molten) inputs sourceable from global stock? (The molten comes
    // from the pool; this gates only the mold/plank.) True for a plain bar cast (no extras).
    public static bool CastExtrasSourceable(Recipe cast) {
        if (!IsMoldedCast(cast)) return true;
        var ic = InventoryController.instance;
        for (int i = 1; i < cast.inputs.Length; i++) {
            ItemQuantity iq = cast.inputs[i];
            if (iq.item == null) continue;
            if (ic == null || ic.TotalAvailableQuantity(iq.item) < iq.quantity) return false;
        }
        return true;
    }

    // The alloy recipe that produces `moltenId` as its output, or null if no alloy makes it (pure metal).
    public static Recipe AlloyProducing(int moltenId) {
        var alloys = Db.GetFoundryAlloyRecipes();
        if (alloys == null) return null;
        foreach (Recipe a in alloys)
            if (a.outputs.Length > 0 && a.outputs[0].item != null && a.outputs[0].item.id == moltenId) return a;
        return null;
    }

    // The fraction of capacity each ore-derived molten of `target` should occupy, so the smith feeds an
    // alloy's components in ratio. For an alloy target the shares come from the alloy's input ratio
    // (bronze 1:1 → copper 0.5, tin 0.5); for a pure metal the single molten gets the whole pot (1.0).
    // Empty when there's no target. Keyed by molten id.
    public static Dictionary<int, float> TargetMoltenShares(Item target) {
        var shares = new Dictionary<int, float>();
        Recipe cast = CastRecipeForBar(target);
        if (cast == null || cast.inputs.Length == 0 || cast.inputs[0].item == null) return shares;
        int targetMolten = cast.inputs[0].item.id;
        Recipe alloy = AlloyProducing(targetMolten);
        if (alloy == null) { shares[targetMolten] = 1f; return shares; } // pure metal: ore melts straight to it
        int total = 0;
        foreach (ItemQuantity iq in alloy.inputs) if (iq.item != null) total += iq.quantity;
        if (total <= 0) return shares;
        foreach (ItemQuantity iq in alloy.inputs) if (iq.item != null) shares[iq.item.id] = iq.quantity / (float)total;
        return shares;
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
            if (!CastExtrasSourceable(r)) continue; // e.g. a tool cast with no clay mold in stock — don't auto-pick it
            int target = (targets != null && targets.TryGetValue(bar.id, out int t)) ? t : 0;
            int have = InventoryController.instance != null ? InventoryController.instance.TotalAvailableQuantity(bar) : 0;
            float need = -Recipe.SurplusRatio(have, target); // lower surplus → more needed
            if (need > bestNeed) { bestNeed = need; best = r; }
        }
        return best?.outputs[0].item;
    }

    // Economic need of the current target OUTPUT, comparable to `Recipe.Score` for a crucible recipe
    // that makes the same item (treating the molten input as freely available — feasibility is gated
    // separately by CanMakeTarget). target/have for a scarce output, +∞ when never produced ("make
    // now"), else 0 (untracked / no target / at-or-over target). Feeds the foundry feed/cast order
    // urgency via UrgencyConfig.CraftBand, so foundry work competes with crucible crafts on equal footing.
    public float TargetNeedScore() {
        Item target = TargetBar();
        if (target == null) return 0f;
        var ic = InventoryController.instance;
        if (ic?.targets == null || !ic.targets.TryGetValue(target.id, out int tgt) || tgt <= 0) return 0f;
        int have = ic.TotalAvailableQuantity(target);
        if (have >= tgt) return 0f;                                   // satisfied — no economic pull
        return have <= 0 ? float.PositiveInfinity : tgt / (float)have; // scarcer → higher
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
    // copper- and tin-source must be available; the alloy product itself isn't ore-derived, so skip it.)
    // "Sourceable" = BestFeedSource finds a feedable input (ore, or a remeltable bar fallback) — so the
    // target=copper case isn't falsely judged feasible from copper bars alone (which would never feed).
    public static bool OreChainSourceable(Item targetBar) {
        HashSet<int> set = ConsistentMoltens(targetBar);
        if (set.Count == 0) return false;
        var ic = InventoryController.instance;
        int targetOutputId = targetBar != null ? targetBar.id : 0;
        foreach (int moltenId in set) {
            bool oreDerived = false;
            foreach (Recipe m in Db.foundryMeltRecipes)
                if (m.outputs.Length > 0 && m.outputs[0].item.id == moltenId) { oreDerived = true; break; }
            if (oreDerived && BestFeedSource(moltenId, targetOutputId, ic) == null) return false;
        }
        return true;
    }

    // The best item to feed for `moltenId`: a raw input that melts to it, preferring ORE over remelting
    // a BAR. A bar (a melt input that is itself a cast OUTPUT) is a FALLBACK — used only when no ore is
    // sourceable, and NEVER when the bar IS the foundry's current target OUTPUT (`targetOutputId`):
    // remelting what we're producing would loop (target=copper bar → remelt copper bar → molten copper →
    // copper bar). Remelting a DIFFERENT bar toward the target is fine — e.g. target=copper TOOLS may
    // remelt copper bars into molten copper to pour into tools (melting a bar to cast a tool is the point).
    // ic == null → skip stock checks (unit tests). Null = nothing feedable.
    public static Item BestFeedSource(int moltenId, int targetOutputId, InventoryController ic) {
        Item ore = null, bar = null;
        foreach (Recipe m in Db.foundryMeltRecipes) {
            if (m.outputs.Length == 0 || m.outputs[0].item.id != moltenId) continue;
            Item input = m.inputs[0].item;
            if (input == null) continue;
            if (ic != null && ic.TotalAvailableQuantity(input) < m.inputs[0].quantity) continue;
            if (CastRecipeForBar(input) != null) {              // input is itself a castable bar → remelt
                if (input.id != targetOutputId && bar == null) bar = input; // never remelt our own output
            } else if (ore == null) {
                ore = input;
            }
        }
        return ore ?? bar; // prefer ore; remelt a bar only as a fallback
    }

    // Picks the consistent input to feed next: among target-consistent moltens with a feedable source
    // (ore preferred, bar remelt as fallback) that still have share-room, the molten furthest behind its
    // target SHARE (proportional fill) — so an alloy's components arrive together (copper + tin for
    // bronze) rather than one input filling the pot first. Null = nothing feedable.
    public Item ChooseFeedOre(HashSet<int> consistentMoltens) {
        var ic = InventoryController.instance;
        if (ic == null) return null;
        Item targetBar = TargetBar();
        int targetOutputId = targetBar != null ? targetBar.id : 0;
        var shares = TargetMoltenShares(targetBar);
        Item bestItem = null; float lowestFill = float.MaxValue;
        foreach (int moltenId in consistentMoltens) {
            Item src = BestFeedSource(moltenId, targetOutputId, ic);
            if (src == null || RoomForOreFen(src) <= 0) continue; // none feedable, at its share, or full
            float share = shares.TryGetValue(moltenId, out float s) ? s : 1f;
            float fill = share > 0f ? MoltenCommittedFen(moltenId) / (share * capacityFen) : float.MaxValue;
            if (fill < lowestFill) { lowestFill = fill; bestItem = src; }
        }
        return bestItem;
    }

    // Is the current cast target actually MAKEABLE right now? Every ore-derived component must be either
    // already in the foundry (pool/chunks/intake) OR feedable from stock (BestFeedSource, which respects
    // the ore-preference + own-output guard). For an ALLOY this needs ALL components — so target=bronze
    // with no tin source anywhere reads false, and the smith won't pointlessly feed copper that can never
    // become bronze. Manual targets skip the Auto-only OreChainSourceable gate, so this is the shared
    // feed-side feasibility check (gates both the FeedFoundry order and FeedFoundryTask).
    public bool CanMakeTarget() {
        Item target = TargetBar();
        if (target == null) return false;
        HashSet<int> consistent = ConsistentMoltens(target);
        if (consistent.Count == 0) return false;
        var ic = InventoryController.instance;
        int targetOutputId = target.id;
        foreach (int moltenId in consistent) {
            bool oreDerived = false;
            foreach (Recipe m in Db.foundryMeltRecipes)
                if (m.outputs.Length > 0 && m.outputs[0].item.id == moltenId) { oreDerived = true; break; }
            if (!oreDerived) continue;                                    // alloy product — formed by alloying its components
            if (MoltenCommittedFen(moltenId) > 0) continue;              // already have some in the foundry
            if (BestFeedSource(moltenId, targetOutputId, ic) != null) continue; // can feed it in
            return false;                                                // this component can't be obtained → not makeable
        }
        return true;
    }

    // Whether the foundry should keep stoking heat. It needs heat ONLY to melt chunks (pool molten
    // doesn't re-solidify), so it wants heat strictly while there's ore to melt — chunks already
    // melting, or ore in intake awaiting the next sweep. No pre-heating of an empty hearth: heat
    // starts the tick ore lands. When nothing's left to melt this goes false and StructController stops
    // burning fuel (and the smith stops fuelling) → heat decays toward ambient.
    public bool WantsHeat() => chunks.Count > 0 || IntakeFen() > 0;

    // ── Casting ─────────────────────────────────────────────────────────────────
    // Molten ids HELD BACK from casting: the alloy COMPONENTS of the current cast target, while their
    // alloy can still complete (every other input is obtainable — present in the foundry or sourceable
    // from stock). Holding lets copper + tin wait and alloy into bronze instead of each pouring out as
    // its own bar. A component is released (cast out) once its partner is unobtainable, so a lone metal
    // can't clog the pool forever. The target molten itself and genuine leftovers are never held — they
    // cast out (target metal as the product bar; the rest to drain). Empty for no/pure-metal target.
    HashSet<int> HeldFromCasting() {
        var held = new HashSet<int>();
        Recipe cast = CastRecipeForBar(TargetBar());
        if (cast == null || cast.inputs.Length == 0 || cast.inputs[0].item == null) return held;
        Recipe alloy = AlloyProducing(cast.inputs[0].item.id);
        if (alloy == null) return held; // pure-metal target: nothing to hold
        foreach (ItemQuantity input in alloy.inputs) {
            if (input.item == null) continue;
            bool partnersObtainable = true;
            foreach (ItemQuantity other in alloy.inputs)
                if (other.item != null && other.item.id != input.item.id && !MoltenObtainable(other.item.id)) {
                    partnersObtainable = false; break;
                }
            if (partnersObtainable) held.Add(input.item.id);
        }
        return held;
    }

    // Could `moltenId` still reach the pool? It's already committed in the foundry (molten / chunk /
    // intake), or an ore that melts to it is in stock. Decides whether holding an alloy component is
    // worthwhile (its partner is coming) or futile (release it to cast).
    bool MoltenObtainable(int moltenId) {
        if (MoltenCommittedFen(moltenId) > 0) return true;
        var ic = InventoryController.instance;
        if (ic == null) return false;
        foreach (Recipe m in Db.foundryMeltRecipes)
            if (m.outputs[0].item.id == moltenId && ic.TotalAvailableQuantity(m.inputs[0].item) >= m.inputs[0].quantity)
                return true;
        return false;
    }

    // Whether there's casting work right now: a non-held molten DRAINABLE to bars — ≥1 whole bar (normal
    // batching) OR a STRANDED sub-unit remainder (won't grow → pour the dregs out) — with output room;
    // OR the molded TARGET cast is firable (tools). The tool-target's reserved molten only counts here
    // when stranded (its leftover under one whole tool drains to a fractional bar). Mirrors the cast logic
    // so the CastFoundry order doesn't spin with nothing to do.
    public bool HasCastableMolten() {
        HashSet<int> held = HeldFromCasting();
        Recipe targetCast = CastRecipeForBar(TargetBar());
        int reservedMolten = (IsMoldedCast(targetCast) && targetCast.inputs[0].item != null) ? targetCast.inputs[0].item.id : -1;
        int reservedPerUnit = reservedMolten >= 0 ? targetCast.inputs[0].quantity : 0; // molten per whole tool
        foreach (KeyValuePair<int, int> kv in moltenPool) {
            if (held.Contains(kv.Key) || kv.Value <= 0) continue;
            Recipe cast = CastBarRecipeForMolten(kv.Key);
            if (cast == null || cast.outputs[0].item == null) continue;
            if (output.GetStorageForItem(cast.outputs[0].item) <= 0) continue; // no room for its bar
            if (kv.Key == reservedMolten) {
                // Reserved for tools. Drain to a bar ONLY a stranded remainder UNDER one whole tool —
                // a full tool's worth must wait to be cast as a tool, never poured to bars.
                if (kv.Value < reservedPerUnit && MoltenStranded(kv.Key)) return true;
                continue;
            }
            if (kv.Value >= cast.inputs[0].quantity || MoltenStranded(kv.Key)) return true;
        }
        return MoldedTargetCastable(targetCast);
    }

    // Can the molded TARGET cast fire: it's a molded recipe, the pool holds ≥1 unit of its molten, the
    // output has room for the tool, and every extra input (mold/plank) is sourceable. The molten is
    // already in the pool; the smith fetches the extras.
    public bool MoldedTargetCastable(Recipe targetCast) {
        if (!IsMoldedCast(targetCast)) return false;
        Item molten = targetCast.inputs[0].item, outItem = targetCast.outputs[0].item;
        if (molten == null || outItem == null) return false;
        if (moltenPool.GetValueOrDefault(molten.id) < targetCast.inputs[0].quantity) return false;
        if (output.GetStorageForItem(outItem) < targetCast.outputs[0].quantity) return false;
        return CastExtrasSourceable(targetCast);
    }

    // How many units of a molded (tool) cast can be poured right now, bounded by the molten in the pool
    // AND room in `output` (the buffer fills regardless of external storage). CastFoundryTask uses this
    // to size its mold/plank fetch so it casts the whole batch in one trip, not one tool at a time.
    public int MoldedUnitsCastable(Recipe cast) {
        if (!IsMoldedCast(cast) || cast.inputs[0].item == null || cast.outputs[0].item == null) return 0;
        int perIn = cast.inputs[0].quantity, perOut = cast.outputs[0].quantity;
        int byMolten = perIn > 0 ? moltenPool.GetValueOrDefault(cast.inputs[0].item.id) / perIn : 0;
        int byRoom = perOut > 0 ? output.GetStorageForItem(cast.outputs[0].item) / perOut : byMolten;
        return Math.Min(byMolten, byRoom);
    }

    // Casts up to `maxUnits` of one cast recipe: consumes its molten (inputs[0]) from the pool and
    // produces the output into `output`. Does NOT touch the recipe's EXTRA inputs (mold/plank) — the
    // caller (CastFoundryTask) consumes those from the smith's own inventory. Bounded by available
    // molten + output room. Returns units actually cast.
    public int CastMolten(Recipe cast, int maxUnits) {
        if (cast == null || cast.inputs.Length == 0 || cast.outputs.Length == 0) return 0;
        Item molten = cast.inputs[0].item, outItem = cast.outputs[0].item;
        if (molten == null || outItem == null) return 0;
        int perIn = cast.inputs[0].quantity, perOut = cast.outputs[0].quantity;
        int byMolten = perIn > 0 ? moltenPool.GetValueOrDefault(molten.id) / perIn : 0;
        int byRoom = perOut > 0 ? output.GetStorageForItem(outItem) / perOut : byMolten;
        int units = Math.Min(maxUnits, Math.Min(byMolten, byRoom));
        if (units <= 0) return 0;
        PoolAdd(moltenPool, molten.id, -units * perIn);
        int outFen = perOut * units;
        StatsTracker.instance?.NoteProduced(outItem, outFen);
        output.Produce(outItem, outFen);
        return units;
    }

    // Pours ALL of a molten into its continuous BAR form (fen-proportional — bars aren't discrete, so a
    // sub-unit remainder casts as a fractional bar instead of lingering). Bounded by `output` room.
    // Returns fen of bar produced. The fix for stranded sub-1-liang molten dregs.
    int DrainMoltenToBar(int moltenId) {
        Recipe cast = CastBarRecipeForMolten(moltenId);
        if (cast == null || cast.outputs.Length == 0 || cast.outputs[0].item == null) return 0;
        Item bar = cast.outputs[0].item;
        int perIn = cast.inputs[0].quantity, perOut = cast.outputs[0].quantity;
        int moltenAvail = moltenPool.GetValueOrDefault(moltenId);
        int roomFen = output.GetStorageForItem(bar);
        int roomMolten = perOut > 0 ? (int)((long)roomFen * perIn / perOut) : moltenAvail;
        int moltenToCast = Math.Min(moltenAvail, roomMolten);
        if (moltenToCast <= 0) return 0;
        int outFen = perIn > 0 ? (int)((long)moltenToCast * perOut / perIn) : moltenToCast;
        if (outFen <= 0) return 0;
        int moltenConsumed = perOut > 0 ? (int)((long)outFen * perIn / perOut) : outFen; // exact conservation
        PoolAdd(moltenPool, moltenId, -moltenConsumed);
        StatsTracker.instance?.NoteProduced(bar, outFen);
        output.Produce(bar, outFen);
        return outFen;
    }

    // True if `moltenId` won't accumulate further — nothing in the foundry melts into it (no chunk or
    // intake ore) AND it isn't a feedable component of the current target (no more will be brought in).
    // Such a molten will never reach a whole cast unit on its own, so its sub-unit remainder should drain
    // to a fractional bar rather than linger. (For a tool target, this is how the leftover under one whole
    // tool gets poured out as a partial bar once the metal source runs dry.)
    bool MoltenStranded(int moltenId) {
        foreach (MeltChunk c in chunks) {
            Recipe m = Db.GetFoundryMeltRecipe(c.ore);
            if (m != null && m.outputs.Length > 0 && m.outputs[0].item.id == moltenId) return false;
        }
        foreach (ItemStack s in intake.itemStacks) {
            if (s.item == null || s.quantity <= 0) continue;
            Recipe m = Db.GetFoundryMeltRecipe(s.item);
            if (m != null && m.outputs.Length > 0 && m.outputs[0].item.id == moltenId) return false;
        }
        Item target = TargetBar();
        if (ConsistentMoltens(target).Contains(moltenId)
            && BestFeedSource(moltenId, target != null ? target.id : 0, InventoryController.instance) != null)
            return false; // still feedable toward the target → more is coming
        return true;
    }

    // Drains castable molten into BARS in `output` — the target metal AND any inconsistent leftover (so
    // molten can't strand), fen-proportional so nothing sub-unit lingers. EXCLUDES held alloy components
    // (HeldFromCasting), and the molten reserved for the current MOLDED (tool) target UNLESS that molten
    // is stranded (then its sub-tool remainder drains to a fractional bar). `ignoreHeld` drains everything
    // incl. held + reserved (deconstruct — nothing silently lost). Returns fen of bars produced.
    public int CastAll(bool ignoreHeld = false) {
        HashSet<int> held = ignoreHeld ? null : HeldFromCasting();
        int reservedMolten = -1, reservedPerUnit = 0;
        if (!ignoreHeld) {
            Recipe targetCast = CastRecipeForBar(TargetBar());
            if (IsMoldedCast(targetCast) && targetCast.inputs[0].item != null) {
                reservedMolten = targetCast.inputs[0].item.id;
                reservedPerUnit = targetCast.inputs[0].quantity; // molten per whole tool
            }
        }
        int produced = 0;
        foreach (int moltenId in new List<int>(moltenPool.Keys)) {
            if (held != null && held.Contains(moltenId)) continue;
            // The tool-target molten is held for tools — pour out ONLY a stranded remainder that's under
            // one whole tool. A full tool's worth waits to be cast as a tool; draining it to bars here is
            // what caused the cast→haul→re-feed loop.
            if (moltenId == reservedMolten
                && !(moltenPool.GetValueOrDefault(moltenId) < reservedPerUnit && MoltenStranded(moltenId)))
                continue;
            produced += DrainMoltenToBar(moltenId);
        }
        return produced;
    }

    // ── Visual: molten-pool fill ─────────────────────────────────────────────────
    // Drives the molten render (the full-bright MoltenGlow sprite via the foundry's `_w` mask). Fill is
    // ONLY the actual molten pool — unmelted ore shows as its own item sprite (FoundryVisuals), so no
    // liquid appears until a chunk finishes melting. Tint is the dominant molten metal's colour.
    public override bool TryGetDisplayLiquid(out float fillFraction, out Color32 tint, out bool surfaceRow) {
        surfaceRow = false; // the emissive mask flags its own surface row; see WaterController
        int molten = MoltenFen();
        if (molten <= 0 || capacityFen <= 0) { fillFraction = 0f; tint = default; return false; }
        fillFraction = Mathf.Clamp01(molten / (float)capacityFen);
        tint = DominantMoltenColor();
        return true;
    }

    // Molten metal self-glows: rendered full-bright (lighting-independent) by the MoltenGlow sprite in
    // the metal's colour, so the pool reads as hot day or night. No cast light (that's the separate
    // firebox LightSource). See WaterController emissive mask + Assets/Lighting/MoltenGlow.shader.
    public override bool DisplayLiquidEmissive() => true;

    // The liquid colour of the metal most-represented in the MOLTEN pool (the visible liquid). Unmelted
    // ore doesn't count — it isn't shown as liquid.
    Color32 DominantMoltenColor() {
        int bestFen = 0, bestId = 0;
        foreach (KeyValuePair<int, int> kv in moltenPool) if (kv.Value > bestFen) { bestFen = kv.Value; bestId = kv.Key; }
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
        CastAll(ignoreHeld: true);
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

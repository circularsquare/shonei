using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Optional component of a Building that represents a workstation (crafting station).
// Owns the player-adjustable worker slot limit. Non-null only when structType.isWorkstation.
// WOM reads workstation.workerLimit when registering craft orders.
public class Workstation {
    public int capacity;  // max workers from StructType.capacity

    // Player-adjustable worker limit. Defaults to capacity (all slots open).
    // Persisted via StructureSaveData.workOrderEffectiveCapacity.
    // Use WorkOrderManager.SetWorkstationCapacity() to change at runtime.
    public int workerLimit;

    // Completed craft rounds at this workstation. Compared against structType.depleteAt
    // to trigger building depletion. Persisted via StructureSaveData.uses.
    public int uses = 0;

    public Workstation(int capacity) {
        this.capacity = capacity;
        this.workerLimit = capacity;
    }
}

// Optional component of a Building that manages an internal consumable-resource inventory.
// Owns inv, fuelItem, capacity, and burn rate. Non-null only when structType.hasFuelInv.
// Works for any drainable resource (fuel, water, etc.).
// LightSource consumes via Burn(). WOM registers a standing SupplyBuilding order via building.reservoir.
// Supply is triggered when quantity falls below refillFraction of capacity (default half).
public class Reservoir {
    public Item fuelItem;     // restrict to this leaf/group (e.g. "wood", "water"); null = accept ANY fuel (fuelValue>0)
    public int capacity;      // max stack size in fen
    public float burnRate;    // ENERGY/day consumed; divided by the stocked fuel's fuelValue at burn (wood=1 → unchanged)
    public float refillFraction; // supply fires when level drops below this fraction of capacity
    public Inventory inv;     // internal inventory: 1 stack, not tied to a tile
    private float burnAccumulator = 0f; // fractional-fen carry so sub-fen burn rates work across frames/ticks

    public Reservoir(Item fuelItem, int capacity, float burnRate, int buildingX, int buildingY, string buildingName, float refillFraction = 0.5f) {
        this.fuelItem = fuelItem;
        this.capacity = capacity;
        this.burnRate = burnRate;
        this.refillFraction = refillFraction;
        inv = new Inventory(1, capacity, Inventory.InvType.Reservoir, buildingX, buildingY);
        inv.displayName = buildingName + "_fuel";
    }

    // Total fen stocked in the (single) slot, regardless of which fuel leaf it holds. Unifies
    // restricted and any-fuel reservoirs: a restricted slot only ever holds the allowed item, so
    // this equals the old inv.Quantity(fuelItem); an any-fuel slot (fuelItem null) can't call that.
    public int Quantity() {
        int total = 0;
        foreach (ItemStack s in inv.itemStacks) if (s.item != null) total += s.quantity;
        return total;
    }

    // The concrete fuel leaf currently stocked (single slot), or null if empty. Used to top up the
    // same type (no mixing) and to read the burning fuel's potency.
    public Item HeldLeaf() {
        foreach (ItemStack s in inv.itemStacks) if (s.item != null && s.quantity > 0) return s.item;
        return null;
    }

    // True when level is below the refill fraction of capacity — triggers a WOM supply order.
    public bool NeedsSupply() => Quantity() < capacity * refillFraction;

    // True when level is above zero.
    public bool HasFuel() => Quantity() > 0;

    // Consumes the resource over time. Call from a per-frame (LightSource) or per-tick
    // (StructController, for non-light reservoirs like the fountain) update. `deltaTime` is in
    // game-seconds. Returns the amount actually consumed (fen). Owns its fractional-fen carry.
    public int Burn(float deltaTime) {
        // burnRate is ENERGY/day; the fen drained depends on the stocked fuel's potency
        // (fuelValue = energy per liang), so a denser fuel (coal 3) lasts proportionally longer.
        // Non-fuel contents (fountain water, fuelValue 0) fall back to raw liang/day via potency 1.
        Item burning = HeldLeaf();
        float potency = (burning != null && burning.fuelValue > 0f) ? burning.fuelValue : 1f;
        float fenPerSecond = burnRate * 100f / (potency * World.ticksInDay);
        burnAccumulator += fenPerSecond * deltaTime;
        if (burnAccumulator < 1f) return 0;

        int toConsume = Mathf.FloorToInt(burnAccumulator);
        int remaining = toConsume;
        foreach (ItemStack stack in inv.itemStacks) {
            if (stack.item == null || stack.quantity == 0 || remaining <= 0) continue;
            int fromThisStack = Mathf.Min(remaining, stack.quantity);
            inv.Produce(stack.item, -fromThisStack);
            remaining -= fromThisStack;
        }
        int consumed = toConsume - remaining;
        burnAccumulator -= consumed > 0 ? consumed : toConsume;
        if (burnAccumulator < 0f) burnAccumulator = 0f;
        return consumed;
    }

    // Debug instant-fill: tops the reservoir up to capacity, spawning the fuel into existence
    // (mirrors Blueprint.InstantFinish spawning the building for free). Returns fen added.
    public int FillToCapacity() {
        int need = capacity - Quantity();
        if (need <= 0) return 0;
        // Resolve a concrete leaf to spawn: a restricted group (e.g. "wood") → its first leaf;
        // an any-fuel reservoir (fuelItem null) → whatever's already stocked, else a picked fuel.
        Item leaf = fuelItem != null ? fuelItem.FirstLeaf() : (HeldLeaf() ?? GlobalInventory.instance?.PickFuel());
        if (leaf == null) return 0;
        inv.Produce(leaf, need);
        return need;
    }

    // Drops remaining contents onto the floor at the given tile. Used during building deconstruct
    // so items aren't silently lost.
    public void DropToFloor(Tile here) {
        if (inv.IsEmpty() || here == null) return;
        foreach (ItemStack stack in inv.itemStacks) {
            if (stack.item == null || stack.quantity == 0) continue;
            int qty = stack.quantity;
            inv.Produce(stack.item, -qty);
            World.instance.ProduceAtTile(stack.item, qty, here);
        }
    }

    public void Destroy() {
        inv.Destroy(reason: "reservoir destroyed");
    }
}

public class Building : Structure {
    // When true, all work orders for this building are suppressed. Player-togglable via UI.
    // Distinct from ConditionsMet() which checks runtime world conditions (e.g. pump has water).
    // Player intent vs. world state — both must be satisfied for the building to accept orders.
    public bool disabled = false;

    // Reachability-alert latch (runtime only, never saved). A workstation whose workNode no
    // animal can reach is silently useless. StructController's usability sweep sets
    // unreachableSince on first sighting, posts ONE EventFeed alert once it persists past a grace
    // window, and resets both when the building becomes reachable again. -1 = reachable / unseen.
    public float unreachableSince = -1f;
    public bool unreachableAlerted = false;

    // True when a mouse could meaningfully use this leisure building right now: not disabled,
    // not broken, fueled (if it has a reservoir), and within its active-hour window. Used by
    // LeisureTask / ReadBookTask seat scans so every leisure-type task applies the same
    // suitability rules — prevents drift like "benches ignore activeStartHour" by accident.
    public bool CanHostLeisureNow() {
        if (disabled || IsBroken) return false;
        if (reservoir != null && !reservoir.HasFuel()) return false;
        if (!SunController.IsHourInRange(structType.activeStartHour, structType.activeEndHour)) return false;
        return true;
    }

    // Non-null only for workstation buildings. Owns the player-adjustable worker slot limit.
    public Workstation workstation { get; private set; }

    // The animal currently running a CraftTask here, if any — registered by WorkObjective.Start and
    // VALIDATED ON READ (see ActiveCraft), so it needs no teardown: an interrupted, finished, or
    // reassigned worker simply stops matching. Drives the cauldron's brew fill/fire and the
    // craft-gated light, and is the reusable "is this workstation being worked right now" signal.
    public Animal workingAnimal;

    // The live CraftTask running at this building this instant, or null. Re-checks the registered
    // worker each call (still alive, still Working, still crafting HERE) so no stale state lingers.
    public CraftTask ActiveCraft {
        get {
            if (workingAnimal == null || workingAnimal.pendingDeath) return null;
            if (workingAnimal.state != Animal.AnimalState.Working) return null;
            return (workingAnimal.task is CraftTask ct && ct.workplace?.building == this) ? ct : null;
        }
    }
    public bool IsBeingCrafted => ActiveCraft != null;
    // True while a worker is actively labouring at this building — a CraftTask OR a tended
    // processor's WorkProcessorTask. Drives the craft-gated fire light (LightSource.craftGated),
    // so the cauldron's fire lights while it brews even though it's a Processor, not a CraftTask.
    // workingAnimal is set each work-tick (validated on read, so no teardown needed).
    public bool IsBeingWorked {
        get {
            if (workingAnimal == null || workingAnimal.pendingDeath) return false;
            if (workingAnimal.state != Animal.AnimalState.Working) return false;
            if (workingAnimal.task is CraftTask ct)         return ct.workplace?.building == this;
            if (workingAnimal.task is WorkProcessorTask wp) return wp.building == this;
            return false;
        }
    }
    public Inventory storage { get; private set; }
    // Non-null only for buildings with a consumable resource reservoir (torch, foundry, fountain, etc.).
    public Reservoir reservoir { get; private set; }
    // Non-null only for buildings with furnishing slots (currently: house). Each named slot
    // holds at most one furnishing item, decays on a fixed lifetime timer, and grants happiness
    // to residents while installed. See FurnishingSlots.cs.
    public FurnishingSlots furnishingSlots { get; private set; }
    // Non-null only for buildings with a passive timed converter. Conditionally created
    // from structType.hasProcessor. See Processor.cs.
    public Processor processor { get; private set; }
    // Non-null only for buildings whose StructType declares powerBoost > 1. Created in
    // OnPlaced so registration order (after WOM orders) is deterministic. Subclasses that
    // implement PowerSystem.IPowerConsumer directly (custom port layouts) should leave
    // structType.powerBoost = 1 so this wrapper isn't created in addition to themselves.
    public BuildingPowerConsumer powerConsumer { get; private set; }

    // ── Decorative liquid zone ─────────────────────────────────────────────
    // Resolves what liquid this building shows in its {name}_w.png companion
    // zone and how full it draws (0..1, bottom-up). The zone art itself is
    // opt-in (Structure.waterPixelOffsets); this is the single source of truth
    // for fill level + tint, polled by WaterController each surface-mask tick.
    // Priority: reservoir (fountain) > liquid storage (tank) > processor
    // (brewery) > plain full zone. A `tint` with alpha 0 renders as the shader's
    // default blue. `surfaceRow` flags whether the top rendered row shimmers
    // white like a pond surface. Returns false -> render nothing.
    public virtual bool TryGetDisplayLiquid(out float fillFraction, out Color32 tint, out bool surfaceRow) {
        fillFraction = 0f;
        tint         = default;             // alpha 0 → shader default blue
        surfaceRow   = true;                // tanks / processors / plain zones shimmer at the liquid top

        if (reservoir != null) {
            // Fountain: the basin reads full whenever the reservoir holds water,
            // dry when broken. No surface row — the authored water band is too
            // thin to give up a pixel to the shimmer highlight.
            if (IsBroken || !reservoir.HasFuel()) return false;
            fillFraction = 1f;
            surfaceRow   = false;
            if (reservoir.fuelItem != null && reservoir.fuelItem.isLiquid)
                tint = reservoir.fuelItem.liquidColor;
            return true;
        }
        if (structType.isLiquidStorage && storage != null) {
            // Tank: the first non-empty liquid stack drives both fill and tint.
            foreach (ItemStack st in storage.itemStacks) {
                if (st?.item == null || !st.item.isLiquid || st.quantity <= 0) continue;
                int capacity = storage.stackSize * storage.nStacks;
                if (capacity <= 0) return false;
                fillFraction = st.quantity / (float)capacity;
                tint         = st.item.liquidColor;
                return fillFraction > 0f;
            }
            return false;
        }
        if (processor != null) {
            // Brewery: state-driven fill + tint (blue while loading, the ferment
            // colour while Working, the output liquid's colour once Ready/Tapped).
            processor.GetVisualFill(out fillFraction, out tint);
            return fillFraction > 0f;
        }
        // Has a _w zone but no liquid-bearing component — render a plain full zone.
        fillFraction = 1f;
        return true;
    }

    // When true, this building's decorative liquid ALSO renders an additive emissive glow (the
    // foundry's molten metal — hot liquid self-glows in its metal colour). WaterController stamps the
    // filled zone pixels into the emissive mask and the MoltenGlow sprite adds the per-tile tint over
    // them. Default false (tanks/fountains/brewery liquids are inert, no glow).
    public virtual bool DisplayLiquidEmissive() => false;

    // Craft-output hook: a building may absorb some/all of a finished CRAFT output straight into
    // its own store instead of having the worker carry it off and drop it. Returns the leftover
    // quantity (fen) the worker must still handle normally. Default: absorb nothing. (Liquid batch
    // output now lands in Processor.output, not here.) See AnimalStateManager.HandleWorking.
    public virtual int TryAbsorbOutput(Item item, int qty) => qty;

    public Building(StructType st, int x, int y, bool mirrored = false, int shapeIndex = 0) : base(st, x, y, mirrored, shapeIndex: shapeIndex){
        go.name = "building_" + structType.name;

        if (st.isWorkstation)
            workstation = new Workstation(Mathf.Max(1, st.capacity));

        if (structType.isStorage){
            Tile sTile = World.instance.GetTileAt(
                x + (mirrored ? (st.nx - 1 - st.storageTileX) : st.storageTileX),
                y + st.storageTileY);
            var invType = structType.name == "market" ? Inventory.InvType.Market : Inventory.InvType.Storage;
            storage = new Inventory(structType.nStacks, structType.storageStackSize, invType, sTile.x, sTile.y, storageClass: structType.storageClass, parentSortingOrder: sr.sortingOrder);
            storage.displayName = structType.name;
            storage.ownerStructure = this; // lets Decay apply the floor rate when this building is broken
            // Floor items stay on the floor — storage is separate (building.storage).

            // Prompt the player to configure a fresh, empty, accepts-nothing store (dry storage
            // and tanks start all-disallowed). Markets accept everything by default, so skip them.
            if (invType == Inventory.InvType.Storage) {
                var sev = go.AddComponent<StorageEmptyVisuals>();
                sev.Init(storage, st.nx, sr.sortingOrder);
            }
        }

        if (st.hasFuelInv) {
            reservoir = new Reservoir(st.fuelItem, st.fuelCapacity, st.fuelBurnRate, x, y, st.name, st.fuelRefillFraction);
            if (st.isLightSource) {
                var ls = go.AddComponent<LightSource>();
                ls.baseIntensity = st.lightIntensity;
                ls.outerRadius   = st.lightOuterRadius;
                ls.innerRadius   = st.lightInnerRadius;
                ls.centerFlatten   = st.lightCenterFlatten;
                ls.flickerAmount   = st.lightFlicker;
                ls.emissionMult    = st.emissionStrength;
                ls.flickerPhase    = x * 0.37f + y * 0.71f; // decorrelate neighbours, deterministic
                ls.reservoir = reservoir;
                ls.building  = this; // gates burn + emission on this.disabled
                ls.sunModulated    = true;
                // Start unlit — Update() will set isLit correctly on the first frame
                // once fuel state is known. Avoids a one-frame flicker on placement/load.
                ls.isLit = false;
            }
        }

        // Craft-gated light + fire (cauldron; opt-in for foundry/crucible): a LightSource whose lit
        // state follows IsBeingCrafted rather than fuel/time. No reservoir. It also owns the fireGO
        // toggle (LightSource.Update), so craft-gated buildings don't manage their own fire art.
        if (st.lightWhileCrafting) {
            var ls = go.AddComponent<LightSource>();
            ls.baseIntensity = st.lightIntensity;
            ls.outerRadius   = st.lightOuterRadius;
            ls.innerRadius   = st.lightInnerRadius;
            ls.centerFlatten = st.lightCenterFlatten;
            ls.flickerAmount = st.lightFlicker;
            ls.emissionMult  = st.emissionStrength;
            ls.flickerPhase  = x * 0.37f + y * 0.71f; // decorrelate neighbours, deterministic
            ls.building      = this;
            ls.craftGated    = true;
            ls.isLit         = false; // Update sets it from craft state on the first frame
        }

        if (st.hasFurnishingSlots) {
            furnishingSlots = new FurnishingSlots(st.furnishingSlotNames, x, y, st.name);
            furnishingSlots.onSlotChanged = OnFurnishingSlotChanged;
            // Wire visuals from the constructor (not AttachAnimations) — base() has already
            // called AttachAnimations before we get here, and furnishingSlots wasn't set yet
            // at that point. By the time this line runs, `go` and `sr` are populated, so
            // FurnishingVisuals.Init can read parentSortingOrder normally.
            var fv = go.AddComponent<FurnishingVisuals>();
            fv.Init(this);
        }

        if (st.hasProcessor) {
            // The batch recipes are ordinary Recipes with tile==this building + a duration.
            System.Collections.Generic.List<Recipe> procRecipes = Db.GetProcessorRecipes(st.name);
            if (procRecipes == null) {
                Debug.LogError($"Building '{st.name}': hasProcessor=true but no recipe with tile=='{st.name}' and a duration — skipping processor.");
            } else {
                Tile pTile = World.instance.GetTileAt(
                    x + (mirrored ? (st.nx - 1 - st.processorTileX) : st.processorTileX),
                    y + st.processorTileY);
                processor = new Processor(procRecipes, st.processorTended, st.processorCapacityLiang * 100, pTile.x, pTile.y, sr.sortingOrder);

                // Discrete-output processors (scriptorium) show an overlay sprite while a batch is
                // loaded. Opt-in by art: only if a `{name}_load` sprite exists. Liquid processors
                // (cauldron) render their batch via the WaterController pot fill, so they ship no
                // `_load` sprite and skip this. Wired here, not AttachAnimations — the processor
                // doesn't exist yet when AttachAnimations runs (see Structure.AttachAnimations).
                Sprite loadSprite = Resources.Load<Sprite>("Sprites/Buildings/" + st.name.Replace(" ", "") + "_load");
                if (loadSprite != null) {
                    var plv = go.AddComponent<ProcessorLoadVisuals>();
                    plv.Init(this, loadSprite, sr.sortingOrder);
                }
            }
        }
    }

    // Fires when a furnishing slot's contents change (install or decay-out). Recomputes
    // happiness for every resident animal and notifies the optional visual component.
    void OnFurnishingSlotChanged(int slotIndex) {
        foreach (Animal a in GetResidents())
            a.happiness?.RecomputeFurnishingBonus(a);
        FurnishingVisuals visuals = go != null ? go.GetComponent<FurnishingVisuals>() : null;
        visuals?.Refresh(slotIndex);
    }

    // Every animal whose home is this building. The single source of truth for residents:
    // Reservable tracks only a count (+ one reservedBy string), so it can't enumerate them —
    // a scan of AnimalController is required. Used by the info panel's occupant list and by
    // furnishing-bonus recompute.
    public System.Collections.Generic.List<Animal> GetResidents() {
        var residents = new System.Collections.Generic.List<Animal>();
        AnimalController ac = AnimalController.instance;
        if (ac == null) return residents;
        for (int i = 0; i < ac.na; i++) {
            Animal a = ac.animals[i];
            if (a != null && a.homeBuilding == this)
                residents.Add(a);
        }
        return residents;
    }

    // Mice currently assigned to this work flag (Step 6). Same scan-is-source-of-truth approach as
    // GetResidents — the flag holds no roster; assignment lives on Animal.assignedFlag.
    public System.Collections.Generic.List<Animal> GetAssignedMice() {
        var assigned = new System.Collections.Generic.List<Animal>();
        AnimalController ac = AnimalController.instance;
        if (ac == null) return assigned;
        for (int i = 0; i < ac.na; i++) {
            Animal a = ac.animals[i];
            if (a != null && a.assignedFlag == this)
                assigned.Add(a);
        }
        return assigned;
    }

    public override void AttachAnimations() {
        base.AttachAnimations();
        // Auto-wrapped power consumers (StructType.powerBoost > 1, no custom IPowerConsumer
        // subclass) get the standard perimeter port stubs here so they show on every side
        // a shaft is wired up. Producer/storage subclasses (wheel, windmill, flywheel)
        // override AttachAnimations and call AttachPortStubs themselves with their own
        // port specs; this base path doesn't fire for them because powerBoost stays 1.
        if (structType.powerBoost > 1f && !(this is PowerSystem.IPowerConsumer)) {
            AttachPortStubs(BuildingPowerConsumer.GetPerimeterPorts(this));
        }
        // Note: FurnishingVisuals is attached from the Building constructor (after
        // furnishingSlots is set), not here. AttachAnimations runs from base() before
        // any of Building's own ctor body executes.
    }

    // Shared rotating-part attachment for power machinery (Windmill blades, Flywheel wheel,
    // and any future rotating visual). Spawns a child GameObject parented to `go`, pivots it
    // at the given edge-aligned hub coordinates, applies mirroring + sort-bucket + flipX, and
    // wires up a RotatingPart for tick-driven rotation. Returns the child GO so callers can
    // attach further visuals if needed; returns null if the sprite is missing.
    //
    // Hub coords are edge-aligned in tiles from the anchor's bottom-left CORNER (which sits
    // at world (x-0.5, y-0.5) since tiles are centred on integer coords). The mirror formula
    // reflects across the building's horizontal centre, also edge-aligned.
    protected GameObject AttachRotatingPart(
        string spriteName,
        float hubX, float hubY,
        System.Func<float> speedSource,
        System.Func<bool> isActive,
        float degPerSecAtMaxSpeed,
        float stallThreshold = 0f,
        float directionSign = -1f,
        int sortingOffset = 1,
        float initialAngle = 0f,
        string goName = "wheel") {
        Sprite sprite = Resources.Load<Sprite>("Sprites/Buildings/" + spriteName);
        if (sprite == null) {
            Debug.LogWarning($"{spriteName} sprite missing at Resources/Sprites/Buildings/{spriteName} — rotating part will not render.");
            return null;
        }
        GameObject partGO = new GameObject(goName);
        partGO.transform.SetParent(go.transform, true);
        float ahubX = mirrored ? (structType.nx - hubX) : hubX;
        partGO.transform.position = new Vector3(x - 0.5f + ahubX, y - 0.5f + hubY, 0f);
        if (initialAngle != 0f) partGO.transform.localRotation = Quaternion.Euler(0f, 0f, initialAngle);

        SpriteRenderer wsr = SpriteMaterialUtil.AddSpriteRenderer(partGO);
        wsr.sprite = sprite;
        wsr.flipX = mirrored;
        wsr.sortingOrder = (sr != null ? sr.sortingOrder : 10) + sortingOffset;
        LightReceiverUtil.SetSortBucket(wsr);

        RotatingPart rot = partGO.AddComponent<RotatingPart>();
        rot.speedSource         = speedSource;
        rot.isActive            = isActive;
        rot.degPerSecAtMaxSpeed = degPerSecAtMaxSpeed;
        rot.stallThreshold      = stallThreshold;
        rot.directionSign       = directionSign;
        return partGO;
    }

    public override void OnPlaced() {
        WorkOrderManager.instance?.RegisterOrdersFor(this);
        // Direct power-interface implementers (Windmill, Flywheel, MouseWheel, Clock,
        // PumpBuilding, Elevator) self-register here so subclasses don't have to write
        // boilerplate OnPlaced overrides. Auto-wrapped consumers (powerBoost > 1, no
        // direct interface) go through EnsurePowerConsumer below. The two paths are
        // mutually exclusive — EnsurePowerConsumer returns false when `this is IPowerConsumer`.
        // The load path (SaveSystem) skips OnPlaced — PowerSystem.RebuildFromWorld
        // re-registers everything in lifecycle Phase 6.
        PowerSystem ps = PowerSystem.instance;
        if (ps != null) {
            if (this is PowerSystem.IPowerProducer p) ps.RegisterProducer(p);
            if (this is PowerSystem.IPowerStorage s)  ps.RegisterStorage(s);
            if (this is PowerSystem.IPowerConsumer c) ps.RegisterConsumer(c);
        }
        if (EnsurePowerConsumer())
            PowerSystem.instance?.RegisterConsumer(powerConsumer);
    }

    // True iff some mouse is currently in WorkObjective at this building — i.e.
    // a runner has actually arrived and is cycling a recipe, not just been dispatched.
    // Used by power participants (MouseWheel, BuildingPowerConsumer) to gate output
    // and demand on real activity instead of WOM reservation state, which fires at
    // dispatch (before the walk). Cheap — scanned at most once per second per power
    // participant from PowerSystem.Tick. Animal.state is Working only during
    // WorkObjective; GoObjective and DropObjective set it to Moving.
    public bool HasActiveCrafter() {
        AnimalController ac = AnimalController.instance;
        if (ac == null) return false;
        for (int i = 0; i < ac.na; i++) {
            Animal a = ac.animals[i];
            if (a.state != Animal.AnimalState.Working) continue;
            if (a.task is CraftTask ct && ct.workplace?.building == this) return true;
        }
        return false;
    }

    // Idempotent wrapper-creation. Returns true if `powerConsumer` is non-null after
    // the call (i.e. the caller should/may register). Skipped for subclasses that
    // implement IPowerConsumer directly — those use their own custom port layout.
    public bool EnsurePowerConsumer() {
        if (powerConsumer != null) return true;
        if (structType.powerBoost <= 1f) return false;
        if (this is PowerSystem.IPowerConsumer) return false;
        powerConsumer = new BuildingPowerConsumer(this);
        return true;
    }

    public override void Destroy() {
        // Drop any animal refs that pointed at this building. The interior waypoints
        // themselves are torn down by Structure.Destroy (base call below); once gone,
        // an animal standing on a now-orphaned interior node sits on a non-standable
        // tile and UpdateMovement's fall integration catches them naturally — no need
        // for an explicit eviction-snap here. insideBuilding is derived from the tile's
        // interiorBuilding back-ref (cleared by that same teardown), so it self-corrects;
        // we only clear the cached home reference so FindHome / task picks don't hit a
        // stale Building.
        if (!WorldController.isClearing) {
            AnimalController ac = AnimalController.instance;
            if (ac != null) {
                for (int i = 0; i < ac.na; i++) {
                    Animal a = ac.animals[i];
                    if (a == null) continue;
                    if (a.homeBuilding == this) {
                        a.homeBuilding = null;
                        a.homeTile = null;
                        a.task?.Fail();
                    }
                    // Demolishing a work flag releases its assigned mice back to their home anchor.
                    if (a.assignedFlag == this) a.assignedFlag = null;
                }
            }
        }
        // Direct power-interface implementers unregister symmetrically to OnPlaced.
        // Auto-wrapped consumers go through `powerConsumer` (the two paths are exclusive).
        PowerSystem ps = PowerSystem.instance;
        if (ps != null) {
            if (this is PowerSystem.IPowerProducer p) ps.UnregisterProducer(p);
            if (this is PowerSystem.IPowerStorage s)  ps.UnregisterStorage(s);
            if (this is PowerSystem.IPowerConsumer c) ps.UnregisterConsumer(c);
        }
        if (powerConsumer != null) {
            PowerSystem.instance?.UnregisterConsumer(powerConsumer);
            powerConsumer = null;
        }
        if (workstation != null)
            WorkOrderManager.instance?.RemoveWorkstationOrders(this);
        if (structType.isStorage && storage != null) {
            if (!storage.IsEmpty() && !WorldController.isClearing)
                Debug.LogError($"Destroying building storage with items in it at ({x},{y})!");
            storage.Destroy(reason: $"{structType.name} deconstructed");
        }
        if (reservoir != null) {
            WorkOrderManager.instance?.RemoveFuelSupplyOrders(this);
            if (!WorldController.isClearing)
                reservoir.DropToFloor(tile);
            reservoir.Destroy();
        }
        if (furnishingSlots != null) {
            WorkOrderManager.instance?.RemoveFurnishingSupplyOrders(this);
            furnishingSlots.Destroy();
            furnishingSlots = null;
        }
        if (processor != null) {
            WorkOrderManager.instance?.RemoveProcessorOrders(this);
            if (!WorldController.isClearing)
                processor.DropToFloor(tile);
            processor.Destroy();
        }
        base.Destroy();
    }
}

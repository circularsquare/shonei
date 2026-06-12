using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// One mouse. Owns all per-animal state (position, inventory slots, needs, skills, job)
// and the picker that chooses the next Task each tick. Per-tick state transitions live
// in AnimalStateManager; pathfinding/movement in Nav. Task dispatch goes through
// WorkOrderManager — Animal never scans the world directly for work.
public class Animal : MonoBehaviour{
    public string aName;
    public int id;
    public float x;
    public float y;
    public float z => -0.0001f * id; // tiny per-animal Z offset to prevent sprite flicker
    public float maxSpeed = 2f;
    public bool facingRight = true;
    public bool pendingRefresh = false; // deferred Refresh() when SetJob fires mid-waypoint

    public Node target;         // path endpoint (tile-node or off-grid waypoint) — where you are currently going
    // The building this animal calls home — the authoritative reservation owner. Replaces
    // the old "homeTile.building" idiom that broke for multi-tile housing (the home tile
    // for a doored shack is the approach tile *outside* the footprint, where no building
    // sits). Set/cleared by FindHome; preserved across save/load via homeBuildingX/Y.
    public Building homeBuilding;
    // The building whose hollow interior this animal is currently standing in, or null.
    // Derived from the tile under the animal (Tile.interiorBuilding) — never cached, so it
    // cannot go stale when the animal is displaced by a fall / snap / elevator / load.
    public Building insideBuilding => TileHere()?.interiorBuilding;
    // The integer tile this animal walks TO when going home. For legacy 1×1 houses this is
    // the building's anchor (today's behaviour). For doored buildings this is the door's
    // approach tile, just outside the footprint. Pathing terminates here; the door system
    // then graph-edges through to the interior anchor (see Structure ctor).
    public Tile homeTile;
    private Tile _currentTile;  // cached for O(1) tile-occupancy tracking

    public Recipe recipe;
    public int numRounds = 0;
    public float workProgress = 0f;

    public Job job;
    public Inventory inv;
    public Inventory foodSlotInv; // equip slot: food only, 1 stack, 5 liang capacity
    public Inventory toolSlotInv; // equip slot: tool, 1 stack
    public Inventory clothingSlotInv; // equip slot: clothing (top), 1 stack
    public Inventory bookSlotInv;     // equip slot: book only (storageClass=Book), 1 stack of size 1
    public GlobalInventory ginv;

    public float energy;     // every time you get 1 energy you can do 1 work
    public float efficiency; // energy gain rate

    public SkillSet skills = new SkillSet();

    public Eating eating;
    public Eeping eeping;
    public Happiness happiness;
    public Nav nav;
    public AnimationController animationController;

    public Task task;

    public enum AnimalState{
        Idle,
        Working,        // For any work at a location (crafting, building, etc)
        Eeping,
        Moving,         // any type of moving, under the task system
        Falling,        // involuntary fall, bypasses task and nav systems
        Leisuring,      // leisure activity (chatting, tea house, etc.)
        Traveling,      // hidden while journeying to/from the off-screen market
    }

    // Which way the paper-doll faces, orthogonal to facingRight (the L/R mirror).
    // Side = the default L/R-mirrored view. Back = climbing a straight ladder / working
    // a back-facing station (crucible). Front is scaffolded but unused — no trigger, no art.
    // Resolved per-frame in AnimationController (never stored), like pose — see SPEC-rendering.
    public enum FacingView{ Side, Back, Front }
    private AnimalStateManager stateManager;
    private AnimalState _state;
    public AnimalState state{
        get { return _state; }
        set { if (_state != value) {
                _state = value;
                animationController?.UpdateState();
            }
        }
    }

    public System.Random random;
    public int rngSeed;         // seed for `random` — saved/loaded so animal AI is reproducible
    public float tickOffset;    // [0,1) — stagger phase for per-frame tick dispatch
    public int tickCounter = 0;

    // World.timer value after which DropTask attempts are allowed again. Set by
    // DropObjective when no drop target is reachable, to avoid log-spamming every
    // tick while the animal is boxed in. Transient — not saved.
    [System.NonSerialized] public float dropCooldownUntil = 0f;

    // Set true by TickUpdate when the mouse has starved to death. AnimalController
    // sweeps for this flag after the tick loop and removes the mouse — never
    // mid-iteration, so animals[] stays safe to compact. Transient — not saved
    // (a starved mouse is removed before the next save could capture it).
    [System.NonSerialized] public bool pendingDeath = false;

    // Set before Start() runs when loading a save; Start() checks this and applies it.
    // [NonSerialized] prevents Unity from serializing a default AnimalSaveData instance in place of null.
    [System.NonSerialized] public AnimalSaveData pendingSaveData = null;

    public GameObject go;
    public SpriteRenderer sr;
    public Sprite sprite;
    public World world;

    Action<Animal, Job> cbAnimalChanged;



    // FRAME 2 (for default world) — runs the frame after the animal's GameObject is spawned.
    // If pendingSaveData is set (load path), applies saved state; otherwise initializes fresh.
    public void Start(){
        world = World.instance;
        this.stateManager = new AnimalStateManager(this);
        this.go = this.gameObject;
        this.sr = go.transform.Find("Body").GetComponent<SpriteRenderer>();
        animationController = go.GetComponent<AnimationController>();
        // Paper-doll parts share sortingOrder 50 and sort by world-Y. Without a
        // SortingGroup, two mice crossing paths interleave their parts (one's head
        // between the other's legs). The group sorts each mouse as a unit. The
        // id-hash offset gives a deterministic winner when two mice tie on Y (e.g.
        // passing on a ladder) — ~6% of pairs still tie within the 15 buckets and
        // fall back to Unity's renderer-id tiebreaker. Phase 4's coarse-bucket
        // collapse will quantize 50..64 back into one batch.
        var sg = go.GetComponent<UnityEngine.Rendering.SortingGroup>();
        if (sg == null) sg = go.AddComponent<UnityEngine.Rendering.SortingGroup>();
        sg.sortingOrder = 50 + (id % 15);
        this.inv = new Inventory(5, 1000, Inventory.InvType.Animal);
        this.foodSlotInv = new Inventory(1, 300, Inventory.InvType.Equip);
        this.toolSlotInv = new Inventory(1, 200, Inventory.InvType.Equip);
        this.clothingSlotInv = new Inventory(1, 200, Inventory.InvType.Equip);
        // Restricted to Book-class items via storageClass — see Inventory.ItemTypeCompatible.
        // Used by ResearchTask (M5) and ReadBookTask (M6) to carry a book during the activity.
        this.bookSlotInv = new Inventory(1, 100, Inventory.InvType.Equip, storageClass: ItemClass.Book);
        this.nav = new Nav(this);
        ginv = GlobalInventory.instance;
        // Seed per-animal RNG deterministically. New animals draw a seed from the central
        // Rng (so the world seed propagates); load path replaces this with the saved seed
        // a few lines below. Result: animal-level decisions reproduce on save/load.
        rngSeed = Rng.NextInt();
        random = new System.Random(rngSeed);

        if (pendingSaveData != null) {
            this.aName = string.IsNullOrEmpty(pendingSaveData.aName) ? Db.DrawName(AnimalController.instance.UsedNames()) : pendingSaveData.aName;
            this.go.name = "animal_" + aName;
            // Restore the saved seed if present. 0 means "old save, no seed persisted" —
            // we keep the fresh seed assigned above, which then gets saved next time.
            if (pendingSaveData.rngSeed != 0) {
                rngSeed = pendingSaveData.rngSeed;
                random = new System.Random(rngSeed);
            }
            this.energy = pendingSaveData.energy;
            this.eating = new Eating();
            this.eating.food = pendingSaveData.food;
            this.eating.starvingTicks = pendingSaveData.starvingTicks;
            this.eeping = new Eeping();
            this.eeping.eep = pendingSaveData.eep;
            this.happiness = new Happiness();
            if (pendingSaveData.satisfactions != null)
                foreach (var kv in pendingSaveData.satisfactions)
                    if (this.happiness.satisfactions.ContainsKey(kv.Key))
                        this.happiness.satisfactions[kv.Key] = kv.Value;
            this.happiness.warmth = pendingSaveData.warmth;
            this.job = Db.GetJobByName(pendingSaveData.jobName) ?? Db.jobs[0];
            this.state = AnimalState.Idle;
            this.efficiency = eating.Efficiency() * eeping.Efficiency() * happiness.TemperatureEfficiency();
            // Restore animal inventory items
            foreach (ItemStackSaveData ssd in pendingSaveData.inv.stacks) {
                if (!string.IsNullOrEmpty(ssd.itemName) && Db.itemByName.ContainsKey(ssd.itemName) && ssd.quantity > 0) {
                    inv.Produce(Db.itemByName[ssd.itemName], ssd.quantity);
                }
            }
            SaveSystem.LoadInventory(foodSlotInv, pendingSaveData.foodSlotInv);
            SaveSystem.LoadInventory(toolSlotInv, pendingSaveData.toolSlotInv);
            SaveSystem.LoadInventory(clothingSlotInv, pendingSaveData.clothingSlotInv);
            SaveSystem.LoadInventory(bookSlotInv, pendingSaveData.bookSlotInv);
            skills.Deserialize(pendingSaveData.skillXp, pendingSaveData.skillLevel);
            // Resume mid-journey travel if the animal was saved while traveling.
            // New saves carry a travelTaskType descriptor so we can rebuild the full
            // HaulTo/FromMarketTask tail — delivering or receiving at the market and
            // walking home with the goods. The resumed TravelingObjective runs the
            // full canonical leg duration (MarketTransitTicks); we then restore
            // workProgress from the save so the mouse picks up at the same fraction
            // of the strip it was on at save time (workProgress is the single source
            // of travel progress — travelProgress on save data is just its persisted
            // form). Legacy saves, or cases where the market / storage building was
            // demolished between save and load, fall through to ResumeTravelTask,
            // which just finishes the remaining ticks and goes idle.
            if (pendingSaveData.isTraveling && pendingSaveData.travelDuration > 0) {
                Task resumed = null;
                switch (pendingSaveData.travelTaskType) {
                    case "HaulToMarket":
                        if (!string.IsNullOrEmpty(pendingSaveData.travelItemName)
                            && Db.itemByName.TryGetValue(pendingSaveData.travelItemName, out Item htItem))
                            resumed = new HaulToMarketTask(this,
                                new ItemQuantity(htItem, pendingSaveData.travelItemQty),
                                pendingSaveData.travelReturnLeg);
                        break;
                    case "HaulFromMarket":
                        if (!string.IsNullOrEmpty(pendingSaveData.travelItemName)
                            && Db.itemByName.TryGetValue(pendingSaveData.travelItemName, out Item hfItem)
                            && pendingSaveData.travelStorageX.HasValue
                            && pendingSaveData.travelStorageY.HasValue) {
                            Tile storageTile = World.instance.GetTileAt(
                                pendingSaveData.travelStorageX.Value,
                                pendingSaveData.travelStorageY.Value);
                            if (storageTile != null)
                                resumed = new HaulFromMarketTask(this,
                                    new ItemQuantity(hfItem, pendingSaveData.travelItemQty),
                                    storageTile, pendingSaveData.travelReturnLeg);
                        }
                        break;
                }
                // Legacy fallback: ResumeTravelTask only knows how to finish the
                // remaining ticks, so it takes the tail-only duration.
                if (resumed == null) {
                    int remaining = Mathf.Max(1, pendingSaveData.travelDuration - (int)pendingSaveData.travelProgress);
                    resumed = new ResumeTravelTask(this, remaining);
                }
                this.task = resumed;
                if (this.task.Start()) {
                    // TravelingObjective.Start() zeroed workProgress; restore it so the
                    // journey-display icon picks up where the save left off.
                    this.workProgress = pendingSaveData.travelProgress;
                } else {
                    this.task = null;
                }
            }
            // Restore the home reservation so the animal doesn't have to re-FindHome on
            // first SlowUpdate (which would also reset furnishing happiness). insideBuilding
            // needs no restore — it's derived from position, so a mouse saved inside a
            // building keeps its saved (x, y) and resolves it for free.
            if (pendingSaveData.homeBuildingX.HasValue && pendingSaveData.homeBuildingY.HasValue) {
                Tile homeAnchor = world.GetTileAt(pendingSaveData.homeBuildingX.Value, pendingSaveData.homeBuildingY.Value);
                if (homeAnchor?.building != null && homeAnchor.building.structType.isHousing) {
                    homeBuilding = homeAnchor.building;
                    homeTile = homeBuilding.doorApproachTile ?? homeBuilding.tile;
                    homeBuilding.res.Reserve(aName);
                    happiness?.RecomputeFurnishingBonus(this);
                }
            }
            pendingSaveData = null;
        } else {
            this.aName = Db.DrawName(AnimalController.instance.UsedNames());
            this.go.name = "animal_" + aName;
            this.state = AnimalState.Idle;
            this.job = Db.jobs[0];
            this.efficiency = 1f;
            this.energy = 0f;
            this.eating = new Eating();
            this.eating.food = this.eating.maxFood;
            this.eeping = new Eeping();
            this.eeping.eep = this.eeping.maxEep;
            this.happiness = new Happiness();
            // Seed social satisfaction so freshly-created mice don't start life lonely.
            this.happiness.satisfactions["social"] = 3f + (float)random.NextDouble() * 2f;
            FindHome();
        }
        // Stagger SlowUpdate across animals so they don't all fire on the same tick
        tickCounter = id % 10;
        // Register initial tile occupancy
        _currentTile = TileHere();
        AnimalController.instance.RegisterAnimalOnTile(_currentTile);
        RefreshInteriorRendering();   // initial layer state (e.g. a mouse loaded inside a burrow)
        // Add to the tickable animals array now that we're fully initialized.
        // This is deferred from AddAnimal() so TickUpdate/UpdateColonyStats never
        // iterate over an animal whose Start() hasn't run yet.
        AnimalController.instance.RegisterReady(this);
    }

    public void TickUpdate() { // called from animalcontroller each second.
        tickCounter++;
        if (tickCounter % 10 == 0) {
            SlowUpdate();
        }
        HandleNeeds();
        // Starvation: warn the player the moment the countdown starts, and flag the
        // mouse for removal once it's gone a full day without food. The dead mouse
        // skips the rest of its tick; AnimalController.RemoveDeadAnimals does the
        // actual teardown after the tick loop.
        if (eating.starvingTicks == 1)
            EventFeed.instance?.Post($"<color=#ff6666>{aName} is starving!</color>");
        if (eating.StarvedToDeath()) { pendingDeath = true; return; }
        UpdateEfficiency();
        energy += efficiency;
        if (energy > 1f) { // if you have enough energy, spend it. also then you can work if you're Working.
            energy -= 1f;
            stateManager.UpdateState();
        }
    }

    private void HandleNeeds() {
        eating.Update();
        eeping.Update();
        // Sleep recovery is wall-clock — ticked here every tick, NOT from HandleEeping.
        // HandleEeping only runs when the energy/efficiency throttle fires UpdateState, so
        // driving recovery from there would scale it with efficiency while depletion
        // (eeping.Update above) stays unthrottled. A hungry, exhausted mouse has low
        // efficiency; throttled recovery + unthrottled depletion would let it lose eep
        // faster than it regains it and never wake. Ticking recovery here keeps it
        // symmetric with depletion — and with eating recovery below.
        if (state == AnimalState.Eeping) { eeping.Eep(1f, AtHome()); }
        if (eating.Hungry()) {
            Item slotFood = foodSlotInv.itemStacks[0].item;
            if (slotFood != null && foodSlotInv.Quantity(slotFood) > 0) {
                int qty = foodSlotInv.Quantity(slotFood);
                if (qty >= 100) {
                    // Full meal
                    foodSlotInv.Produce(slotFood, -100);
                    eating.Eat(slotFood.foodValue);
                    happiness.NoteAte(slotFood, 1f);
                } else {
                    // Partial meal — consume remainder, scale nutrition, partial happiness credit
                    foodSlotInv.Produce(slotFood, -qty);
                    eating.Eat(slotFood.foodValue * qty / 100f);
                    happiness.NoteAte(slotFood, qty / 100f);
                }
            }
        }

    }
    private void UpdateEfficiency() {
        efficiency = eating.Efficiency() * eeping.Efficiency() * happiness.TemperatureEfficiency();
        maxSpeed = 1.5f * efficiency;
    }

    // Bedtime urgency — ramps 0 → 1 across the early-night window so mice don't all
    // decide to sleep on the same tick. Scaled by Eeping.bedtimeMaxBoost (0.5) inside
    // ShouldSleep, so the effective threshold range is [0.4, 0.9]:
    //   bedtime=0   → sleep only if e < 0.4   (daytime: emergency nap when fatigued)
    //   bedtime=0.5 → sleep if e < 0.65       (mid-evening: tired mice peel off)
    //   bedtime=1   → sleep if e < 0.9        (deep night: even mostly-rested mice sleep,
    //                                          but fully-rested mice (e ≥ 0.9) stay up)
    // Window: 0 before 7pm, linearly 0→1 from 7pm to 11pm, holds at 1 through to 6am.
    private float BedtimeUrgency() {
        float phase = (World.instance.timer % World.ticksInDay) / (float)World.ticksInDay;
        float hour = phase * 24f;
        if (hour >= 6f && hour < 19f) return 0f;
        if (hour >= 19f && hour < 23f) return (hour - 19f) / 4f;
        return 1f;
    }

    // True between 5 pm and 9 pm — mice prefer leisure over work during this window.
    private bool IsLeisureTime() {
        float phase = (World.instance.timer % World.ticksInDay) / (float)World.ticksInDay;
        return phase >= 17f / 24f && phase < 21f / 24f;
    }

    // Estimates what fraction of a 24-hour day an animal spends on productive work,
    // accounting for sleep and leisure/idle time. Returns 0–1.
    // Defaults match current game constants; override for what-if analysis.
    // Animals sleep until maxEep (not just nightSleepThreshold), then work the rest of the night.
    public static float EstimateDailyWorkFraction(
        float nightStartHour = 21f,
        float nightEndHour = 6f,
        float leisureStartHour = 17f,
        float workChanceDuringWork = 0.80f,
        float workChanceDuringLeisure = 0.20f,
        float tireRate = 0.2f,    // Eeping.tireRate
        float eepRate = 1f,       // Eeping.eepRate (at home)
        float maxEep = 100f,      // Eeping.maxEep
        float sleepThreshold = 0.85f // Eeping.nightSleepThreshold
    ) {
        float ticksPerHour = World.ticksInDay / 24f;

        // How long the animal is awake before night (6AM → nightStart)
        float wakingBeforeNight = nightStartHour - nightEndHour; // 15h
        float eepAtBedtime = sleepThreshold * maxEep - tireRate * wakingBeforeNight * ticksPerHour;
        eepAtBedtime = Mathf.Max(0f, eepAtBedtime); // can't go below 0

        // Sleep until maxEep, recovery = eepRate per tick
        float sleepTicks = (maxEep - eepAtBedtime) / eepRate;
        float sleepHours = sleepTicks / ticksPerHour;

        // If sleep exceeds the night window it spills into daytime
        float nightHours = (24f - nightStartHour) + nightEndHour;
        float leisureHours = nightStartHour - leisureStartHour;
        float awakeNightHours = Mathf.Max(0f, nightHours - sleepHours);

        // Work-window hours: daytime minus leisure, plus any awake nighttime
        float workHours = (24f - nightHours - leisureHours) + awakeNightHours;
        float sleepSpill = Mathf.Max(0f, sleepHours - nightHours);
        workHours -= sleepSpill;
        if (workHours < 0f) {
            leisureHours = Mathf.Max(0f, leisureHours + workHours);
            workHours = 0f;
        }

        return (workHours * workChanceDuringWork + leisureHours * workChanceDuringLeisure) / 24f;
    }

    public void SlowUpdate() { // called every 10 or so seconds
        FindHome();
        eating.SlowUpdate();
        happiness.UpdateComfortRange(this);
        happiness.SlowUpdate(this);
        ScanForNearbyDecorations();
    }

    // Scans the Chebyshev neighbourhood for active decoration buildings and notifies Happiness.
    // Each decoration type has its own timer — all in-range types are refreshed per call.
    // A decoration with a reservoir only counts when it has fuel (e.g. fountain needs water).
    // Computed from DB at startup — always equals the largest decorRadius across all structTypes.
    private static int MaxDecoScanRadius => Db.maxDecoScanRadius;

    private void ScanForNearbyDecorations() {
        int ax = Mathf.RoundToInt(x);
        int ay = Mathf.RoundToInt(y);
        for (int dx = -MaxDecoScanRadius; dx <= MaxDecoScanRadius; dx++) {
            for (int dy = -MaxDecoScanRadius; dy <= MaxDecoScanRadius; dy++) {
                Tile t = world.GetTileAt(ax + dx, ay + dy);
                if (t == null) continue;
                foreach (Structure s in t.structs) {
                    if (s == null) continue;
                    if (!(s is Building b) || !b.structType.isDecoration) continue;
                    int dist = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
                    if (dist > b.structType.decorRadius) continue;
                    if (b.IsBroken) continue;
                    if (b.reservoir != null && !b.reservoir.HasFuel()) continue;
                    if (string.IsNullOrEmpty(b.structType.decorationNeed)) {
                        Debug.LogError($"Decoration building '{b.structType.name}' has no decorationNeed set");
                        continue;
                    }
                    happiness.NoteSawDecoration(b.structType.decorationNeed);
                }
            }
        }
    }

    public void Update() { // called all the time, for movement and detecting arrival
        stateManager.UpdateMovement(Time.deltaTime);
    }


    // ── Urgency-based task selection (see plans/urgency-system.md) ───────────────────
    // Every category the idle mouse could act on yields a 0..1 urgency; the mouse attempts
    // categories in descending-urgency order and takes the first that starts a task. This
    // replaces the old hard ladder + random leisure/idle dice with a smooth comparison, so
    // needs scale (a slightly-hungry mouse finishes nearby work; a starving one drops everything
    // to eat) and urgent work can beat evening leisure. Each category delegates to its EXISTING
    // helper (FindFood / ChooseCraftTask / TryPickLeisure / …) — urgency only orders the attempts,
    // it does not flatten those subsystems' internal scoring.
    //
    // Dropping carried main-inv items (policy: idle mice keep main inv empty, food/tools in equip
    // slots) is itself a scored category (DropUrgency), not a hard pre-step — so a starving or
    // exhausted mouse eats / sleeps before crawling off to offload. It still carries its own cooldown
    // fallback (DropObjective sets it on a boxed-in give-up) so it doesn't loop forever.
    public void ChooseTask() {
        if (task != null){ return; }

        // Score every category, jittering each score (see Jitter). Helpers that no-op when nothing
        // applies (full slot, no orders) get urgency 0 so they're never attempted. Idle is the floor.
        // Craft and leisure are gathered once here and threaded into both their urgency and their
        // pick, so the (non-trivial) scan isn't run twice per idle tick.
        var candidates = new List<(float urgency, System.Func<bool> tryStart)>();

        candidates.Add((Jitter(eating.HungerUrgency()), FindFood));
        candidates.Add((Jitter(eeping.SleepUrgency(BedtimeUrgency())), TryStartSleep));
        // Equip/clothing: fixed pull, only when the slot is actually empty (else the helper would
        // rank then no-op — see validation note in the plan).
        float equipU = (job.usesTools && toolSlotInv.itemStacks[0].item == null) ? UrgencyConfig.EquipUrgency : 0f;
        float clothingU = clothingSlotInv.itemStacks[0].item == null ? UrgencyConfig.EquipUrgency : 0f;
        candidates.Add((Jitter(equipU), FindEquipment));
        candidates.Add((Jitter(clothingU), FindClothing));
        candidates.Add((Jitter(DropUrgency()), TryStartDrop));

        var wom = WorkOrderManager.instance;
        if (wom != null) {
            wom.PruneStale();
            candidates.Add((Jitter(wom.BestWorkUrgency(this)), TryStartWorkOrder));
            var craft = ScoreCraftRecipes();
            candidates.Add((Jitter(CraftUrgency(craft)), () => ChooseCraftTask(craft)));
        }
        var leisure = GatherLeisureCandidates();
        candidates.Add((Jitter(LeisureUrgency(leisure)), () => TryPickLeisure(leisure)));
        candidates.Add((Jitter(IdleUrgency()), () => { task = null; return true; })); // idle floor — always "succeeds"

        // Highest urgency first; attempt each until one starts a task.
        candidates.Sort((a, b) => b.urgency.CompareTo(a.urgency));
        foreach (var c in candidates) {
            if (c.urgency <= 0f) continue;
            if (c.tryStart()) return;
        }
        task = null;
    }

    // Adds two-directional Gaussian noise to a category score, scaled by headroom:
    // s + (1-s) * N(0, JitterStdev). The (1-s) factor means urgent scores (s→1) barely move — so when
    // something is pressing the pick stays deterministic — while low scores get real variety, so a
    // chill mouse picks among comparable options differently each time. The normal tail occasionally
    // produces a large nudge, so a mouse rarely does something well off the obvious choice. A nudge
    // can push a low score below 0; ChooseTask then skips it (harmless — that category just sits out
    // this tick). Uses `random` (seeded, saved) for reproducibility. A 0 score stays 0 so a
    // genuinely-unavailable category gets no spurious pull.
    private float Jitter(float s) {
        if (s <= 0f) return 0f;
        return s + (1f - s) * UrgencyConfig.JitterStdev * (float)NextGaussian();
    }

    // Standard normal sample N(0,1) via Box-Muller, drawing from the seeded `random` so the result
    // is reproducible across save/load. Discards the second Box-Muller output rather than caching it,
    // to avoid carrying a spare-sample field that would need to be serialized.
    private double NextGaussian() {
        double u1 = 1.0 - random.NextDouble(); // shift to (0,1] so Log is never -infinity
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // Tries the work-order tiers in the same internal order as before (p1 → p2 → p3-excl-craft → p4).
    // Craft is handled by its own category (ChooseCraftTask) so recipe economics, not proximity,
    // drives station choice. Returns true if a task was started.
    private bool TryStartWorkOrder() {
        var wom = WorkOrderManager.instance;
        if (wom == null) return false;
        task = wom.ChooseOrder(this, 1); if (task != null) return true;
        task = wom.ChooseOrder(this, 2); if (task != null) return true;
        task = wom.ChooseOrder(this, 3, exclude: WorkOrderManager.OrderType.Craft); if (task != null) return true;
        task = wom.ChooseOrder(this, 4); if (task != null) return true;
        return false;
    }

    private bool TryStartSleep() {
        task = new EepTask(this);
        return task.Start();
    }

    // 0..1 urgency to dump stale main-inventory carry-over (idle mice keep main inv empty;
    // food/tools live in equip slots). Scales with how many of the main inv's stacks are
    // occupied — a single stack pulls at DropFloor, a full pack at DropCeil — so the more a
    // mouse is carrying, the harder it wants to offload. The band sits below the hunger/sleep
    // peaks so a starving/exhausted mouse acts on those first (fixing the "crawl off to drop
    // while starving" behaviour), and above the work tiers so a laden idle mouse still drops
    // promptly. Returns 0 when the inv is empty or the boxed-in cooldown hasn't elapsed, so the
    // category sits out of the ranking on those ticks.
    private float DropUrgency() {
        if (inv.IsEmpty() || World.instance.timer < dropCooldownUntil) return 0f;
        int occupied = 0;
        foreach (ItemStack s in inv.itemStacks)
            if (s != null && s.quantity > 0) occupied++;
        float fullness = (float)occupied / inv.itemStacks.Length;
        return UrgencyConfig.DropFloor
             + (UrgencyConfig.DropCeil - UrgencyConfig.DropFloor) * fullness;
    }

    private bool TryStartDrop() {
        task = new DropTask(this);
        if (task.Start()) return true;
        task = null;
        return false;
    }

    // Scores all of this animal's recipes globally, then finds the nearest building for the
    // top-scoring recipe. Falls through to lower-scoring recipes when no building is available.
    // This is recipe-first selection: economic score drives which building type to visit,
    // rather than proximity driving which recipes are considered.
    private bool ChooseCraftTask(List<(Recipe recipe, float score)> scored) {
        var wom = WorkOrderManager.instance;
        if (wom == null) return false;

        foreach (var (recipe, _) in scored) {
            var found = wom.FindCraftOrder(recipe.tile, this);
            if (found == null) continue;
            var (order, building) = found.Value;
            var craftTask = new CraftTask(this, building, recipe);
            if (craftTask.Start()) {
                order.res.Reserve();
                craftTask.workOrder = order;
                task = craftTask;
                return true;
            }
        }
        return false;
    }

    // Eligible recipes for this animal's job, scored by economic need and sorted best-first.
    // Shared by ChooseCraftTask (which station to visit) and CraftUrgency (how badly to craft).
    private List<(Recipe recipe, float score)> ScoreCraftRecipes() {
        var targets = InventoryController.instance?.targets;
        var scored = new List<(Recipe recipe, float score)>();
        foreach (var r in job.recipes) {
            if (r == null) continue;
            if (!r.IsEligibleForPicking()) continue;
            if (!ginv.SufficientResources(r.inputs)) continue;
            if (r.AllOutputsSatisfied(targets)) continue;
            scored.Add((r, r.Score(targets)));
        }
        scored.Sort((a, b) => b.score.CompareTo(a.score)); // highest score first
        return scored;
    }

    // 0..1 urgency for the craft category. Recipe.Score is unbounded (0..+∞), so it can't be
    // compared directly to the tier-based work urgencies — map it into the fixed [CraftFloor,
    // CraftCeil] band via s/(1+s) (monotonic, lands in (0,1)). The floor sits just above the
    // daytime idle floor so any eligible recipe (s>0) is never soft-locked out; the ceil is the
    // asymptote a scarce-output recipe approaches.
    private float CraftUrgency(List<(Recipe recipe, float score)> scored) {
        if (scored.Count == 0) return 0f;
        float s = scored[0].score; // best-first
        if (s <= 0f) return 0f;
        // A never-yet-produced output makes Recipe.Score +Infinity (the recipe is "infinitely"
        // needed). s/(1+s) would then be ∞/∞ = NaN, which sinks craft below the idle floor in
        // ChooseTask's sort and the mouse never crafts. Saturate to the ceil instead (s/(1+s) → 1).
        if (float.IsInfinity(s)) return UrgencyConfig.CraftCeil;
        return UrgencyConfig.CraftFloor
             + (UrgencyConfig.CraftCeil - UrgencyConfig.CraftFloor) * (s / (1f + s));
    }

    // Picks up one tool into toolSlotInv if the slot is empty.
    private bool FindEquipment() {
        if (!job.usesTools) return false;                          // job gains no tool benefit; don't seek one
        if (toolSlotInv.itemStacks[0].item != null) return false; // already holding a tool
        foreach (Item equipment in Db.equipmentItems) {
            task = new ObtainTask(this, equipment, 100, toolSlotInv);
            if (task.Start()) return true;
        }
        return false;
    }

    // Picks up one clothing item into clothingSlotInv if the slot is empty.
    private bool FindClothing() {
        if (clothingSlotInv.itemStacks[0].item != null) return false; // already wearing clothing
        foreach (Item clothing in Db.clothingItems) {
            task = new ObtainTask(this, clothing, 100, clothingSlotInv);
            if (task.Start()) return true;
        }
        return false;
    }

    // Scores each reachable edible by foodValue * cravingMult * discount, where
    // discount = 1 / (1 + pathCost * urgency) and urgency = (1 - fullness) / starvingHalfDistance.
    // Distance therefore bites harder the hungrier the mouse is — a starving mouse with wheat at
    // her feet won't trudge across the map for a craved tofu meal, but a topped-up mouse will.
    // Food goes directly into foodSlotInv (equip slot), not the main inventory.
    private bool FindFood() {
        Item slotItem = foodSlotInv.itemStacks[0].item; // null if empty
        int room = slotItem != null
            ? foodSlotInv.GetStorageForItem(slotItem)
            : foodSlotInv.stackSize;
        if (room <= 0) return false; // slot already full

        int amountToPickUp = Math.Min(room, 100);
        float urgency = (1f - eating.Fullness()) / Eating.starvingHalfDistance;

        // Peek nearest reachable source per food (no reservation — FindPathItemStack is read-only),
        // score it, then try ObtainTask in descending score order so a stolen stack falls through to
        // the next-best candidate rather than aborting the whole pick.
        //
        // Two tiers: a seed item we're nearly out of (< seedReserveFen in the world) is held back
        // into a fallback list so mice don't eat the colony's last planting stock when other food
        // exists. The fallback is still tried before giving up, so a mouse with nothing else
        // reachable eats it rather than starves — the reserve is a soft preference, not a lock.
        var candidates = new List<(float score, Item food)>();
        var scarceSeeds = new List<(float score, Item food)>();
        foreach (Item food in Db.edibleItems) {
            if (slotItem != null && slotItem != food) continue; // slot has a different food
            var (path, _) = nav.FindPathItemStack(food);
            if (path == null) continue;
            float cravingMult = happiness.WouldHelp(food) ? Eating.cravingMultiplier : 1f;
            float discount = 1f / (1f + path.cost * urgency);
            var entry = (food.foodValue * cravingMult * discount, food);

            bool scarceSeed = Db.seedItems.Contains(food)
                && GlobalInventory.instance.Quantity(food) < seedReserveFen;
            (scarceSeed ? scarceSeeds : candidates).Add(entry);
        }

        candidates.Sort((a, b) => b.score.CompareTo(a.score));
        scarceSeeds.Sort((a, b) => b.score.CompareTo(a.score));
        foreach (var (_, food) in candidates.Concat(scarceSeeds)) { // normal food first, scarce seeds as fallback
            task = new ObtainTask(this, food, amountToPickUp, foodSlotInv);
            if (task.Start()) return true;
        }
        return false;
    }

    // Below this world-total (fen) a seed item is treated as scarce and eaten only as a last
    // resort — 3 whole units (300 fen) of replanting stock kept in reserve. See FindFood.
    private const int seedReserveFen = 300;


    // Picks the best available leisure activity by targeting the lowest happiness satisfaction.
    // Sorts the gathered options by satisfaction and tries the least-satisfied need first.
    private bool TryPickLeisure(List<(float sat, System.Func<bool> tryStart)> candidates) {
        if (candidates.Count == 0) return false;

        // Sort by satisfaction ascending — try the least-satisfied need first
        candidates.Sort((a, b) => a.sat.CompareTo(b.sat));
        foreach (var c in candidates) {
            if (c.tryStart()) return true;
        }
        return false;
    }

    // Gathers the currently-available leisure options as (satisfaction, tryStart) pairs.
    // Shared by TryPickLeisure (which attempts them least-satisfied first) and LeisureUrgency
    // (which reads the lowest satisfaction to size the category's pull). Each option's actual
    // building/seat selection lives in its task's Initialize — these are just the eligible needs.
    private List<(float sat, System.Func<bool> tryStart)> GatherLeisureCandidates() {
        var candidates = new List<(float sat, System.Func<bool> tryStart)>();

        // Chat option (social need) — only seek chat when social is low
        float socialSat = happiness.GetSatisfaction("social");
        if (socialSat < 2.0f && AnimalController.instance.FindIdleAnimalNear(this, 6) != null)
            candidates.Add((socialSat, FindChatPartner));

        // Reading option (reading need) — eligible whenever a fiction book exists somewhere in the
        // world. Reachability is verified properly inside ReadBookTask.Initialize; this cheap
        // global-quantity check just avoids enqueuing a candidate when there's literally no book.
        if (Db.itemByName.TryGetValue("fiction_book", out Item fiction)
            && GlobalInventory.instance.Quantity(fiction) > 0) {
            float readingSat = happiness.GetLeisureSatisfaction("reading");
            candidates.Add((readingSat, TryStartReading));
        }

        // Drinking option ("alcohol" need) — eligible whenever rice wine exists anywhere in
        // the world. DrinkTask.Initialize verifies reachability; this cheap global-quantity
        // check just avoids enqueuing a candidate when there's no wine at all.
        if (Db.itemByName.TryGetValue("rice wine", out Item wine)
            && GlobalInventory.instance.Quantity(wine) > 0) {
            float alcoholSat = happiness.GetLeisureSatisfaction("alcohol");
            candidates.Add((alcoholSat, TryStartDrinking));
        }

        // Building options: one candidate per unique leisureNeed. Actual building selection
        // (filter suitability, pathfind, pick nearest-by-path, reserve seat) lives in
        // LeisureTask.Initialize — so if the candidate gets tried, it commits to the best
        // reachable building rather than a crow-flies pre-pick that may not be pathable.
        var sc = StructController.instance;
        if (sc != null) {
            var needsSeen = new HashSet<string>();
            foreach (Building b in sc.GetLeisureBuildings()) {
                string need = b.structType.leisureNeed;
                if (string.IsNullOrEmpty(need)) {
                    Debug.LogError($"Leisure building '{b.structType.name}' has no leisureNeed set");
                    continue;
                }
                if (!needsSeen.Add(need)) continue;
                float sat = happiness.GetLeisureSatisfaction(need);
                string captured = need; // don't capture loop var
                candidates.Add((sat, () => TryStartLeisureFor(captured)));
            }
        }
        return candidates;
    }

    // 0..1 urgency for the leisure category. Time-of-day is the dial (evening makes leisure
    // competitive with work; daytime keeps a low-but-present pull so mice still occasionally take a
    // break); the least-satisfied available need sets how strong the pull is within that dial.
    // Returns 0 when no leisure option is available, so the category is simply skipped.
    private float LeisureUrgency(List<(float sat, System.Func<bool> tryStart)> candidates) {
        if (candidates.Count == 0) return 0f;
        float lowestSat = float.MaxValue;
        foreach (var c in candidates) if (c.sat < lowestSat) lowestSat = c.sat;
        float needPull = Mathf.Clamp01((Happiness.wantThreshold - lowestSat) / Happiness.wantThreshold);
        float bias = IsLeisureTime() ? UrgencyConfig.LeisureBiasEvening : UrgencyConfig.LeisureBiasDay;
        return bias * needPull;
    }

    // Always-available baseline "take a break" urgency — the floor that low-value work and leisure
    // must clear to be worth doing. When nothing is pressing, a mouse sometimes idles/chats rather
    // than always grabbing the nearest low-value haul; a real need or nearby work out-scores it.
    // Evening idles more than daytime. Randomness comes from the general Jitter applied in ChooseTask.
    private float IdleUrgency() {
        return IsLeisureTime() ? UrgencyConfig.IdleBaseEvening : UrgencyConfig.IdleBaseDay;
    }

    private bool FindChatPartner() {
        Animal partner = AnimalController.instance.FindIdleAnimalNear(this, 6);
        if (partner == null) return false;
        task = new ChatTask(this, partner);
        if (task.Start()) return true;
        task = null;
        return false;
    }

    private bool TryStartLeisureFor(string leisureNeed) {
        task = new LeisureTask(this, leisureNeed);
        if (task.Start()) return true;
        task = null;
        return false;
    }

    private bool TryStartReading() {
        task = new ReadBookTask(this);
        if (task.Start()) return true;
        task = null;
        return false;
    }

    // Drinking is consume-in-place leisure: DrinkTask finds rice wine wherever it's
    // stored, walks there, and drinks 1 liang. Not tied to any building.
    private bool TryStartDrinking() {
        task = new DrinkTask(this);
        if (task.Start()) return true;
        task = null;
        return false;
    }

    // this uses the task/objective system!
    public void OnArrival(){
        task?.OnArrival();
    }

    public void Refresh(){ // end task, go idle, AND drop items
        // Debug.Log(aName + " refreshed! interrupting current task");
        pendingRefresh = false; // clear in case this was deferred
        task?.Fail();
        task = new DropTask(this);
        if (!task.Start()){
            task = null;
            state = AnimalState.Idle;
        }
    }

    // ── Item movement (maybe these should be moved into Inventory class??) ───
    // Moves item from equip slot back to main inventory. Leftover stays in slot if inv is full.
    public void Unequip(Inventory slotInv) {
        Item item = slotInv.itemStacks[0].item;
        if (item == null) return;
        slotInv.MoveItemTo(inv, item, slotInv.Quantity(item));
    }
    // moves item to tile here. returns amount *not* dropped
    public int DropItem(Item item, int quantity = -1){ 
        if (quantity == -1) { quantity = inv.Quantity(item); }
        return (inv.MoveItemTo(TileHere().EnsureFloorInventory(), item, quantity));
        // maybe need failsafe?
    }
    public int DropItem(ItemQuantity iq) { return(DropItem(iq.item, iq.quantity)); }

    // Spills everything the mouse is carrying — main inventory and all four equip
    // slots — onto the floor when it dies, so the player can recover the food and
    // tools. Each item type routes through FindPathToDrop so it lands on a tile
    // with space (mirrors Produce's overflow handling); MoveItemTo keeps the
    // GlobalInventory totals correct. Called by AnimalController before Destroy().
    public void DropInventoryToFloor() {
        Inventory[] sources = { inv, foodSlotInv, toolSlotInv, clothingSlotInv, bookSlotInv };
        foreach (Inventory src in sources) {
            if (src == null) continue;
            foreach (ItemStack stack in src.itemStacks) {
                Item item = stack.item;
                int quantity = stack.quantity;
                if (item == null || quantity == 0) continue;
                Path dropPath = nav.FindPathToDrop(item, quantity);
                Tile dropTile = dropPath?.tile ?? TileHere();
                if (dropTile == null) {
                    Debug.LogError($"{aName} died but found nowhere to drop {item.name} — {quantity} fen lost");
                    continue;
                }
                int leftover = src.MoveItemTo(dropTile.EnsureFloorInventory(), item, quantity);
                if (leftover > 0)
                    Debug.Log($"{aName}'s {item.name} ({leftover} fen) had no floor space on death — lost");
            }
        }
    }

    // produces item in ani inv, dumps at nearby tile if inv full
    public void Produce(Item item, int quantity = 1){
        if (quantity < 0){
            Debug.LogError("called produce with negative quantity, use consume instead");
            Consume(item, -quantity); return;
        }
        int leftover = inv.Produce(item, quantity); 
        if (leftover > 0){
            Debug.LogError(aName + " produced without space in inventory");
            Path dropPath = nav.FindPathToDrop(item, leftover);
            if (dropPath == null){
                Debug.Log("no place to drop " + item.name + "!! excess item disappearing.");
                return;
            }
            Tile dTile = dropPath.tile;
            if (dTile.inv == null){
                dTile.inv = new Inventory(x: dTile.x, y: dTile.y);
            }
            dTile.inv.Produce(item, leftover);
        }
    }
    public void Produce(string itemName, int quantity = 1) { Produce(Db.itemByName[itemName], quantity); }
    public void Produce(ItemQuantity iq) { Produce(iq.item, iq.quantity); }
    public void Produce(ItemQuantity[] iqs) { Array.ForEach(iqs, iq => Produce(iq)); }
    public void Produce(Recipe recipe) { // checks inv for recipe inputs
        if (recipe != null && inv.ContainsItems(recipe.inputs)){
            foreach (ItemQuantity iq in recipe.inputs) { inv.Produce(iq.item, -iq.quantity); }
            Produce(recipe.outputs);
        }
        else { Debug.Log($"{aName} ({job.name}) called produce without ingredients for recipe at ({(int)x},{(int)y})"); }
    }
    // `workBuilding` is the building the active task says we're working at — the source
    // of truth, not whatever `TileHere().building` happens to be. Matters when the
    // workNode is an off-grid waypoint that drops the worker into a different tile
    // (digging pit's elevated/sinking stand spot, wheel's centred runner pose).
    public bool CanProduce(Recipe recipe, Building workBuilding) {
        return workBuilding != null && inv.ContainsItems(recipe.inputs) && recipe.tile == workBuilding.structType.name;
    }
    public void Consume(Item item, int quantity = 1){
        // Group items (e.g. "planks") can't exist in inventories — resolve to the leaf actually held.
        if (item.children != null) {
            ItemStack stack = inv.GetItemStack(item);
            if (stack == null) {
                Debug.LogError($"tried consuming {item.name} but no matching leaf found in inventory!");
                return;
            }
            item = stack.item;
        }
        if (inv.Produce(item, -quantity) < 0){
            Debug.LogError("tried consuming more than you have!");
        }
    }


    // ── Thinking ─────────────────────────────────────────────────────────────
    public Recipe PickRecipeRandom(){ // randomized selection
        if (job.recipes.Length == 0) { return null; }
        List<Recipe> eligibleRecipes = new List<Recipe>();
        foreach (Recipe recipe in job.recipes){
            if (recipe == null) continue;
            if (!recipe.IsEligibleForPicking()) continue;
            if (ginv.SufficientResources(recipe.inputs)){
                if (!Db.structTypeByName.ContainsKey(recipe.tile) ||
                    !nav.CanReachBuilding(Db.structTypeByName[recipe.tile])) continue;
                eligibleRecipes.Add(recipe);
            }
        }
        if (eligibleRecipes.Count == 0) { return null; }
        int index = random.Next(0, eligibleRecipes.Count);
        return eligibleRecipes[index];
    }
    // True if every class-restricted output (Liquid, Book) has at least one matching storage
    // inventory with space. Default-class outputs are exempt — they can land on the floor and be
    // hauled later. Used to skip recipes whose output literally has nowhere to go (e.g. a scribe
    // book recipe when every bookshelf is full), preventing items from piling up on floors.
    private static bool OutputsHaveClassStorage(Recipe recipe) {
        if (recipe.outputs == null) return true;
        var ic = InventoryController.instance;
        if (ic == null) return true;
        foreach (var iq in recipe.outputs) {
            if (iq.item.itemClass == ItemClass.Default) continue;
            if (!ic.byType.TryGetValue(Inventory.InvType.Storage, out var list)) return false;
            bool found = false;
            foreach (Inventory inv in list) {
                if (inv.GetStorageForItem(iq.item) > 0) { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }

    public Recipe PickRecipe(){ // score based selection
        if (job.recipes.Length == 0) { return null; }
        var targets = InventoryController.instance.targets;
        float maxScore = 0;
        Recipe bestRecipe = null;
        // Reservoir sampling for k=1 across recipes tied at maxScore — see PickRecipeForBuilding
        // for the rationale (avoids deterministic id-order bias when multiple recipes tie).
        int tiedCount = 0;
        foreach (Recipe recipe in job.recipes){
            if (recipe == null) continue;
            if (!recipe.IsEligibleForPicking()) continue;
            if (!OutputsHaveClassStorage(recipe)) continue;
            if (ginv.SufficientResources(recipe.inputs)){
                if (!Db.structTypeByName.ContainsKey(recipe.tile) ||
                    !nav.CanReachBuilding(Db.structTypeByName[recipe.tile])) continue;
                float score = recipe.Score(targets);
                if (score > maxScore){
                    maxScore = score;
                    bestRecipe = recipe;
                    tiedCount = 1;
                } else if (score == maxScore) {
                    tiedCount++;
                    if (random.NextDouble() < 1.0 / tiedCount) bestRecipe = recipe;
                }
            }
        }
        return bestRecipe;
    }

    // Like PickRecipe but scoped to a specific building instance assigned by the WorkOrderManager.
    // Only considers recipes for that building type; skips the CanReachBuilding check since
    // the building reference is already known to the caller.
    public Recipe PickRecipeForBuilding(Building building){
        var targets = InventoryController.instance.targets;
        float maxScore = 0;
        Recipe bestRecipe = null;
        // Reservoir sampling for k=1 among recipes tied at maxScore: each tied recipe is equally
        // likely to be picked, instead of deterministic iteration (recipe id) order winning.
        // Especially visible for newly-unlocked book recipes — all start with output qty 0, so
        // their scores all blow up to +Infinity (Score divides by qty/target = 0). Without this
        // tiebreak, scribes would always write the lowest-id book first.
        int tiedCount = 0;
        foreach (Recipe recipe in job.recipes){
            if (recipe == null) continue;
            if (recipe.tile != building.structType.name) continue;
            if (!recipe.IsEligibleForPicking()) continue;
            if (!ginv.SufficientResources(recipe.inputs)) continue;
            if (recipe.AllOutputsSatisfied(targets)) continue;
            if (!OutputsHaveClassStorage(recipe)) continue;
            float score = recipe.Score(targets);
            if (score > maxScore){
                maxScore = score;
                bestRecipe = recipe;
                tiedCount = 1;
            } else if (score == maxScore) {
                tiedCount++;
                if (random.NextDouble() < 1.0 / tiedCount) bestRecipe = recipe;
            }
        }
        return bestRecipe;
    }

    // Single source of truth for the per-task work-stint cap: an animal works at most this many
    // ticks (1 tick ~ 1s) on one task before it completes and re-evaluates — eat, sleep, switch to
    // a closer/better task. Craft derives its batch size from this budget (below); construction
    // counts ticks against it directly (see AnimalStateManager.HandleWorking). Harvest, research,
    // and maintenance already finish a stint well under this.
    public const int MaxWorkStintTicks = 25;

    public int CalculateWorkPossible(Recipe recipe){
        // looks at inputs in gInv, and first available storage you can find in animal range, at least 1.
        // the storage thing makes it a bit conservative.

        // Cap rounds by estimated time rather than a fixed count, so slow recipes
        // don't lock the mouse for ages. We estimate seconds-per-round from the
        // recipe workload and the animal's current work efficiency (ticks are 1 s).
        Skill? skill = null;
        if (recipe.skill != null && System.Enum.TryParse<Skill>(recipe.skill, ignoreCase: true, out Skill s))
            skill = s;
        float workEff = ModifierSystem.GetWorkMultiplier(this, skill);
        int numRounds = workEff > 0f
            ? Math.Max((int)(MaxWorkStintTicks * workEff / recipe.workload), 1)
            : 1;
        int n;
        foreach (ItemQuantity input in recipe.inputs){
            n = InventoryController.instance.TotalAvailableQuantity(input.item) / input.quantity;
            if (n < numRounds) { numRounds = n; }
        }
        // Target cap: never overshoot the player's per-output target. See Recipe.CapRoundsByTarget.
        var targets = InventoryController.instance.targets;
        numRounds = recipe.CapRoundsByTarget(numRounds, targets);
        // Storage cap — skipped entirely when every output is scarce enough to
        // justify batch-crafting onto the workshop floor. Time + input + target
        // caps above still apply either way. See Recipe.AllOutputsScarce.
        if (!recipe.AllOutputsScarce(targets)){
            foreach (ItemQuantity output in recipe.outputs){
                var (storePath, storeInv) = nav.FindPathToStorage(output.item);
                if (storePath == null) { n = 0; }
                else{
                    n = storeInv.GetStorageForItem(output.item) / output.quantity;
                }
                if (n < numRounds) { numRounds = Math.Max(n, 1); }
            }
        }
        return numRounds;
    }


    // ── Utils ────────────────────────────────────────────────────────────────
    public void SetJob(Job newJob){
        Job oldJob = this.job;
        this.job = newJob;
        if (cbAnimalChanged != null){
            cbAnimalChanged(this, oldJob);
        }
        // Only interrupt the current task if it's actually job-tied. Sleeping, eating,
        // socializing, leisure etc. should carry on across a job change. Task.IsWork
        // marks the personal-needs tasks; everything else (haul, craft, harvest, …)
        // defaults to work and gets refreshed.
        if (task != null && !task.IsWork) return;

        // Defer Refresh until solid ground if we're mid-waypoint traversal.
        // preventFall==true means the animal is between waypoint nodes (cliff/stair);
        // calling Refresh now would leave them frozen on an unstandable tile.
        if (nav != null && nav.preventFall){
            pendingRefresh = true;
        } else {
            Refresh();
        }
    }
    public void SetJob(string jobStr) { SetJob(Db.GetJobByName(jobStr)); }


    public void FindHome(){
        if (nav == null) return;

        // Need a new home if (a) we don't have one, (b) the assignment was somehow lost
        // its housing flag (StructType edited at runtime — unlikely but cheap to check),
        // or (c) the building is broken and unusable.
        bool needsHome = homeBuilding == null
                      || !homeBuilding.structType.isHousing
                      || homeBuilding.IsBroken;
        if (needsHome) {
            Building best = FindReachableHousing();
            if (best != null) {
                if (homeBuilding != null) homeBuilding.res.Unreserve();
                AssignHome(best);
                happiness?.RecomputeFurnishingBonus(this);
            } else if (homeBuilding != null && happiness != null) {
                // Home was lost (demolished / broken) but no replacement found — clear any
                // stale furnishing bonus that was pinned to the old building.
                happiness.RecomputeFurnishingBonus(this);
            }
        } else if (!homeBuilding.res.Available()) {
            // Current home is full — look for a different housing building with ≥ 2 free slots
            // (a one-slot gap isn't enough to be worth a move).
            Building currentHome = homeBuilding;
            Building better = FindReachableHousing(b =>
                b != currentHome && b.res.capacity - b.res.reserved >= 2);
            if (better != null) {
                homeBuilding.res.Unreserve();
                AssignHome(better);
                happiness?.RecomputeFurnishingBonus(this);
            }
        }
    }

    // Commits a new home assignment: sets homeBuilding + homeTile (the path target —
    // either the door's approach tile or the building's anchor for legacy housing) and
    // reserves a slot on the building's Reservable.
    void AssignHome(Building b) {
        homeBuilding = b;
        homeTile = b.doorApproachTile ?? b.tile;
        b.res.Reserve(aName);
    }

    // Scans all reachable housing buildings (any StructType with isHousing == true) for the
    // cheapest-cost path, optionally restricted by an extra filter (e.g. "must have ≥ 2 free
    // slots" when migrating to a less-cramped house). Iterates StructController rather than
    // routing through Nav.FindPathToStruct because housing isn't keyed by a single StructType.
    private Building FindReachableHousing(System.Func<Building, bool> extraFilter = null) {
        Node myNode = PathStartNode();
        if (myNode == null) return null;
        Building best = null;
        float bestCost = float.MaxValue;
        foreach (Structure s in StructController.instance.GetStructures()) {
            Building b = s as Building;
            if (b == null) continue;
            if (!b.structType.isHousing) continue;
            if (b.IsBroken) continue;
            if (!b.res.Available()) continue;
            if (extraFilter != null && !extraFilter(b)) continue;
            // Doored buildings route through the first interior waypoint (A* picks up the
            // door edge automatically); legacy housing (no interior tiles declared) uses
            // workNode = workTile.node = anchor tile.
            Node target = (b.interiorNodes != null && b.interiorNodes.Length > 0)
                ? b.interiorNodes[0]
                : b.workNode;
            if (target == null) continue;
            Path p = world.graph.Navigate(myNode, target);
            if (p == null) continue;
            if (p.cost < bestCost) { best = b; bestCost = p.cost; }
        }
        return best;
    }

    public Tile TileHere() { return world.GetTileAt(x, y); }

    // Pathing start node — the Node any FindPath / Navigate call should treat as the
    // animal's "current" graph position. For mice outside a building, that's just the
    // tile node under their feet. For mice inside a doored building (insideBuilding
    // != null), the tile node may be solid dirt / non-standable (burrow's preserved
    // dirt) and have no graph edges — using it as a start would orphan every path
    // request. Return the nearest interior waypoint instead; it's edged to the door
    // approach so A* can route out via the door automatically. Falls back to TileHere
    // if interior data is missing (corrupted state).
    public Node PathStartNode() {
        Building ib = insideBuilding; // property does a tile lookup — read once
        if (ib != null && ib.interiorNodes != null && ib.interiorNodes.Length > 0) {
            Node best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < ib.interiorNodes.Length; i++) {
                Node n = ib.interiorNodes[i];
                if (n == null) continue;
                float d = (n.wx - x) * (n.wx - x) + (n.wy - y) * (n.wy - y);
                if (d < bestDist) { bestDist = d; best = n; }
            }
            if (best != null) return best;
        }
        return TileHere()?.node;
    }

    // Single point of truth for "set the animal's position." Mirrors (x, y) into the
    // GameObject transform so the model and view stay in sync — every other site that
    // mutates (x, y) (per-frame lerp, fall integration, elevator boarding/unloading,
    // arrival snap) routes through here. Assumes go is set; Start() guarantees that.
    public void SnapTo(float newX, float newY) {
        x = newX;
        y = newY;
        go.transform.position = new Vector3(x, y, z);
    }

    // Call after any position change to keep tile-occupancy tracking up to date.
    public void UpdateCurrentTile() {
        Tile newTile = TileHere();
        if (newTile != _currentTile) {
            AnimalController.instance.UnregisterAnimalFromTile(_currentTile);
            AnimalController.instance.RegisterAnimalOnTile(newTile);
            _currentTile = newTile;
            RefreshInteriorRendering();
        }
    }

    // Tracks whether this mouse's sprites are currently on the Interior (directional-only)
    // layer. A mouse standing inside an enclosed building (burrow) renders sun + ambient
    // only, so torchlight from above doesn't bleed onto it underground. Driven by the
    // existing insideBuilding property — flips only on the tile-change boundary.
    private bool _interiorRendered;
    private void RefreshInteriorRendering() {
        Building ib = insideBuilding;   // property: tile lookup, never stale
        bool wantInterior = ib != null && ib.structType.enclosed;
        if (wantInterior == _interiorRendered) return;
        _interiorRendered = wantInterior;
        InteriorLayer.SetSpriteLayers(go, wantInterior ? InteriorLayer.Interior : InteriorLayer.Default);
    }

    // True when the animal is physically inside its assigned home. For legacy 1×1 housing
    // this fires whenever they're standing on the home tile (no separate "inside" concept).
    // For doored buildings this returns true only once the mouse is actually on an interior
    // tile (insideBuilding == homeBuilding); standing on the approach tile outside the door
    // doesn't count as "at home".
    public bool AtHome() {
        if (homeBuilding == null) return false;
        if (insideBuilding == homeBuilding) return true;
        // Legacy path: 1×1 housing without interior tiles — standing on the building's
        // anchor tile is "at home". Doored buildings always exercise the insideBuilding
        // check above (derived from the tile under the mouse).
        return (homeBuilding.interiorNodes == null || homeBuilding.interiorNodes.Length == 0)
            && TileHere() == homeBuilding.tile;
    }

    public bool HasHouse => homeBuilding?.structType.isHousing == true;

    public bool IsMoving(){
        return state == AnimalState.Moving || state == AnimalState.Falling;
    }

    public float SquareDistance(float x1, float x2, float y1, float y2) { return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2); }
    public void Destroy() {
        // Fail before tearing down inv — otherwise in-flight reservations strand on about-to-die stacks.
        task?.Fail();
        AnimalController.instance.UnregisterAnimalFromTile(_currentTile);
        _currentTile = null;
        // Release the home slot so another mouse can claim it.
        homeBuilding?.res.Unreserve();
        homeBuilding = null;
        // Destroy every owned inventory — main + the four equip slots. Without the
        // equip-slot teardown they leak into InventoryController's lists as dead
        // references: the ClearWorld path happens to catch them via its inventory
        // sweep, but a runtime death (starvation) does not.
        if (inv != null)             { inv.Destroy(reason: $"{aName} died"); inv = null; }
        if (foodSlotInv != null)     { foodSlotInv.Destroy(reason: $"{aName} died"); foodSlotInv = null; }
        if (toolSlotInv != null)     { toolSlotInv.Destroy(reason: $"{aName} died"); toolSlotInv = null; }
        if (clothingSlotInv != null) { clothingSlotInv.Destroy(reason: $"{aName} died"); clothingSlotInv = null; }
        if (bookSlotInv != null)     { bookSlotInv.Destroy(reason: $"{aName} died"); bookSlotInv = null; }
        GameObject.Destroy(gameObject);
    }

    public void RegisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged += callback; }
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged -= callback; }
}



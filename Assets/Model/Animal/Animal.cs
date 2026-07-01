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

    // Stuck (cut-off) tracking — transient, not persisted. stuckSince is the Time.time at
    // which this mouse was first found unable to reach the main settlement; -1 means "not
    // stuck". stuckAlerted prevents re-posting the player alert each tick. stuckSnapped marks
    // that this episode's one auto-rescue snap has been attempted — so a snap that fails to
    // reconnect the mouse escalates to an alert instead of looping silently. All reset when
    // the mouse is no longer cut off. Owned by AnimalStateManager.HandleStuckCheck.
    // See SPEC-systems §Stuck (cut-off) rescue.
    public float stuckSince = -1f;
    public bool stuckAlerted = false;
    public bool stuckSnapped = false;

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

    // Work flag this mouse is assigned to (Step 6), or null. When set it OVERRIDES home as the
    // work anchor, so the mouse gathers near the flag and works that area instead of around home.
    // Stored as a live Building ref; persisted via assignedFlagX/Y and re-resolved on load. The
    // flag keeps no roster — its assigned mice are found by scanning AnimalController (like home
    // residents), so a dead mouse needs no cleanup and a demolished flag clears this in Destroy.
    public Building assignedFlag;

    // The tile a mouse gravitates toward when idle, and the territory origin for its work search
    // (Step 5: work anchors). The assigned work flag wins; otherwise the mouse's home. Null for a
    // mouse with neither (no anchor pull — it just wanders locally and searches mouse-relative).
    public Tile WorkAnchorTile =>
        assignedFlag != null ? (assignedFlag.doorApproachTile ?? assignedFlag.tile) : homeTile;

    // Assigns/clears the mouse's work flag. Assigning a non-flag building is ignored (logged).
    public void AssignToFlag(Building flag) {
        if (flag != null && !flag.structType.isWorkFlag) {
            Debug.LogError($"AssignToFlag: {flag.structType.name} is not a work flag");
            return;
        }
        assignedFlag = flag;
    }
    public void UnassignFlag() { assignedFlag = null; }

    public Recipe recipe;
    public int numRounds = 0;
    public float workProgress = 0f;

    public Job job;
    // Main carry pack: general work carry-over + fetched craft inputs. Equip slots (food/tool/
    // clothing/hat/book) are separate. Size is single-sourced here so Db's carry-capacity
    // validation and CraftTask's carry cap agree with what's actually constructed.
    public const int MainInvStacks = 5;
    public const int MainInvStackSize = 1000; // fen per stack
    public Inventory inv;
    public Inventory foodSlotInv; // equip slot: food only, 1 stack, 5 liang capacity
    public Inventory toolSlotInv; // equip slot: tool, 1 stack
    public Inventory clothingSlotInv; // equip slot: clothing (top), 1 stack
    public Inventory hatSlotInv;      // equip slot: hat (head), 1 stack — profession identifier + small bonus
    public Inventory bookSlotInv;     // equip slot: book only (storageClass=Book), 1 stack of size 1
    public GlobalInventory ginv;

    public float energy;     // every time you get 1 energy you can do 1 work
    public float efficiency; // energy gain rate

    public SkillSet skills = new SkillSet();
    public BuffSet  buffs  = new BuffSet(); // timed tonic buffs (work speed, temp tolerance, sleep)
    public ActivityTracker activity = new ActivityTracker(); // recency-weighted time-per-state (population panel)

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
        this.inv = new Inventory(MainInvStacks, MainInvStackSize, Inventory.InvType.Animal);
        this.foodSlotInv = new Inventory(1, 500, Inventory.InvType.Equip);
        this.toolSlotInv = new Inventory(1, 200, Inventory.InvType.Equip);
        this.clothingSlotInv = new Inventory(1, 200, Inventory.InvType.Equip);
        this.hatSlotInv = new Inventory(1, 200, Inventory.InvType.Equip);
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
            SaveSystem.LoadInventory(hatSlotInv, pendingSaveData.hatSlotInv);
            SaveSystem.LoadInventory(bookSlotInv, pendingSaveData.bookSlotInv);
            skills.Deserialize(pendingSaveData.skillXp, pendingSaveData.skillLevel);
            activity.Deserialize(pendingSaveData.activity); // null on old saves → stays zeroed, re-warms
            buffs.Deserialize(pendingSaveData.buffs);
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
                List<ItemQuantity> travelItems = ReadTravelItems(pendingSaveData);
                switch (pendingSaveData.travelTaskType) {
                    case "HaulToMarket":
                        if (travelItems.Count > 0)
                            resumed = new HaulToMarketTask(this, travelItems, pendingSaveData.travelReturnLeg);
                        break;
                    case "HaulFromMarket":
                        List<Tile> storageTiles = ReadTravelStorageTiles(pendingSaveData, travelItems.Count);
                        if (travelItems.Count > 0 && storageTiles != null)
                            resumed = new HaulFromMarketTask(this, travelItems, storageTiles, pendingSaveData.travelReturnLeg);
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
            // Restore the assigned work flag (Step 6) — re-resolve the live Building from coords.
            if (pendingSaveData.assignedFlagX.HasValue && pendingSaveData.assignedFlagY.HasValue) {
                Tile flagTile = world.GetTileAt(pendingSaveData.assignedFlagX.Value, pendingSaveData.assignedFlagY.Value);
                if (flagTile?.building != null && flagTile.building.structType.isWorkFlag)
                    assignedFlag = flagTile.building;
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
        // Tint this mouse's fur from its (now-finalized) rngSeed — deterministic and stable
        // across save/load. Recolors only the gray fur shades; eyes and pink paws stay constant.
        animationController?.ApplyFurColor(Db.FurColorForSeed(rngSeed));
        // Add to the tickable animals array now that we're fully initialized.
        // This is deferred from AddAnimal() so TickUpdate/UpdateColonyStats never
        // iterate over an animal whose Start() hasn't run yet.
        AnimalController.instance.RegisterReady(this);
    }

    // Reads a mid-transit merchant's carried items from the save descriptor. Prefers the
    // multi-item arrays; falls back to the legacy single travelItemName/Qty fields for saves
    // written before multi-item hauling. Items whose name no longer resolves are skipped.
    static List<ItemQuantity> ReadTravelItems(AnimalSaveData sd) {
        var list = new List<ItemQuantity>();
        if (sd.travelItemNames != null && sd.travelItemQtys != null
            && sd.travelItemNames.Length == sd.travelItemQtys.Length) {
            for (int i = 0; i < sd.travelItemNames.Length; i++)
                if (Db.itemByName.TryGetValue(sd.travelItemNames[i], out Item it))
                    list.Add(new ItemQuantity(it, sd.travelItemQtys[i]));
        } else if (!string.IsNullOrEmpty(sd.travelItemName)
            && Db.itemByName.TryGetValue(sd.travelItemName, out Item single)) {
            list.Add(new ItemQuantity(single, sd.travelItemQty));
        }
        return list;
    }

    // Reads the per-item home storage tiles for a HaulFromMarket descriptor, parallel to
    // ReadTravelItems. Prefers the arrays; falls back to the legacy single tile when there's
    // exactly one item. A tile that no longer exists comes back null — the resume constructor
    // drops that item's delivery gracefully. Returns null only when the descriptor is unusable
    // (count mismatch with no legacy fallback), routing the loader to ResumeTravelTask.
    static List<Tile> ReadTravelStorageTiles(AnimalSaveData sd, int count) {
        if (sd.travelStorageXs != null && sd.travelStorageYs != null
            && sd.travelStorageXs.Length == sd.travelStorageYs.Length
            && sd.travelStorageXs.Length == count) {
            var tiles = new List<Tile>(count);
            for (int i = 0; i < count; i++)
                tiles.Add(World.instance.GetTileAt(sd.travelStorageXs[i], sd.travelStorageYs[i]));
            return tiles;
        }
        if (count == 1 && sd.travelStorageX.HasValue && sd.travelStorageY.HasValue)
            return new List<Tile> { World.instance.GetTileAt(sd.travelStorageX.Value, sd.travelStorageY.Value) };
        return null;
    }

    public void TickUpdate() { // called from animalcontroller each second.
        tickCounter++;
        // Sample the state held since the last tick into the recency-weighted activity
        // record (population-panel "job load" bar). Before the starvation early-return so
        // a starving mouse's last ticks still register.
        activity.Tick(state);
        if (tickCounter % 10 == 0) {
            SlowUpdate();
        }
        HandleNeeds();
        // Expire timed tonic buffs; recompute the cached comfort range at once if a temperature buff
        // just lapsed (otherwise it would linger until the next 10-tick SlowUpdate).
        if (buffs.Tick()) happiness.UpdateComfortRange(this);
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
        eating.Update(1f, state == AnimalState.Eeping);
        eeping.Update();
        // Sleep recovery is wall-clock — ticked here every tick, NOT from HandleEeping.
        // HandleEeping only runs when the energy/efficiency throttle fires UpdateState, so
        // driving recovery from there would scale it with efficiency while depletion
        // (eeping.Update above) stays unthrottled. A hungry, exhausted mouse has low
        // efficiency; throttled recovery + unthrottled depletion would let it lose eep
        // faster than it regains it and never wake. Ticking recovery here keeps it
        // symmetric with depletion — and with eating recovery below.
        if (state == AnimalState.Eeping) { eeping.Eep(1f + buffs.Total(BuffType.SleepRecovery), AtHome()); }
        // Nibble from the carried food slot to top the belly up toward full. The belly
        // (eating.food, cap maxFood) is "eat to full"; the slot is the carried "to go" buffer.
        // We eat at most ~1 liang/tick (gradual munch, not an instant gulp) and never more than
        // the deficit to full — so a near-full mouse takes a partial bite instead of wasting a
        // whole liang to the maxFood clamp. Runs whenever there's slot food and any deficit,
        // so a mouse stays topped off as long as it's carrying food.
        Item slotFood = foodSlotInv.itemStacks[0].item;
        if (slotFood != null && slotFood.foodValue > 0 && eating.food < eating.maxFood) {
            int available = foodSlotInv.Quantity(slotFood);
            if (available > 0) {
                // Fen needed to reach full at this food's nutrition density.
                int deficitFen = Mathf.CeilToInt((eating.maxFood - eating.food) / slotFood.foodValue * 100f);
                int eat = Math.Min(Math.Min(100, deficitFen), available);
                foodSlotInv.Produce(slotFood, -eat);
                eating.Eat(slotFood.foodValue * eat / 100f);
                happiness.NoteAte(slotFood, eat / 100f);
                StatsTracker.instance?.NoteConsumed(slotFood, eat);
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
    public float BedtimeUrgency() {
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
    // Display-only readout of the mouse's current priority: the category that won the most recent
    // ChooseTask, and its pre-jitter urgency. Set below, surfaced in the mouse info panel, not saved
    // (it's recomputed on the next decision). raw value (not the jittered one used for the sort) so
    // the number is stable and meaningful.
    public string topUrgencyLabel = "idle";
    public float  topUrgencyValue;

    public void ChooseTask() {
        if (task != null){ return; }

        // Score every category, jittering each score (see Jitter). Helpers that no-op when nothing
        // applies (full slot, no orders) get urgency 0 so they're never attempted. Idle is the floor.
        // Craft and leisure are gathered once here and threaded into both their urgency and their
        // pick, so the (non-trivial) scan isn't run twice per idle tick.
        // Each candidate carries its pre-jitter urgency and a label purely so the winner can be
        // recorded for the info panel (topUrgencyLabel/Value) — they don't affect the choice.
        var candidates = new List<(float urgency, float raw, string label, System.Func<bool> tryStart)>();

        float eatU = eating.HungerUrgency();
        candidates.Add((Jitter(eatU), eatU, "eat", FindFood));
        float sleepU = eeping.SleepUrgency(BedtimeUrgency());
        candidates.Add((Jitter(sleepU), sleepU, "sleep", TryStartSleep));
        // Equip/clothing: fixed pull, only when the slot is actually empty (else the helper would
        // rank then no-op — see validation note in the plan).
        float equipU = (job.usesTools && toolSlotInv.itemStacks[0].item == null) ? UrgencyConfig.EquipUrgency : 0f;
        float clothingU = clothingSlotInv.itemStacks[0].item == null ? UrgencyConfig.EquipUrgency : 0f;
        // Self-heal the hard hat-uniform rule: drop a hat that isn't this mouse's preferred one
        // (kept across a job change, or loaded from a pre-gating save) so the slot frees to seek the
        // right one. Pairs with SetJob (immediate on swap) and FindHat (only ever fetches the preferred hat).
        if (hatSlotInv.itemStacks[0].item != null && hatSlotInv.itemStacks[0].item != job.preferredHatItem)
            Unequip(hatSlotInv);
        float hatU = (job.preferredHatItem != null && hatSlotInv.itemStacks[0].item == null) ? UrgencyConfig.EquipUrgency : 0f;
        candidates.Add((Jitter(equipU), equipU, "equip", FindEquipment));
        candidates.Add((Jitter(clothingU), clothingU, "clothing", FindClothing));
        candidates.Add((Jitter(hatU), hatU, "hat", FindHat));
        var wom = WorkOrderManager.instance;
        // Craft is scored before drop so a craft blocked purely by a cluttered pack can lift drop
        // urgency above the craft band (clear clutter now, craft next tick). The scan is
        // wom-independent; the craft *candidate* still needs wom, added in the block below.
        var craft = wom != null ? ScoreCraftRecipes() : null;
        bool craftBlockedByClutter = craft != null && craft.Count > 0 && CraftBlockedByClutter(craft[0].recipe);

        float dropU = DropUrgency(craftBlockedByClutter);
        candidates.Add((Jitter(dropU), dropU, "drop", TryStartDrop));

        if (wom != null) {
            wom.PruneStale();
            float workU = wom.BestWorkUrgency(this);
            candidates.Add((Jitter(workU), workU, "work", TryStartWorkOrder));
            float craftU = CraftUrgency(craft);
            candidates.Add((Jitter(craftU), craftU, "craft", () => ChooseCraftTask(craft)));
        }
        var leisure = GatherLeisureCandidates();
        float leisureU = LeisureUrgency(leisure);
        candidates.Add((Jitter(leisureU), leisureU, "leisure", () => TryPickLeisure(leisure)));
        // Drink a tonic for its timed buff. ChooseTonic returns null when nothing applies (no stock,
        // already buffed, or comfortable) → urgency 0, so the category is simply skipped.
        (Item tonic, float tonicU) = ChooseTonic();
        if (tonic != null) candidates.Add((Jitter(tonicU), tonicU, "tonic", () => TryStartDrinkTonic(tonic)));
        float idleU = IdleUrgency();
        candidates.Add((Jitter(idleU), idleU, "idle", () => { task = null; return true; })); // idle floor — always "succeeds"

        // Highest urgency first; attempt each until one starts a task. Record the winner for display.
        candidates.Sort((a, b) => b.urgency.CompareTo(a.urgency));
        foreach (var c in candidates) {
            if (c.urgency <= 0f) continue;
            if (c.tryStart()) { topUrgencyLabel = c.label; topUrgencyValue = c.raw; return; }
        }
        topUrgencyLabel = "idle";
        topUrgencyValue = idleU;
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
    private float DropUrgency(bool craftBlocked = false) {
        if (inv.IsEmpty() || World.instance.timer < dropCooldownUntil) return 0f;
        // A craft the mouse wants can't fit its inputs because of carried clutter: spike above the
        // craft band so it offloads now and crafts next tick, rather than possibly doing other work
        // while boxed in. Still below hunger/sleep peaks and near-p1 work (see UrgencyConfig).
        if (craftBlocked) return UrgencyConfig.DropCraftBlocked;
        int occupied = 0;
        foreach (ItemStack s in inv.itemStacks)
            if (s != null && s.quantity > 0) occupied++;
        float fullness = (float)occupied / inv.itemStacks.Length;
        return UrgencyConfig.DropFloor
             + (UrgencyConfig.DropCeil - UrgencyConfig.DropFloor) * fullness;
    }

    // True when the best craftable recipe can't fit even one round in the current pack, yet WOULD
    // fit an empty pack — carried clutter is the sole blocker. Signals ChooseTask to spike drop
    // urgency so the mouse clears the pack, then crafts next tick. Volume-aware (fen per input,
    // ceil over stacks, deduped by item); fuel is approximated as one stack and a group input uses
    // the group item as a stand-in. This only gates the *drop* decision — CraftTask.Initialize's
    // exact, leaf-resolved carry cap is the real gate, so a coarse estimate here is harmless.
    private bool CraftBlockedByClutter(Recipe recipe) {
        if (recipe == null || recipe.IsExtraction || inv.IsEmpty()) return false;
        var adds = new List<(Item, int)>(recipe.inputs.Length);
        foreach (ItemQuantity iq in recipe.inputs) adds.Add((iq.item, iq.quantity));
        int need = inv.EmptyStacksToAbsorb(adds) + (recipe.fuelCost > 0f ? 1 : 0);
        int total = inv.itemStacks.Length;
        return need <= total && need > inv.CountEmptyStacks();
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
            // Prefer the foundry for metalworking: skip a non-foundry recipe whose output the foundry
            // can cast (e.g. crucible smelt → copper bar) when a foundry sits within medium radius of
            // this station. Lets the player leave both crucible + foundry enabled without the crucible
            // stealing metal work. Recipes the foundry can't do (clay-mold firing) are unaffected.
            if (FoundrySupersedes(recipe) && wom.FoundryWithinRadius(building.x, building.y, Task.MediumFindRadius))
                continue;
            // The well reuses the craft demand/dispatch layer (so it competes for water like a pump)
            // but runs a bespoke lowering-bucket draw instead of the fixed-workload craft loop.
            Task chosen = building is Well well
                ? new DrawWaterTask(this, well, recipe)
                : new CraftTask(this, building, recipe);
            if (chosen.Start()) {
                order.res.Reserve();
                chosen.workOrder = order;
                task = chosen;
                return true;
            }
        }
        return false;
    }

    // A craft recipe is "foundry-superseded" when it's NOT a foundry recipe yet the foundry can cast its
    // primary output (e.g. crucible smelt-malachite → copper bar, or crucible cast-copper-tools). Smiths
    // skip these at the crucible when a foundry is in range (see ChooseCraftTask), so both buildings can
    // stay enabled. Data-driven: keyed on whether any foundry cast recipe produces the same output.
    private static bool FoundrySupersedes(Recipe r) {
        if (r == null || r.tile == "foundry") return false;
        if (r.outputs.Length == 0 || r.outputs[0].item == null) return false;
        return Foundry.CastRecipeForBar(r.outputs[0].item) != null;
    }

    // Eligible recipes for this animal's job, scored by economic need and sorted best-first.
    // Shared by ChooseCraftTask (which station to visit) and CraftUrgency (how badly to craft).
    private List<(Recipe recipe, float score)> ScoreCraftRecipes() {
        var targets = InventoryController.instance?.targets;
        var scored = new List<(Recipe recipe, float score)>();
        foreach (var r in job.recipes) {
            if (r == null) continue;
            if (!r.IsEligibleForPicking()) continue;
            if (!ginv.CanCraft(r)) continue;   // inputs in stock AND fuel energy (if recipe burns fuel)
            if (r.IsExtraction) {
                // Quarry / digging pit: flat urgency (via the band-inverse score) while ANY reachable
                // wall still has a wanted output — base OR rare drop below target. Skipped entirely once
                // every possible output is over target, so a fully-satisfied cluster stops digging.
                if (AnyExtractorWanted(r, targets))
                    scored.Add((r, UrgencyConfig.ExtractionScoreForBand()));
                continue;
            }
            if (r.AllOutputsSatisfied(targets)) continue;
            scored.Add((r, r.Score(targets)));
        }
        scored.Sort((a, b) => b.score.CompareTo(a.score)); // highest score first
        return scored;
    }

    // True if any live extractor building for this recipe's type still has a wanted output in its
    // captured wall (Recipe.ExtractionWanted). Recipe-level gate for the flat extraction urgency —
    // per-building wall differences are ignored here (v1: which building the miner visits is chosen
    // by ChooseCraftTask/proximity). Returns false when no such building exists.
    private bool AnyExtractorWanted(Recipe r, Dictionary<int, int> targets) {
        var sc = StructController.instance;
        if (sc == null) return false;
        foreach (Structure s in sc.GetStructures())
            if (s is IExtractor ex && s.structType.name == r.tile
                && Recipe.ExtractionWanted(ex.CapturedProducts, targets))
                return true;
        return false;
    }

    // 0..1 urgency for the craft category. Recipe.Score is unbounded (0..+∞), so it can't be
    // compared directly to the tier-based work urgencies — map it into the fixed [CraftFloor,
    // CraftCeil] band via s/(1+s) (monotonic, lands in (0,1)). The floor sits just above the
    // daytime idle floor so any eligible recipe (s>0) is never soft-locked out; the ceil is the
    // asymptote a scarce-output recipe approaches.
    private float CraftUrgency(List<(Recipe recipe, float score)> scored) {
        if (scored.Count == 0) return 0f;
        return UrgencyConfig.CraftBand(scored[0].score); // best-first; shared band (handles s<=0 and +∞)
    }

    // Picks up one tool into toolSlotInv if the slot is empty.
    private bool FindEquipment() {
        if (!job.usesTools) return false;                          // job gains no tool benefit; don't seek one
        if (toolSlotInv.itemStacks[0].item != null) return false; // already holding a tool
        var ic = InventoryController.instance;
        foreach (Item equipment in Db.equipmentItems) {
            if (ic != null && ic.IsConsumptionDisabled(equipment)) continue; // "consume" off — mice won't equip it
            task = new ObtainTask(this, equipment, 100, toolSlotInv);
            if (task.Start()) return true;
        }
        return false;
    }

    // Picks up one clothing item into clothingSlotInv if the slot is empty.
    private bool FindClothing() {
        if (clothingSlotInv.itemStacks[0].item != null) return false; // already wearing clothing
        var ic = InventoryController.instance;
        foreach (Item clothing in Db.clothingItems) {
            if (ic != null && ic.IsConsumptionDisabled(clothing)) continue; // "consume" off — mice won't wear it
            task = new ObtainTask(this, clothing, 100, clothingSlotInv);
            if (task.Start()) return true;
        }
        return false;
    }

    // Picks up this mouse's profession hat into hatSlotInv if the slot is empty. Unlike clothing
    // (any item works), a mouse seeks only the one hat its job prefers — so hats read as a uniform.
    private bool FindHat() {
        if (hatSlotInv.itemStacks[0].item != null) return false; // already wearing a hat
        Item hat = job.preferredHatItem;
        if (hat == null) return false;                            // job seeks no hat
        var ic = InventoryController.instance;
        if (ic != null && ic.IsConsumptionDisabled(hat)) return false; // "consume" off — mice won't wear it
        task = new ObtainTask(this, hat, 100, hatSlotInv);
        return task.Start();
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

        // Normally fill the whole slot so a mouse eats to full and carries a buffer (fewer
        // trips, less time hungry). But when the colony is short on food (< 2 days in storage,
        // matching the reproduction hard floor) fall back to a single liang so scarce food
        // stays in shared, reachable storage instead of being locked in mouse slots.
        const float scarceFoodDays = 2f;
        bool foodScarce = AnimalController.instance != null
            && AnimalController.instance.daysOfFoodInStorage < scarceFoodDays;
        int amountToPickUp = foodScarce ? Math.Min(room, 100) : room;
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
        var ic = InventoryController.instance;
        foreach (Item food in Db.edibleItems) {
            if (slotItem != null && slotItem != food) continue; // slot has a different food
            if (ic != null && ic.IsConsumptionDisabled(food)) continue; // "don't consume" — mice won't fetch it to eat
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

    // ── Tonic drinking (timed buffs) ─────────────────────────────────────────
    // Picks the most worthwhile in-stock tonic to drink and its urgency, or (null, 0) if none applies.
    // A tonic whose effect the mouse already has is skipped — this self-limits drinking, since one
    // dose lasts the whole effect duration before that tonic is a candidate again.
    private (Item tonic, float urgency) ChooseTonic() {
        if (Db.tonicItems == null) return (null, 0f);
        Item best = null;
        float bestU = 0f;
        var ic = InventoryController.instance;
        foreach (Item t in Db.tonicItems) {
            if (!t.buffEffect.HasValue) continue;
            if (buffs.Has(t.buffEffect.Value)) continue;             // already have this effect
            if (ic != null && ic.IsConsumptionDisabled(t)) continue; // "consume" off — mice won't drink it
            if (GlobalInventory.instance.Quantity(t) <= 0) continue; // none in stock
            float u = TonicUrgency(t.buffEffect.Value);
            if (u > bestU) { bestU = u; best = t; }
        }
        return (best, bestU);
    }

    // Urgency to drink a tonic granting `type`. Temperature buffs are need-driven — they scale with
    // how far outside the comfort band the mouse is (0 when comfortable). Vigor/restful are "always
    // eligible" at a small baseline just above the idle floor, so an idle mouse tops them up but they
    // never preempt real work or needs.
    private float TonicUrgency(BuffType type) {
        float temp = WeatherSystem.instance != null ? WeatherSystem.instance.temperature : 17.5f;
        switch (type) {
            case BuffType.ColdTolerance:
                return temp < happiness.comfortTempLow
                    ? Mathf.Clamp((happiness.comfortTempLow - temp) / UrgencyConfig.TonicTempSpan, 0f, UrgencyConfig.TonicTempCeil)
                    : 0f;
            case BuffType.HeatTolerance:
                return temp > happiness.comfortTempHigh
                    ? Mathf.Clamp((temp - happiness.comfortTempHigh) / UrgencyConfig.TonicTempSpan, 0f, UrgencyConfig.TonicTempCeil)
                    : 0f;
            default: // WorkSpeed, SleepRecovery
                return UrgencyConfig.TonicBaseline;
        }
    }

    private bool TryStartDrinkTonic(Item tonic) {
        task = new DrinkTonicTask(this, tonic);
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
        Tile here = TileHere();
        int leftover = inv.MoveItemTo(here.EnsureFloorInventory(), item, quantity);
        // Post-drop fall: if these items landed on a non-standable tile, let them fall (and
        // vanish if nothing's below) rather than float. No-op in the common case — a mouse
        // stands on standable ground, so its own tile is standable.
        World.instance.FallIfUnstandable(here.x, here.y);
        return leftover;
    }
    public int DropItem(ItemQuantity iq) { return(DropItem(iq.item, iq.quantity)); }

    // Spills the mouse's carried goods onto the floor when it dies so the player can
    // recover food and gear — EXCEPT the equipped tool, which is destroyed. Each dropped
    // item routes through FindPathToDrop so it lands on a tile with space; MoveItemTo keeps
    // the GlobalInventory totals correct. Called by AnimalController before Destroy().
    public void DropInventoryToFloor() {
        // Destroy worn equipment (tool + clothing) rather than dropping it: both wear down
        // while equipped, but durability isn't tracked once an item is an unequipped floor
        // item — dropping would silently restore full durability (a value dupe). Decrement the
        // world total here; Animal.Destroy zeroes the slots afterward (Inventory.Destroy is
        // ginv-neutral). Food and books still drop for recovery (no durability to lose).
        foreach (Inventory worn in new[] { toolSlotInv, clothingSlotInv, hatSlotInv }) {
            if (worn == null) continue;
            foreach (ItemStack stack in worn.itemStacks) {
                if (stack.item == null || stack.quantity == 0) continue;
                ginv.AddItem(stack.item, -stack.quantity);
            }
        }

        Inventory[] sources = { inv, foodSlotInv, bookSlotInv };
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
                // A spilled item can land on a non-standable tile (FindPathToDrop only needs
                // floor space, not footing) — make it fall so it doesn't float; FallItems
                // deletes it if there's nowhere standable below.
                World.instance.FallIfUnstandable(dropTile.x, dropTile.y);
            }
        }
    }

    // produces item in ani inv, dumps at nearby tile if inv full
    public void Produce(Item item, int quantity = 1){
        if (quantity < 0){
            Debug.LogError("called produce with negative quantity, use consume instead");
            Consume(item, -quantity); return;
        }
        // Colony production tally (food-points-per-day chart, etc.). The single chokepoint
        // for harvest, craft, and construction output — and never hit during save-load.
        StatsTracker.instance?.NoteProduced(item, quantity);
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
            if (ginv.CanCraft(recipe)){
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
            if (ginv.CanCraft(recipe)){
                if (!Db.structTypeByName.ContainsKey(recipe.tile) ||
                    !nav.CanReachBuilding(Db.structTypeByName[recipe.tile])) continue;
                // Gate out fully-satisfied recipes, mirroring PickRecipeForBuilding/ScoreCraftRecipes.
                // Must precede the Score/maxScore compare: a satisfied recipe could otherwise enter
                // the score==maxScore reservoir-tie branch. (Score no longer self-suppresses on
                // over-target outputs — surplus outputs are skipped — so this gate is load-bearing.)
                if (recipe.AllOutputsSatisfied(targets)) continue;
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
            if (!ginv.CanCraft(recipe)) continue;
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

    // Like PickRecipeForBuilding but for a Processor batch: scores among the building's processor
    // recipes (Db.GetProcessorRecipes — every recipe with tile==name + a duration) regardless of
    // the picking animal's job, since whoever fills the building chooses the batch. No output-
    // storage gate: the batch lands in the processor's own `output` buffer (always has room for
    // one batch — the Fill order only re-opens once `output` has drained), then eviction-hauls.
    public Recipe PickProcessorRecipe(Building building){
        var recipes = Db.GetProcessorRecipes(building.structType.name);
        if (recipes == null) return null;
        var targets = InventoryController.instance.targets;
        float maxScore = 0;
        Recipe best = null;
        int tiedCount = 0; // reservoir sampling for ties, as in PickRecipeForBuilding
        foreach (Recipe recipe in recipes){
            if (recipe == null) continue;
            if (!recipe.IsEligibleForPicking()) continue;   // player toggle + research unlock
            if (!ginv.CanCraft(recipe)) continue;           // inputs in stock AND fuel energy
            if (recipe.AllOutputsSatisfied(targets)) continue;
            float score = recipe.Score(targets);
            if (score > maxScore){
                maxScore = score;
                best = recipe;
                tiedCount = 1;
            } else if (score == maxScore) {
                tiedCount++;
                if (random.NextDouble() < 1.0 / tiedCount) best = recipe;
            }
        }
        return best;
    }

    // Single source of truth for the per-task work-stint cap: an animal works at most this many
    // ticks (1 tick ~ 1s) on one task before it completes and re-evaluates — eat, sleep, switch to
    // a closer/better task. Craft derives its batch size from this budget (below); construction
    // counts ticks against it directly (see AnimalStateManager.HandleWorking). Harvest, research,
    // and maintenance already finish a stint well under this.
    public const int MaxWorkStintTicks = 30;

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
        // Fuel isn't in recipe.inputs, so the loop above doesn't bound it — cap by total fuel
        // energy. Coarse (sums all fuel types; CraftTask commits to one), but the fetch path
        // trims further if the picked type alone can't cover the rounds. The CanCraft gate
        // already guaranteed ≥1 round's worth before we got here.
        if (recipe.fuelCost > 0f){
            n = (int)(ginv.ConsumableFuelEnergy() / recipe.fuelCost);
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
        // Hats are a hard profession uniform: a mouse wears ONLY its job's preferredHat. On a job
        // change, drop any hat that no longer matches (including switching to a hatless job) so it
        // returns to circulation for a mouse that actually wants it. FindHat enforces the same rule
        // on the pickup side, so the two together make "preferred" a hard requirement, not a default.
        if (hatSlotInv != null) {
            Item worn = hatSlotInv.itemStacks[0].item;
            if (worn != null && worn != newJob?.preferredHatItem)
                Unequip(hatSlotInv);
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

        // A mouse only (re)homes when it genuinely lacks a usable home: (a) none assigned,
        // (b) the building lost its housing flag (StructType edited at runtime — unlikely but
        // cheap to check), or (c) it's broken and unusable. Mice never voluntarily migrate
        // between valid homes. That used to happen here — a mouse whose home was full would
        // hop to a less-crowded house every slow tick — which silently undid the player's
        // housing arrangements. Crowding is now the player's call (lower a house's cap, or
        // evict), not the mouse's.
        bool needsHome = homeBuilding == null
                      || !homeBuilding.structType.isHousing
                      || homeBuilding.IsBroken;
        if (!needsHome) return;

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
    }

    // Commits a new home assignment: sets homeBuilding + homeTile (the path target —
    // either the door's approach tile or the building's anchor for legacy housing) and
    // reserves a slot on the building's Reservable.
    void AssignHome(Building b) {
        homeBuilding = b;
        homeTile = b.doorApproachTile ?? b.tile;
        b.res.Reserve(aName);
    }

    // Player-initiated eviction from the current home. Moves the mouse to the best OTHER
    // reachable house so it doesn't immediately re-home into the one it was just evicted
    // from (FindHome picks the cheapest free slot — usually the freed one). If there's no
    // other house, the home is cleared and the next SlowUpdate re-homes it (back here if
    // this is the only housing). No-op if the mouse has no home.
    public void EvictFromHome() {
        if (homeBuilding == null) return;
        Building other = FindReachableHousing(exclude: homeBuilding);
        homeBuilding.res.Unreserve();
        if (other != null) {
            AssignHome(other);
        } else {
            homeBuilding = null;
            homeTile = null;
        }
        happiness?.RecomputeFurnishingBonus(this);
    }

    // Scans all reachable housing buildings (any StructType with isHousing == true) for the
    // cheapest-cost path with a free slot. Iterates StructController rather than routing
    // through Nav.FindPathToStruct because housing isn't keyed by a single StructType.
    // `exclude` skips one building — used by eviction so a freed-then-rehomed mouse doesn't
    // just snap straight back into the house it was evicted from.
    private Building FindReachableHousing(Building exclude = null) {
        Node myNode = PathStartNode();
        if (myNode == null) return null;
        Building best = null;
        float bestCost = float.MaxValue;
        foreach (Structure s in StructController.instance.GetStructures()) {
            Building b = s as Building;
            if (b == null) continue;
            if (b == exclude) continue;
            if (!b.structType.isHousing) continue;
            if (b.IsBroken) continue;
            if (!b.res.Available()) continue;
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
    // tile node under their feet.
    //
    // The interior-waypoint indirection below exists ONLY for tiles you can't actually
    // stand on: inside a doored building the grid node may be solid dirt / non-standable
    // (a burrow's preserved dirt) with no graph edges, so starting a path from it would
    // orphan every request — we return the nearest interior waypoint instead, edged to
    // the door approach so A* routes out automatically. But when the grid node under the
    // mouse IS standable, that's real footing — use it directly. This also self-heals a
    // stale `interiorBuilding` back-ref left on an emptied/standable tile (e.g. a legacy
    // extraction structure whose tile was dug out under the old non-preserving behaviour):
    // its interior waypoint is orphaned (disconnected) and would otherwise strand the mouse
    // one step from perfectly walkable ground. Falls back to TileHere if interior data is
    // missing (corrupted state).
    public Node PathStartNode() {
        Node gridNode = TileHere()?.node;
        if (gridNode != null && gridNode.standable) return gridNode;
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
        return gridNode;
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
    // Public so the live re-apply on a settings change (InteriorLightingApplier) can re-evaluate
    // every mouse when the interior mode flips — BurrowAsBuilding makes inside-mice want Default.
    public void RefreshInteriorRendering() {
        Building ib = insideBuilding;   // property: tile lookup, never stale
        // In BurrowAsBuilding mode the burrow (and anyone inside it) shades like a surface
        // building, so inside-mice stay on Default too.
        bool asBuilding = SettingsManager.instance != null && SettingsManager.instance.burrowAsBuilding;
        bool wantInterior = ib != null && ib.structType.enclosed && !asBuilding;
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

    // A broken home confers no housing benefit — it stops counting as the mouse's home for
    // happiness, the furnishing bonus, and the "has a home" tally until it's repaired. FindHome
    // already tries to relocate mice out of broken homes (needsHome polls IsBroken); this covers
    // the stuck case where no replacement exists.
    public bool HasHouse => homeBuilding != null && homeBuilding.structType.isHousing && !homeBuilding.IsBroken;

    public bool IsMoving(){
        return state == AnimalState.Moving || state == AnimalState.Falling;
    }

    public float SquareDistance(float x1, float x2, float y1, float y2) { return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2); }
    public void Destroy() {
        // Fail before tearing down inv — otherwise in-flight reservations strand on about-to-die stacks.
        // Skipped during bulk teardown (ClearWorld): every stack, WOM order, and reservation is being
        // destroyed together, so failing here only triggers pointless Cleanup side effects — e.g. a
        // ReadBookTask/ResearchTask dumping its held book to the floor, which re-creates a tile.inv
        // (defeating ClearWorld's tile.inv null-out) and spawns a FallAnim coroutine that leaks into
        // the freshly-loaded world. Same guard pattern as Inventory.Destroy.
        if (!WorldController.isClearing) task?.Fail();
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
        if (hatSlotInv != null)      { hatSlotInv.Destroy(reason: $"{aName} died"); hatSlotInv = null; }
        if (bookSlotInv != null)     { bookSlotInv.Destroy(reason: $"{aName} died"); bookSlotInv = null; }
        GameObject.Destroy(gameObject);
    }

    public void RegisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged += callback; }
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged -= callback; }
}



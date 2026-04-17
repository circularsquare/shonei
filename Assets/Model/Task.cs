using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Task / Objective system
//
// Lifecycle:  Idle → ChooseTask() → task.Start()
//   Start() calls Initialize(), which validates feasibility, builds the objective queue,
//   and reserves resources (source items via ReserveStack, destination space via ReserveSpace).
//   If Initialize returns false, Cleanup() releases any partial reservations.
//   Otherwise objectives execute sequentially via StartNextObjective().
//   When the last objective completes → Complete() → Cleanup() → Idle.
//   Any objective can call Fail() → Cleanup() → Idle.
//
// WOM linkage: most tasks are dispatched via WorkOrderManager. When a WOM order is claimed,
//   task.workOrder is set; Cleanup() calls workOrder.res.Unreserve() so the order becomes
//   available for the next animal. See SPEC-ai.md for the full dispatch sequence.
//
// Adding a new task: see the 11-step checklist in SPEC-ai.md.

public abstract class Task {
    public Animal animal;
    protected LinkedList<Objective> objectives = new LinkedList<Objective>();
    // One-way travel time to/from the off-screen market. Symmetric — same duration each leg.
    // 1 tick = 1 second (ticksInDay=240, 10s = 1 in-game hour), so 20 ticks = 20 real seconds.
    protected static readonly int MarketTransitTicks = World.ticksInDay / 12;
    public Objective currentObjective;
    private readonly List<(ItemStack stack, int amount)> reservedStacks = new();
    private readonly List<(ItemStack stack, int amount)> reservedSpaces = new();
    // Number of entries the most recent Task.ReserveSpace call appended to reservedSpaces.
    // A single call may touch multiple stacks (e.g. distributing a large reserve across a
    // multi-slot storage), so UndoLastSpaceReservation needs to know how many to pop.
    private int lastReserveSpaceEntryCount = 0;
#if UNITY_EDITOR
    // Editor-only read access for ReservationInvariant.Check — debug only, zero runtime surface.
    public IReadOnlyList<(ItemStack stack, int amount)> ReservedStacks => reservedStacks;
    public IReadOnlyList<(ItemStack stack, int amount)> ReservedSpaces => reservedSpaces;
#endif
    // Set by WorkOrderManager.ChooseOrder when this task fulfills a work order.
    // Null for non-WOM tasks (Craft, Eep, Obtain, etc.). Released in Cleanup().
    public WorkOrderManager.WorkOrder workOrder;

    // De minimis: skip hauls/drops below this unless it clears the source stack entirely.
    public const int MinHaulQuantity = 20; // 0.20 liang

    // Strict minimum for market hauls — no exceptions for stack-clearing or topping off.
    // Merchants shouldn't make a trip for a trickle.
    public const int MinMarketHaulQuantity = 100;       // 1.0 liang (most items)
    public const int MinMarketHaulQuantitySilver = 40;  // 0.4 liang (silver moves in smaller amounts)

    // ── Search radii ─────────────────────────────────────────────────────
    // Every task pathfind should be gated by one of these radii × FindRadiusTolerance.
    // A candidate target is rejected if the *actual* path cost to reach it exceeds
    // radius × tolerance. This prevents mice committing to journeys that look close
    // crow-flies but wind endlessly around terrain.
    public const int MediumFindRadius = 40;         // default for almost every task
    public const int MarketFindRadius = 120;        // market portal only — intentionally long
    public const float FindRadiusTolerance = 1.2f;  // path cost may exceed radius by this factor

    protected static int MinMarketHaul(Item item) =>
        item.name == "silver" ? MinMarketHaulQuantitySilver : MinMarketHaulQuantity;

    // check whether a task is possible. create objectives, make reservations
    public abstract bool Initialize();

    public Task(Animal animal){
        this.animal = animal;
    }

    // Reserves items on a stack and tracks for cleanup. Single entry point for all source reservations.
    public void ReserveStack(ItemStack stack, int amount){
        int reserved = stack.Reserve(amount, this);
        if (reserved > 0) reservedStacks.Add((stack, reserved));
    }

    // Sum of source reservations against a specific inventory — for diagnostics when a
    // reserved source is torn down. Matches by inv only; stack.item may have been nulled.
    public int ReservedAmountFromInv(Inventory inv){
        int total = 0;
        foreach (var (stack, amount) in reservedStacks)
            if (stack.inv == inv) total += amount;
        return total;
    }

    // Reserves destination space in an inventory and tracks for cleanup. Returns total amount reserved.
    // The per-stack breakdown is recorded in reservedSpaces so Cleanup can release each stack
    // directly (no inventory-wide lookup, no item-key matching — works correctly even when the
    // reservation key is a group item but the stack later materializes a leaf).
    public int ReserveSpace(Inventory inv, Item item, int amount){
        var entries = inv.ReserveSpace(item, amount, this);
        int total = 0;
        foreach (var entry in entries){
            reservedSpaces.Add(entry);
            total += entry.amount;
        }
        lastReserveSpaceEntryCount = entries.Count;
        return total;
    }

    // Enqueues a FetchObjective and reserves items on the stack, tracking for cleanup.
    protected void FetchAndReserve(ItemQuantity iq, Tile tile, ItemStack stack, int amount){
        objectives.AddLast(new FetchObjective(this, iq, tile, sourceInv: stack.inv));
        ReserveStack(stack, amount);
    }
    protected void FetchAndReserve(ItemQuantity iq, Tile tile, ItemStack stack){
        FetchAndReserve(iq, tile, stack, iq.quantity);
    }
    // Overload that routes pickup into an equip slot instead of the animal's main inventory.
    protected void FetchAndReserve(ItemQuantity iq, Tile tile, ItemStack stack, Inventory targetInv){
        objectives.AddLast(new FetchObjective(this, iq, tile, targetInv, sourceInv: stack.inv));
        ReserveStack(stack, iq.quantity);
    }

    // Estimated walk time from anywhere on-map to the market portal at x=0.
    // protected so HaulToMarketTask can reuse it as a budget for the piggyback
    // portal→storage walk (see TryAppendPickup).
    protected const float WalkToPortalSeconds = 20f;

    // Finds the nearest reachable food source. If slotItem is non-null, only searches
    // for that item (can't mix foods in the slot). Otherwise picks nearest of any edible item.
    private (Path path, ItemStack stack, Item food) FindNearestFood(Item slotItem) {
        Path bestPath = null;
        ItemStack bestStack = null;
        Item bestFood = null;

        if (slotItem != null) {
            (bestPath, bestStack) = animal.nav.FindPathItemStack(slotItem);
            if (bestPath != null) bestFood = slotItem;
        }
        if (bestPath == null) {
            foreach (Item food in Db.edibleItems) {
                (Path p, ItemStack s) = animal.nav.FindPathItemStack(food);
                if (p == null) continue;
                if (bestPath == null || p.cost < bestPath.cost) {
                    bestPath = p; bestStack = s; bestFood = food;
                }
            }
        }
        return (bestPath, bestStack, bestFood);
    }

    // Checks whether the animal is provisioned for a market journey and fetches food if not.
    // transitTicks = per-leg transit ticks (MarketTransitTicks). The 2× factor inside
    // journeySeconds covers the round trip (walk to portal + one transit + walk home + one transit).
    // extraGroundSeconds adds slack for any extra on-map walking at trip end — used by the
    // HaulToMarket piggyback path, which terminates with a walk from the portal to a
    // specific home storage tile rather than going idle at x=0.
    // Accounts for body food + food slot. Only tops up the slot item already loaded; if the slot
    // is empty, picks the nearest available food.
    // Returns false (→ Initialize should abort) if enough food cannot be secured.
    protected bool PrependFoodFetchForMarketJourney(int transitTicks, float extraGroundSeconds = 0f) {
        float journeySeconds = 2f * (WalkToPortalSeconds + transitTicks) + extraGroundSeconds;
        // Buffer so the animal arrives with food above the efficiency drop threshold.
        float foodNeeded = animal.eating.hungerRate * journeySeconds + animal.eating.maxFood * Eating.hungryThreshold;

        ItemStack slotStack = animal.foodSlotInv.itemStacks[0];
        float slotFoodPoints = slotStack.item != null
            ? (slotStack.quantity / 100f) * slotStack.item.foodValue
            : 0f;
        float foodHave = animal.eating.food + slotFoodPoints;

        if (foodHave >= foodNeeded) return true; // already provisioned

        float deficit = foodNeeded - foodHave;

        var (bestPath, bestStack, bestFood) = FindNearestFood(slotStack.item);
        if (bestPath == null) return false; // no food reachable — refuse the journey

        int fenNeeded = (int)Math.Ceiling(deficit * 100f / bestFood.foodValue);
        int slotSpace = animal.foodSlotInv.stackSize - slotStack.quantity;
        int qty = Math.Min(slotSpace, Math.Min(bestStack.quantity - bestStack.resAmount, fenNeeded));
        if (qty <= 0) return false;

        // Verify the fetch will actually cover the deficit.
        float fetchedPoints = (qty / 100f) * bestFood.foodValue;
        if (foodHave + fetchedPoints < foodNeeded) return false;
        ItemQuantity foodIq = new(bestFood, qty);
        objectives.AddFirst(new FetchObjective(this, foodIq, bestPath.tile, animal.foodSlotInv, softFetch: false, sourceInv: bestStack.inv));
        ReserveStack(bestStack, qty);
        return true;
    }

    // Prepends a soft food-fetch to the front of the objective queue if the animal's food slot
    // is less than half full. Call at the end of Initialize() for tasks involving long journeys.
    // Uses softFetch so a missing food source won't fail the whole task.
    protected void PrependFoodFetchIfNeeded() {
        ItemStack slotStack = animal.foodSlotInv.itemStacks[0];
        int have     = slotStack.quantity;
        int capacity = animal.foodSlotInv.stackSize; // 300 fen = 3 liang
        if (have >= capacity / 2) return; // already half-stocked, good enough

        int space = capacity - have;

        var (bestPath, bestStack, bestFood) = FindNearestFood(slotStack.item);
        if (bestPath == null) return; // no food reachable — travel hungry

        int qty = Math.Min(space, bestStack.quantity - bestStack.resAmount);
        if (qty <= 0) return;

        ItemQuantity foodIq = new(bestFood, qty);
        // AddFirst so food is fetched before any other objectives run.
        // softFetch=true: if food is gone on arrival, objective completes rather than fails.
        objectives.AddFirst(new FetchObjective(this, foodIq, bestPath.tile, animal.foodSlotInv, softFetch: true, sourceInv: bestStack.inv));
        ReserveStack(bestStack, qty);
    }

    public bool Start(){ // calls some task specific Initialize
        bool initialized = Initialize();
        if (!initialized) Cleanup(); // release any reservations made before Initialize bailed
        else if (objectives.Count > 0) StartNextObjective();
        return initialized;
    }
    public virtual void Complete(){ // advances to next objective, or finishes the task if none remain
        if (objectives.Count > 0){StartNextObjective();}
        else {
            Cleanup();
            animal.task = null;
            animal.state = Animal.AnimalState.Idle;
        }
    }
    public void Fail(){ // end task, go idle
        if (currentObjective == null){
            Debug.Log($"{animal.aName} ({animal.job.name}) failed {ToString()} task, null objective");
        } else {
            Debug.Log($"{animal.aName} ({animal.job.name}) failed {ToString()} task at {currentObjective}");
        }
        Cleanup();
        animal.task = null;
        animal.state = Animal.AnimalState.Idle;
    }
    public virtual void Cleanup(){
        workOrder?.res.Unreserve();
        workOrder = null;
        foreach (var (stack, amount) in reservedStacks){
            stack.Unreserve(Math.Min(amount, stack.resAmount));
        }
        reservedStacks.Clear();
        foreach (var (stack, amount) in reservedSpaces){
            // Clamp against current resSpace: AddItem may have auto-cleared/reduced it
            // when the stack filled (see ItemStack.AddItem), without notifying us.
            stack.UnreserveSpace(Math.Min(amount, stack.resSpace));
        }
        reservedSpaces.Clear();
        lastReserveSpaceEntryCount = 0;
        objectives.Clear();
    }
    // Undoes the most recent Task.ReserveSpace call. Use when Initialize needs to back out
    // a single just-made reservation without clearing all task state (vs full Cleanup).
    // Pops every entry that call appended (one call may touch multiple stacks).
    protected void UndoLastSpaceReservation() {
        for (int i = 0; i < lastReserveSpaceEntryCount; i++){
            if (reservedSpaces.Count == 0) break;
            var (stack, amount) = reservedSpaces[^1];
            stack.UnreserveSpace(amount);
            reservedSpaces.RemoveAt(reservedSpaces.Count - 1);
        }
        lastReserveSpaceEntryCount = 0;
    }

    public void EnqueueFront(Objective obj) { objectives.AddFirst(obj); }

    // Objectives still queued AFTER currentObjective. StartNextObjective() pops the
    // current one off before it runs, so the list equals the remaining queue.
    // Used by MerchantJourneyDisplay to detect which leg a traveling merchant is on.
    public IEnumerable<Objective> RemainingObjectives() => objectives;

    public void StartNextObjective(){
        currentObjective = objectives.First.Value;
        objectives.RemoveFirst();
        currentObjective.Start();
    }
    public void OnArrival(){
        currentObjective?.OnArrival();
    }

    public virtual string GetTaskName(){
        return this.GetType().Name.Replace("Task", "");
    }
    public override string ToString(){
        return GetTaskName();
    }

}

public class CraftTask : Task {
    public Recipe recipe;
    public Tile workplace;
    public int roundsRemaining;
    private List<(Item item, int perRound)> _inputsToFetch;
    private int _fetchInputIndex;
    private readonly Building _building; // always set — assigned by WOM via RegisterWorkstation
    private readonly Recipe _preChosenRecipe; // set by ChooseCraftTask; null → PickRecipeForBuilding fallback

    public CraftTask(Animal animal, Building building, Recipe preChosenRecipe = null) : base(animal){
        _building = building;
        _preChosenRecipe = preChosenRecipe;
    }

    public override bool Initialize(){
        recipe = _preChosenRecipe ?? animal.PickRecipeForBuilding(_building);
        if (recipe == null) { return false; }
        Path p = animal.nav.PathTo(_building.workTile);
        if (!animal.nav.WithinRadius(p, MediumFindRadius)) { return false; }
        workplace = _building.workTile;

        roundsRemaining = animal.CalculateWorkPossible(recipe);
        if (recipe.maxRoundsPerTask > 0 && roundsRemaining > recipe.maxRoundsPerTask)
            roundsRemaining = recipe.maxRoundsPerTask;
        if (roundsRemaining == 0) { return false; }

        // Build the fetch list in forward order (skipping what's already in animal inv)
        _inputsToFetch = new List<(Item, int)>();
        foreach (ItemQuantity iq in recipe.inputs) {
            if (!animal.inv.ContainsItem(iq, roundsRemaining))
                _inputsToFetch.Add((iq.item, iq.quantity));
        }
        _fetchInputIndex = 0;

        // Queue tail: Go → Work → Drops
        objectives.AddLast(new GoObjective(this, workplace));
        objectives.AddLast(new WorkObjective(this, recipe));
        foreach (ItemQuantity output in recipe.outputs)
            objectives.AddLast(new DropObjective(this, output.item));

        // Prepend fetch objectives in reverse so index 0 ends at front
        for (int i = _inputsToFetch.Count - 1; i >= 0; i--) {
            var (item, perRound) = _inputsToFetch[i];
            int toFetch = perRound * roundsRemaining - animal.inv.Quantity(item);
            (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(item);
            if (itemPath == null) { return false; }
            ReserveStack(stack, toFetch);
            objectives.AddFirst(new FetchObjective(this, new ItemQuantity(item, toFetch), itemPath.tile, softFetch: true, sourceInv: stack.inv));
        }

        return true;
    }
    public override void Complete(){
        // When a FetchObjective finishes and we're still in the fetch phase, run continuation logic
        if (currentObjective is FetchObjective && _fetchInputIndex < _inputsToFetch.Count) {
            var (item, perRound) = _inputsToFetch[_fetchInputIndex];
            int have = animal.inv.Quantity(item);
            int needed = perRound * roundsRemaining;

            if (have < needed) {
                int stillNeed = needed - have;
                (Path p, ItemStack stack) = animal.nav.FindPathItemStack(item);
                if (p != null) {
                    ReserveStack(stack, stillNeed);
                    // Pass `needed` (not stillNeed) as iq.quantity so FetchObjective.Start's early-exit
                    // (have >= iq.quantity) only fires when the animal truly has everything it needs.
                    // OnArrival calculates how much to take as (iq.quantity - have), so it still fetches
                    // only the remaining gap.
                    EnqueueFront(new FetchObjective(this, new ItemQuantity(item, needed), p.tile, softFetch: true, sourceInv: stack.inv));
                    // Don't increment — check this ingredient again after the retry
                    base.Complete();
                    return;
                }
                // No sources left — trim rounds to what we can actually do
                int achievable = have / perRound;
                if (achievable == 0) { Fail(); return; }
                Debug.Log($"{animal.aName}: trimming rounds {roundsRemaining}→{achievable} (short on {item.name})");
                roundsRemaining = achievable;
            }
            _fetchInputIndex++;
        }
        base.Complete();
    }
}


public class ObtainTask : Task {
    public ItemQuantity iq;
    public Inventory targetInv; // null = animal's main inventory; pass foodSlotInv to equip
    public ObtainTask(Animal animal, ItemQuantity iq, Inventory targetInv = null) : base(animal){
        this.iq = iq;
        this.targetInv = targetInv;
    }
    public ObtainTask(Animal animal, Item item, int quantity, Inventory targetInv = null) : base(animal){
        iq = new ItemQuantity(item, quantity);
        this.targetInv = targetInv;
    }
    public override bool Initialize(){
        (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(iq.item);
        if (itemPath == null) { return false; }
        if (targetInv != null) FetchAndReserve(iq, itemPath.tile, stack, targetInv);
        else FetchAndReserve(iq, itemPath.tile, stack);
        return true;
    }
}
public class GoTask : Task {
    public Tile tile;
    public GoTask (Animal animal, Tile tile) : base(animal){ this.tile = tile;}
    public override bool Initialize(){
        if (animal.nav.PathTo(tile) == null) return false;
        objectives.AddLast(new GoObjective(this, tile)); return true;
    }
}
public class EepTask : Task {
    public EepTask(Animal animal) : base(animal){}
    public override bool Initialize(){
        if (animal.homeTile == null){
            animal.FindHome();
        }
        // Only walk home if within a reasonable radius; otherwise sleep in place (bed is a bonus,
        // not required). Without this, a walled-off or distant home causes a fail-and-retry loop
        // since the animal remains eepy and re-picks EepTask immediately.
        if (animal.homeTile != null && animal.nav.WithinRadius(animal.nav.PathTo(animal.homeTile), MediumFindRadius)){
            objectives.AddLast(new GoObjective(this, animal.homeTile));
        }
        objectives.AddLast(new EepObjective(this));
        return true;
    }
}
public class HarvestTask : Task {
    public Tile tile;
    public HarvestTask(Animal animal, Tile tile) : base(animal){
        this.tile = tile;
    }
    public override bool Initialize() {
        Plant plant = tile.plant;
        if (plant == null) return false;
        if (!plant.harvestable) return false;
        // Reject unreachable plants and plants whose actual path is significantly longer than the
        // medium search radius (e.g. 5 tiles crow-flies but 150 tiles around a chasm).
        if (!animal.nav.WithinRadius(animal.nav.PathTo(tile), MediumFindRadius)) return false;

        objectives.AddLast(new GoObjective(this, tile));
        objectives.AddLast(new HarvestObjective(this, plant));
        foreach (ItemQuantity output in plant.plantType.products){
            objectives.AddLast(new DropObjective(this, output.item));
        }
        return true;
    }
}
// haultask is only for moving a specific floor stack to storage (always WOM-targeted).
public class HaulTask : Task {
    private readonly ItemStack targetStack;
    private ItemQuantity _iq;        // tracked for sub-threshold completion logging
    private int _intendedQuantity;   // what we planned to haul at Initialize (for shrinkage detection)
    public HaulTask(Animal animal, ItemStack targetStack) : base(animal){
        this.targetStack = targetStack;
    }
    public override bool Initialize() {
        if (targetStack.item == null || targetStack.quantity == 0) return false; // stale
        Item item = targetStack.item;
        Tile itemTile = World.instance.GetTileAt(targetStack.inv.x, targetStack.inv.y);
        if (itemTile == null) return false;
        // Gate the source leg too — unreachable or obscenely-winding pickup should skip the task.
        // Storage leg is already gated by FindPathToStorage's internal radius cap.
        if (!animal.nav.WithinRadius(animal.nav.PathTo(itemTile), MediumFindRadius)) return false;
        var (storagePath, storageInv) = animal.nav.FindPathToStorage(item);
        if (storagePath == null) return false;
        int available = targetStack.quantity - targetStack.resAmount;
        int quantity = Math.Min(available, storageInv.GetStorageForItem(item));
        if (quantity <= 0) return false;
        // De minimis check before reserving: GetStorageForItem already accounts for resSpace,
        // so this is safe. Avoids leaking a space reservation that would linger until stale expiry.
        if (quantity < MinHaulQuantity && quantity < available) return false; // de minimis — no reservation made yet
        // Reserve destination space so other haulers don't target the same slots
        int spaceReserved = ReserveSpace(storageInv, item, quantity);
        if (spaceReserved <= 0) return false;
        quantity = Math.Min(quantity, spaceReserved);
        _iq = new ItemQuantity(item, quantity);
        _intendedQuantity = quantity;
        FetchAndReserve(_iq, itemTile, targetStack, quantity);
        objectives.AddLast(new GoObjective(this, storagePath.tile));
        objectives.AddLast(new DeliverToInventoryObjective(this, _iq, storageInv));
        return true;
    }
    public override void Complete() {
        // Flag only hauls that shrank mid-task (decay / reservation drain) — intentional
        // stack-clearing cleanups below MinHaulQuantity are expected and don't log.
        if (objectives.Count == 0 && _iq != null && _iq.quantity < MinHaulQuantity && _iq.quantity < _intendedQuantity) {
            Debug.Log($"{animal.aName} ({animal.job.name}) tiny Haul: delivered {_iq.quantity} fen of {_iq.item.name} (intended {_intendedQuantity} — shrunk mid-task)");
        }
        base.Complete();
    }
}
// consolidatetask is only for consolidating a specific floor stack when no storage is available (always WOM-targeted).
public class ConsolidateTask : Task {
    private readonly ItemStack stack;
    private ItemQuantity _iq;
    private int _intendedQuantity;
    public ConsolidateTask(Animal animal, ItemStack stack) : base(animal) {
        this.stack = stack;
    }
    public override bool Initialize() {
        HaulInfo h = animal.nav.FindFloorConsolidation(stack);
        if (h == null) return false;
        int available = h.itemStack.quantity - h.itemStack.resAmount;
        // Reserve first because spaceReserved caps the quantity we'll actually move.
        // If de minimis fails below, Start() → Cleanup() releases this reservation.
        int spaceReserved = ReserveSpace(h.destTile.inv, h.item, h.quantity);
        if (spaceReserved <= 0) return false;
        int quantity = Math.Min(h.quantity, spaceReserved);
        // Cleanup releases the space reservation we just made before bailing.
        if (quantity < MinHaulQuantity && quantity < available) return false; // de minimis
        _iq = new ItemQuantity(h.item, quantity);
        _intendedQuantity = quantity;
        FetchAndReserve(_iq, h.itemTile, h.itemStack, quantity);
        objectives.AddLast(new DeliverObjective(this, _iq, h.destTile));
        return true;
    }
    public override void Complete() {
        if (objectives.Count == 0 && _iq != null && _iq.quantity < MinHaulQuantity && _iq.quantity < _intendedQuantity) {
            Debug.Log($"{animal.aName} ({animal.job.name}) tiny Consolidate: moved {_iq.quantity} fen of {_iq.item.name} (intended {_intendedQuantity} — shrunk mid-task)");
        }
        base.Complete();
    }
}
public class DropTask : Task {
    public DropTask(Animal animal) : base(animal){}
    public override bool Initialize(){
        List<Item> itemsToDrop = animal.inv.GetItemsList();
        if (itemsToDrop.Count == 0) { return false; }
        foreach (Item item in itemsToDrop) {
            objectives.AddLast(new DropObjective(this, item)); }
        return true;
    }
}

public class ConstructTask : Task {
    public Blueprint blueprint;
    public bool deconstructing;
    public ConstructTask(Animal animal, Blueprint bp, bool deconstructing = false) : base(animal){
        this.blueprint = bp;
        this.deconstructing = deconstructing;
    }
    public override bool Initialize() {
        if (blueprint == null || blueprint.cancelled) return false;
        // State guard (was previously implicit in FindPathsAdjacentToBlueprints filter)
        var expectedState = deconstructing ? Blueprint.BlueprintState.Deconstructing : Blueprint.BlueprintState.Constructing;
        if (blueprint.state != expectedState) return false;
        if (blueprint.WouldCauseItemsFall()) {
            if (deconstructing) WorkOrderManager.instance?.PromoteHaulsFor(blueprint);
            return false;
        }
        if (blueprint.StorageNeedsEmptying()) {
            if (deconstructing) WorkOrderManager.instance?.PromoteHaulsFor(blueprint);
            return false;
        }
        Path standPath = blueprint.structType.isTile
            ? animal.nav.PathStrictlyAdjacent(blueprint.tile)
            : animal.nav.PathToOrAdjacent(blueprint.centerTile);
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;
        objectives.AddLast(new GoObjective(this, standPath.tile));
        objectives.AddLast(new ConstructObjective(this, blueprint));
        return true;
    }
}
public class SupplyBlueprintTask : Task {
    Blueprint blueprint;
    ItemQuantity iq;
    public SupplyBlueprintTask(Animal animal, Blueprint bp) : base(animal){
        this.blueprint = bp;
    }
    public override bool Initialize() {
        if (blueprint == null || blueprint.cancelled) return false;
        if (blueprint.state != Blueprint.BlueprintState.Receiving) return false;
        Path standPath = blueprint.structType.isTile
            ? animal.nav.PathStrictlyAdjacent(blueprint.tile)
            : animal.nav.PathToOrAdjacent(blueprint.centerTile);
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;
        for (int i = 0; i < blueprint.costs.Length; i++) {
            int needed = blueprint.costs[i].quantity - blueprint.inv.Quantity(blueprint.costs[i].item);
            if (needed <= 0) continue;
            Item costItem = blueprint.costs[i].item;
            iq = new ItemQuantity(costItem, needed);
            if (!animal.inv.ContainsItem(iq)) {
                // For group-item costs (e.g. "wood"), commit to the specific leaf with the most
                // global inventory before pathfinding. This prevents the animal from collecting a
                // mix of leaf types that would then lock the blueprint to whichever leaf happens to
                // be delivered first — potentially a scarce one (e.g. 2 oak over 20 pine).
                Item supplyItem = PickSupplyLeaf(costItem);
                (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(supplyItem);
                if (itemPath == null) continue; // can't find this item — try next cost slot
                iq = new ItemQuantity(supplyItem, needed);
                FetchAndReserve(iq, itemPath.tile, stack);
            }
            objectives.AddLast(new GoObjective(this, standPath.tile));
            objectives.AddLast(new DeliverToBlueprintObjective(this, iq, blueprint));
            return true;
        }
        return false;
    }

    // Returns the leaf item of a group with the highest global inventory (most available to use).
    // If item is already a leaf, returns it unchanged.
    private static Item PickSupplyLeaf(Item item) {
        if (item.children == null) return item;
        Item best = null;
        int bestQty = -1;
        CollectBestLeaf(item, ref best, ref bestQty);
        return best ?? item; // fallback to group if tree is somehow empty (shouldn't happen)
    }
    private static void CollectBestLeaf(Item item, ref Item best, ref int bestQty) {
        if (item.children == null || item.children.Length == 0) {
            int qty = GlobalInventory.instance.Quantity(item);
            if (qty > bestQty) { bestQty = qty; best = item; }
            return;
        }
        foreach (Item child in item.children) CollectBestLeaf(child, ref best, ref bestQty);
    }
}
// Hauls fuel items to a building's internal fuel inventory (torch wood, furnace coal, etc.).
// Registered as a standing SupplyBuilding order; isActive suppresses it when fuel >= target.
public class SupplyFuelTask : Task {
    private readonly Building building;
    public SupplyFuelTask(Animal animal, Building building) : base(animal) {
        this.building = building;
    }
    public override bool Initialize() {
        var fuel = building?.reservoir;
        if (fuel == null) return false;
        int needed = fuel.capacity - fuel.Quantity();
        if (needed <= 0) return false;
        Path standPath = animal.nav.PathToOrAdjacent(building.tile);
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;
        (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(fuel.fuelItem);
        if (itemPath == null) return false;
        int available = stack.quantity - stack.resAmount;
        int qty = Math.Min(needed, available);
        if (qty <= 0) return false;
        // Reserve destination space on fuel inventory
        int spaceReserved = ReserveSpace(fuel.inv, fuel.fuelItem, qty);
        if (spaceReserved <= 0) return false;
        qty = Math.Min(qty, spaceReserved);
        if (qty < MinHaulQuantity && qty < available) return false; // de minimis
        ItemQuantity iq = new ItemQuantity(fuel.fuelItem, qty);
        FetchAndReserve(iq, itemPath.tile, stack);
        objectives.AddLast(new GoObjective(this, standPath.tile));
        objectives.AddLast(new DeliverToInventoryObjective(this, iq, fuel.inv));
        return true;
    }
}
// Repairs a structure's condition by fetching repair materials, walking to the work
// tile, and gradually ticking condition back up over several ticks. Registered as a
// WOM Maintenance order at priority 2; only menders accept it.
//
// Material cost: for every cost item in structType.costs,
//     needed = ceil(cost.quantity × RepairCostFraction × repairAmount)
// So a 0.40 repair of a furnace consumes 10% of its full build cost per item. All
// cost items are required — the mender won't start a repair it can't fully supply.
//
// A single task restores at most MaxRepairPerTask (0.40) condition. A fully-broken
// structure (condition=0) therefore needs three visits to reach 1.0 (0→0.40→0.80→1.0,
// with the last visit capped to the remaining 0.20).
public class MaintenanceTask : Task {
    public readonly Structure target;
    public readonly float startCondition;
    public readonly float repairAmount;           // how much condition this task aims to restore
    public float targetCondition => Mathf.Min(1f, startCondition + repairAmount);

    public MaintenanceTask(Animal animal, Structure target) : base(animal) {
        this.target = target;
        this.startCondition = target != null ? target.condition : 0f;
        this.repairAmount = target != null
            ? Mathf.Min(Structure.MaxRepairPerTask, 1f - target.condition)
            : 0f;
    }

    public override bool Initialize() {
        if (animal.job.name != "mender") return false;
        if (target == null || target.go == null) return false;
        if (!target.NeedsMaintenance) return false;
        if (repairAmount <= 0f) return false;
        if (target.structType.costs == null || target.structType.costs.Length == 0) return false;

        Tile workTile = target.workTile ?? target.tile;
        Path standPath = animal.nav.PathToOrAdjacent(workTile);
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;

        // Fetch each cost item. Fail if any single item is unavailable — partial visits
        // are wasteful (animal walks then can't finish) and leaves reserved materials stranded.
        foreach (ItemQuantity cost in target.structType.costs) {
            int needed = Mathf.CeilToInt(cost.quantity * Structure.RepairCostFraction * repairAmount);
            if (needed <= 0) continue;

            Item costItem = cost.item;
            Item supplyItem = costItem.IsGroup ? PickMaintenanceSupplyLeaf(costItem) : costItem;
            (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(supplyItem);
            if (itemPath == null || stack == null) return false;
            int available = stack.quantity - stack.resAmount;
            if (available < needed) return false;

            ItemQuantity iq = new ItemQuantity(supplyItem, needed);
            FetchAndReserve(iq, itemPath.tile, stack);
        }

        objectives.AddLast(new GoObjective(this, standPath.tile));
        objectives.AddLast(new MaintenanceObjective(this));
        return true;
    }

    // Same logic as SupplyBlueprintTask.PickSupplyLeaf — highest-global-inventory leaf wins.
    // Duplicated locally (rather than exposing PickSupplyLeaf) because MaintenanceTask lives in
    // the same file and SupplyBlueprintTask's helper is private. If a third caller appears,
    // lift it to a shared static.
    private static Item PickMaintenanceSupplyLeaf(Item item) {
        if (item.children == null || item.children.Length == 0) return item;
        Item best = null;
        int bestQty = -1;
        Collect(item, ref best, ref bestQty);
        return best ?? item;
    }
    private static void Collect(Item item, ref Item best, ref int bestQty) {
        if (item.children == null || item.children.Length == 0) {
            int qty = GlobalInventory.instance.Quantity(item);
            if (qty > bestQty) { bestQty = qty; best = item; }
            return;
        }
        foreach (Item child in item.children) Collect(child, ref best, ref bestQty);
    }
}
// Travel duration for market journeys: one quarter of a game day.
// The market is off-screen at the left world edge; merchants walk there and disappear
// while "travelling to/from market", then reappear when the trip is complete.
public class HaulToMarketTask : Task {
    // Persisted for save/load so a mid-transit merchant can reconstruct the delivery
    // tail on load (see resume constructor below). Set by the normal Initialize once
    // an item + quantity has been chosen.
    public ItemQuantity iq;

    // ── Opportunistic return-leg pickup (piggyback) ──────────────────────────
    // If the market simultaneously needs items delivered AND has excess to haul away,
    // a single merchant does both in one round trip: outbound delivery, then receive
    // excess at market, return with goods, deliver into home storage. The pickup is
    // planned at Initialize time and reserved up-front; if no viable pickup exists
    // the task behaves identically to the pre-piggyback HaulToMarketTask.
    // pickupIq / pickupStorageTile are null when no pickup is planned.
    public ItemQuantity pickupIq;
    public Tile pickupStorageTile;
    // Claimed HaulFromMarket WOM order — reserved in TryAppendPickup so a second
    // merchant doesn't race us, released in Cleanup override.
    private WorkOrderManager.WorkOrder pickupOrder;

    // True once the merchant has already delivered to market and is on the return leg.
    // Distinguishes market-deliver from the piggyback home-deliver via TargetInv, so
    // this stays correct even when the queue contains two DeliverToInventoryObjectives.
    public bool IsReturnLeg {
        get {
            Inventory marketInv = MarketBuilding.instance?.storage;
            if (marketInv == null) return true; // market demolished — no way back to a pre-deliver state
            if (currentObjective is DeliverToInventoryObjective cd && cd.TargetInv == marketInv) return false;
            foreach (var o in RemainingObjectives())
                if (o is DeliverToInventoryObjective d && d.TargetInv == marketInv) return false;
            return true;
        }
    }

    // True once the piggyback pickup has been received into the animal's inventory
    // (i.e. the ReceiveFromInventoryObjective has completed). Used by SaveSystem to
    // decide whether a mid-return-travel save should be persisted as a HaulFromMarket
    // descriptor (carrying goods home) or a plain HaulToMarket return descriptor.
    public bool PickupReceived {
        get {
            if (pickupIq == null) return false;
            if (currentObjective is ReceiveFromInventoryObjective) return false;
            foreach (var o in RemainingObjectives())
                if (o is ReceiveFromInventoryObjective) return false;
            return true;
        }
    }

    // Resume-mode flag — when true, Initialize skips gameplay gates (Eepness,
    // food fetch, market path, item search) because the task is being re-created
    // for a merchant already mid-journey. The merchant's travel progress lives
    // on animal.workProgress, restored by Animal.Start() after task.Start().
    private readonly bool isResume;
    private readonly bool resumeReturnLeg;

    public HaulToMarketTask(Animal animal) : base(animal) {}

    // Resume constructor. Called from Animal.Start() when a save records the
    // merchant was mid-transit on a HaulToMarket. `returnLeg = true` means the
    // merchant has already delivered and is on the second (homeward) TravelingObjective;
    // false means still outbound with goods in inventory.
    // Note: piggyback pickup state is never resumed — the pickup descriptor (when received)
    // is saved as a HaulFromMarket entry instead, which routes to HaulFromMarketTask's
    // resume constructor. See SaveSystem.GatherAnimal / SPEC-trading.md save-load mapping.
    public HaulToMarketTask(Animal animal, ItemQuantity iq, bool returnLeg) : base(animal) {
        this.iq              = iq;
        this.isResume        = true;
        this.resumeReturnLeg = returnLeg;
    }

    public override bool Initialize() {
        if (isResume) return InitializeResume();

        // Market trips have a stricter eep gate than the general night-sleep threshold —
        // a merchant who leaves close to the threshold could dip into efficiency-loss
        // territory mid-transit and arrive useless at the far side.
        if (animal.eeping.Eepness() < 0.75f) return false;
        if (MarketBuilding.instance == null) return false;
        Inventory marketInv = MarketBuilding.instance.storage;
        if (marketInv == null) return false;
        // Any reachable x=0 tile is a valid portal — not just the market building's own tile.
        Path marketPath = animal.nav.FindMarketPath();
        if (marketPath == null) return false;
        Tile marketTile = marketPath.tile;

        foreach (var kvp in marketInv.targets) {
            Item item = kvp.Key;
            // Targets on groups are ignored — UI hides them and model skips them.
            if (item.IsGroup) continue;
            int quantityNeeded = kvp.Value - marketInv.Quantity(item);
            if (quantityNeeded <= 0) continue;
            if (marketInv.allowed[item.id] == false) continue;

            var (itemPath, stack) = animal.nav.FindPathItemStack(item);
            if (itemPath == null || stack == null) continue;

            int firstAvail = stack.quantity - stack.resAmount;
            if (firstAvail <= 0) continue;
            // Reserve market space for the full amount needed so FetchObjective can aggregate
            // from multiple stacks in one trip, rather than one small trip per stack.
            int spaceReserved = ReserveSpace(marketInv, item, quantityNeeded);
            if (spaceReserved <= 0) continue;
            if (spaceReserved < MinMarketHaul(item)) { UndoLastSpaceReservation(); continue; }
            this.iq = new ItemQuantity(item, spaceReserved);
            // Pre-reserve only the nearest stack; FetchObjective reserves additional stacks
            // as it visits them until iq.quantity is gathered.
            FetchAndReserve(iq, itemPath.tile, stack, firstAvail);
            // Walk to portal → travel to market → deliver → [optional pickup tail] → travel back.
            // If TryAppendPickup succeeds it splices in [Receive → Travel → GoStorage → DeliverHome],
            // otherwise we just append the return travel and the merchant reappears at x=0 idle.
            objectives.AddLast(new GoObjective(this, marketTile));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, marketInv));
            float extraGround = 0f;
            if (TryAppendPickup(marketInv)) {
                // Piggyback adds a portal→storage walk at trip end; budget it in the food fetch.
                // Over-estimating slightly (WalkToPortalSeconds) keeps it simple and safe.
                extraGround = WalkToPortalSeconds;
            } else {
                objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            }
            // transitTicks = per-leg; PrependFoodFetchForMarketJourney doubles internally for round trip.
            return PrependFoodFetchForMarketJourney(MarketTransitTicks, extraGround);
        }
        return false;
    }

    // Attempts to extend the current delivery queue with an opportunistic pickup from
    // the same market visit. On success: reserves the market source stack + home storage
    // space, claims the HaulFromMarket WOM order, populates pickupIq/pickupStorageTile,
    // and appends [ReceiveFromInventory → Travel(return) → Go(storage) → DeliverHome]
    // to the objective queue. On failure: no mutations, caller should append its own
    // plain return TravelingObjective.
    private bool TryAppendPickup(Inventory marketInv) {
        if (marketInv.targets == null) return false;

        // We scan all market targets for an item with (excess > 0) where we can secure a
        // stack reservation, storage space, and reachable home storage. The WOM
        // HaulFromMarket order is claimed only as a nicety (so a parallel pure-pickup
        // merchant doesn't duplicate this excess) — its absence or prior reservation
        // does NOT block piggyback, because stack-level `resAmount` already prevents
        // double-booking of the physical items.
        Item   chosenItem        = null;
        int    chosenQty         = 0;
        ItemStack chosenStack    = null;
        Tile   chosenStorageTile = null;
        Inventory chosenStorage  = null;

        foreach (var kvp in marketInv.targets) {
            Item item = kvp.Key;
            if (item.IsGroup) continue; // targets on groups are ignored (see SPEC-trading: market targets are leaf-only)
            int excess = marketInv.Quantity(item) - kvp.Value;
            if (excess <= 0) continue;

            ItemStack stack = marketInv.GetItemStack(item);
            if (stack == null) continue;
            int avail = stack.quantity - stack.resAmount;
            if (avail <= 0) continue;

            // Pick the storage with the most free space so the full piggyback payload lands in one
            // drop-off — a near-full nearest storage would cap spaceReserved below MinMarketHaul.
            var (storagePath, storageInv) = animal.nav.FindPathToStorageMostSpace(item, minSpace: MinMarketHaul(item));
            if (storagePath == null) continue;

            int qty = Math.Min(excess, avail);
            if (qty < MinMarketHaul(item)) continue;

            chosenItem        = item;
            chosenQty         = qty;
            chosenStack       = stack;
            chosenStorageTile = storagePath.tile;
            chosenStorage     = storageInv;
            break;
        }

        if (chosenItem == null) return false;

        // Reserve storage space now (may be smaller than chosenQty if space tightened).
        int spaceReserved = ReserveSpace(chosenStorage, chosenItem, chosenQty);
        if (spaceReserved <= 0) return false;
        int finalQty = Math.Min(chosenQty, spaceReserved);
        if (finalQty < MinMarketHaul(chosenItem)) {
            UndoLastSpaceReservation();
            return false;
        }

        this.pickupIq          = new ItemQuantity(chosenItem, finalQty);
        this.pickupStorageTile = chosenStorageTile;
        ReserveStack(chosenStack, finalQty);

        // Opportunistically claim the HaulFromMarket WOM order if one exists and is free.
        // If the order is missing or already reserved by another merchant, we proceed anyway
        // — stack-level reservation is the real race guard. Cleanup only unreserves when we
        // did reserve (pickupOrder is set).
        WorkOrderManager wom = WorkOrderManager.instance;
        WorkOrderManager.WorkOrder order = wom?.FindMarketHaulFromOrder(marketInv);
        if (order != null && order.res.Available()) {
            order.res.Reserve(animal.aName);
            this.pickupOrder = order;
        }

        objectives.AddLast(new ReceiveFromInventoryObjective(this, pickupIq, marketInv));
        objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        objectives.AddLast(new GoObjective(this, pickupStorageTile));
        objectives.AddLast(new DeliverToInventoryObjective(this, pickupIq, chosenStorage));
        return true;
    }

    public override void Cleanup() {
        // Release the claimed HaulFromMarket order before base.Cleanup() clears objectives.
        pickupOrder?.res.Unreserve();
        pickupOrder = null;
        base.Cleanup();
    }

    // Rebuilds the tail of the task for a merchant loaded mid-transit. Two shapes:
    //   outbound: [Travel(remaining) → DeliverToInventory → Travel(return)]
    //   return:   [Travel(remaining)]                  (items already delivered)
    // Returns false only if the market has been demolished between save and load,
    // in which case Animal.Start() falls back to ResumeTravelTask.
    private bool InitializeResume() {
        if (iq?.item == null) return false;
        if (MarketBuilding.instance?.storage == null) return false;
        Inventory marketInv = MarketBuilding.instance.storage;

        if (resumeReturnLeg) {
            // Items already delivered pre-save — no space reservation, no delivery step.
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        } else {
            // Re-issue the destination space reservation. Save/load invariant: reservations
            // are never persisted — on load, ItemStacks are freshly constructed (resAmount/
            // resSpace = 0) and Reservables default to reserved=0. Non-resumable tasks are
            // implicitly aborted at save, which is safe because their reservations vanish
            // with the (recreated) world state. Only resumable tasks need to re-reserve here.
            // Any new resumable task type MUST re-make every reservation its normal
            // Initialize() made, or the task will run without backing reservations.
            ReserveSpace(marketInv, iq.item, iq.quantity);
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, marketInv));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        }
        return true;
    }
}
public class HaulFromMarketTask : Task {
    // Persisted for save/load so a mid-transit merchant can reconstruct the
    // receive/deliver tail on load. Set by the normal Initialize.
    public ItemQuantity iq;
    public Tile storageTile;

    // True once the merchant has already fetched from market and is past the
    // ReceiveFromInventoryObjective — i.e. on the return leg carrying goods home.
    // Mirrors MerchantJourneyDisplay's leg-detection so behaviour stays consistent.
    public bool IsReturnLeg => !RemainingObjectives().Any(o => o is ReceiveFromInventoryObjective)
                            && !(currentObjective is ReceiveFromInventoryObjective);

    // See HaulToMarketTask for isResume rationale. Travel progress itself lives on
    // animal.workProgress; Animal.Start() restores it after task.Start() zeroes it.
    private readonly bool isResume;
    private readonly bool resumeReturnLeg;

    public HaulFromMarketTask(Animal animal) : base(animal) {}

    // Resume constructor. `returnLeg = true` means the merchant has already visited
    // the market and is heading home with goods; false means still outbound.
    public HaulFromMarketTask(Animal animal, ItemQuantity iq, Tile storageTile, bool returnLeg) : base(animal) {
        this.iq              = iq;
        this.storageTile     = storageTile;
        this.isResume        = true;
        this.resumeReturnLeg = returnLeg;
    }

    public override bool Initialize() {
        if (isResume) return InitializeResume();

        if (animal.eeping.Eepness() < 0.75f) return false; // see HaulToMarketTask for rationale
        if (MarketBuilding.instance == null) return false;
        Inventory marketInv = MarketBuilding.instance.storage;
        if (marketInv == null) return false;
        // Any reachable x=0 tile is a valid portal — not just the market building's own tile.
        Path marketPath = animal.nav.FindMarketPath();
        if (marketPath == null) return false;
        Tile marketTile = marketPath.tile;

        foreach (var kvp in marketInv.targets) {
            Item item = kvp.Key;
            if (item.IsGroup) continue; // targets on groups are ignored (see SPEC-trading: market targets are leaf-only)
            int excess = marketInv.Quantity(item) - kvp.Value;
            if (excess <= 0) continue;

            ItemStack stack = marketInv.GetItemStack(item);
            if (stack == null) continue;
            // Pick the storage with the most free space so the full excess lands in one drop-off —
            // a near-full nearest storage would cap spaceReserved below MinMarketHaul.
            var (storagePath, storageInv) = animal.nav.FindPathToStorageMostSpace(item, minSpace: MinMarketHaul(item));
            if (storagePath == null) continue;

            int qty = Math.Min(excess, stack.quantity - stack.resAmount);
            if (qty <= 0) continue;
            int spaceReserved = ReserveSpace(storageInv, item, qty);
            if (spaceReserved <= 0) continue;
            qty = Math.Min(qty, spaceReserved);
            if (qty < MinMarketHaul(item)) { UndoLastSpaceReservation(); continue; }
            this.iq          = new ItemQuantity(item, qty);
            this.storageTile = storagePath.tile;
            // Reserve the items in the market now so no other task double-counts them.
            ReserveStack(stack, qty);
            // Walk to portal → travel to market → receive goods → travel back → deliver to storage.
            objectives.AddLast(new GoObjective(this, marketTile));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new ReceiveFromInventoryObjective(this, iq, marketInv));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new GoObjective(this, storageTile));
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, storageInv));
            // transitTicks = per-leg; PrependFoodFetchForMarketJourney doubles internally for round trip.
            return PrependFoodFetchForMarketJourney(MarketTransitTicks);
        }
        return false;
    }

    // Rebuilds the task tail for a merchant loaded mid-transit. Two shapes:
    //   return leg:   [Travel(remaining) → Go(storage) → DeliverToInventory]
    //   outbound leg: [Travel(remaining) → ReceiveFromInventory → Travel(return) →
    //                   Go(storage) → DeliverToInventory]
    // The home storage inventory is resolved from the saved tile each load —
    // if the storage building or market has been demolished since save,
    // returns false so Animal.Start() falls back to ResumeTravelTask.
    private bool InitializeResume() {
        if (iq?.item == null || storageTile == null) return false;
        if (MarketBuilding.instance?.storage == null) return false;
        Inventory marketInv  = MarketBuilding.instance.storage;
        Inventory storageInv = storageTile.building?.storage;
        if (storageInv == null) return false;

        // Both legs still need to deliver into home storage — reserve space there now
        // so other haul tasks don't eat the slot while we're still travelling.
        ReserveSpace(storageInv, iq.item, iq.quantity);

        if (resumeReturnLeg) {
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new GoObjective(this, storageTile));
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, storageInv));
        } else {
            // Outbound — re-reserve the source stack eagerly so a competing merchant
            // can't drain the market while we're still travelling toward it.
            // Stack may have shrunk since save; ReceiveFromInventoryObjective tolerates
            // shortfall (takes min(available, iq.quantity), only Fails on empty).
            ItemStack stack = marketInv.GetItemStack(iq.item);
            if (stack != null) ReserveStack(stack, Math.Min(iq.quantity, stack.quantity - stack.resAmount));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new ReceiveFromInventoryObjective(this, iq, marketInv));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new GoObjective(this, storageTile));
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, storageInv));
        }
        return true;
    }
}
// Created on load to finish an interrupted TravelingObjective.
// After completion the animal becomes idle at x=0 and WOM assigns fresh work.
public class ResumeTravelTask : Task {
    private readonly int remainingTicks;
    public ResumeTravelTask(Animal animal, int remainingTicks) : base(animal) {
        this.remainingTicks = remainingTicks;
    }
    public override bool Initialize() {
        if (remainingTicks <= 0) return false;
        objectives.AddLast(new TravelingObjective(this, remainingTicks));
        return true;
    }
}
public class ResearchTask : Task {
    private readonly Building _lab;
    // Which tech this scientist is working on, chosen by PickStudyTarget at task creation.
    // -1 means nothing to do (scientist will idle through the work loop with no effect).
    public int studyTargetId = -1;
    public ResearchTask(Animal animal, Building lab) : base(animal) { _lab = lab; }
    public override bool Initialize() {
        if (_lab == null) return false;
        Path p = animal.nav.PathToOrAdjacent(_lab.tile);
        if (!animal.nav.WithinRadius(p, MediumFindRadius)) return false;

        // Optional book borrow: if a matching book exists somewhere reachable, queue a
        // fetch into bookSlotInv before the research, and a return (unequip + drop to
        // shelf) after. The 3× progress multiplier is applied later in HandleWorking
        // based on bookSlotInv contents — this method just sets up the fetch/return tail.
        // softFetch on the FetchObjective: if the book becomes unavailable between this
        // check and execution (e.g. another scientist grabs it), the scientist proceeds
        // to research without the multiplier instead of failing the whole task.
        Item book = null;
        if (Db.bookItemIdByTechId.TryGetValue(studyTargetId, out int bookItemId)) {
            Item candidate = Db.items[bookItemId];
            (Path bookPath, ItemStack bookStack) = animal.nav.FindPathItemStack(candidate);
            if (bookPath != null) {
                book = candidate;
                ReserveStack(bookStack, 100);
                objectives.AddLast(new FetchObjective(this, new ItemQuantity(book, 100), bookPath.tile, animal.bookSlotInv, softFetch: true, sourceInv: bookStack.inv));
            }
        }

        objectives.AddLast(new GoObjective(this, p.tile));
        objectives.AddLast(new ResearchObjective(this));

        if (book != null) {
            // After research: move book from slot to main inv, then deliver to a shelf
            // (DropObjective finds an accepting storage inv — books only fit in bookshelves).
            // If shelves are full, DropObjective falls back to the floor; haulers will then
            // move it to a shelf when one frees up.
            objectives.AddLast(new UnequipObjective(this, animal.bookSlotInv));
            objectives.AddLast(new DropObjective(this, book));
        }
        return true;
    }

    // If the task ends with a book still equipped (Fail mid-flight before the return objectives
    // ran), dump it from the slot to the animal's tile so it's not orphaned in the equip slot.
    // Floor drop creates a haul order; haulers will move it to a shelf eventually.
    public override void Cleanup() {
        if (animal.bookSlotInv != null && !animal.bookSlotInv.IsEmpty()) {
            var stack = animal.bookSlotInv.itemStacks[0];
            Item book = stack.item;
            int qty = stack.quantity;
            animal.Unequip(animal.bookSlotInv);
            animal.DropItem(book, qty);
        }
        base.Cleanup();
    }
}

// ── Leisure tasks ───────────────────────────────────────────────────

public class ChatTask : Task {
    public Animal partner;
    public Tile myTile; // tile this animal should stand on during chat
    public ChatTask(Animal animal, Animal partner) : base(animal) {
        this.partner = partner;
    }
    public override bool Initialize() {
        Tile partnerTile = partner.TileHere();
        if (partnerTile == null) return false;

        // Detect whether we're the initiator or the recruited partner.
        // The recruited partner's Initialize() is called second, after the initiator
        // already assigned us a ChatTask pointing back at them.
        bool isInitiator = !(partner.task is ChatTask ct && ct.partner == animal);

        if (isInitiator) {
            // Find a pair of horizontally adjacent tiles so the animals stand side by side.
            // Partner stays on (or near) their current tile; initiator takes the neighbor.
            Tile initiatorTile = FindAdjacentChatTile(partnerTile);
            if (initiatorTile != null) {
                myTile = initiatorTile;
                // Recruit partner — give them a reciprocal ChatTask
                if (partner.task == null && partner.state == Animal.AnimalState.Idle) {
                    var partnerChat = new ChatTask(partner, animal);
                    partnerChat.myTile = partnerTile;
                    partner.task = partnerChat;
                    partner.task.Start();
                }
                objectives.AddLast(new GoObjective(this, initiatorTile));
            } else {
                // Fallback: no horizontal neighbor available, walk to partner's tile
                if (!animal.nav.WithinRadius(animal.nav.PathTo(partnerTile), MediumFindRadius)) return false;
                if (partner.task == null && partner.state == Animal.AnimalState.Idle) {
                    partner.task = new ChatTask(partner, animal);
                    partner.task.Start();
                }
                objectives.AddLast(new GoObjective(this, partnerTile));
            }
        } else if (myTile != null && myTile != animal.TileHere()) {
            // Partner was assigned a tile by the initiator — walk there if not already on it
            objectives.AddLast(new GoObjective(this, myTile));
        }

        objectives.AddLast(new ChatObjective(this, partner, 10));
        return true;
    }

    // Try horizontal neighbors of partnerTile (left and right). Return the one the
    // initiator can path to, preferring the shorter path. Returns null if neither works.
    private Tile FindAdjacentChatTile(Tile partnerTile) {
        World w = animal.world;
        Tile left  = w.GetTileAt(partnerTile.x - 1, partnerTile.y);
        Tile right = w.GetTileAt(partnerTile.x + 1, partnerTile.y);

        Path pathLeft  = (left  != null && left.node.standable)  ? animal.nav.PathTo(left)  : null;
        Path pathRight = (right != null && right.node.standable) ? animal.nav.PathTo(right) : null;

        // Reject paths that blow past the medium radius — a chat shouldn't require a cross-map hike.
        if (!animal.nav.WithinRadius(pathLeft,  Task.MediumFindRadius)) pathLeft  = null;
        if (!animal.nav.WithinRadius(pathRight, Task.MediumFindRadius)) pathRight = null;

        if (pathLeft != null && pathRight != null)
            return pathLeft.cost <= pathRight.cost ? left : right;
        if (pathLeft != null) return left;
        if (pathRight != null) return right;
        return null;
    }
    public override void Complete() {
        // Social satisfaction is granted gradually per-tick in HandleChatting — no lump grant here.
        base.Complete();
    }
    public override void Cleanup() {
        // If partner hasn't entered the chat phase yet, force-fail them.
        // If they're already chatting, HandleChatting will detect our departure.
        Animal p = partner;
        partner = null;
        if (p?.task is ChatTask pt && pt.partner == animal) {
            bool partnerChatting = p.state == Animal.AnimalState.Leisuring
                && p.task.currentObjective is ChatObjective;
            if (!partnerChatting) {
                pt.partner = null;
                p.task.Fail();
            }
        }
        base.Cleanup();
    }
}

// Generic "go to a leisure building and spend time there" task.
// The building dispatches its benefit on completion (fireplace → warmth, etc.).
// Adding a new leisure building: add a case in Complete() for the building name.
public class LeisureTask : Task {
    public Building building;
    public Tile seatTile; // the specific work tile this animal is heading to
    private int seatIndex = -1; // index into building.seatRes[] for per-seat reservation

    public LeisureTask(Animal animal, Building building) : base(animal) {
        this.building = building;
    }

    public override bool Initialize() {
        if (building == null || building.disabled || building.IsBroken) return false;
        Path bestPath = null;

        if (building.seatRes != null) {
            // Per-seat reservation: find the first seat that is available AND reachable within radius
            for (int i = 0; i < building.seatRes.Length; i++) {
                if (!building.seatRes[i].Available()) continue;
                Tile seat = building.WorkTileAt(i);
                if (seat == null) continue;
                Path p = animal.nav.PathTo(seat);
                if (animal.nav.WithinRadius(p, MediumFindRadius)) {
                    building.seatRes[i].Reserve(animal.aName);
                    seatIndex = i;
                    bestPath = p;
                    seatTile = seat;
                    break;
                }
            }
        } else {
            // Legacy single-res path for non-leisure buildings (shouldn't normally hit)
            if (building.res != null) {
                if (!building.res.Available()) return false;
                building.res.Reserve(animal.aName);
            }
            for (int i = 0; i < building.structType.nworkTiles.Length; i++) {
                Tile seat = building.WorkTileAt(i);
                if (seat == null) continue;
                Path p = animal.nav.PathTo(seat);
                if (animal.nav.WithinRadius(p, MediumFindRadius)) { bestPath = p; seatTile = seat; break; }
            }
        }

        // Fall back to adjacent if no direct path to any seat
        if (bestPath == null) {
            Path adj = animal.nav.PathToOrAdjacent(building.workTile);
            if (animal.nav.WithinRadius(adj, MediumFindRadius)) {
                bestPath = adj;
                seatTile = adj.tile;
            }
        }
        if (bestPath == null) {
            return false; // Start() calls Cleanup() which unreserves via seatIndex/building.res
        }

        objectives.AddLast(new GoObjective(this, bestPath.tile));
        objectives.AddLast(new LeisureObjective(this, 15));
        return true;
    }

    public override void Complete() {
        // Grant the happiness benefit for this building's leisure need
        string need = building.structType.leisureNeed;
        if (!string.IsNullOrEmpty(need)) {
            animal.happiness.NoteLeisure(need);
        } else {
            Debug.LogError($"LeisureTask.Complete: building '{building.structType.name}' has no leisureNeed");
        }

        // Social satisfaction for socialWhenShared buildings is granted per-tick in HandleLeisure.
        base.Complete();
    }

    public override void Cleanup() {
        if (seatIndex >= 0 && building.seatRes != null)
            building.seatRes[seatIndex].Unreserve();
        else if (building.res != null)
            building.res.Unreserve();
        base.Cleanup();
    }
}

// Animal grabs a fiction book from a shelf, walks to a nearby unoccupied tile, reads it for
// ReadDuration ticks gaining "reading" leisure happiness, then returns the book to a shelf.
// Spawned by Animal.TryPickLeisure when a fiction book exists in the world. Unlike LeisureTask
// this isn't tied to a building — any standable tile near the animal that no other mouse is on
// works. Mirrors ResearchTask's borrow/return pattern (M5) for consistency.
public class ReadBookTask : Task {
    private const int ReadDuration = 10; // ticks of reading (matches ChatObjective duration)
    private const int SpotSearchRadius = 5; // tile radius around the animal to look for a free spot

    public ReadBookTask(Animal animal) : base(animal) {}

    public override bool Initialize() {
        if (!Db.itemByName.TryGetValue("fiction_book", out Item fiction)) return false;

        (Path bookPath, ItemStack bookStack) = animal.nav.FindPathItemStack(fiction);
        if (bookPath == null) return false;

        Tile readTile = FindNearbyReadingTile();
        if (readTile == null) return false;

        ReserveStack(bookStack, 100);
        objectives.AddLast(new FetchObjective(this, new ItemQuantity(fiction, 100), bookPath.tile, animal.bookSlotInv, sourceInv: bookStack.inv));
        objectives.AddLast(new GoObjective(this, readTile));
        objectives.AddLast(new LeisureObjective(this, ReadDuration));
        objectives.AddLast(new UnequipObjective(this, animal.bookSlotInv));
        objectives.AddLast(new DropObjective(this, fiction));
        return true;
    }

    // No Complete override needed: reading happiness accrues per-tick in
    // AnimalStateManager.HandleLeisure (mirrors ChatTask's per-tick social grant).

    public override void Cleanup() {
        // Mirror ResearchTask: if Fail tears the task down with the book still equipped,
        // dump it to the floor at the animal's tile so a hauler re-shelves it.
        if (animal.bookSlotInv != null && !animal.bookSlotInv.IsEmpty()) {
            var stack = animal.bookSlotInv.itemStacks[0];
            Item book = stack.item;
            int qty = stack.quantity;
            animal.Unequip(animal.bookSlotInv);
            animal.DropItem(book, qty);
        }
        base.Cleanup();
    }

    // Finds any standable tile within SpotSearchRadius of the animal that no other mouse is
    // currently on, and that the animal can path to within MediumFindRadius. No reservation —
    // if two mice race for the same spot we just accept that one ends up sharing or re-picks
    // on next tick; reading itself is harmless even with overlap.
    private Tile FindNearbyReadingTile() {
        Tile here = animal.TileHere();
        if (here == null) return null;
        World w = animal.world;
        AnimalController ac = AnimalController.instance;
        for (int dx = -SpotSearchRadius; dx <= SpotSearchRadius; dx++) {
            for (int dy = -SpotSearchRadius; dy <= SpotSearchRadius; dy++) {
                Tile t = w.GetTileAt(here.x + dx, here.y + dy);
                if (t == null || t.node == null || !t.node.standable) continue;
                if (TileHasAnimal(t, ac)) continue;
                Path p = animal.nav.PathTo(t);
                if (animal.nav.WithinRadius(p, MediumFindRadius)) return t;
            }
        }
        return null;
    }

    private static bool TileHasAnimal(Tile t, AnimalController ac) {
        for (int i = 0; i < ac.na; i++) {
            Animal a = ac.animals[i];
            if (a == null) continue;
            if ((int)a.x == t.x && (int)a.y == t.y) return true;
        }
        return false;
    }
}

// ── Objectives ──────────────────────────────────────────────────────
public abstract class Objective {
    protected Task task;
    protected Animal animal;
    public Objective(Task task){
        this.task = task;
        this.animal = task.animal;
    }
    public abstract void Start();
    public virtual void OnArrival(){} // default do nothing, need to implement 
    public void Complete() {
        task.Complete();  
    }
    public void Fail(){
        task.Fail();
    }
    public virtual string GetObjectiveName() {
        return this.GetType().Name.Replace("Objective", "");
    }
    public override string ToString() {return GetObjectiveName();}
}

public class FetchObjective : Objective {
    private ItemQuantity iq;
    private Tile destination;
    private Tile sourceTile;
    private Inventory sourceInv; // which inventory to take from (storage or floor); set by Start() or caller
    private Inventory targetInv; // null = animal's main inventory; non-null = equip into that slot
    private bool softFetch; // if true: Complete (not Fail) when no path found or nothing taken; no cross-tile retry
    private Inventory Dest => targetInv ?? animal.inv;
    public FetchObjective(Task task, ItemQuantity iq, Tile sourceTile = null, Inventory targetInv = null, bool softFetch = false, Inventory sourceInv = null) : base(task) {
        this.iq = iq;
        this.sourceTile = sourceTile;
        this.sourceInv = sourceInv;
        this.targetInv = targetInv;
        this.softFetch = softFetch;
    }
    public override string GetObjectiveName() { return $"Fetch({iq.item.name})"; }
    public override void Start(){
        // Start() may be called more than once (non-soft): if the animal arrives and the stack is partially
        // exhausted, sourceTile/sourceInv are cleared and Start() re-runs to find a new source.
        if (Dest.Quantity(iq.item) >= iq.quantity){Complete(); return;}
        Path itemPath;
        if (sourceTile != null) {
            itemPath = animal.nav.PathTo(sourceTile);
        } else {
            ItemStack stack;
            (itemPath, stack) = animal.nav.FindPathItemStack(iq.item);
            if (itemPath != null) {
                task.ReserveStack(stack, iq.quantity);
                sourceTile = itemPath.tile;
                sourceInv = stack.inv;
            }
        }
        if (itemPath != null){
            destination = itemPath.tile;
            animal.nav.Navigate(itemPath);
            animal.state = Animal.AnimalState.Moving;
        } else {
            if (softFetch) { Complete(); return; }
            // If the animal already has a partial amount from a prior fetch attempt, deliver what it has
            // rather than failing and dropping everything — avoids a tight drop-and-re-fetch loop.
            // Cap iq.quantity to what we actually fetched so downstream deliver objectives don't log
            // spurious partial-fill mismatches (they compare moved against iq.quantity).
            if (Dest.Quantity(iq.item) > 0) {
                int have = Dest.Quantity(iq.item);
                Debug.Log($"{animal.aName} ({animal.job.name}) partial fetch: has {have}/{iq.quantity} {iq.item.name}, no more found — capping deliver target");
                iq.quantity = have;
                Complete(); return;
            }
            Fail(); Debug.Log($"{animal.aName} ({animal.job.name}) found no path to fetch {iq.item.name} at ({(int)animal.x},{(int)animal.y})");
        }
    }
    public override void OnArrival(){
        int needed = iq.quantity - Dest.Quantity(iq.item); // how much more we still need
        if (needed <= 0) { Complete(); return; }
        // Use tracked sourceInv (storage or floor); fall back to tile.inv for floor items
        Inventory src = sourceInv ?? animal.TileHere()?.inv;
        if (src == null) {
            if (softFetch) { Complete(); return; }
            Fail(); Debug.Log($"{animal.aName} FetchObjective: no source inv at ({(int)animal.x},{(int)animal.y})"); return;
        }
        // Source torn down between Initialize and arrival — usually decay silently drained the
        // reserved stack (ItemStack.AddItem zeros resAmount when the stack empties). Log what
        // we reserved, not the raw request, so "expected vs gone" is visible.
        if (src.destroyed) {
            int expected = task.ReservedAmountFromInv(src);
            Debug.LogWarning($"{animal.aName} FetchObjective: source {src.invType} '{src.displayName}' at ({src.x},{src.y}) destroyed before arrival — expected {expected} fen of {iq.item.name} (iq.quantity={iq.quantity}, needed={needed})");
            if (softFetch) { Complete(); return; }
            Fail(); return;
        }
        int amountTaken = src.MoveItemTo(Dest, iq.item, needed);
        // Clean up empty floor inventories (replicates TakeItem behavior)
        if (src.invType == Inventory.InvType.Floor && src.IsEmpty()) {
            Tile t = World.instance.GetTileAt(src.x, src.y);
            if (t != null) { src.Destroy(); t.inv = null; }
        }
        if (amountTaken == 0) {
            if (softFetch) { Complete(); return; }
            Fail(); Debug.Log($"{animal.aName} Couldn't fetch any {iq.item.name} (needed {needed} fen)"); return;
        }
        // softFetch: always complete after one tile visit (CraftTask.Complete handles retry logic)
        // targetInv != null means equip slot (food/tool) — partial fills are fine there, don't retry
        if (softFetch || Dest.Quantity(iq.item) >= iq.quantity || Dest.GetStorageForItem(iq.item) < 5 || targetInv != null) {
            Complete();
        } else {
            sourceTile = null; // source tile may be exhausted; search for another
            sourceInv = null;
            Start();
        }
    }
}
public class DeliverObjective : Objective { // navigates and drops off item
    private ItemQuantity iq;
    private Tile destination;
    public DeliverObjective(Task task, ItemQuantity iq, Tile destination) : base(task) {
        this.iq = iq;
        this.destination = destination;
    }
    public override void Start(){
        Path path = animal.nav.PathTo(destination);
        if (path != null){
            animal.nav.Navigate(path);
            animal.state = Animal.AnimalState.Moving;
        } else {Fail();}
    }
    public override void OnArrival(){
        if (animal.inv.Quantity(iq.item) == 0){ Debug.Log($"{animal.aName} DeliverObjective: missing {iq.item.name} to deliver to ({destination.x},{destination.y})"); Fail(); return; }
        int moved = animal.DropItem(iq);  // drops the amount you have up to iq.quantity. if have less, just drops that.
        if (moved < iq.quantity)
            Debug.Log($"{animal.aName} ({animal.job.name}) [{task}] at ({(int)animal.x},{(int)animal.y}): partial/failed drop of {iq.item.name} — moved {moved}/{iq.quantity} to ({destination.x},{destination.y})");
        Complete();
    }
}
public class DeliverToBlueprintObjective : Objective { // always queued after GoObjective; Start() runs immediately once the animal is in position
    private ItemQuantity iq;
    private Blueprint blueprint;
    public DeliverToBlueprintObjective(Task task, ItemQuantity iq, Blueprint blueprint) : base(task) {
        this.iq = iq;
        this.blueprint = blueprint;
    }
    public override void Start(){
        if (blueprint == null || blueprint.cancelled) { Fail(); return; }
        if (animal.inv.Quantity(iq.item) > 0) {
            int needed = 0;
            // Use MatchesItem so a leaf iq.item (e.g. "pine") matches a group cost.item (e.g. "wood")
            // that hasn't been locked yet, as well as the exact match once it is locked.
            foreach (var cost in blueprint.costs)
                if (Inventory.MatchesItem(iq.item, cost.item)) { needed = cost.quantity - blueprint.inv.Quantity(cost.item); break; }
            animal.inv.MoveItemTo(blueprint.inv, iq.item, Math.Min(animal.inv.Quantity(iq.item), needed));
            blueprint.LockGroupCostsAfterDelivery();
            if (blueprint.IsFullyDelivered()) {
                blueprint.state = Blueprint.BlueprintState.Constructing;
                WorkOrderManager.instance?.PromoteToConstruct(blueprint);
            }
            Complete();
        } else {
            Debug.Log($"{animal.aName} could not deliver {iq.item.name} to blueprint at ({blueprint.x},{blueprint.y})");
            Fail();
        }
    }
}
// Generic delivery objective: moves items from the animal's inventory into any target inventory.
// Always queued after GoObjective so the animal is already in position when Start() runs.
public class DeliverToInventoryObjective : Objective {
    private ItemQuantity iq;
    // Target inventory exposed read-only so tasks can introspect their queue
    // (e.g. HaulToMarketTask distinguishes market-deliver vs home-deliver for phase detection).
    public Inventory TargetInv { get; }
    public DeliverToInventoryObjective(Task task, ItemQuantity iq, Inventory targetInv) : base(task) {
        this.iq = iq;
        this.TargetInv = targetInv;
    }
    public override string GetObjectiveName() { return $"DeliverTo({iq.item.name}>{TargetInv.displayName})"; }
    public override void Start(){
        if (TargetInv == null) { Fail(); return; }
        int have = animal.inv.Quantity(iq.item);
        if (have <= 0) { Debug.Log($"{animal.aName} DeliverToInventoryObjective: missing {iq.item.name}"); Fail(); return; }
        int toDeliver = Math.Min(have, iq.quantity);
        int moved = animal.inv.MoveItemTo(TargetInv, iq.item, toDeliver);
        if (moved < toDeliver) {
            // Diagnostic dump: destination-side partial fill means FreeSpace shrunk between reserve and
            // delivery despite the reservation system. Dump every stack's reservation state so we can
            // tell whether our resSpace was clamped, eaten by another task, or never set.
            var sb = new System.Text.StringBuilder();
            sb.Append($"{animal.aName} delivered {moved}/{toDeliver} {iq.item.name} to {TargetInv.displayName} — partial fill\n");
            sb.Append($"  task={task} iq.quantity={iq.quantity} animal.inv.Quantity={have}\n");
            sb.Append($"  TargetInv stacks ({TargetInv.invType} at ({TargetInv.x},{TargetInv.y})):\n");
            for (int i = 0; i < TargetInv.nStacks; i++) {
                var s = TargetInv.itemStacks[i];
                string resOwner = s.resSpaceTask == this.task ? "OURS" : (s.resSpaceTask?.ToString() ?? "null");
                sb.Append($"    [{i}] item={s.item?.name ?? "null"} qty={s.quantity}/{s.stackSize} resAmount={s.resAmount} resSpace={s.resSpace} resSpaceItem={s.resSpaceItem?.name ?? "null"} resSpaceTask={resOwner}\n");
            }
            Debug.LogWarning(sb.ToString());
        }
        Complete();
    }
}
// Moves items from a source inventory directly into the animal's inventory.
// Always queued after a TravelingObjective — the animal is conceptually "at" the source
// (the market), so no navigation is needed. Symmetric counterpart to DeliverToInventoryObjective.
public class ReceiveFromInventoryObjective : Objective {
    private ItemQuantity iq;
    private Inventory sourceInv;
    public ReceiveFromInventoryObjective(Task task, ItemQuantity iq, Inventory sourceInv) : base(task) {
        this.iq = iq;
        this.sourceInv = sourceInv;
    }
    public override string GetObjectiveName() => $"ReceiveFrom({iq.item.name})";
    public override void Start() {
        if (sourceInv == null) { Fail(); return; }
        ItemStack stack = sourceInv.GetItemStack(iq.item);
        int available = stack != null ? stack.quantity - stack.resAmount : 0;
        if (available <= 0) { Debug.Log($"{animal.aName} ReceiveFromInventory: no {iq.item.name} available in source"); Fail(); return; }
        int toReceive = Math.Min(available, iq.quantity);
        int moved = sourceInv.MoveItemTo(animal.inv, iq.item, toReceive);
        if (moved <= 0) { Debug.Log($"{animal.aName} ReceiveFromInventory: couldn't move {iq.item.name} to animal inv"); Fail(); return; }
        Complete();
    }
}
// Hides the animal and waits for durationTicks before reappearing — representing travel time
// to/from the off-screen market. AnimalStateManager.HandleTraveling drives the timer.
public class TravelingObjective : Objective {
    public readonly int durationTicks;
    public TravelingObjective(Task task, int durationTicks) : base(task) {
        this.durationTicks = durationTicks;
    }
    public override string GetObjectiveName() => $"Traveling({durationTicks}t)";
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Traveling;
        animal.go.SetActive(false);
    }
}
public class DropObjective : Objective {
    private Item item;
    private Tile destination;
    private Inventory targetInv; // null = drop on floor
    public DropObjective(Task task, Item item) : base(task) {
        this.item = item;
    }
    public override void Start(){
        if (animal.inv.Quantity(item) == 0) {Complete(); return;}
        int qty = animal.inv.Quantity(item);
        var (dropPath, storageInv) = animal.nav.FindPathToDropTarget(item, qty);
        if (dropPath != null){
            targetInv = storageInv;
            destination = dropPath.tile;
            // Reserve destination space (best-effort — don't fail if no space, floor drop is always possible)
            Inventory destInv = storageInv ?? dropPath.tile.EnsureFloorInventory();
            task.ReserveSpace(destInv, item, qty);
            animal.nav.Navigate(dropPath);
            animal.state = Animal.AnimalState.Moving;
        } else {
            // No reachable drop target (boxed in, all neighbours full, etc.) — warn and
            // back off for 3s so ChooseTask falls through to other branches instead of
            // respawning DropTask every tick. Warning (not error) because this is a
            // recoverable, expected-at-edge-of-colony condition.
            Debug.LogWarning($"{animal.aName} ({animal.job.name}) can't find a place to drop {item.name} at ({(int)animal.x},{(int)animal.y}) — retrying in 3s");
            animal.dropCooldownUntil = World.instance.timer + 3f;
            Fail();
        }
    }
    public override void OnArrival(){
        if (targetInv != null && targetInv.GetStorageForItem(item) > 0) {
            animal.inv.MoveItemTo(targetInv, item, animal.inv.Quantity(item));
        } else {
            animal.DropItem(item);
        }
        Complete();
    }
}

// Moves the contents of an equip slot back to the animal's main inventory and completes.
// Used as a step before returning a borrowed item (e.g. a book) to its storage location:
// callers typically queue this immediately before a DropObjective for the same item.
// No-op if the slot is empty.
public class UnequipObjective : Objective {
    private Inventory slot;
    public UnequipObjective(Task task, Inventory slot) : base(task) { this.slot = slot; }
    public override void Start() {
        animal.Unequip(slot);
        Complete();
    }
}

public class GoObjective : Objective {
    private Tile destination;
    public GoObjective(Task task, Tile destination) : base(task) {
        this.destination = destination;
    }
    public override void Start(){
        if (animal.TileHere() == destination) {Complete(); return;}
        Path path = animal.nav.PathTo(destination);
        if (path != null){
            animal.nav.Navigate(path);
            animal.state = Animal.AnimalState.Moving;
        } else {Fail();}
    }
    public override void OnArrival(){ Complete(); }
    public override string GetObjectiveName() {
        if (destination != null) return $"Go ({destination.x}, {destination.y})";
        return "Go";
    }
}
public class WorkObjective : Objective {
    private Recipe recipe;
    public WorkObjective(Task task, Recipe recipe) : base(task) {
        this.recipe = recipe;
    }
    public override void Start(){
        // TODO: check if you're actually at a workplace!
        if (!animal.inv.ContainsItems(recipe.inputs)) {
            Debug.Log($"{animal.aName} WorkObjective: missing inputs for {recipe.description}, failing");
            Fail(); return;
        }
        animal.workProgress = 0f;
        animal.recipe = recipe;
        animal.state = Animal.AnimalState.Working;
    }
    // animalstatemanager.HandleWorking will call task.Complete() when it's done!
}
public class ConstructObjective : Objective {
    public Blueprint blueprint;
    public ConstructObjective(Task task, Blueprint blueprint) : base(task) {
        this.blueprint = blueprint;
    }
    public override void Start(){
        if (blueprint == null || blueprint.cancelled) { Fail(); return; }
        animal.state = Animal.AnimalState.Working;
        // AnimalStateManager.HandleWorking calls task.Complete() when construction finishes.
    }
}
public class EepObjective : Objective {
    public EepObjective(Task task): base(task){}
    public override void Start(){
        animal.state = Animal.AnimalState.Eeping;
        // AnimalStateManager.HandleEeping calls task.Complete() when sleep finishes.
    }
}
public class HarvestObjective : Objective {
    private Plant plant;
    public HarvestObjective(Task task, Plant plant) : base(task) {
        this.plant = plant;
    }
    public override void Start(){
        if (plant == null || !plant.harvestable) { Fail(); return; }
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Working;
        // AnimalStateManager.HandleWorking calls task.Complete() when harvesting finishes.
    }
}

public class ResearchObjective : Objective {
    public ResearchObjective(Task task) : base(task) {}
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Working;
        // AnimalStateManager.HandleWorking calls task.Complete() when research finishes.
    }
}

// Drives the "stand and repair" phase of a MaintenanceTask. The actual condition bump
// happens in AnimalStateManager.HandleWorking (see the MaintenanceTask branch there) —
// this objective just parks the animal in the Working state until that handler calls Complete().
public class MaintenanceObjective : Objective {
    public MaintenanceObjective(Task task) : base(task) {}
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Working;
    }
}

public class LeisureObjective : Objective {
    public int duration;
    public bool isSocializing; // set per-tick by HandleLeisure when co-seated at a socialWhenShared building
    public LeisureObjective(Task task, int duration) : base(task) {
        this.duration = duration;
    }
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Leisuring;
        // Leisure building: face toward the center of the building
        if (task is LeisureTask leisure && leisure.building != null) {
            float center = leisure.building.x + leisure.building.structType.nx / 2f;
            animal.facingRight = (animal.x < center);
            if (animal.go != null) animal.go.transform.localScale = new Vector3(animal.facingRight ? 1 : -1, 1, 1);
        }
        // AnimalStateManager.HandleLeisure ticks workProgress and calls Complete() when done.
    }
}

// ChatObjective: handles the "stand and chat" phase of a ChatTask.
// All chat-specific tick logic lives in AnimalStateManager.HandleChatting().
public class ChatObjective : Objective {
    public Animal partner;
    public int duration;
    public ChatObjective(Task task, Animal partner, int duration) : base(task) {
        this.partner = partner;
        this.duration = duration;
    }
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Leisuring;
        // Face partner
        animal.facingRight = (partner.x > animal.x);
        if (animal.go != null) animal.go.transform.localScale = new Vector3(animal.facingRight ? 1 : -1, 1, 1);
        // If partner is already waiting in their ChatObjective, sync both timers
        if (partner.state == Animal.AnimalState.Leisuring
            && partner.task?.currentObjective is ChatObjective partnerObj) {
            partner.workProgress = 0f;
            animal.animationController?.UpdateState();
            partner.animationController?.UpdateState();
        }
        // AnimalStateManager.HandleChatting ticks workProgress and calls Complete() when done.
    }
}

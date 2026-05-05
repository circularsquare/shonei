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

    // Returns the leaf of a group item with the highest global-inventory count.
    // If item is already a leaf, returns it unchanged. Used to commit to a single
    // leaf before a supply/repair haul so the destination doesn't get locked to a
    // scarce variant just because it happened to be delivered first.
    protected static Item PickSupplyLeaf(Item item) {
        if (item.children == null || item.children.Length == 0) return item;
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

    // check whether a task is possible. create objectives, make reservations
    public abstract bool Initialize();

    public Task(Animal animal){
        this.animal = animal;
    }

    // Reserves items on a stack and tracks for cleanup. Single entry point for all source reservations.
    public int ReserveStack(ItemStack stack, int amount){
        int reserved = stack.Reserve(amount, this);
        if (reserved > 0) reservedStacks.Add((stack, reserved));
        return reserved;
    }

    // Sum of source reservations against a specific inventory — for diagnostics when a
    // reserved source is torn down. Matches by inv only; stack.item may have been nulled.
    public int ReservedAmountFromInv(Inventory inv){
        int total = 0;
        foreach (var (stack, amount) in reservedStacks)
            if (stack.inv == inv) total += amount;
        return total;
    }

    // True if this task has any source or destination reservation against `inv`.
    // Inventory.Destroy uses this to find tasks that need to abort early so the
    // animal re-plans instead of walking to a dead source.
    public bool HasReservationOn(Inventory inv){
        foreach (var (stack, _) in reservedStacks)
            if (stack.inv == inv) return true;
        foreach (var (stack, _) in reservedSpaces)
            if (stack.inv == inv) return true;
        return false;
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
        int reserved = ReserveStack(stack, amount);
        objectives.AddLast(new FetchObjective(this, iq, tile, sourceInv: stack.inv, sourceLimit: reserved));
    }
    protected void FetchAndReserve(ItemQuantity iq, Tile tile, ItemStack stack){
        FetchAndReserve(iq, tile, stack, iq.quantity);
    }
    // Overload that routes pickup into an equip slot instead of the animal's main inventory.
    protected void FetchAndReserve(ItemQuantity iq, Tile tile, ItemStack stack, Inventory targetInv){
        int reserved = ReserveStack(stack, iq.quantity);
        objectives.AddLast(new FetchObjective(this, iq, tile, targetInv, sourceInv: stack.inv, sourceLimit: reserved));
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
        int reserved = ReserveStack(bestStack, qty);
        objectives.AddFirst(new FetchObjective(this, foodIq, bestPath.tile, animal.foodSlotInv, softFetch: false, sourceInv: bestStack.inv, sourceLimit: reserved));
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
        int reserved = ReserveStack(bestStack, qty);
        objectives.AddFirst(new FetchObjective(this, foodIq, bestPath.tile, animal.foodSlotInv, softFetch: true, sourceInv: bestStack.inv, sourceLimit: reserved));
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
    // silent=true skips the "failed X task at Y" log. Use when the caller has already logged
    // a more specific message about why the task is being aborted (e.g. Inventory.Destroy
    // proactive notify, which logs the destroyer + reason just before calling Fail).
    public void Fail(bool silent = false){ // end task, go idle
        // Idempotent — Inventory.Destroy proactively fails reserving tasks, after which
        // the same task's own code path (e.g. FetchObjective.OnArrival) may fall through
        // to its own Fail(). Skip if we no longer own the animal.
        if (animal.task != this) return;
        if (!silent && !WorldController.isClearing) {
            if (currentObjective == null){
                Debug.Log($"{animal.aName} ({animal.job.name}) failed {ToString()} task, null objective");
            } else {
                Debug.Log($"{animal.aName} ({animal.job.name}) failed {ToString()} task at {currentObjective}");
            }
        }
        Cleanup();
        animal.task = null;
        animal.state = Animal.AnimalState.Idle;
    }
    // Softer fail path for objectives that run while the animal is conceptually off-screen
    // at the market. Default = plain Fail(). Market tasks override to enqueue a return
    // TravelingObjective so the merchant walks home before going idle, rather than
    // snapping back to the town portal with no apparent travel.
    public virtual void FailAtMarket(){ Fail(); }
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

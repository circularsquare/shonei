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

    // Set once by Fail() so the failure path is idempotent and so Start() can tell a synchronous
    // first-objective failure (cleaned up, task dead) from a task that's actually running.
    private bool aborted = false;

    // De minimis: skip hauls/drops below this unless it clears the source stack entirely.
    public const int MinHaulQuantity = 20; // 0.20 liang

    // True if a move of `amount` is worth making: it's at least MinHaulQuantity, OR it takes the
    // whole `wholeAmount` in play (clears a source stack / fits the entire carried load), so a
    // legitimately small-but-complete move is never blocked as a trickle. Single source of truth
    // for the de-minimis rule — every haul / consolidate / fuel / drop site tests through this.
    public static bool MeetsHaulMinimum(int amount, int wholeAmount) =>
        amount >= MinHaulQuantity || amount >= wholeAmount;

    // Strict minimum for market hauls — no exceptions for stack-clearing or topping off.
    // Merchants shouldn't make a trip for a trickle.
    public const int MinMarketHaulQuantity = 100;       // 1.0 liang (most items)
    public const int MinMarketHaulQuantitySilver = 40;  // 0.4 liang (silver moves in smaller amounts)

    // Minimum animal eepness to start a market trip — stricter than the general night-sleep
    // threshold. A merchant who leaves close to the threshold could dip into efficiency-loss
    // territory mid-transit and arrive useless at the far side.
    public const float MinMarketEepness = 0.75f;

    // ── Search radii ─────────────────────────────────────────────────────
    // Every task pathfind should be gated by one of these radii × FindRadiusTolerance.
    // A candidate target is rejected if the *actual* path cost to reach it exceeds
    // radius × tolerance. This prevents mice committing to journeys that look close
    // crow-flies but wind endlessly around terrain.
    public const int MediumFindRadius = 32;         // default for almost every task; also the work-anchor TERRITORY radius
    public const int MarketFindRadius = 120;        // market portal only — intentionally long
    public const int WorkConvenienceRadius = 15;    // Step 5b: small circle around the mouse for grabbing work underfoot, outside its anchor territory
    public const float FindRadiusTolerance = 1.2f;  // path cost may exceed radius by this factor

    protected static int MinMarketHaul(Item item) =>
        item.name == "silver" ? MinMarketHaulQuantitySilver : MinMarketHaulQuantity;

    // Path-cost units over which a leaf's surplus halves in ResolveConsumeLeaf. With the
    // exponential discount 2^(-cost/HalfDist) this is exactly the crossover: a leaf with 2× the
    // surplus is worth walking ~HalfDist extra cost-units for. (cost ≈ tiles on flat ground;
    // roads/water/ladders weight it — tune by feel.)
    protected const float ConsumeLeafHalfDist = 5f;

    // Score bonus for a leaf already present in a single-type destination slot (fuel reservoir /
    // processor buffer). Bounded, not infinite: we keep topping up with the existing leaf unless
    // another is >4× more attractive (surplus × nearness), in which case we let the slot drain and
    // switch on the next from-empty fill. Prevents perpetual "can't deposit a different leaf" aborts
    // when a slot holds one type, while still self-correcting when stocks become wildly imbalanced.
    protected const float ExistingLeafBonus = 4f;

    // Resolves a consumption item to the concrete leaf a mouse should fetch and consume.
    // Leaf items return unchanged. A group item (wildcard, e.g. "wood") resolves to the in-stock,
    // reachable leaf descendant maximising surplus (qty/target, capped) discounted by walk distance
    // — so mice drain the type they hold most over target, preferring nearby stock, and scoring
    // (Recipe.GeoMeanInputs, which uses the max-surplus leaf, distance-free) agrees with what gets
    // consumed. Ties resolve to the first leaf in LeafDescendants order (deterministic). Falls back
    // to the group unchanged if no leaf is reachable — the caller's own FindPathItemStack then
    // fails as before. Replaces the old max-global-quantity PickSupplyLeaf (which ignored both
    // targets and distance).
    //
    // preferLeaf: when the destination already holds a concrete leaf (single-type slot), pass it to
    // bias selection ×ExistingLeafBonus toward continuity. If another leaf still wins, this returns
    // it and the caller's space-reservation fails (can't mix in a single stack) — i.e. no top-up
    // this cycle, by design (see ExistingLeafBonus).
    // excludeLeafIds: leaf item ids to skip outright — used by SupplyBlueprintTask to honour a
    // blueprint's per-build variant ban (Blueprint.disallowedLeaves). Null = no extra exclusions.
    protected Item ResolveConsumeLeaf(Item item, Item preferLeaf = null, HashSet<int> excludeLeafIds = null) {
        if (!item.IsGroup) return item;
        Item best = null;
        float bestScore = -1f;
        var ic = InventoryController.instance;
        var targets = ic?.targets;
        foreach (Item leaf in item.LeafDescendants()) {
            if (excludeLeafIds != null && excludeLeafIds.Contains(leaf.id)) continue; // banned for this caller
            if (leaf.excludeFromGroupInput) continue; // never auto-substituted for a group (e.g. gypsum for "stone")
            // Note: the "consume" flag is NOT checked here. Crafting, construction, processor-fill,
            // and repair are all "always allowed" uses (see SPEC-systems §Consume protection); only
            // direct end-uses (eat/drink/equip/fuel/furnish) gate on it, at their own selection sites.
            var (path, stack) = animal.nav.FindPathItemStack(leaf);
            if (path == null || stack == null) continue; // not in stock / not reachable
            int target = (targets != null && targets.TryGetValue(leaf.id, out int t)) ? t : 100;
            float score = Recipe.SurplusRatio(GlobalInventory.instance.Quantity(leaf), target)
                          * Mathf.Pow(2f, -path.cost / ConsumeLeafHalfDist);
            if (leaf == preferLeaf) score *= ExistingLeafBonus;
            if (score > bestScore) { bestScore = score; best = leaf; }
        }
        return best ?? item;
    }

    // check whether a task is possible. create objectives, make reservations
    public abstract bool Initialize();

    // True if this task is tied to the animal's job (recipes, work orders, role-specific
    // hauls etc.). When the player changes an animal's job, Animal.SetJob fails the current
    // task only if IsWork — sleeping, eating, leisure, etc. should continue uninterrupted.
    // Mixed-purpose tasks (e.g. ObtainTask, used for both equipment and food) stay work
    // by default; the wasted fetch on a job swap is rare and self-corrects.
    public virtual bool IsWork => true;

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

    // ── Market multi-item cargo budget ───────────────────────────────────────
    // A merchant fills its main inventory with several item types per trip. Capacity is
    // counted in whole stacks, not raw fen: distinct item types never share a stack, so an
    // item of q fen consumes ceil(q / stackSize) stacks and strands the tail of its last
    // stack. CargoBudget hands out a per-item cap (Cap) and is decremented as items are
    // committed (Commit). Construct one fresh per trip phase — outbound goods are fully
    // delivered at the market before the return pickup, so the inventory is empty again.
    protected class CargoBudget {
        private int remainingStacks;
        private readonly int stackSize;
        public CargoBudget(Animal animal){
            stackSize = animal.inv.stackSize;
            foreach (ItemStack s in animal.inv.itemStacks)
                if (s.item == null) remainingStacks++;
        }
        public bool Exhausted => remainingStacks <= 0;
        public int Cap => remainingStacks * stackSize;          // max fen of one item type still loadable
        public void Commit(int q){
            remainingStacks = Math.Max(0, remainingStacks - (q + stackSize - 1) / stackSize); // ceil division
        }
    }

    // Selects above-target market leaf items to haul home, filling the merchant's cargo
    // budget with as many item types as fit. For each pick it resolves the home storage with
    // the most space and reserves both that storage space and the market source stack. Shared
    // by HaulFromMarketTask (pure pickup trip) and HaulToMarketTask's return-leg piggyback;
    // the caller turns the picks into Receive/Go/Deliver objectives.
    protected List<(ItemQuantity iq, Tile tile, Inventory inv)> SelectMarketPickups(Inventory marketInv){
        var picks = new List<(ItemQuantity iq, Tile tile, Inventory inv)>();
        if (marketInv?.targets == null) return picks;
        var budget = new CargoBudget(animal);
        foreach (var kvp in marketInv.targets){
            if (budget.Exhausted) break;
            Item item = kvp.Key;
            if (item.IsGroup) continue; // targets on groups are ignored (market targets are leaf-only)
            int excess = marketInv.Quantity(item) - kvp.Value;
            if (excess <= 0) continue;

            ItemStack stack = marketInv.GetItemStack(item);
            if (stack == null) continue;
            int avail = stack.quantity - stack.resAmount;
            if (avail <= 0) continue;

            int want = Math.Min(Math.Min(excess, avail), budget.Cap);
            if (want < MinMarketHaul(item)) continue;

            // Pick the storage with the most free space so the full payload lands in one drop-off.
            var (storagePath, storageInv) = animal.nav.FindPathToStorageMostSpace(item, minSpace: MinMarketHaul(item));
            if (storagePath == null) continue;

            // ReserveSpace is the only reservation here that can fail-then-undo, and nothing else
            // calls ReserveSpace before the next iteration, so UndoLastSpaceReservation is safe.
            int spaceReserved = ReserveSpace(storageInv, item, want);
            if (spaceReserved < MinMarketHaul(item)) { UndoLastSpaceReservation(); continue; }
            int finalQty = Math.Min(want, spaceReserved);

            ReserveStack(stack, finalQty);
            picks.Add((new ItemQuantity(item, finalQty), storagePath.tile, storageInv));
            budget.Commit(finalQty);
        }
        return picks;
    }

    // Greedy nearest-neighbour visiting order over a set of source tiles, starting from
    // (startX, startY). Returns indices into `tiles` in the order that minimises the fetch walk —
    // a cheap heuristic (not optimal, ignores the final deliver leg), but for the handful of
    // ingredients a recipe has it beats fetching in authoring order (closest needed item first).
    // Null tiles sort to the end so they're never dropped.
    protected static List<int> NearestFetchOrder(float startX, float startY, List<Tile> tiles){
        var order = new List<int>(tiles.Count);
        var taken = new bool[tiles.Count];
        float cx = startX, cy = startY;
        for (int n = 0; n < tiles.Count; n++){
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < tiles.Count; i++){
                if (taken[i]) continue;
                float d = tiles[i] != null ? Mathf.Abs(tiles[i].x - cx) + Mathf.Abs(tiles[i].y - cy) : float.MaxValue;
                if (d < bestD){ bestD = d; best = i; }
            }
            if (best < 0) break;
            taken[best] = true;
            order.Add(best);
            if (tiles[best] != null){ cx = tiles[best].x; cy = tiles[best].y; }
        }
        return order;
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

    public bool Start(){ // runs Initialize, then kicks off the first objective
        bool initialized = Initialize();
        if (!initialized) { Cleanup(); return false; } // release any reservations made before Initialize bailed
        if (objectives.Count > 0) StartNextObjective();
        // The first objective can fail synchronously (e.g. FetchObjective finds no path), which
        // routes through Fail() → Cleanup() and sets `aborted`. Initialize() already returned true,
        // so report the real outcome here — otherwise dispatch (ChooseOrder / ChooseCraftTask) would
        // adopt a task that's already dead and strand the reservations Initialize() made.
        return !aborted;
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
    public void Fail(bool silent = false){ // end task, release reservations, go idle
        // Idempotent via `aborted`: a task fails exactly once. Two paths can both call Fail() on the
        // same task — Inventory.Destroy proactively fails reserving tasks, then the task's own code
        // (e.g. FetchObjective.OnArrival) falls through to Fail() too — so the flag collapses the
        // second call to a no-op.
        if (aborted) return;
        aborted = true;
        if (!silent && !WorldController.isClearing) {
            if (currentObjective == null){
                Debug.Log($"{animal.aName} ({animal.job.name}) failed {ToString()} task, null objective");
            } else {
                Debug.Log($"{animal.aName} ({animal.job.name}) failed {ToString()} task at {currentObjective}");
            }
        }
        // Always release reservations, even when this task hasn't been assigned to animal.task yet:
        // the dispatch path (ChooseOrder / ChooseCraftTask) Start()s the first objective before the
        // assignment, so a synchronous failure here must still clean up or its reservations leak.
        Cleanup();
        // Only relinquish the animal if we actually own it. During dispatch (pre-assignment) we
        // don't — leave its task/state alone and let the dispatcher react to Start()'s false return.
        if (animal.task == this) {
            animal.task = null;
            animal.state = Animal.AnimalState.Idle;
        }
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

    // Releases this task's SOURCE (resAmount) reservation on `inv` by `amount` — call right after
    // physically taking reserved items (Inventory.MoveItemTo does this when given a `by` task). The
    // taken fen leave the reservation accounting exactly once, here, so Cleanup() doesn't release
    // them a second time. Shrinks the tracked reservedStacks entries for `inv`; the per-stack
    // Unreserve is clamped to current resAmount so it can't underflow.
    public void ConsumeSourceReservation(Inventory inv, int amount){
        if (amount <= 0) return;
        for (int i = 0; i < reservedStacks.Count && amount > 0; i++){
            var (stack, entryAmt) = reservedStacks[i];
            if (stack.inv != inv) continue;
            int release = Math.Min(entryAmt, amount);
            stack.Unreserve(Math.Min(release, stack.resAmount));
            amount -= release;
            if (release >= entryAmt){ reservedStacks.RemoveAt(i); i--; }
            else reservedStacks[i] = (stack, entryAmt - release);
        }
    }

    // Destination-space (resSpace) counterpart of ConsumeSourceReservation — call right after
    // depositing into space this task reserved, so the filled space leaves the accounting once.
    public void ConsumeSpaceReservation(Inventory inv, int amount){
        if (amount <= 0) return;
        for (int i = 0; i < reservedSpaces.Count && amount > 0; i++){
            var (stack, entryAmt) = reservedSpaces[i];
            if (stack.inv != inv) continue;
            int release = Math.Min(entryAmt, amount);
            stack.UnreserveSpace(Math.Min(release, stack.resSpace));
            amount -= release;
            if (release >= entryAmt){ reservedSpaces.RemoveAt(i); i--; }
            else reservedSpaces[i] = (stack, entryAmt - release);
        }
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

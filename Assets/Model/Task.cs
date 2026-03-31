using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

/*
    an Idle mouse will ChooseTask, which generate a Task that depends on their job.
    a Task will queue several Objectives, which may Fail, or all succeed and Complete the Task.
    then the mouse will become Idle again.
*/

public abstract class Task {
    public Animal animal;
    protected LinkedList<Objective> objectives = new LinkedList<Objective>();
    public Objective currentObjective;
    private readonly List<(ItemStack stack, int amount)> reservedStacks = new();
    // Set by WorkOrderManager.ChooseOrder when this task fulfills a work order.
    // Null for non-WOM tasks (Craft, Eep, Obtain, etc.). Released in Cleanup().
    public WorkOrderManager.WorkOrder workOrder;

    // De minimis: skip hauls/drops below this unless it clears the source stack entirely.
    public const int MinHaulQuantity = 20; // 0.20 liang

    // check whether a task is possible. create objectives, make reservations
    public abstract bool Initialize();

    public Task(Animal animal){
        this.animal = animal;
    }

    // Reserves items on a stack and tracks for cleanup. Single entry point for all item reservations.
    public void ReserveStack(ItemStack stack, int amount){
        int reserved = stack.Reserve(amount);
        if (reserved > 0) reservedStacks.Add((stack, reserved));
    }

    // Enqueues a FetchObjective and reserves items on the stack, tracking for cleanup.
    protected void FetchAndReserve(ItemQuantity iq, Tile tile, ItemStack stack, int amount){
        objectives.AddLast(new FetchObjective(this, iq, tile));
        ReserveStack(stack, amount);
    }
    protected void FetchAndReserve(ItemQuantity iq, Tile tile, ItemStack stack){
        FetchAndReserve(iq, tile, stack, iq.quantity);
    }
    // Overload that routes pickup into an equip slot instead of the animal's main inventory.
    protected void FetchAndReserve(ItemQuantity iq, Tile tile, ItemStack stack, Inventory targetInv){
        objectives.AddLast(new FetchObjective(this, iq, tile, targetInv));
        ReserveStack(stack, iq.quantity);
    }

    public bool Start(){ // calls some task specific Initialize
        bool initialized = Initialize();
        if (initialized && objectives.Count > 0){StartNextObjective();}
        return initialized;
    }
    public virtual void Complete(){ // called whenever an objective is complete;
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
        objectives.Clear();
    }
    public void EnqueueFront(Objective obj) { objectives.AddFirst(obj); }

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
        if (p == null) { return false; }
        workplace = _building.workTile;

        roundsRemaining = animal.CalculateWorkPossible(recipe);
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
            objectives.AddFirst(new FetchObjective(this, new ItemQuantity(item, toFetch), itemPath.tile, softFetch: true));
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
                    EnqueueFront(new FetchObjective(this, new ItemQuantity(item, needed), p.tile, softFetch: true));
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
    public override void Cleanup(){
        base.Cleanup();
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
        if (animal.homeTile != null){
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
    public HaulTask(Animal animal, ItemStack targetStack) : base(animal){
        this.targetStack = targetStack;
    }
    public override bool Initialize() {
        if (targetStack.item == null || targetStack.quantity == 0) return false; // stale
        Item item = targetStack.item;
        Tile itemTile = World.instance.GetTileAt(targetStack.inv.x, targetStack.inv.y);
        if (itemTile == null) return false;
        if (!animal.nav.CanReach(itemTile)) return false;
        Path storagePath = animal.nav.FindPathToStorage(item);
        if (storagePath == null) return false;
        int available = targetStack.quantity - targetStack.resAmount;
        int quantity = Math.Min(available, storagePath.tile.GetStorageForItem(item));
        if (quantity <= 0) return false;
        if (quantity < MinHaulQuantity && quantity < available) return false; // de minimis
        ItemQuantity iq = new(item, quantity);
        FetchAndReserve(iq, itemTile, targetStack, quantity);
        objectives.AddLast(new DeliverObjective(this, iq, storagePath.tile));
        return true;
    }
}
// consolidatetask is only for consolidating a specific floor stack when no storage is available (always WOM-targeted).
public class ConsolidateTask : Task {
    private readonly ItemStack stack;
    public ConsolidateTask(Animal animal, ItemStack stack) : base(animal) {
        this.stack = stack;
    }
    public override bool Initialize() {
        HaulInfo h = animal.nav.FindFloorConsolidation(stack);
        if (h == null) return false;
        int available = h.itemStack.quantity - h.itemStack.resAmount;
        if (h.quantity < MinHaulQuantity && h.quantity < available) return false; // de minimis
        ItemQuantity iq = new(h.item, h.quantity);
        FetchAndReserve(iq, h.itemTile, h.itemStack, h.quantity);
        objectives.AddLast(new DeliverObjective(this, iq, h.storageTile));
        return true;
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
            : animal.nav.PathToOrAdjacent(blueprint.tile);
        if (standPath == null) return false;
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
            : animal.nav.PathToOrAdjacent(blueprint.tile);
        if (standPath == null) return false;
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
        if (!animal.nav.CanReach(building.tile)) return false;
        Path standPath = animal.nav.PathToOrAdjacent(building.tile);
        if (standPath == null) return false;
        (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(fuel.fuelItem);
        if (itemPath == null) return false;
        int available = stack.quantity - stack.resAmount;
        int qty = Math.Min(needed, available);
        if (qty <= 0) return false;
        if (qty < MinHaulQuantity && qty < available) return false; // de minimis
        ItemQuantity iq = new ItemQuantity(fuel.fuelItem, qty);
        FetchAndReserve(iq, itemPath.tile, stack);
        objectives.AddLast(new GoObjective(this, standPath.tile));
        objectives.AddLast(new DeliverToInventoryObjective(this, iq, fuel.inv));
        return true;
    }
}
public class HaulToMarketTask : Task {
    public HaulToMarketTask(Animal animal) : base(animal) {}
    public override bool Initialize() {
        Path marketPath = animal.nav.FindMarketPath();
        if (marketPath == null) return false;
        Building market = marketPath.tile.building as Building;
        if (market == null) return false;
        Tile marketStorageTile = market.storageTile;
        Inventory marketInv = marketStorageTile.inv;
        if (marketInv == null) return false;

        foreach (var kvp in marketInv.targets) {
            int quantityNeeded = kvp.Value - marketInv.Quantity(kvp.Key);
            if (quantityNeeded <= 0) continue;
            Item item = kvp.Key;
            if (marketInv.allowed[item.id] == false) continue;

            Path itemPath = animal.nav.FindPathTo(
                t => t.inv != null && t.inv.invType != Inventory.InvType.Market && t.inv.invType != Inventory.InvType.Blueprint && t.inv.ContainsAvailableItem(item));
            if (itemPath == null) continue;
            ItemStack stack = itemPath.tile.inv.GetItemStack(item);
            if (stack == null) continue;

            int qty = Math.Min(quantityNeeded, stack.quantity - stack.resAmount);
            if (qty <= 0) continue;
            int sourceQty = stack.quantity - stack.resAmount;
            bool worthHauling = qty >= MinHaulQuantity || qty >= sourceQty || qty >= quantityNeeded / 4;
            if (!worthHauling) continue;
            ItemQuantity iq = new(item, qty);
            FetchAndReserve(iq, itemPath.tile, stack, qty);
            objectives.AddLast(new DeliverObjective(this, iq, marketStorageTile));
            return true;
        }
        return false;
    }
}
public class HaulFromMarketTask : Task {
    public HaulFromMarketTask(Animal animal) : base(animal) {}
    public override bool Initialize() {
        Path marketPath = animal.nav.FindMarketPath();
        if (marketPath == null) return false;
        Building market = marketPath.tile.building as Building;
        if (market == null) return false;
        Tile marketStorageTile = market.storageTile;
        Inventory marketInv = marketStorageTile.inv;
        if (marketInv == null) return false;

        foreach (var kvp in marketInv.targets) {
            int excess = marketInv.Quantity(kvp.Key) - kvp.Value;
            if (excess <= 0) continue;
            Item item = kvp.Key;

            ItemStack stack = marketInv.GetItemStack(item);
            if (stack == null) continue;
            Path storagePath = animal.nav.FindPathToStorage(item);
            if (storagePath == null) continue;

            int qty = Math.Min(excess, stack.quantity - stack.resAmount);
            if (qty <= 0) continue;
            int sourceQty = stack.quantity - stack.resAmount;
            bool worthHauling = qty >= MinHaulQuantity || qty >= sourceQty || qty >= excess / 4;
            if (!worthHauling) continue;
            ItemQuantity iq = new(item, qty);
            FetchAndReserve(iq, marketStorageTile, stack, qty);
            objectives.AddLast(new DeliverObjective(this, iq, storagePath.tile));
            return true;
        }
        return false;
    }
}
public class ResearchTask : Task {
    private readonly Building _lab;
    // Which research this scientist is maintaining, or -1 to work on activeResearchId.
    public int maintenanceTargetId = -1;
    public ResearchTask(Animal animal, Building lab) : base(animal) { _lab = lab; }
    public override bool Initialize() {
        if (_lab == null) return false;
        Path p = animal.nav.PathToOrAdjacent(_lab.tile);
        if (p == null) return false;
        objectives.AddLast(new GoObjective(this, p.tile));
        objectives.AddLast(new ResearchObjective(this));
        return true;
    }
    public override void Cleanup() {
        if (maintenanceTargetId >= 0)
            ResearchSystem.instance?.ReleaseMaintenanceClaim(maintenanceTargetId);
        base.Cleanup();
    }
}


// ------------------------------
// ------ OBJECTIVES ------------
// ------------------------------
public abstract class Objective {
    protected Task task;
    protected Animal animal;
    protected Tile destination;
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
    private Tile sourceTile;
    private Inventory targetInv; // null = animal's main inventory; non-null = equip into that slot
    private bool softFetch; // if true: Complete (not Fail) when no path found or nothing taken; no cross-tile retry
    private Inventory Dest => targetInv ?? animal.inv;
    public FetchObjective(Task task, ItemQuantity iq, Tile sourceTile = null, Inventory targetInv = null, bool softFetch = false) : base(task) {
        this.iq = iq;
        this.sourceTile = sourceTile;
        this.targetInv = targetInv;
        this.softFetch = softFetch;
    }
    public override string GetObjectiveName() { return $"Fetch({iq.item.name})"; }
    public override void Start(){
        // Start() may be called more than once (non-soft): if the animal arrives and the stack is partially
        // exhausted, sourceTile is cleared and Start() re-runs to find a new source tile.
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
            if (Dest.Quantity(iq.item) > 0) {
                Debug.Log($"{animal.aName} ({animal.job.name}) partial fetch: has {Dest.Quantity(iq.item)} {iq.item.name}, no more found — delivering partial");
                Complete(); return;
            }
            Fail(); Debug.Log($"{animal.aName} ({animal.job.name}) found no path to fetch {iq.item.name} at ({(int)animal.x},{(int)animal.y})");
        }
    }
    public override void OnArrival(){
        int needed = iq.quantity - Dest.Quantity(iq.item); // how much more we still need
        if (needed <= 0) { Complete(); return; }
        int amountBefore = Dest.Quantity(iq.item);
        animal.TakeItem(new ItemQuantity(iq.item, needed), targetInv); // only take what's still needed
        int amountTaken = Dest.Quantity(iq.item) - amountBefore;
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
            Start();
        }
    }
}
public class DeliverObjective : Objective { // navigates and drops off item
    private ItemQuantity iq; 
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
    private Inventory targetInv;
    public DeliverToInventoryObjective(Task task, ItemQuantity iq, Inventory targetInv) : base(task) {
        this.iq = iq;
        this.targetInv = targetInv;
    }
    public override string GetObjectiveName() { return $"DeliverTo({iq.item.name}>{targetInv.displayName})"; }
    public override void Start(){
        if (targetInv == null) { Fail(); return; }
        int have = animal.inv.Quantity(iq.item);
        if (have <= 0) { Debug.Log($"{animal.aName} DeliverToInventoryObjective: missing {iq.item.name}"); Fail(); return; }
        int toDeliver = Math.Min(have, iq.quantity);
        int moved = animal.inv.MoveItemTo(targetInv, iq.item, toDeliver);
        if (moved < toDeliver)
            Debug.Log($"{animal.aName} delivered {moved}/{toDeliver} {iq.item.name} to {targetInv.displayName} — partial fill");
        Complete();
    }
}
public class DropObjective : Objective {
    private Item item;
    private Inventory targetInv; // null = drop on floor
    public DropObjective(Task task, Item item) : base(task) {
        this.item = item;
    }
    public override void Start(){
        if (animal.inv.Quantity(item) == 0) {Complete(); return;}
        var (dropPath, storageInv) = animal.nav.FindPathToDropTarget(item, animal.inv.Quantity(item));
        if (dropPath != null){
            targetInv = storageInv;
            destination = dropPath.tile;
            animal.nav.Navigate(dropPath);
            animal.state = Animal.AnimalState.Moving;
        } else {
            Debug.LogError($"{animal.aName} ({animal.job.name}) can't find a place to drop {item.name} at ({(int)animal.x},{(int)animal.y})!");
            Fail();
        }
    }
    public override void OnArrival(){
        if (targetInv != null && targetInv.GetStorageForItem(item) > 0) {
            animal.inv.MoveItemTo(targetInv, item, animal.inv.Quantity(item));
            // Any leftover (storage filled up between planning and arrival) stays in inv, retried next DropTask
        } else {
            animal.DropItem(item);
        }
        Complete();
    }
}

public class GoObjective : Objective {
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

// ── Leisure ──────────────────────────────────────────────────────────

public class LeisureObjective : Objective {
    public int duration;
    public LeisureObjective(Task task, int duration) : base(task) {
        this.duration = duration;
    }
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Leisuring;
        // Chat sync and facing
        if (task is ChatTask chat && chat.partner != null) {
            // If initiator just arrived, sync both timers so they start on the same tick.
            if (chat.partner.task is ChatTask partnerChat && partnerChat.partner == animal
                && chat.partner.state == Animal.AnimalState.Leisuring) {
                chat.chatStarted = true;
                partnerChat.chatStarted = true;
                chat.partner.workProgress = 0f;
            }
            // Face partner
            animal.facingRight = (chat.partner.x > animal.x);
            if (animal.go != null) animal.go.transform.localScale = new Vector3(animal.facingRight ? 1 : -1, 1, 1);
        }
        // AnimalStateManager.HandleLeisure ticks workProgress and calls Complete() when done.
    }
}

public class ChatTask : Task {
    public Animal partner;
    public bool socializedEarly; // true once happiness granted at 8 ticks
    public bool chatStarted;     // set by initiator's LeisureObjective.Start — syncs both timers
    public Tile myTile;          // tile this animal should stand on during chat
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
                if (animal.nav.PathTo(partnerTile) == null) return false;
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

        objectives.AddLast(new LeisureObjective(this, 10));
        return true;
    }

    /// Try horizontal neighbors of partnerTile (left and right). Return the one the
    /// initiator can path to, preferring the shorter path. Returns null if neither works.
    private Tile FindAdjacentChatTile(Tile partnerTile) {
        World w = animal.world;
        Tile left  = w.GetTileAt(partnerTile.x - 1, partnerTile.y);
        Tile right = w.GetTileAt(partnerTile.x + 1, partnerTile.y);

        Path pathLeft  = (left  != null && left.node.standable)  ? animal.nav.PathTo(left)  : null;
        Path pathRight = (right != null && right.node.standable) ? animal.nav.PathTo(right) : null;

        if (pathLeft != null && pathRight != null)
            return pathLeft.cost <= pathRight.cost ? left : right;
        return pathLeft != null ? left : right; // right may be null (= no neighbor found)
    }
    public override void Complete() {
        if (!socializedEarly) animal.happiness.NoteSocialized();
        base.Complete();
    }
    public override void Cleanup() {
        // If the initiator fails before arriving (chatStarted=false), the partner
        // is stuck waiting forever. Release them. Once started, HandleLeisure manages it.
        if (!chatStarted) {
            Animal p = partner;
            partner = null;
            if (p?.task is ChatTask pt && pt.partner == animal) {
                p.task.Fail();
            }
        }
        base.Cleanup();
    }
}



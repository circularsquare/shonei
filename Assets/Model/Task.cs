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
            Debug.Log(animal.aName + " failed " + ToString() + " task, null objective");
        } else {
            Debug.Log(animal.aName + " failed " + ToString() + " task at " + currentObjective.ToString());
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

    public CraftTask(Animal animal) : base(animal){}
    public override bool Initialize(){
        recipe = animal.PickRecipe();
        if (recipe == null){ return false; }
        Path p = null;
        if (Db.structTypeByName.ContainsKey(recipe.tile)) {
            p = animal.nav.FindPathToBuilding(Db.structTypeByName[recipe.tile]);
        }
        if (p == null) { return false; }
        workplace = p.tile;

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

        if (!workplace.building.res.Reserve()) return false;
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
        workplace.building?.res.Unreserve();
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
        if (!(tile.building is Plant plant)) return false;
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
            iq = new ItemQuantity(blueprint.costs[i].item, needed);
            if (!animal.inv.ContainsItem(iq)) {
                (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(iq.item);
                if (itemPath == null) continue; // can't find this item — try next cost slot
                FetchAndReserve(iq, itemPath.tile, stack);
            }
            objectives.AddLast(new GoObjective(this, standPath.tile));
            objectives.AddLast(new DeliverToBlueprintObjective(this, iq, blueprint));
            return true;
        }
        return false;
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
    public ResearchTask(Animal animal, Building lab) : base(animal) { _lab = lab; }
    public override bool Initialize() {
        if (_lab == null) return false;
        Path p = animal.nav.PathToOrAdjacent(_lab.tile);
        if (p == null) return false;
        objectives.AddLast(new GoObjective(this, p.tile));
        objectives.AddLast(new ResearchObjective(this));
        return true;
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
            Debug.Log($"{animal.aName} ({animal.job.name}) at ({(int)animal.x},{(int)animal.y}): partial/failed drop of {iq.item.name} — moved {moved}/{iq.quantity} to ({destination.x},{destination.y})");
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
            foreach (var cost in blueprint.costs)
                if (cost.item == iq.item) { needed = cost.quantity - blueprint.inv.Quantity(iq.item); break; }
            animal.inv.MoveItemTo(blueprint.inv, iq.item, Math.Min(animal.inv.Quantity(iq.item), needed));
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
public class DropObjective : Objective { 
    private Item item;
    public DropObjective(Task task, Item item) : base(task) {
        this.item = item;
    }
    public override void Start(){
        if (animal.inv.Quantity(item) == 0) {Complete(); return;}
        Path dropPath = animal.nav.FindPathToDrop(item, animal.inv.Quantity(item));
        if (dropPath != null){
            destination = dropPath.tile;
            animal.nav.Navigate(dropPath);
            animal.state = Animal.AnimalState.Moving;
        } else {
            Debug.LogError($"{animal.aName} ({animal.job.name}) can't find a place to drop {item.name} at ({(int)animal.x},{(int)animal.y})!");
            Fail();
        }
    }
    public override void OnArrival(){
        animal.DropItem(item);
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



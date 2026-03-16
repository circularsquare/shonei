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
    protected Queue<Objective> objectives = new Queue<Objective>();
    public Objective currentObjective;
    protected List<(ItemStack stack, int amount)> reservedStacks = new List<(ItemStack, int)>();

    // check whether a task is possible. create objectives, make reservations
    public abstract bool Initialize();

    public Task(Animal animal){
        this.animal = animal;
    }

    // Enqueues a FetchObjective and reserves items on the stack, tracking for cleanup.
    protected void FetchAndReserve(ItemQuantity iq, Tile tile, ItemStack stack, int amount){
        objectives.Enqueue(new FetchObjective(this, iq, tile));
        int reserved = stack.res.Reserve(amount);
        if (reserved > 0) reservedStacks.Add((stack, reserved));
    }
    protected void FetchAndReserve(ItemQuantity iq, Tile tile, ItemStack stack){
        FetchAndReserve(iq, tile, stack, iq.quantity);
    }
    // Overload that routes pickup into an equip slot instead of the animal's main inventory.
    protected void FetchAndReserve(ItemQuantity iq, Tile tile, ItemStack stack, Inventory targetInv){
        objectives.Enqueue(new FetchObjective(this, iq, tile, targetInv));
        int reserved = stack.res.Reserve(iq.quantity);
        if (reserved > 0) reservedStacks.Add((stack, reserved));
    }

    // Shared setup for simple haul tasks: reserve item, queue Fetch + Deliver.
    protected bool InitFromHaul(HaulInfo h) {
        if (h == null) return false;
        ItemQuantity iq = new ItemQuantity(h.item, h.quantity);
        FetchAndReserve(iq, h.itemTile, h.itemStack, h.quantity);
        objectives.Enqueue(new DeliverObjective(this, iq, h.storageTile));
        return true;
    }

    public bool Start(){ // calls some task specific Initialize
        bool initialized = Initialize();
        if (objectives.Count > 0){StartNextObjective();}
        return initialized;
    }
    public void Complete(){ // called whenever an objective is complete;
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
    // Called by FetchObjective when it acquires a new source tile mid-task (partial fetch retry).
    public void AddReservedStack(ItemStack stack, int amount) {
        int reserved = stack.res.Reserve(amount);
        if (reserved > 0) reservedStacks.Add((stack, reserved));
    }
    public virtual void Cleanup(){
        foreach (var (stack, amount) in reservedStacks){
            stack.res.Unreserve(Math.Min(amount, stack.res.reserved));
        }
        reservedStacks.Clear();
        objectives.Clear();
    }
    public void StartNextObjective(){
        currentObjective = objectives.Dequeue();
        currentObjective.Start();
    }
    public void OnArrival(){
        currentObjective?.OnArrival();
    }

    public virtual string GetTaskName (){
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

    public CraftTask(Animal animal) : base(animal){}
    public override bool Initialize(){
        recipe = animal.PickRecipe(); // TODO: should this function be moved here to Task?
        if (recipe == null){ return false; }
        Path p = null; 
        if (Db.structTypeByName.ContainsKey(recipe.tile)) {
            p = animal.nav.FindPathToBuilding(Db.structTypeByName[recipe.tile]);
        }
        if (p == null) { return false; }
        workplace = p.tile;

        roundsRemaining = animal.CalculateWorkPossible(recipe);
        if (roundsRemaining == 0) { return false; }
        foreach (ItemQuantity iq in recipe.inputs){
            if (!animal.inv.ContainsItem(iq, roundsRemaining)){
                (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(iq.item);
                if (itemPath == null) { return false; }
                int totalNeeded = iq.quantity * roundsRemaining;
                FetchAndReserve(new ItemQuantity(iq.item, totalNeeded), itemPath.tile, stack, totalNeeded);
            }
        }
        objectives.Enqueue(new GoObjective(this, workplace));
        objectives.Enqueue(new WorkObjective(this, recipe));

        foreach (ItemQuantity output in recipe.outputs){
            objectives.Enqueue(new DropObjective(this, output.item));
        }
        if (!workplace.building.res.Reserve()) Debug.Log("reserved workplace that is not available!");
        return true;
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
        objectives.Enqueue(new GoObjective(this, tile)); return true;
    }
}
public class EepTask : Task {
    public EepTask(Animal animal) : base(animal){}
    public override bool Initialize(){
        if (animal.homeTile == null){
            animal.FindHome();
        }
        if (animal.homeTile != null){
            objectives.Enqueue(new GoObjective(this, animal.homeTile));
        }
        objectives.Enqueue(new EepObjective(this));
        return true;
    }
}
public class HarvestTask : Task {
    public Tile tile;
    public HarvestTask(Animal animal, Tile tile) : base(animal){
        this.tile = tile;
    }
    public override bool Initialize() {
        if (!(tile.building is Plant)){
            return false;
        } 
        Plant plant = tile.building as Plant;
        
        objectives.Enqueue(new GoObjective(this, tile));
        objectives.Enqueue(new HarvestObjective(this, plant));
        foreach (ItemQuantity output in plant.plantType.products){
            objectives.Enqueue(new DropObjective(this, output.item));
        }
        if (!plant.res.Reserve()) Debug.Log("reserved plant that is not available!");
        return true;
    }
    public override void Cleanup() {
        tile.building?.res.Unreserve();
        base.Cleanup();
    }
}
// haultask is only for moving random items to storage!
public class HaulTask : Task {
    public HaulTask(Animal animal) : base(animal){}
    public override bool Initialize() => InitFromHaul(animal.nav.FindAnyItemToHaul());
}
// consolidatetask is only for merging floor stacks when no storage is available!
public class ConsolidateTask : Task {
    public ConsolidateTask(Animal animal) : base(animal) {}
    public override bool Initialize() => InitFromHaul(animal.nav.FindFloorConsolidation());
}
public class DropTask : Task {
    ItemQuantity iq;
    public DropTask(Animal animal) : base(animal){}
    public override bool Initialize(){
        List<Item> itemsToDrop = animal.inv.GetItemsList();
        if (itemsToDrop.Count == 0) { return false; }
        foreach (Item item in itemsToDrop) {
            objectives.Enqueue(new DropObjective(this, item)); }
        return true;
    }
}

public class ConstructTask : Task {
    public Blueprint blueprint;
    public bool deconstructing;
    public ConstructTask(Animal animal, bool deconstructing = false) : base(animal){
        this.deconstructing = deconstructing;
    }
    public override bool Initialize() {
        var state = deconstructing ? Blueprint.BlueprintState.Deconstructing : Blueprint.BlueprintState.Constructing;
        var candidates = animal.nav.FindPathsAdjacentToBlueprints(animal.job, state);
        foreach (var (bpTile, standPath, bp) in candidates) {
            blueprint = bp;
            objectives.Enqueue(new GoObjective(this, standPath.tile));
            objectives.Enqueue(new ConstructObjective(this, blueprint));
            if (!blueprint.res.Reserve()) Debug.Log("reserved blueprint that is not available!");
            return true;
        }
        return false;
    }
    public override void Cleanup() {
        blueprint?.res.Unreserve();
        base.Cleanup();
    }
}
public class SupplyBlueprintTask : Task {
    Blueprint blueprint;
    ItemQuantity iq; 
    public SupplyBlueprintTask(Animal animal) : base(animal){}
    public override bool Initialize() {
        var candidates = animal.nav.FindPathsAdjacentToBlueprints(animal.job, Blueprint.BlueprintState.Receiving);
        foreach (var (bpTile, standPath, bp) in candidates) {
            for (int i = 0; i < bp.costs.Length; i++) {
                if (bp.inv.Quantity(bp.costs[i].item) < bp.costs[i].quantity) {
                    iq = new ItemQuantity(bp.costs[i].item,
                        bp.costs[i].quantity - bp.inv.Quantity(bp.costs[i].item));
                    if (!animal.inv.ContainsItem(iq)){
                        (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(iq.item);
                        if (itemPath == null) { continue; }
                        FetchAndReserve(iq, itemPath.tile, stack);
                    }
                    blueprint = bp;
                    objectives.Enqueue(new GoObjective(this, standPath.tile));
                    objectives.Enqueue(new DeliverToBlueprintObjective(this, iq, blueprint));
                    if (!blueprint.res.Reserve()) Debug.Log("reserved blueprint that is not available!");
                    return true;
                }
            }
        }
        return false;
    }
    public override void Cleanup(){
        blueprint?.res.Unreserve();
        base.Cleanup();
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

            int qty = Math.Min(quantityNeeded, stack.quantity - stack.res.reserved);
            if (qty <= 0) continue;
            ItemQuantity iq = new(item, qty);
            FetchAndReserve(iq, itemPath.tile, stack, qty);
            objectives.Enqueue(new DeliverObjective(this, iq, marketStorageTile));
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

            int qty = Math.Min(excess, stack.quantity - stack.res.reserved);
            if (qty <= 0) continue;
            ItemQuantity iq = new(item, qty);
            FetchAndReserve(iq, marketStorageTile, stack, qty);
            objectives.Enqueue(new DeliverObjective(this, iq, storagePath.tile));
            return true;
        }
        return false;
    }
}
public class ResearchTask : Task {
    public Tile labTile;
    public ResearchTask(Animal animal) : base(animal) {}
    public override bool Initialize() {
        if (!Db.structTypeByName.ContainsKey("laboratory")) return false;
        Path p = animal.nav.FindPathToBuilding(Db.structTypeByName["laboratory"]);
        if (p == null) return false;
        labTile = p.tile;
        if (!labTile.building.res.Reserve()) return false;
        objectives.Enqueue(new GoObjective(this, labTile));
        objectives.Enqueue(new ResearchObjective(this));
        return true;
    }
    public override void Cleanup() {
        labTile?.building?.res.Unreserve();
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
    public FetchObjective(Task task, ItemQuantity iq, Tile sourceTile = null, Inventory targetInv = null) : base(task) {
        this.iq = iq;
        this.sourceTile = sourceTile;
        this.targetInv = targetInv;
    }
    public override void Start(){
        Inventory dest = targetInv ?? animal.inv;
        if (dest.Quantity(iq.item) >= iq.quantity){Complete(); return;}
        Path itemPath;
        if (sourceTile != null) {
            itemPath = animal.nav.PathTo(sourceTile);
        } else {
            ItemStack stack;
            (itemPath, stack) = animal.nav.FindPathItemStack(iq.item);
            if (itemPath != null) {
                task.AddReservedStack(stack, iq.quantity);
                sourceTile = itemPath.tile;
            }
        }
        if (itemPath != null){
            destination = itemPath.tile;
            animal.nav.Navigate(itemPath);
            animal.state = Animal.AnimalState.Moving;
        } else {
            Fail(); Debug.Log($"{animal.aName} ({animal.job.name}) found no path to fetch {iq.item.name} at ({(int)animal.x},{(int)animal.y})");
        }
    }
    public override void OnArrival(){
        Inventory dest = targetInv ?? animal.inv;
        int needed = iq.quantity - dest.Quantity(iq.item); // how much more we still need
        if (needed <= 0) { Complete(); return; }
        int amountBefore = dest.Quantity(iq.item);
        animal.TakeItem(new ItemQuantity(iq.item, needed), targetInv); // only take what's still needed
        int amountTaken = dest.Quantity(iq.item) - amountBefore;
        if (amountTaken == 0) { Fail(); Debug.Log($"{animal.aName} Couldn't fetch any {iq.item.name} (needed {needed} fen)"); return; }
        // targetInv != null means equip slot (food/tool) — partial fills are fine there,
        // so don't retry across tiles. Main-inv crafting fetches (targetInv == null) do retry.
        if (dest.Quantity(iq.item) >= iq.quantity || dest.GetStorageForItem(iq.item) < 5 || targetInv != null) {
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
        if (animal.inv.Quantity(iq.item) == 0){ Debug.Log($"{animal.aName} DeliverObjective: missing {iq.item.name} to deliver to ({destination.x},{destination.y})"); Fail(); }
        animal.DropItem(iq);  // drops the amount you have up to iq.quantity. if have less, just drops that.
        Complete();
    }
}
public class DeliverToBlueprintObjective : Objective { // does not navigate, happens on arrival
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
            if (blueprint.IsFullyDelivered()) blueprint.state = Blueprint.BlueprintState.Constructing;
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
        Path dropPath = animal.nav.FindPathToDrop(item);
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
    }
}
public class EepObjective : Objective {
    public EepObjective(Task task): base(task){}
    public override void Start(){
        animal.state = Animal.AnimalState.Eeping;
    }
    // asm.handleeeping calls task.complete
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
    }
}



public class ResearchObjective : Objective {
    public ResearchObjective(Task task) : base(task) {}
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Working;
    }
    // AnimalStateManager.HandleWorking drives progress and calls task.Complete().
}



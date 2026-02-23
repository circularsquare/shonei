using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;


/*
    an Idle mouse will FindWork, which generate a Task that depends on their job.
    a Task will queue several Objectives, which may Fail, or all succeed and Complete the Task.
    then the mouse will become Idle again.
*/

public abstract class Task {
    public Animal animal;
    protected Queue<Objective> objectives = new Queue<Objective>();
    public Objective currentObjective;

    // check whether a task is possible. create objectives, make reservations
    public abstract bool Initialize(); 

    public Task(Animal animal){
        this.animal = animal;
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
    public virtual void Cleanup(){
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
    Tile workplace; 

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

        int numRounds = animal.CalculateWorkPossible(recipe);
        foreach (ItemQuantity input in recipe.inputs){
            if (!animal.inv.ContainsItem(input, numRounds)){
                objectives.Enqueue(new FetchObjective(this, input));
                // TODO: reserve
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
        objectives.Clear();
    }
}


public class ObtainTask : Task {
    public ItemQuantity iq;
    public ObtainTask(Animal animal, ItemQuantity iq) : base(animal){
        this.iq = iq;
    }
    public ObtainTask(Animal animal, Item item, int quantity) : base(animal){
        iq = new ItemQuantity(item, quantity);
    }
    public override bool Initialize(){
        // TODO: reserve
        if (animal.nav.FindPathToItem(iq.item) == null){ return false; } // to catch some failures to find item, such as wheat
        objectives.Enqueue(new FetchObjective(this, iq));
        return true;
    }
    public override void Cleanup(){
        // TODO: unreserve
        objectives.Clear();
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
        objectives.Clear();
    }
}
// haultask is only for moving random items to storage!
public class HaulTask : Task { 
    HaulInfo haulInfo;
    public HaulTask(Animal animal) : base(animal){}
    public override bool Initialize() {
        haulInfo = animal.nav.FindAnyItemToHaul();
        if (haulInfo == null) {return false;}
        ItemQuantity iq = new ItemQuantity(haulInfo.item, haulInfo.quantity);
        objectives.Enqueue(new FetchObjective(this, iq, haulInfo.itemTile));
        objectives.Enqueue(new DeliverObjective(this, iq, haulInfo.storageTile));
        if (!haulInfo.itemStack.res.Reserve()) Debug.Log("reserved itemstack that is not available!");
        return true;
    }
    public override void Cleanup(){
        objectives.Clear();
        haulInfo.itemStack?.res.Unreserve();
    }
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
    public Tile tile;
    public Blueprint blueprint;
    public ConstructTask(Animal animal) : base(animal){}
    public override bool Initialize() {
        (Tile, Path) info = animal.nav.FindPathAdjacentToBlueprint(animal.job, constructing: true);
        if (info.Item1 == null){return false;}
        tile = info.Item1;
        blueprint = tile.GetMatchingBlueprint(b => b.structType.job == animal.job && b.state == Blueprint.BlueprintState.Constructing);
        objectives.Enqueue(new GoObjective(this, info.Item2.tile));
        objectives.Enqueue(new ConstructObjective(this, blueprint));
        if (!blueprint.res.Reserve()) Debug.Log("reserved blueprint that is not available!");
        return true;
    }
    public override void Cleanup(){
        objectives.Clear();
        blueprint?.res.Unreserve();
    }
}
public class SupplyBlueprintTask : Task {
    Blueprint blueprint;
    ItemQuantity iq; 
    public SupplyBlueprintTask(Animal animal) : base(animal){}
    public override bool Initialize() {
        //Path blueprintPath = animal.nav.FindReceivingBlueprint(animal.job);
        (Tile bpTile, Path standPath) = animal.nav.FindPathAdjacentToBlueprint(animal.job, constructing: false);
        if (bpTile == null) {return false;}
        blueprint = bpTile.GetMatchingBlueprint(b => b.structType.job == animal.job && b.state == Blueprint.BlueprintState.Receiving);
        for (int i = 0; i < blueprint.costs.Length; i++) {
            if (blueprint.deliveredResources[i].quantity < blueprint.costs[i].quantity) {
                iq = new ItemQuantity(blueprint.costs[i].item,
                    blueprint.costs[i].quantity - blueprint.deliveredResources[i].quantity);
                if (!animal.inv.ContainsItem(iq)){
                    Path itemPath = animal.nav.FindPathToItem(iq.item);    // check if this item exists anywhere
                    if (itemPath == null) { continue; } 
                    objectives.Enqueue(new FetchObjective(this, iq, itemPath.tile));
                    // TODO: reserve
                }
                objectives.Enqueue(new GoObjective(this, standPath.tile));
                objectives.Enqueue(new DeliverToBlueprintObjective(this, iq, blueprint));
                if (!blueprint.res.Reserve()) Debug.Log("reserved blueprint that is not available!");
                return true;
            }
        }
        return false;
    }
    public override void Cleanup(){
        objectives.Clear();
        blueprint?.res.Unreserve();
    }
}
public class FallTask : Task {
    public FallTask(Animal animal) : base(animal) {}
    public override bool Initialize() {
        Tile below = animal.world.GetTileAt(animal.x, animal.y - 1);
        if (below == null) return false;
        objectives.Enqueue(new FallObjective(this, below));
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
    public FetchObjective(Task task, ItemQuantity iq, Tile sourceTile = null) : base(task) {
        this.iq = iq;
        this.sourceTile = sourceTile;
    }
    public override void Start(){
        if (animal.inv.ContainsItem(iq)){Complete(); return;}
        Path itemPath;
        if (sourceTile != null) {
            itemPath = animal.nav.PathTo(sourceTile);
        } else {
            itemPath = animal.nav.FindPathToItem(iq.item);
        }
        if (itemPath != null){
            destination = itemPath.tile;
            animal.nav.Navigate(itemPath); 
            animal.state = Animal.AnimalState.Moving;
        } else {
            Fail(); Debug.Log("no path to fetch item...");
        }
    }
    public override void OnArrival(){
        int amountBefore = animal.inv.Quantity(iq.item);
        animal.TakeItem(iq);
        int amountTaken = animal.inv.Quantity(iq.item) - amountBefore;
        if (amountTaken == 0){Fail(); Debug.Log("Couldn't fetch any " + iq.item); return;}
        int desiredItemQuantity = iq.quantity - animal.inv.Quantity(iq.item); 
        if (desiredItemQuantity > 0 && animal.inv.GetStorageForItem(iq.item) >= 5){
            iq.quantity = desiredItemQuantity;
            Start(); // restart fetching for the amount you still want
        } else {
            Complete();
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
        if (animal.inv.Quantity(iq.item) == 0){ Debug.Log(iq.item.name); Fail(); }
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
        if (animal.inv.Quantity(iq.item) > 0 && blueprint != null) {
            int delivered = blueprint.ReceiveResource(iq.item, iq.quantity);
            animal.inv.AddItem(iq.item, -delivered);
            Complete();
        } else {
            Debug.Log(iq.item.name + " could not be delivered to blueprint");
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
            Debug.LogError("can't find a place to drop " + item.name + "!");
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
}
public class WorkObjective : Objective {
    private Recipe recipe;
    public WorkObjective(Task task, Recipe recipe) : base(task) {
        this.recipe = recipe;
    }
    public override void Start(){
        // TODO: check if you're actually at a workplace!
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
        if (plant != null && plant.harvestable) {
            animal.Produce(plant.Harvest());
            Complete();
        } else { Fail(); }    
    }
}



public class FallObjective : Objective {
    public FallObjective(Task task, Tile below) : base(task) {
        this.destination = below;
    }
    public override void Start() {
        // Build a direct path bypassing standability checks
        List<Node> nodes = new List<Node> { animal.TileHere().node, destination.node };
        animal.nav.NavigateTo(destination, new Path(nodes));
        animal.state = Animal.AnimalState.Moving;
    }
    public override void OnArrival() {
        if (!animal.TileHere().node.standable) {
            // Keep falling — queue another fall objective
            Tile below = animal.world.GetTileAt(animal.x, animal.y - 1);
            //if (below == null) { Fail(); Debug.Log(animal.y); return; }
            destination = below;
            Start(); // re-navigate one tile down
        } else {
            Complete();
        }
    }
}
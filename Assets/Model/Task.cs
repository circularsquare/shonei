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
            animal.state = Animal.AnimalState.Idle; // SETTING STATE!
        }
    }
    public void Fail(){
        Cleanup();
        animal.task = null;
        animal.state = Animal.AnimalState.Idle;
    }
    public abstract void Cleanup(); // task specific cleanup
    public void StartNextObjective(){
        currentObjective = objectives.Dequeue();
        currentObjective.Start();
    }
    public void OnArrival(){
        currentObjective?.OnArrival();
    }
}

public class CraftTask : Task {
    public Recipe recipe;
    Tile workplace; 

    public CraftTask(Animal animal) : base(animal){}
    public override bool Initialize(){
        // TODO: reserve
        recipe = animal.PickRecipe(); // TODO: should this function be moved here to Task?
        if (recipe == null){ return false; }
        Path p = null; 
        if (Db.structTypeByName.ContainsKey(recipe.tile)) {
            p = animal.nav.FindBuilding(Db.structTypeByName[recipe.tile]);
        }
        if (p == null) { return false; }
        workplace = p.tile;

        int numRounds = 1; // animal.CalculateWorkPossible(recipe);
        foreach (ItemQuantity input in recipe.inputs){
            if (!animal.inv.ContainsItem(input, numRounds)){
                objectives.Enqueue(new FetchObjective(this, input));
            }
        }
        objectives.Enqueue(new GoObjective(this, workplace));
        objectives.Enqueue(new WorkObjective(this, recipe));

        foreach (ItemQuantity output in recipe.outputs){
            objectives.Enqueue(new DropObjective(this, output.item));
        }
        return true;
    }
    public override void Cleanup(){
        // TODO: unreserve
        objectives.Clear();
    }
}


public class ObtainTask : Task {
    ItemQuantity iq;
    public ObtainTask(Animal animal, ItemQuantity iq) : base(animal){
        this.iq = iq;
    }
    public override bool Initialize(){
        // TODO: reserve
        objectives.Enqueue(new FetchObjective(this, iq));
        return true;
    }
    public override void Cleanup(){
        // TODO: unreserve
        objectives.Clear();
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
}

public class FetchObjective : Objective {
    private ItemQuantity iq;
    public FetchObjective(Task task, ItemQuantity iq) : base(task) {
        this.iq = iq;
    }
    public override void Start(){
        Path itemPath = animal.nav.FindItem(iq.item);
        if (itemPath != null){
            destination = itemPath.tile;
            animal.nav.Navigate(itemPath); 
            animal.state = Animal.AnimalState.Moving; // todo: switch tolike "walking"
        } else {
            Fail();
        }
    }
    public override void OnArrival(){
        animal.TakeItem(iq);
        int desiredItemQuantity = iq.quantity - animal.inv.Quantity(iq.item); 
        if (desiredItemQuantity > 0 && animal.inv.GetStorageForItem(iq.item) >= 5){
            iq.quantity = desiredItemQuantity;
            Start(); // restart fetching for the amount you still want
        } else {
            Complete();
        }
    }
}
public class DeliverObjective : Objective {
    private ItemQuantity iq;
    public DeliverObjective(Task task, ItemQuantity iq, Tile destination) : base(task) {
        this.iq = iq;
        this.destination = destination;
    }
    public override void Start(){
        Path path = animal.nav.FindPathTo(destination);
        if (path != null){
            animal.nav.Navigate(path);
            animal.state = Animal.AnimalState.Moving;
        } else {Fail();}
    }
    public override void OnArrival(){
        int leftover = animal.DropItem(iq);
        if (leftover > 0){
            Fail();
        } else {
            Complete();
        }
    }
}
public class DropObjective : Objective { // drops ALL of an item. can't predict how many to drop. 
    private Item item;
    public DropObjective(Task task, Item item) : base(task) {
        this.item = item;
    }
    public DropObjective(Task task, ItemQuantity iq) : base(task) {
        this.item = iq.item;
    }
    public override void Start(){
        Path dropPath = animal.nav.FindPlaceToDrop(item);
        if (dropPath != null){
            destination = dropPath.tile;
            animal.nav.Navigate(dropPath);
            animal.state = Animal.AnimalState.Delivering;
        } else {
            Debug.LogError("can't find a place to drop!");
            // Fail(); remember, failing would call drop
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
        Path path = animal.nav.FindPathTo(destination);
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

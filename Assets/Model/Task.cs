using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public abstract class Task {
    public Animal animal;
    protected Queue<Objective> objectives = new Queue<Objective>();
    public Objective currentObjective;
    public abstract void Initialize(); // create objectives, make reservations
    public abstract void Cleanup(); 
    public Task(Animal animal){
        this.animal = animal;
    }
    public void Start(){ // calls some task specific Initialize
        Initialize();
        if (objectives.Count > 0){StartNextObjective();}
    }
    public void Complete(){ // called whenever an objective is complete;
        if (objectives.Count > 0){StartNextObjective();}
        else {
            Cleanup();
            animal.task = null;
        }
    }
    public void Fail(){
        Cleanup();
        animal.task = null;
    }
    public void StartNextObjective(){
        currentObjective = objectives.Dequeue();
        currentObjective.Start();
    }
    public void OnArrival(){
        currentObjective?.OnArrival();
    }
}

public class CraftTask : Task {
    Recipe recipe;
    Tile workplace; 

    public CraftTask(Animal animal) : base(animal){}
    public override void Initialize(){
        // TODO: reserve
        recipe = animal.PickRecipe(); // TODO: should this function be moved here to Task?
        if (recipe == null){ Fail(); return; }
        Path p = null; 
        if (Db.structTypeByName.ContainsKey(recipe.tile)) {
            p = animal.nav.FindBuilding(Db.structTypeByName[recipe.tile]);
        }
        if (p == null) { Fail(); return; }
        workplace = p.tile;

        int numRounds = 1; // animal.CalculateWorkPossible(recipe);
        foreach (ItemQuantity input in recipe.inputs){
            if (!animal.inv.ContainsItem(input, numRounds)){
                objectives.Enqueue(new FetchObjective(this, input));
            }
        }
        objectives.Enqueue(new DeliverObjective(this, workplace));
        objectives.Enqueue(new WorkObjective(this, recipe));

        Path storagePath = animal.nav.FindStorage(recipe.outputs[0].item);
        if (storagePath != null) {
            objectives.Enqueue(new DeliverObjective(this, storagePath.tile));
        }
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
            animal.state = Animal.AnimalState.Fetching;
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
    public DeliverObjective(Task task, Tile destination) : base(task) {
        
    }
    public override void Start(){

    }
}
public class WorkObjective : Objective {
    public WorkObjective(Task task, Recipe recipe) : base(task) {
        
    }
    public override void Start(){

    }
}

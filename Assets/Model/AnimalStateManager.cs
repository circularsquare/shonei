using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

using AnimalState = Animal.AnimalState;

public class AnimalStateManager {

    private Animal animal;

    public AnimalStateManager(Animal animal) {
        this.animal = animal;
    }

    public void OnStateEnter(AnimalState newState) {
        switch(newState){
            case AnimalState.Idle:
                break;
        }
    }

    public void UpdateState() { // called every animal.tickupdate!
        switch (animal.state){ // fetching and delivering and such are handled in Update() cuz they require deltatime?
            case AnimalState.Idle:
                HandleIdle();
                break;
            case AnimalState.Working:
                HandleWorking();
                break;
            case AnimalState.Eeping:
                HandleEeping();
                break;
        }
    }

    

    private void HandleIdle() {
        animal.objective = Animal.Objective.None;
        
        animal.FindWork();
        if (animal.state == AnimalState.Idle) {
            // Random walking when nothing else to do
            if (UnityEngine.Random.Range(0, 5) == 0) {
                // animal.GoTo(animal.x + UnityEngine.Random.Range(-1, 2), animal.y);
                animal.task = new GoTask(animal, 
                    animal.world.GetTileAt(animal.x + UnityEngine.Random.Range(-1, 2), animal.y));
                animal.task.Start();
            }
        }
    }

    private void HandleWorking() {
        if (animal.workTile?.blueprint != null && 
                   animal.workTile.blueprint.state == Blueprint.BlueprintState.Constructing) {
            if (animal.workTile.blueprint.ReceiveConstruction(1f * animal.efficiency)){
                animal.state = AnimalState.Idle; // if finished
                animal.task?.Complete(); // completes task to set state. TODO: remove other setting of state above.
            }
        } 
        // crafting!
        // else if (animal.recipe != null && animal.inv.ContainsItems(animal.recipe.inputs) && animal.AtWork()) {
        //     animal.Produce(animal.recipe);
        // } else {
        //     animal.state = AnimalState.Idle;
        // }
        else if (animal.task is CraftTask craftTask) {
            Recipe recipe = craftTask.recipe;
            if (animal.CanProduce(recipe)){
                animal.Produce(recipe);
            } else {
                animal.task.Complete();
            }
            return;
        }
        Debug.Log(animal.aName + " in working state but no work to do");
    }

    private void HandleEeping() {
        animal.eeping.Eep(1f, animal.AtHome());
        // reproduction! 
        if (animal.AtHome() && animal.homeTile.building.reserved < animal.homeTile.building.capacity 
            && animal.homeTile.building.reserved > 2) {
            if (animal.random.Next(0, 50) < 2) {
                AnimalController.instance.AddAnimal(animal.x, animal.y);
            }
        }
        if (animal.eeping.eep >= animal.eeping.maxEep) {
            animal.task.Complete();
        }
    }

    public void UpdateMovement(float deltaTime) {
        // Handle falling
        if (!animal.TileHere().node.standable) {
            animal.nav.Fall();
        }

        if (IsMovingState(animal.state)) {
            if (animal.target == null) {
                // Error handling for missing target
                Debug.LogError("movement target null! " + animal.state.ToString());
                animal.StartDropping();
                //animal.state = AnimalState.Idle;
            }
            else {
                bool done = animal.nav.Move(deltaTime);
                if (done && animal.SquareDistance(animal.x, animal.target.x, animal.y, animal.target.y) < 0.001f) {
                    // Arrived at destination
                    animal.x = animal.target.x;
                    animal.y = animal.target.y;
                    animal.go.transform.position = new Vector3(animal.x, animal.y, 0);

                    HandleArrival();
                }
                animal.sr.flipX = !animal.isMovingRight;
            }
        }
    }
    private bool IsMovingState(AnimalState state) {
        return state == AnimalState.Walking ||
               state == AnimalState.Fetching ||
               state == AnimalState.Delivering || 
               state == AnimalState.Moving;
    }
    private void HandleArrival() {
        // Tile here = animal.TileHere();
        // Debug.Log($"Arrived: state={animal.state}, " + 
        //       $"here=({here.x},{here.y}), " +
        //       $"workTile=({animal.workTile?.x},{animal.workTile?.y}), " +
        //       $"equals={here == animal.workTile}");
        switch (animal.state) {
            case AnimalState.Walking:
                    // Check if we arrived at workTile or homeTile
                if (animal.objective == Animal.Objective.Construct){
                    animal.state = AnimalState.Working;
                } 
                // else if (animal.TileHere() == animal.workTile) {
                //     if (animal.TileHere().building is Plant) { // work tile is plant 
                //         Plant plant = animal.TileHere().building as Plant;
                //         if (plant.harvestable) {
                //             animal.Produce(plant.Harvest());
                //             animal.workTile = null;
                //         }
                //         animal.state = AnimalState.Idle;
                //     }
                //     else { // worktile is not plant 
                //         animal.state = AnimalState.Working;
                //     }
                // } 
                else {
                    animal.state = AnimalState.Idle;
                }
                break;
            case AnimalState.Fetching:
                animal.OnArrivalFetch();
                break;
            case AnimalState.Delivering:
                animal.OnArrivalDeliver();
                break;
            case AnimalState.Moving: // this is the new state that is set when you use a task!
                animal.OnArrival();
                break;
        }
    }
}
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
        animal.ChooseTask();
        if (animal.state == AnimalState.Idle) {
            // Random walking when nothing else to do
            if (UnityEngine.Random.Range(0, 5) == 0) {
                animal.task = new GoTask(animal, 
                    animal.world.GetTileAt(animal.x + UnityEngine.Random.Range(-1, 2), animal.y));
                animal.task.Start();
            }
        }
    }

    private void HandleWorking() {
        if (animal.task is HarvestTask harvestTask) {
            Plant plant = harvestTask.tile.building as Plant;
            if (plant == null || !plant.harvestable) { harvestTask.Fail(); return; }
            animal.workProgress += 1f;
            if (animal.workProgress < plant.plantType.harvestTime) { return; }
            animal.workProgress -= plant.plantType.harvestTime;
            animal.Produce(plant.Harvest());
            harvestTask.Complete();
            return;
        }
        if (animal.task is ConstructTask constructTask){
            Blueprint blueprint = constructTask.blueprint;
            if (blueprint == null) {constructTask.Fail(); return;}
            if (blueprint.ReceiveConstruction(1f * animal.efficiency)){
                constructTask.Complete();
            }
            return;
        }
        else if (animal.task is CraftTask craftTask) {
            Recipe recipe = craftTask.recipe;
            animal.workProgress += 1f;
            if (animal.workProgress < recipe.workload) { return; }
            animal.workProgress -= recipe.workload;
            if (animal.CanProduce(recipe)){
                animal.Produce(recipe);
            } else if (animal.inv.ContainsItems(recipe.inputs)) {
                craftTask.Fail(); // has inputs but can't produce — wrong location
            } else {
                craftTask.Complete(); // out of inputs — done
            }
            return;
        }
        Debug.Log(animal.aName + " in working state but no work to do");
    }

    private void HandleEeping() {
        animal.eeping.Eep(1f, animal.AtHome());
        // reproduction! 
        if (animal.AtHome() && animal.homeTile.building.res.Available()
            && animal.homeTile.building.res.reserved > 2) {
            if (animal.random.Next(0, 50) < 2) {
                AnimalController.instance.AddAnimal(animal.x, animal.y);
            }
        }
        if (animal.eeping.eep >= animal.eeping.maxEep) {
            animal.task.Complete();
        }
    }

    public void UpdateMovement(float deltaTime) {
        // Only fall when standing still — moving animals follow their nav path
        if (animal.state != AnimalState.Moving && !animal.TileHere().node.standable) {
            animal.nav.Fall();
        }
        if (IsMovingState(animal.state)) {
            if (animal.target == null) {
                // Error handling for missing target
                Debug.LogError(animal.aName + " movement target null! failing task " + animal.state.ToString());
                animal.task?.Fail();
                animal.state = Animal.AnimalState.Idle;
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
        return state == AnimalState.Moving;
    }
    private void HandleArrival() {
        // Tile here = animal.TileHere();
        // Debug.Log($"Arrived: state={animal.state}, " + 
        //       $"here=({here.x},{here.y}), " +
        //       $"workTile=({animal.workTile?.x},{animal.workTile?.y}), " +
        //       $"equals={here == animal.workTile}");
        switch (animal.state) {
            case AnimalState.Moving: 
                animal.OnArrival();
                break;
        }
    }
}
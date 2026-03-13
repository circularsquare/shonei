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
                if (!animal.task.Start()) animal.task = null;
            }
        }
    }

    private void HandleWorking() {
        bool hasTool = animal.toolSlotInv.itemStacks[0].item != null;
        float toolMult = hasTool ? 1.25f : 1f;
        float workEfficiency = 1f * animal.efficiency * toolMult;

        if (animal.task is HarvestTask harvestTask) {
            Plant plant = harvestTask.tile.building as Plant;
            if (plant == null || !plant.harvestable) { harvestTask.Fail(); return; }
            animal.workProgress += workEfficiency;
            if (animal.workProgress < plant.plantType.harvestTime) { return; }
            animal.workProgress -= plant.plantType.harvestTime;
            animal.Produce(plant.Harvest());
            harvestTask.Complete();
            return;
        } else if (animal.task is ConstructTask constructTask){
            Blueprint blueprint = constructTask.blueprint;
            if (blueprint == null || blueprint.cancelled) {constructTask.Fail(); return;}
            float progressAmount = workEfficiency;
            float progressAfter = blueprint.constructionProgress + progressAmount;
            if (blueprint.structType.isTile && progressAfter >= blueprint.constructionCost) {
                if (AnimalController.instance.IsAnimalOnTile(blueprint.tile)) {
                    constructTask.Fail(); // leave blueprint intact so another mouse can retry later
                    return;
                }
            }
            if (blueprint.ReceiveConstruction(progressAmount)){
                constructTask.Complete();
            }
            return;
        } else if (animal.task is CraftTask craftTask) {
            Recipe recipe = craftTask.recipe;
            animal.workProgress += workEfficiency;
            if (animal.workProgress < recipe.workload) { return; }
            animal.workProgress -= recipe.workload;
            if (animal.CanProduce(recipe)){
                // Consume inputs and produce outputs, rolling chance for each output
                foreach (ItemQuantity iq in recipe.inputs) animal.Consume(iq.item, iq.quantity);
                foreach (ItemQuantity output in recipe.outputs) {
                    if (output.chance >= 1f || UnityEngine.Random.value < output.chance)
                        animal.Produce(output.item, output.quantity);
                }
                // Track uses and deplete building if applicable
                Building workBuilding = craftTask.workplace?.building;
                if (workBuilding != null && workBuilding.structType.depleteAt > 0) {
                    workBuilding.uses++;
                    if (workBuilding.uses >= workBuilding.structType.depleteAt) {
                        Tile depletedTile = craftTask.workplace;
                        workBuilding.Destroy();
                        StructController.instance.Construct(Db.structTypeByName["platform"], depletedTile);
                        craftTask.Complete();
                        return;
                    }
                }
                craftTask.roundsRemaining--;
                if (craftTask.roundsRemaining <= 0) { craftTask.Complete(); }
            } else if (animal.inv.ContainsItems(recipe.inputs)) {
                craftTask.Fail();
            } else {
                craftTask.Complete(); // out of inputs
            }
            return;
        }
        if (animal.task is ResearchTask) {
            float workload = 10f;
            animal.workProgress += 1f;
            if (animal.workProgress >= workload) {
                animal.workProgress -= workload;
                animal.task.Complete();
            }
            return;
        }
        Debug.Log(animal.aName + " in working state but no work to do");
    }

    private void HandleEeping() {
        animal.eeping.Eep(1f, animal.AtHome());
        // reproduction: logistic growth, gated by population and housing capacity
        if (animal.AtHome()) {
            AnimalController ac = AnimalController.instance;
            if (ac.na < ac.populationCapacity && ac.na < ac.totalHousingCapacity) {
                float p = ac.na;
                float pmax = ac.populationCapacity;
                float birthChance = 0.2f * (pmax - p) / pmax;
                if ((float)animal.random.NextDouble() < birthChance) {
                    ac.AddAnimal(animal.x, animal.y);
                }
            }
        }
        if (animal.eeping.eep >= animal.eeping.maxEep) {
            animal.task.Complete();
        }
    }

    public void UpdateMovement(float deltaTime) {
        // Fall unless the current nav edge is deliberately vertical (ladder/cliff/stair)
        if (!animal.nav.preventFall && !animal.TileHere().node.standable) {
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
                if (done) {
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
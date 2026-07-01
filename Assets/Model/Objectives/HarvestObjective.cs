using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Parks the animal in Working state to harvest a plant. AnimalStateManager.HandleWorking
// ticks progress and calls task.Complete() when harvesting finishes.
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
    // Turn the harvester's back to the camera — a visual cue that farm work is happening
    // (shared by the till/water/plant objectives). Unconditional: the farmer works the plant
    // at their feet, so unlike construction there's no adjacent-tile side-view case.
    public override string ViewOverride => "back";
}

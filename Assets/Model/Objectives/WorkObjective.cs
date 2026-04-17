using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

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

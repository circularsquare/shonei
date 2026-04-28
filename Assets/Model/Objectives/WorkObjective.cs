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
    // Body pose override — mirrors LeisureObjective. Only CraftTask carries a workplace
    // building; other Tasks that use WorkObjective (Construct/Maintenance/Research/Harvest
    // are NOT this objective — they have their own) leave pose null.
    // The wheel uses this with "walk" so the runner sprite cycles its legs while producing
    // power, instead of standing idle. AnimationController routes "walk" to the existing
    // walk animator state — no new pose layer needed.
    public override string PoseOverride {
        get {
            if (task is CraftTask ct) return ct.workplace?.building?.structType.workPose;
            return null;
        }
    }
    // animalstatemanager.HandleWorking will call task.Complete() when it's done!
}

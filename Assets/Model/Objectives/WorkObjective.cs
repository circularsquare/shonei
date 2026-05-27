using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Parks the animal at a workstation to run a Recipe. AnimalStateManager.HandleWorking
// ticks workProgress, consumes inputs, produces outputs each round, and calls
// task.Complete() when roundsRemaining hits 0. PoseOverride pulls the workstation's
// workPose (e.g. "walk" for the wheel runner so its legs cycle while producing power).
public class WorkObjective : Objective {
    private Recipe recipe;
    public WorkObjective(Task task, Recipe recipe) : base(task) {
        this.recipe = recipe;
    }
    public override void Start(){
        // Only CraftTask uses WorkObjective today, always preceded by GoObjective — so the
        // animal SHOULD be at the workNode position when we arrive here. Guard catches a
        // future caller that skips Go, or a race where the workplace was reassigned mid-task.
        //
        // Position-based (not tile-based): workNode may be an off-grid waypoint that snaps
        // the animal to a sub-tile location whose nearest-int tile rounds away from the
        // workplace tile (digging pit's elevated stand-on-dirt-roof spot when the dish is
        // fresh; wheel's centred runner waypoint that lands on the right half of a 2×2).
        // A tile-equality check rejected those legitimate states.
        if (task is CraftTask ct && ct.workplace != null) {
            Building wb = ct.workplace.building;
            Node target = wb?.workNode;
            bool atSpot = target != null
                ? (Mathf.Abs(animal.x - target.wx) < 0.5f && Mathf.Abs(animal.y - target.wy) < 0.5f)
                : animal.TileHere() == ct.workplace;
            if (!atSpot) {
                Debug.LogError($"{animal.aName} WorkObjective.Start: not at workplace ({ct.workplace.x},{ct.workplace.y}), animal at ({animal.x},{animal.y})");
                Fail(); return;
            }
        }
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

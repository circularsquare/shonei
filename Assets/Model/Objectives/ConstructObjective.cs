using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Parks the animal in Working state to construct (or deconstruct) a blueprint.
// AnimalStateManager.HandleWorking ticks progress and calls task.Complete() when done.
public class ConstructObjective : Objective {
    public Blueprint blueprint;
    public ConstructObjective(Task task, Blueprint blueprint) : base(task) {
        this.blueprint = blueprint;
    }
    public override void Start(){
        if (blueprint == null || blueprint.cancelled) { Fail(); return; }
        animal.state = Animal.AnimalState.Working;
        // AnimalStateManager.HandleWorking calls task.Complete() when construction finishes.
    }
    // Facing-view override — turn the builder's back to the camera while it works from
    // INSIDE the footprint (standing on one of the blueprint's own tiles). A builder
    // working from an adjacent ground tile keeps the default side view. Mirrors
    // WorkObjective's data-driven workView, but the trigger is positional, not a flag.
    public override string ViewOverride {
        get {
            if (blueprint == null) return null;
            Tile here = animal.TileHere();
            return here != null && blueprint.FootprintTiles().Contains(here) ? "back" : null;
        }
    }
}

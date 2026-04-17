using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

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
}

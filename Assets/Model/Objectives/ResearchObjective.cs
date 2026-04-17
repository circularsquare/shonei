using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class ResearchObjective : Objective {
    public ResearchObjective(Task task) : base(task) {}
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Working;
        // AnimalStateManager.HandleWorking calls task.Complete() when research finishes.
    }
}

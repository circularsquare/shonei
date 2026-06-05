using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Parks the animal in Working state at a lab. AnimalStateManager.HandleWorking adds
// research progress to the chosen tech and calls task.Complete() when the work tick ends.
public class ResearchObjective : Objective {
    public ResearchObjective(Task task) : base(task) {}
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Working;
        // AnimalStateManager.HandleWorking calls task.Complete() when research finishes.
    }
    // Facing-view override — mirrors WorkObjective. A lab declaring workView:"back" turns
    // its scientist to face the desk while studying. Null (default side view) otherwise.
    public override string ViewOverride => (task as ResearchTask)?.Lab?.structType.workView;
}

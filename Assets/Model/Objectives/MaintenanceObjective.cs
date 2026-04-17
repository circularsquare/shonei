using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Drives the "stand and repair" phase of a MaintenanceTask. The actual condition bump
// happens in AnimalStateManager.HandleWorking (see the MaintenanceTask branch there) —
// this objective just parks the animal in the Working state until that handler calls Complete().
public class MaintenanceObjective : Objective {
    public MaintenanceObjective(Task task) : base(task) {}
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Working;
    }
}

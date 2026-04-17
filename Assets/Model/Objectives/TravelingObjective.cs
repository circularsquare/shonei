using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Hides the animal and waits for durationTicks before reappearing — representing travel time
// to/from the off-screen market. AnimalStateManager.HandleTraveling drives the timer.
public class TravelingObjective : Objective {
    public readonly int durationTicks;
    public TravelingObjective(Task task, int durationTicks) : base(task) {
        this.durationTicks = durationTicks;
    }
    public override string GetObjectiveName() => $"Traveling({durationTicks}t)";
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Traveling;
        animal.go.SetActive(false);
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Created on load to finish an interrupted TravelingObjective.
// After completion the animal becomes idle at x=0 and WOM assigns fresh work.
public class ResumeTravelTask : Task {
    private readonly int remainingTicks;
    public ResumeTravelTask(Animal animal, int remainingTicks) : base(animal) {
        this.remainingTicks = remainingTicks;
    }
    public override bool Initialize() {
        if (remainingTicks <= 0) return false;
        objectives.AddLast(new TravelingObjective(this, remainingTicks));
        return true;
    }
}

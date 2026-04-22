using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class LeisureObjective : Objective {
    public int duration;
    public bool isSocializing; // set per-tick by HandleLeisure when co-seated at a socialWhenShared building
    public LeisureObjective(Task task, int duration) : base(task) {
        this.duration = duration;
    }
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Leisuring;
        // Leisure building: face toward the center of the building
        if (task is LeisureTask leisure && leisure.building != null) {
            float center = leisure.building.x + leisure.building.structType.nx / 2f;
            animal.facingRight = (animal.x < center);
            if (animal.go != null) animal.go.transform.localScale = new Vector3(animal.facingRight ? 1 : -1, 1, 1);
        }
        // AnimalStateManager.HandleLeisure ticks workProgress and calls Complete() when done.
    }
    // Pose comes from the seated building's StructType (JSON-authored). A ReadBookTask on its
    // floor-fallback tile has no seatBuilding → null → no pose override.
    public override string PoseOverride {
        get {
            if (task is LeisureTask lt) return lt.building?.structType.leisurePose;
            if (task is ReadBookTask rb) return rb.seatBuilding?.structType.leisurePose;
            return null;
        }
    }
}

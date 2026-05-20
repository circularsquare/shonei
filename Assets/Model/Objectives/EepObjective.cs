using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Puts the animal into the Eeping state. AnimalStateManager.HandleEeping ticks the
// sleep meter and calls task.Complete() when the mouse is fully rested. The preceding
// GoObjective (when present) has already walked the mouse onto its own id-indexed
// interior tile — see EepTask — so this objective does no repositioning.
public class EepObjective : Objective {
    public EepObjective(Task task): base(task){}
    public override void Start(){
        animal.state = Animal.AnimalState.Eeping;
        // AnimalStateManager.HandleEeping calls task.Complete() when sleep finishes.
    }
}

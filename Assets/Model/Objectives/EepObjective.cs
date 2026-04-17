using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class EepObjective : Objective {
    public EepObjective(Task task): base(task){}
    public override void Start(){
        animal.state = Animal.AnimalState.Eeping;
        // AnimalStateManager.HandleEeping calls task.Complete() when sleep finishes.
    }
}

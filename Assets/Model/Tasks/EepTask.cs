using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class EepTask : Task {
    public override bool IsWork => false;
    public EepTask(Animal animal) : base(animal){}
    public override bool Initialize(){
        if (animal.homeTile == null){
            animal.FindHome();
        }
        // Only walk home if within a reasonable radius; otherwise sleep in place (bed is a bonus,
        // not required). Without this, a walled-off or distant home causes a fail-and-retry loop
        // since the animal remains eepy and re-picks EepTask immediately.
        if (animal.homeTile != null && animal.nav.WithinRadius(animal.nav.PathTo(animal.homeTile), MediumFindRadius)){
            objectives.AddLast(new GoObjective(this, animal.homeTile));
        }
        objectives.AddLast(new EepObjective(this));
        return true;
    }
}

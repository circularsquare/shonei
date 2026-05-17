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
        if (animal.homeBuilding == null){
            animal.FindHome();
        }
        if (animal.homeBuilding == null) {
            // Homeless — sleep where we stand. The bed is a bonus, not required.
            objectives.AddLast(new EepObjective(this));
            return true;
        }
        // Pick the sleep target — an interior node for doored housing (graph routes the
        // mouse through the door automatically), or the building's anchor tile node for
        // legacy 1×1 housing (today's behaviour: walk to tile, sleep on top). EepTask
        // doesn't care which; either way it's just "path to this Node, then sleep."
        Building home = animal.homeBuilding;
        Node target = (home.interiorNodes != null && home.interiorNodes.Length > 0)
            ? home.interiorNodes[0]
            : home.workNode;
        if (target != null && animal.nav.WithinRadius(animal.nav.PathTo(target), MediumFindRadius)) {
            objectives.AddLast(new GoObjective(this, target));
        }
        objectives.AddLast(new EepObjective(this));
        return true;
    }
}

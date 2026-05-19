using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Sends the animal home to sleep. Triggered by Eeping.ShouldSleep — at night or any
// time when eep < 50%. Homeless mice sleep where they stand.
//
// Queue: Go(home interior node) → Eep.   (Eep only, for homeless.)
// Reserves: Nothing (residents stack at the same interior node; no per-slot reservation).
// IsWork = false.
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
        // Pick the sleep target — the first interior node for doored housing (graph
        // routes the mouse through the door automatically), or the building's workNode
        // for any doorless legacy housing. All residents stack at the same node for
        // v1: simplest, no per-mouse slot tracking needed. Visible stacking in fully-
        // occupied housing is the known downside; revisit with per-slot rendering
        // offsets once Reservable can track per-resident slot indices.
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

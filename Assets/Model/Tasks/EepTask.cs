using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Sends the animal home to sleep. Triggered by Eeping.ShouldSleep — at night or any
// time when eep < 50%. Homeless mice sleep where they stand.
//
// Queue: Go(own interior tile) → Eep.   (Eep only, for homeless.)
// Reserves: Nothing. Each resident is id-indexed onto an interior tile; collisions when
// two residents share id % interiorNodes.Length are accepted (cosmetic only).
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
        // Pick the sleep target — an id-indexed interior tile for doored housing, or the
        // building's workNode for any doorless legacy housing. interiorNodes are edged
        // together, so A* walks the mouse all the way onto its own tile (through the door,
        // up an interior ladder if needed) — no end-of-path snap. The id index is stable,
        // so a mouse always returns to the same spot. Two residents sharing
        // id % interiorNodes.Length overlap; that's accepted (purely cosmetic).
        Building home = animal.homeBuilding;
        Node target = (home.interiorNodes != null && home.interiorNodes.Length > 0)
            ? home.interiorNodes[Mathf.Abs(animal.id) % home.interiorNodes.Length]
            : home.workNode;
        if (target != null && animal.nav.WithinRadius(animal.nav.PathTo(target), MediumFindRadius)) {
            objectives.AddLast(new GoObjective(this, target));
        }
        objectives.AddLast(new EepObjective(this));
        return true;
    }
}

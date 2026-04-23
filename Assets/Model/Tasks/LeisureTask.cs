using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Generic "go to a leisure building and spend time there" task.
// Takes a leisure need (e.g. "fireplace", "bench") and delegates seat selection to
// Nav.FindPathToLeisureSeat — same Chebyshev-sort + first-fit-within-radius pattern as
// FindPathToStruct / FindPathToInv. Filter = leisureNeed match + Building.CanHostLeisureNow().
// Benefit dispatch happens via Happiness.NoteLeisure(need, building.structType.leisureGrant).
public class LeisureTask : Task {
    public string leisureNeed; // authoritative field — building is resolved from it in Initialize
    public Building building;  // set on successful Initialize; read by HandleLeisure (socialWhenShared co-presence)
    public Tile seatTile;      // the specific work tile this animal is heading to
    private int seatIndex = -1; // index into building.seatRes[] for per-seat reservation

    public LeisureTask(Animal animal, string leisureNeed) : base(animal) {
        this.leisureNeed = leisureNeed;
    }

    public override bool Initialize() {
        if (string.IsNullOrEmpty(leisureNeed)) return false;
        var (path, b, idx) = animal.nav.FindPathToLeisureSeat(
            b => b.structType.leisureNeed == leisureNeed && b.CanHostLeisureNow());
        if (path == null) return false;

        b.seatRes[idx].Reserve(this);
        building = b;
        seatIndex = idx;
        seatTile = path.tile;

        objectives.AddLast(new GoObjective(this, path.tile));
        objectives.AddLast(new LeisureObjective(this, 15));
        return true;
    }

    public override void Complete() {
        // Grant the happiness benefit for this building's leisure need, scaled by leisureGrant.
        string need = building.structType.leisureNeed;
        if (!string.IsNullOrEmpty(need)) {
            animal.happiness.NoteLeisure(need, building.structType.leisureGrant);
        } else {
            Debug.LogError($"LeisureTask.Complete: building '{building.structType.name}' has no leisureNeed");
        }

        // Social satisfaction for socialWhenShared buildings is granted per-tick in HandleLeisure.
        base.Complete();
    }

    public override void Cleanup() {
        if (seatIndex >= 0 && building != null && building.seatRes != null)
            building.seatRes[seatIndex].Unreserve();
        base.Cleanup();
    }
}

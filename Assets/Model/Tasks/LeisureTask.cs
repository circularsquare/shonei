using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Generic "go to a leisure building and spend time there" task.
// The building dispatches its benefit on completion (fireplace → warmth, etc.).
// Adding a new leisure building: add a case in Complete() for the building name.
public class LeisureTask : Task {
    public Building building;
    public Tile seatTile; // the specific work tile this animal is heading to
    private int seatIndex = -1; // index into building.seatRes[] for per-seat reservation

    public LeisureTask(Animal animal, Building building) : base(animal) {
        this.building = building;
    }

    public override bool Initialize() {
        if (building == null || building.disabled || building.IsBroken) return false;
        Path bestPath = null;

        if (building.seatRes != null) {
            // Per-seat reservation: find the first seat that is available AND reachable within radius
            for (int i = 0; i < building.seatRes.Length; i++) {
                if (!building.seatRes[i].Available()) continue;
                Tile seat = building.WorkTileAt(i);
                if (seat == null) continue;
                Path p = animal.nav.PathTo(seat);
                if (animal.nav.WithinRadius(p, MediumFindRadius)) {
                    building.seatRes[i].Reserve(animal.aName);
                    seatIndex = i;
                    bestPath = p;
                    seatTile = seat;
                    break;
                }
            }
        } else {
            // Legacy single-res path for non-leisure buildings (shouldn't normally hit)
            if (building.res != null) {
                if (!building.res.Available()) return false;
                building.res.Reserve(animal.aName);
            }
            for (int i = 0; i < building.structType.nworkTiles.Length; i++) {
                Tile seat = building.WorkTileAt(i);
                if (seat == null) continue;
                Path p = animal.nav.PathTo(seat);
                if (animal.nav.WithinRadius(p, MediumFindRadius)) { bestPath = p; seatTile = seat; break; }
            }
        }

        // Fall back to adjacent if no direct path to any seat
        if (bestPath == null) {
            Path adj = animal.nav.PathToOrAdjacent(building.workTile);
            if (animal.nav.WithinRadius(adj, MediumFindRadius)) {
                bestPath = adj;
                seatTile = adj.tile;
            }
        }
        if (bestPath == null) {
            return false; // Start() calls Cleanup() which unreserves via seatIndex/building.res
        }

        objectives.AddLast(new GoObjective(this, bestPath.tile));
        objectives.AddLast(new LeisureObjective(this, 15));
        return true;
    }

    public override void Complete() {
        // Grant the happiness benefit for this building's leisure need
        string need = building.structType.leisureNeed;
        if (!string.IsNullOrEmpty(need)) {
            animal.happiness.NoteLeisure(need);
        } else {
            Debug.LogError($"LeisureTask.Complete: building '{building.structType.name}' has no leisureNeed");
        }

        // Social satisfaction for socialWhenShared buildings is granted per-tick in HandleLeisure.
        base.Complete();
    }

    public override void Cleanup() {
        if (seatIndex >= 0 && building.seatRes != null)
            building.seatRes[seatIndex].Unreserve();
        else if (building.res != null)
            building.res.Unreserve();
        base.Cleanup();
    }
}

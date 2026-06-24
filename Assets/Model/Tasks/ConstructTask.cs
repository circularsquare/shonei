using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Builds (or deconstructs) a blueprint. Spawned from WOM Construct/Deconstruct orders.
//
// Queue: Go(adjacent to blueprint) → Construct.
// Reserves: Nothing (blueprint already holds delivered materials; no per-task reservation).
// Initialize rejects suspended blueprints and blueprints that would cause an items-fall
// or storage-empty hazard; for the deconstruct case it promotes hauls to clear those first.
public class ConstructTask : Task {
    public Blueprint blueprint;
    public bool deconstructing;
    // Ticks of construction this task has put in. When it reaches Animal.MaxWorkStintTicks the
    // builder yields (see AnimalStateManager.HandleWorking) so it re-evaluates needs/priorities;
    // a fresh ConstructTask resumes against the blueprint's persisted progress.
    public int ticksWorked;
    public ConstructTask(Animal animal, Blueprint bp, bool deconstructing = false) : base(animal){
        this.blueprint = bp;
        this.deconstructing = deconstructing;
    }
    public override bool Initialize() {
        if (blueprint == null || blueprint.cancelled) return false;
        // State guard (was previously implicit in FindPathsAdjacentToBlueprints filter)
        var expectedState = deconstructing ? Blueprint.BlueprintState.Deconstructing : Blueprint.BlueprintState.Constructing;
        if (blueprint.state != expectedState) return false;
        // Defensive: don't initialize on a suspended blueprint even if a stale order somehow
        // dispatched here (e.g. support vanished between order registration and pickup).
        // Order.isActive=ConditionsMet should already filter these out — this is belt-and-suspenders.
        if (!deconstructing && blueprint.IsSuspended()) return false;
        if (blueprint.WouldCauseItemsFall()) {
            if (deconstructing) WorkOrderManager.instance?.PromoteHaulsFor(blueprint);
            return false;
        }
        if (blueprint.StorageNeedsEmptying()) {
            if (deconstructing) WorkOrderManager.instance?.PromoteHaulsFor(blueprint);
            return false;
        }
        Path standPath = blueprint.structType.isTile
            ? animal.nav.PathStrictlyAdjacent(blueprint.tile)
            : animal.nav.PathToOrAdjacentBlueprint(blueprint);
        if (!animal.nav.WithinWorkRange(standPath)) return false;
        objectives.AddLast(new GoObjective(this, standPath.tile));
        objectives.AddLast(new ConstructObjective(this, blueprint));
        return true;
    }
}

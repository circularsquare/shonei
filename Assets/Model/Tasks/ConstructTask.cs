using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class ConstructTask : Task {
    public Blueprint blueprint;
    public bool deconstructing;
    public ConstructTask(Animal animal, Blueprint bp, bool deconstructing = false) : base(animal){
        this.blueprint = bp;
        this.deconstructing = deconstructing;
    }
    public override bool Initialize() {
        if (blueprint == null || blueprint.cancelled) return false;
        // State guard (was previously implicit in FindPathsAdjacentToBlueprints filter)
        var expectedState = deconstructing ? Blueprint.BlueprintState.Deconstructing : Blueprint.BlueprintState.Constructing;
        if (blueprint.state != expectedState) return false;
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
            : animal.nav.PathToOrAdjacent(blueprint.centerTile);
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;
        objectives.AddLast(new GoObjective(this, standPath.tile));
        objectives.AddLast(new ConstructObjective(this, blueprint));
        return true;
    }
}

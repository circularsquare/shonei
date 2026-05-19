using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Harvests a Plant in place. Spawned from WOM Harvest orders registered by Plant
// when it becomes harvestable.
//
// Queue: Go(plant tile) → Harvest → Drop(product₁) → Drop(product₂) → …
// Reserves: Nothing (the plant itself isn't a reservable resource — harvestable flag gates it).
public class HarvestTask : Task {
    public Tile tile;
    public HarvestTask(Animal animal, Tile tile) : base(animal){
        this.tile = tile;
    }
    public override bool Initialize() {
        Plant plant = tile.plant;
        if (plant == null) return false;
        if (!plant.harvestable) return false;
        // Reject unreachable plants and plants whose actual path is significantly longer than the
        // medium search radius (e.g. 5 tiles crow-flies but 150 tiles around a chasm).
        if (!animal.nav.WithinRadius(animal.nav.PathTo(tile), MediumFindRadius)) return false;

        objectives.AddLast(new GoObjective(this, tile));
        objectives.AddLast(new HarvestObjective(this, plant));
        foreach (ItemQuantity output in plant.plantType.products){
            objectives.AddLast(new DropObjective(this, output.item));
        }
        return true;
    }
}

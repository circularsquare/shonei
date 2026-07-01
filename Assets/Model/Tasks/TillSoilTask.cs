using UnityEngine;

// Tills a dirt tile into farmland so a till-requiring crop (wheat/rice/soybean) can be planted
// on it. Spawned from WOM Till orders, which a till-requiring crop blueprint registers on the
// dirt directly below itself (see WorkOrderManager.RegisterTill). The farmer stands on the tile
// above the soil (where the crop will grow) and works the ground for TillTime.
//
// Tilling needs no materials — just farmer labor. On completion the soil's `tilled` flag flips
// (persistent: a replant reuses it without re-tilling), which un-suppresses the crop's
// Construct/Supply orders (gated by Blueprint.ConditionsMet → TillReady).
//
// Queue: Go(tile above soil) → Till(work)
// Reserves: Nothing (the order's isActive=!soil.tilled gates duplicate work).
public class TillSoilTask : Task {
    public Tile soil;                 // the dirt tile being tilled

    // In-game seconds (ticks) of farmer labor to till one tile. Tunable starting point.
    public const float TillTime = 10f;

    public TillSoilTask(Animal animal, Tile soil) : base(animal) {
        this.soil = soil;
    }

    public override bool Initialize() {
        if (animal.job.name != "farmer") return false;
        if (soil == null || soil.tilled) return false;
        if (soil.type.name != "dirt" && soil.type.name != "dirt_placed") return false;

        // Farmer works the ground from the tile directly above the soil (where the crop grows).
        Tile standTile = World.instance.GetTileAt(soil.x, soil.y + 1);
        if (standTile == null) return false;
        if (!animal.nav.WithinWorkRange(animal.nav.PathTo(standTile))) return false;

        objectives.AddLast(new GoObjective(this, standTile));
        objectives.AddLast(new TillObjective(this));
        return true;
    }

    // Converts the dirt tile to the "dirttilled" type (farmland) and drops the now-satisfied Till
    // order. Swapping the type IS the tilled state — it persists, renders its own body sprite, and
    // the tile-type setter clears the grass overlay for free. Called by AnimalStateManager.
    // HandleWorking once TillTime of work accumulates.
    public void DoTill() {
        if (soil == null || soil.tilled) return;
        soil.type = Db.tileTypeByName["dirttilled"];
        WorkOrderManager.instance?.UnregisterTill(soil);
    }
}

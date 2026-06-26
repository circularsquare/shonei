using UnityEngine;

// Waters a thirsty plant's moisture reservoir: fetches the "water" item, walks to the plant, and
// pours — converting carried water into moisture at the world's pump/seep exchange rate
// (MoistureSystem.MoistureForWaterFen). The reservoir is the soil tile below (ground crops &
// ground-mode greenhouses) or a self-contained greenhouse's isolated pool. Spawned from WOM Water
// orders (priority 3, below Harvest) that a plant carries while its reservoir sits at/below the
// plant's moisture-comfort floor AND the colony holds water.
//
// Queue: Fetch(water) → Go(plant tile) → Water(pour into reservoir)
// Reserves: the fetched water stack (released by base.Cleanup).
public class WaterPlantTask : Task {
    public Tile tile;                 // the plant's tile
    private readonly Item waterItem;

    // In-game seconds the farmer spends pouring before the soil is watered.
    public const float WaterTime = 5f;

    public WaterPlantTask(Animal animal, Tile tile) : base(animal) {
        this.tile = tile;
        waterItem = Db.itemByName["water"];
    }

    // Soil-moisture level this task tops the tile up to — the plant's comfort upper bound
    // (or full saturation when the plant declares no upper bound).
    private static int TargetMoisture(PlantType pt) => pt.moistureMax ?? MoistureSystem.MoistureMax;

    public override bool Initialize() {
        if (animal.job.name != "farmer") return false;
        Plant plant = tile.plant;
        if (plant == null) return false;
        if (!plant.plantType.moistureMin.HasValue) return false;   // no comfort floor → never waters

        // Water the plant's reservoir — the soil below (ground) or a self-contained greenhouse pool.
        int rm = plant.ReservoirMoisture();
        if (rm < 0) return false;
        // Only act when the reservoir is at/below the comfort floor (matches the WOM order's isActive gate).
        if (rm > plant.plantType.moistureMin.Value) return false;

        int deficit = TargetMoisture(plant.plantType) - rm;
        if (deficit <= 0) return false;
        int fenNeeded = MoistureSystem.WaterFenForMoisture(deficit);
        if (fenNeeded <= 0) return false;

        // Reject unreachable plants and plants whose actual path winds far around terrain.
        if (!animal.nav.WithinWorkRange(animal.nav.PathTo(tile))) return false;

        // Find water to carry.
        (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(waterItem);
        if (itemPath == null) return false;
        int available = stack.quantity - stack.resAmount;
        int qty = Mathf.Min(fenNeeded, available);
        if (qty <= 0) return false;

        ItemQuantity iq = new ItemQuantity(waterItem, qty);
        FetchAndReserve(iq, itemPath.tile, stack);
        objectives.AddLast(new GoObjective(this, tile));
        objectives.AddLast(new WaterObjective(this, plant));
        return true;
    }

    // Pours the carried water into the plant's reservoir (soil below or self-contained greenhouse
    // pool), consuming exactly the fen needed for the moisture actually added (the reservoir may have
    // dried or been watered since Initialize). Any leftover water stays in the animal's inventory and
    // is offloaded by the normal drop policy.
    public void PourWater() {
        Plant plant = tile.plant;
        if (plant == null) return;
        int rm = plant.ReservoirMoisture();
        if (rm < 0) return;
        int held = animal.inv.Quantity(waterItem);
        if (held <= 0) return;
        int room = TargetMoisture(plant.plantType) - rm;
        if (room <= 0) return;
        int gain = Mathf.Min(MoistureSystem.MoistureForWaterFen(held), room);
        if (gain <= 0) return;
        int fenUsed = Mathf.Min(held, MoistureSystem.WaterFenForMoisture(gain));
        if (fenUsed > 0) animal.Consume(waterItem, fenUsed);
        plant.AddReservoirMoisture(gain);
    }
}

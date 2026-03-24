
/// <summary>
/// A 2-tile workstation (anchor=left, pump head=right).
/// Inactive (suppresses WOM craft order) whenever the pump-head tile has no water.
/// Water draining/filling gates the order lazily on each ChooseOrder cycle — no polling needed.
///
/// Drains WaterDrainPerRound units from the source tile each time a craft round completes,
/// called by AnimalStateManager after producing outputs.
/// </summary>
public class PumpBuilding : Building {
    /// <summary>
    /// Water units drained from the source tile per completed craft round.
    /// WaterMax=160 = one full tile. Final intended value: WaterMax/5 = 32 (1/5 tile per round).
    /// Raised to WaterMax temporarily for easier testing.
    /// </summary>
    private const int WaterDrainPerRound = WaterController.WaterMax/32;

    public PumpBuilding(StructType st, int x, int y) : base(st, x, y) { }

    public override bool IsActive() {
        // Pump head is at dx=1; check the tile *below* it for water
        Tile belowPumpTile = World.instance.GetTileAt(x + 1, y - 1);
        return belowPumpTile != null && belowPumpTile.water > 0;
    }

    /// <summary>
    /// Called by AnimalStateManager after each completed craft round.
    /// Drains WaterDrainPerRound units from the source tile (clamped to 0).
    /// </summary>
    public void DrainForCraft() {
        Tile waterTile = World.instance.GetTileAt(x + 1, y - 1);
        if (waterTile == null) return;
        waterTile.water = (ushort)UnityEngine.Mathf.Max(0, waterTile.water - WaterDrainPerRound);
    }
}

// A 2-tile workstation (anchor=left, pump head=right).
// Inactive (suppresses WOM craft order) whenever the pump-head tile has no water.
// Water draining/filling gates the order lazily on each ChooseOrder cycle — no polling needed.
//
// Drains WaterDrainPerRound units from the source tile each time a craft round completes,
// called by AnimalStateManager after producing outputs.
public class PumpBuilding : Building {
    // Water units drained from the source tile per completed craft round.
    // WaterMax=160 = one full tile. Final intended value: WaterMax/5 = 32 (1/5 tile per round).
    // Raised to WaterMax temporarily for easier testing.
    private const int WaterDrainPerRound = WaterController.WaterMax/32;

    public PumpBuilding(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    // Pump head is at dx=1 normally; mirrored pump head is at dx=0.
    private int PumpHeadDx => mirrored ? 0 : 1;

    public override bool ConditionsMet() {
        Tile belowPumpTile = World.instance.GetTileAt(x + PumpHeadDx, y - 1);
        return belowPumpTile != null && belowPumpTile.water > 0;
    }

    // Called by AnimalStateManager after each completed craft round.
    // Drains WaterDrainPerRound units from the source tile (clamped to 0).
    public void DrainForCraft() {
        Tile waterTile = World.instance.GetTileAt(x + PumpHeadDx, y - 1);
        if (waterTile == null) return;
        waterTile.water = (ushort)UnityEngine.Mathf.Max(0, waterTile.water - WaterDrainPerRound);
    }
}

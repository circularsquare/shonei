/// <summary>
/// A 2-tile workstation (anchor=left, pump head=right).
/// Inactive (suppresses WOM craft order) whenever the pump-head tile has no water.
/// Water draining/filling gates the order lazily on each ChooseOrder cycle — no polling needed.
/// </summary>
public class PumpBuilding : Building {
    public PumpBuilding(StructType st, int x, int y) : base(st, x, y) { }

    public override bool IsActive() {
        // Pump head is at dx=1; check the tile *below* it for water
        Tile belowPumpTile = World.instance.GetTileAt(x + 1, y - 1);
        return belowPumpTile != null && belowPumpTile.water > 0;
    }
}

using System.Collections.Generic;

// A 2-tile workstation (anchor=left, pump head=right).
// Inactive (suppresses WOM craft order) whenever the pump-head tile has no water.
// Water draining/filling gates the order lazily on each ChooseOrder cycle — no polling needed.
//
// Drains WaterDrainPerRound units from the source tile each time a craft round completes,
// called by AnimalStateManager after producing outputs.
//
// Implements IPowerConsumer directly (rather than letting Building.OnPlaced auto-wrap
// with the perimeter-port BuildingPowerConsumer) because the pump's right tile is the
// pump head/spout — visually solid, no shaft can attach there. Authoring custom ports
// here keeps the port-stub visuals on just the two sides where an axle makes sense.
public class PumpBuilding : Building, PowerSystem.IPowerConsumer {
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

    public override void OnPlaced() {
        // base.OnPlaced does WOM order registration AND the auto-wrapper path. Auto-wrapper
        // is suppressed here because we implement IPowerConsumer directly (Building.EnsurePowerConsumer
        // skips when `this is IPowerConsumer`), so we need to register ourselves explicitly.
        base.OnPlaced();
        PowerSystem.instance?.RegisterConsumer(this);
    }

    public override void AttachAnimations() {
        base.AttachAnimations();
        AttachPortStubs(Ports);
    }

    // ── IPowerConsumer ────────────────────────────────────────────────
    public Structure Structure => this;

    // Demand mirrors BuildingPowerConsumer's gate exactly: only while a mouse is in
    // WorkObjective at the pump. Idle / unmanned = 0, so an empty pump doesn't drain
    // its network or its flywheels.
    public float CurrentDemand =>
        !disabled && !IsBroken && HasActiveCrafter() ? BuildingPowerConsumer.Demand : 0f;

    public IEnumerable<PowerSystem.PowerPort> Ports {
        get {
            // Two ports only:
            //   - Left side of the anchor tile (horizontal axle entering from the left).
            //   - Bottom of the anchor tile (vertical axle entering from below).
            // Mirroring flips dx via PowerSystem.FindAttachedNetwork's standard rule, so a
            // mirrored pump (head on the left, anchor on the right) ends up taking power
            // from the right + bottom-right naturally — same axle layout, mirrored.
            yield return new PowerSystem.PowerPort(-1, 0, PowerSystem.Axis.Horizontal);
            yield return new PowerSystem.PowerPort( 0,-1, PowerSystem.Axis.Vertical);
        }
    }
}

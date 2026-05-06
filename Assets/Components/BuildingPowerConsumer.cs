using System.Collections.Generic;
using UnityEngine;

// Generic power-consumer wrapper for Buildings whose StructType declares powerBoost > 1.
// Used by pump and press out of the box (no subclass needed) — Building.OnPlaced
// instantiates and registers one. Future powered-by-default buildings get the same
// treatment for free as long as they declare powerBoost in JSON.
//
// The port spec is a single port at the building anchor with axis Both. That works
// for 1×1 buildings (press) and for pump (anchor at its left tile, where a horizontal
// shaft can attach). Buildings that need a different port layout should subclass and
// implement IPowerConsumer directly instead of relying on this wrapper.
public class BuildingPowerConsumer : PowerSystem.IPowerConsumer {
    public Building building { get; }

    // Demand value reported to PowerSystem.Allocate. Constant 1.0 for v1 — power
    // allocation is binary (full or none). Future fractional-fulfilment can use this
    // to express priority / scaling.
    public const float Demand = 1.0f;

    public BuildingPowerConsumer(Building b) { this.building = b; }

    public Structure Structure => building;

    // Demand is gated on a mouse actually being in WorkObjective at the building.
    // Without this, an unmanned pump permanently sucks power from its network — wasting
    // wheel output and draining flywheels for nothing. With it, demand exactly matches
    // the cycles the operator is running, so power expenditure tracks work done.
    public float CurrentDemand =>
        building != null && !building.disabled && !building.IsBroken
        && building.HasActiveCrafter() ? Demand : 0f;

    public IEnumerable<PowerSystem.PowerPort> Ports => GetPerimeterPorts(building);

    // Perimeter ports: one Axis.Both port for every tile around the building's footprint
    // — top, bottom, left, right. Lets the player route a shaft to any adjacent tile and
    // have it count, instead of forcing the shaft onto the building's own anchor tile
    // (which usually overlaps the operator position). Mirroring is handled by
    // PowerSystem.FindAttachedNetwork's standard mirror flip on X offsets, so the
    // perimeter is correct in both orientations.
    //
    // Exposed as a static helper so Building.AttachAnimations can spawn matching port
    // stubs without needing the wrapper instance (the wrapper only exists post-OnPlaced,
    // but AttachAnimations runs in the Structure constructor — earlier than OnPlaced).
    public static IEnumerable<PowerSystem.PowerPort> GetPerimeterPorts(Building b) {
        int nx = b.structType.nx;
        int ny = Mathf.Max(1, b.structType.ny);
        for (int i = 0; i < nx; i++) {
            yield return new PowerSystem.PowerPort(i, -1, PowerSystem.Axis.Both); // bottom
            yield return new PowerSystem.PowerPort(i, ny, PowerSystem.Axis.Both); // top
        }
        for (int j = 0; j < ny; j++) {
            yield return new PowerSystem.PowerPort(-1, j, PowerSystem.Axis.Both); // left
            yield return new PowerSystem.PowerPort(nx, j, PowerSystem.Axis.Both); // right
        }
    }
}

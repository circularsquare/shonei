// A power-transmission tile. Three StructTypes share this subclass:
//   power shaft       — straight; axis switches between horizontal (rotation 0/2) and
//                        vertical (rotation 1/3). Rotation is the only way to make it vertical.
//   power shaft turn  — corner / turning shaft; axis is always Both (connects on either side
//                        of the corner). Rotation cycles which corner the bend visually faces.
//                        Mirroring (F key) is also meaningful for the corner asymmetry.
//   power shaft 4     — 4-way junction; axis is always Both. Connectivity is identical to
//                        `power shaft turn` — the difference is purely visual (the sprite
//                        shows shaft stubs on all four sides, signalling that this is a
//                        branch point rather than a corner).
//
// Inherits Structure (not Building) — shafts have no workstation, no storage, no fuel.
// Depth 4 — shafts have their own slot in tile.structs[], so they can coexist with
// buildings, platforms, foreground decorations, and roads on the same tile. Visually
// they render at sortingOrder 5 (between roads and buildings — see Structure.cs), so
// shafts appear *behind* most things, like wall-mounted plumbing.
public class PowerShaft : Structure {
    public PowerSystem.Axis axis { get; private set; }

    public PowerShaft(StructType st, int x, int y, bool mirrored = false, int rotation = 0)
        : base(st, x, y, mirrored, rotation)
    {
        axis = ComputeAxis(st.name, rotation);
    }

    static PowerSystem.Axis ComputeAxis(string name, int rotation) {
        // Both `turn` and `power shaft 4` connect on all 4 sides — the difference is the
        // sprite, not the topology rule.
        if (name == "power shaft turn" || name == "power shaft 4") return PowerSystem.Axis.Both;
        // Straight shaft: rotation 0 / 180 are horizontal, 90 / 270 are vertical.
        return (rotation % 2 == 0) ? PowerSystem.Axis.Horizontal : PowerSystem.Axis.Vertical;
    }

    public override void OnPlaced() {
        PowerSystem.instance?.RegisterShaft(this);
    }

    public override void Destroy() {
        PowerSystem.instance?.UnregisterShaft(this);
        base.Destroy();
    }

    // Animate shaft tiles whenever their network is currently flowing — i.e. at least one
    // consumer is powered, or storage is absorbing surplus. Both `power shaft` and
    // `power shaft turn` use this same override; the sheet name is taken from the StructType.
    public override void AttachAnimations() {
        AttachFrameAnimator(structType.name.Replace(" ", ""),
            () => PowerSystem.instance != null && PowerSystem.instance.IsShaftActivelyTransmitting(this),
            baseFps: 6f);
    }
}

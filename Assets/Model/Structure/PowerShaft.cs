// A power-transmission tile. Three StructTypes share this subclass:
//   power shaft       — straight; mates on the two ends of its axis. Rotation 0/2 = horizontal
//                        (Left+Right), 1/3 = vertical (Up+Down).
//   power shaft turn  — corner / turning shaft; mates on exactly TWO adjacent sides (one
//                        horizontal, one vertical). Rotation chooses which corner; F mirrors it.
//                        It is NOT a 4-way: a turn only continues the run on its two open sides.
//   power shaft 4     — 4-way junction; mates on all four sides. Rotationally symmetric.
//
// Connectivity is modelled per-side (`openSides`), not per-axis, precisely so the turn can be
// directional. The older `axis` field (Horizontal/Vertical/Both) is now *derived* from openSides
// and used only for producer/consumer port coupling, where a corner legitimately presents both a
// horizontal and a vertical face. Shaft-to-shaft connection always goes through openSides.
//
// Inherits Structure (not Building) — shafts have no workstation, no storage, no fuel.
// Depth 4 — shafts have their own slot in tile.structs[], so they can coexist with
// buildings, platforms, foreground decorations, and roads on the same tile. Visually
// they render at sortingOrder 5 (between roads and buildings — see Structure.cs), so
// shafts appear *behind* most things, like wall-mounted plumbing.
using Side = PowerSystem.Side;
using Axis = PowerSystem.Axis;

public class PowerShaft : Structure {
    // The cardinal sides this shaft mates on — the single source of truth for connectivity.
    public Side openSides { get; private set; }

    // The axis a producer/consumer port couples to here: a shaft "carries" the horizontal axis
    // if it opens a left/right side, vertical if up/down. A corner opens one of each, so it
    // presents Both faces to ports — even though it only *continues a run* on its two open sides.
    public Axis axis {
        get {
            bool h = (openSides & (Side.Left | Side.Right)) != 0;
            bool v = (openSides & (Side.Up | Side.Down)) != 0;
            if (h && v) return Axis.Both;
            return h ? Axis.Horizontal : Axis.Vertical;
        }
    }

    public PowerShaft(StructType st, int x, int y, bool mirrored = false, int rotation = 0)
        : base(st, x, y, mirrored, rotation)
    {
        openSides = ComputeOpenSides(st.name, rotation, mirrored);
    }

    // Maps a shaft StructType + orientation to the cardinal sides it mates on.
    //   power shaft 4    — all four sides; rotation/mirror irrelevant.
    //   power shaft turn — corner: the base sprite (rotation 0, unmirrored) joins UP and LEFT.
    //                      Mirroring flips Left<->Right first (SpriteRenderer.flipX in local
    //                      space), then each rotation step rotates the open sides 90° clockwise
    //                      to match the parent transform's -90°/step spin.
    //   power shaft      — straight: both ends of its axis (rotation 0/2 = L+R, 1/3 = U+D).
    static Side ComputeOpenSides(string name, int rotation, bool mirrored) {
        if (name == "power shaft 4")
            return Side.Left | Side.Right | Side.Up | Side.Down;
        if (name == "power shaft turn") {
            Side baseSides = Side.Up | (mirrored ? Side.Right : Side.Left);
            return RotateCW(baseSides, rotation);
        }
        return (rotation % 2 == 0) ? (Side.Left | Side.Right) : (Side.Up | Side.Down);
    }

    // Rotate a set of sides 90° clockwise per step (Up->Right->Down->Left->Up), `steps` times.
    static Side RotateCW(Side sides, int steps) {
        steps = ((steps % 4) + 4) % 4;
        for (int i = 0; i < steps; i++) {
            Side r = Side.None;
            if ((sides & Side.Up)    != 0) r |= Side.Right;
            if ((sides & Side.Right) != 0) r |= Side.Down;
            if ((sides & Side.Down)  != 0) r |= Side.Left;
            if ((sides & Side.Left)  != 0) r |= Side.Up;
            sides = r;
        }
        return sides;
    }

    // ── Shaft-connection support ──────────────────────────────────────────
    // True if `st` is one of the shaft StructTypes (the three that map to this subclass).
    public static bool IsShaft(StructType st) =>
        st != null && (st.name == "power shaft" || st.name == "power shaft turn" || st.name == "power shaft 4");

    // The sides a shaft StructType opens on at the given orientation. Public so placement /
    // suspension checks can reason about a shaft before it's instantiated.
    public static Side OpenSidesFor(StructType st, int rotation, bool mirrored) =>
        ComputeOpenSides(st.name, rotation, mirrored);

    // (offset, side of THIS shaft facing the neighbour, side of the NEIGHBOUR that must be open).
    static readonly (int dx, int dy, Side mine, Side theirs)[] NeighbourDirs = {
        (-1, 0, Side.Left,  Side.Right),
        ( 1, 0, Side.Right, Side.Left),
        ( 0, -1, Side.Down, Side.Up),
        ( 0,  1, Side.Up,   Side.Down),
    };

    // True if a shaft of type `st` (orientation rotation/mirrored) placed at `tile` would connect
    // to at least one existing shaft — a neighbour whose facing side mates with ours. This is
    // shaft-specific "support": a connected run of shafts is self-bearing (rigid axles
    // cantilevering off the run), so a shaft that hooks onto the run is placeable without solid
    // ground beneath it. The connecting neighbour traces back recursively to a grounded anchor,
    // so runs never float. Connection is directional — a turn only mates on its two open sides,
    // so e.g. a horizontal shaft can't be supported by a shaft above/below it.
    //
    // includeBlueprints: placement passes true so a whole run can be queued at once (hook onto a
    // not-yet-built shaft blueprint); IsSuspended passes false so the run still *builds* outward
    // from a real, load-bearing shaft — mirroring the blueprint-below support asymmetry.
    public static bool ConnectsToShaft(StructType st, Tile tile, int rotation, bool mirrored, bool includeBlueprints) {
        World world = World.instance;
        if (world == null) return false;
        Side sides = ComputeOpenSides(st.name, rotation, mirrored);
        int depth = st.depth;
        foreach ((int dx, int dy, Side mine, Side theirs) in NeighbourDirs) {
            if ((sides & mine) == 0) continue;   // this shaft doesn't open toward that neighbour
            Tile n = world.GetTileAt(tile.x + dx, tile.y + dy);
            if (n == null) continue;
            if (n.structs[depth] is PowerShaft ps && (ps.openSides & theirs) != 0) return true;
            if (includeBlueprints) {
                Blueprint bp = n.GetBlueprintAt(depth);
                if (bp != null && !bp.cancelled && IsShaft(bp.structType)
                        && (ComputeOpenSides(bp.structType.name, bp.rotation, bp.mirrored) & theirs) != 0)
                    return true;
            }
        }
        return false;
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

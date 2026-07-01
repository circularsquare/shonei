using UnityEngine;

// The shipping quarry (stone) and digging pit (earth). Built on an OPEN tile that has a
// revealed background wall; the miner works from an interior node INSIDE the tile (entering
// through a wired door from an adjacent walkable side, like a burrow) and digs into the wall's
// DEPTH rather than mining a solid tile out. Working inside means a quarry needs no floor of
// its own — it can be dug into a wall over open space, or stacked in a column, reached from a
// side. The wall's material (captured at construction) sets the per-cycle yield via its
// nExtractionProducts. On depletion the wall is exhausted: the tile is marked
// backgroundQuarriedOut (can't be re-quarried; darkened render later) and the building removed.
//
// Quarry vs pit differ only in JSON (job, required wall group, cost, research gate); the
// mechanics here are material-agnostic. Placement (StructPlacement.requiresBackgroundWall)
// guarantees an open, un-exhausted wall of the right group with at least one open side.
//
// Distinct from ExtractionBuilding (the solid-tile dish excavator): a wall quarry has no
// substrate to carve, so no dish. Both share the extraction contract via IExtractor.
public class WallQuarry : Building, IExtractor {
    // The background-wall material this quarry digs, captured at construction (live) or
    // restored from save. Drives the yield; null only if capture failed (logged).
    public TileType capturedWall;

    // Orthogonal faces a miner can enter through (bottom excluded — you step in from a side or
    // from above, not up through the floor). Matches ExtractionBuilding's dig faces.
    static readonly (int dx, int dy, string side)[] Faces = {
        (0, 1, "top"), (-1, 0, "left"), (1, 0, "right"),
    };

    public WallQuarry(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    public override void OnPlaced() {
        base.OnPlaced();
        WireOpenFaceDoors();   // live path; load re-wires via RestoreOnLoad
    }

    // Called by StructController.Construct at build time (the tile stays open — nothing to
    // capture from tile.type, so we read the revealed wall material instead).
    public void CaptureWall(Tile tile) {
        if (tile?.backgroundTile == null) {
            Debug.LogError($"WallQuarry.CaptureWall: no background wall at {x},{y}");
            return;
        }
        capturedWall = tile.backgroundTile;
    }

    // ── IExtractor ───────────────────────────────────────────────────────────

    public ItemQuantity[] CapturedProducts => capturedWall?.extractionProducts;

    public ItemQuantity[] GetExtractionOutputs() {
        if (capturedWall == null) {
            Debug.LogError($"WallQuarry at {x},{y} has no capturedWall — falling back to recipe outputs");
            return null;
        }
        if (capturedWall.extractionProducts == null) {
            Debug.LogError($"WallQuarry at {x},{y}: wall '{capturedWall.name}' has no nExtractionProducts defined");
            return null;
        }
        return capturedWall.extractionProducts;
    }

    // No per-round visual yet — the wall doesn't visibly recede in v1. The phase-6
    // back-facing miner + descending darken mask will hook in here.
    public void OnExtractionRound(Animal animal) { }

    // Fully mined: mark the wall exhausted so it renders darkened and can't be re-quarried.
    // The tile was already open, so nothing else to restore — the building is gone.
    public void OnExtractionDepleted(Tile tile) {
        if (tile != null) tile.backgroundQuarriedOut = true;
    }

    // ── Load ───────────────────────────────────────────────────────────────
    // OnPlaced is skipped on load, so SaveSystem calls this after restoring capturedWall.
    public void RestoreOnLoad() {
        WireOpenFaceDoors();
    }

    // ── Door wiring ──────────────────────────────────────────────────────────
    // Connect the interior node (1×1 footprint) out to every OPEN (non-solid) orthogonal face.
    // We wire on openness, NOT current standability: an edge to a face that isn't standable yet
    // stays dormant (A* won't traverse a non-standable approach) and lights up for free once a
    // floor appears beside the quarry — so building a drawer/platform next to it later makes the
    // quarry reachable without any door bookkeeping. (Faces solid at build time and mined open
    // later aren't picked up; rare enough to accept for v1.) Idempotent enough for one call.
    void WireOpenFaceDoors() {
        if (interiorNodes == null || interiorNodes.Length == 0) return;
        Node interior = interiorNodes[0];
        World w = World.instance;
        foreach (var f in Faces) {
            Tile nt = w.GetTileAt(x + f.dx, y + f.dy);
            if (nt == null || nt.type.solid) continue;   // open faces only; standability checked by A* at run time
            WireDoorEdge(interior, x, y, f.side);
        }
    }
}

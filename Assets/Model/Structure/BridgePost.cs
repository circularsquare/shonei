using UnityEngine;

// One of the two endpoint posts of a rope bridge. A normal 1×1 depth-2
// structure for every purpose except its lifecycle entanglement with its
// partner post on the other end of the bridge.
//
// Posts are placed in pairs by the two-click flow in BuildPanel. Each post
// remembers its partner's coordinates (partnerX/partnerY) so that the
// RopeBridge side-car entity can be reconstructed on load without an extra
// top-level save list.
//
// Lifecycle:
//   - Live build: `OnPlaced()` looks for an already-built partner post. If
//     found (i.e. we're the second post to finish), spin up the RopeBridge
//     linking us. If not, do nothing — the partner's later OnPlaced will
//     create the bridge once it's built.
//   - Load: SaveSystem calls `RopeBridge.PairAllAfterLoad()` between Phase 3
//     (contents) and Phase 4 (graph build) so the resulting waypoint chain
//     is included in the initial RebuildComponents sweep.
//   - Destroy: notify the bridge (if any) so it tears down the waypoint
//     chain, destroys visuals, AND destroys the other post. The bridge
//     nulls our `bridge` ref first to avoid re-entering the cascade when
//     the other post's own Destroy() runs.
public class BridgePost : Structure {

    public int partnerX;
    public int partnerY;

    // The side-car entity owning the rope curve + waypoint chain shared with
    // our partner post. Null while the partner is unbuilt and while the
    // bridge is being torn down (the bridge nulls this BEFORE destroying us).
    public RopeBridge bridge;

    // The post sprite has its pole on the left half of the un-mirrored sprite,
    // so the LEFT post of a bridge is constructed mirrored=true (pole flips to
    // the right side of the tile, facing the bridge) and the RIGHT post stays
    // mirrored=false. Blueprint.Complete passes the correct flag per post.
    // Load path threads it through StructureSaveData.mirrored as usual.
    public BridgePost(StructType st, int x, int y, bool mirrored, int partnerX, int partnerY)
        : base(st, x, y, mirrored: mirrored, rotation: 0, shapeIndex: 0) {
        this.partnerX = partnerX;
        this.partnerY = partnerY;
    }

    public override void OnPlaced() {
        base.OnPlaced();
        // Live-build path: the second post to be constructed finds the first
        // already in place and materializes the bridge. The first post's
        // OnPlaced runs before we exist on the partner tile, so its
        // FindPartner returns null — no bridge then. Once we run, we find
        // the partner and spin up the RopeBridge linking us.
        BridgePost partner = FindPartner();
        if (partner != null && partner.bridge == null && bridge == null)
            RopeBridge.Create(partner, this);
    }

    // Returns the BridgePost at our recorded partner coords, or null if no
    // post exists there. Used by both the live-build path and load-time
    // pairing.
    public BridgePost FindPartner() {
        Tile t = World.instance?.GetTileAt(partnerX, partnerY);
        if (t == null) return null;
        return t.structs[structType.depth] as BridgePost;
    }

    public override void Destroy() {
        // Tear the bridge down first. RopeBridge.OnPostDestroyed clears our
        // own `bridge` ref + the partner's BEFORE recursing into the
        // partner's Destroy, so we don't re-enter this branch infinitely.
        if (bridge != null) bridge.OnPostDestroyed(this);
        base.Destroy();
    }
}

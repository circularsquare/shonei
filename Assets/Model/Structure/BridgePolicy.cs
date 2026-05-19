using UnityEngine;

// Per-instance EdgePolicy for one rope bridge's segment edges. Used on every
// waypoint↔waypoint edge along the catenary chain, where Graph.ResolveEdgePolicy
// picks the policy via the "both endpoints share the same EdgePolicy reference"
// rule (Navigation.cs ResolveEdgePolicy). That rule is checked first, so a
// non-flat segment (e.g. on a slanted bridge) won't fall through to LadderPolicy
// even when |Δy| > 0.1.
//
// IsNeighbor (Navigation.cs IsNeighbor) uses the same shared-reference check to
// preserve the segment edges across UpdateNeighbors rebuilds — without this,
// triggering a graph rebuild on a tile next to a bridge would drop every
// interior segment edge.
//
// Per-instance (not a singleton) because each bridge owns its own waypoint
// chain; sharing one policy across multiple bridges would make IsNeighbor
// glue unrelated waypoints together.
//
// The approach edges (waypoint ↔ post tile-node) deliberately do NOT carry
// this policy — they're regular waypoint approaches and resolve to the shared
// WaypointApproachPolicy.Instance singleton, which already gives euclidean
// cost.
public sealed class BridgePolicy : EdgePolicy {

    public override (float cost, float length) GetEdgeInfo(Node from, Node to) {
        float dx = to.wx - from.wx;
        float dy = to.wy - from.wy;
        float dist = Mathf.Sqrt(dx * dx + dy * dy);
        return (dist, dist);
    }
}

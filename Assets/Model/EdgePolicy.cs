using UnityEngine;

// EdgePolicy unifies the dispatch for "this graph edge has special semantics."
// Today's covered edges:
//   - Constant policies (singletons): ladder, cliff middle leg, stair middle leg, waypoint approach.
//   - Per-instance policy: elevator transit (and future trains, trams, etc.).
// Plain horizontal edges have no policy — they fall through to the default branch in
// Graph.ComputeEdge where road bonus + water modifiers apply.
//
// Resolution: Graph.ResolveEdgePolicy(from, to) returns the EdgePolicy governing an
// edge between two nodes, or null for plain horizontal. Per-instance dispatch works
// by both endpoints carrying the same policy reference — e.g. both elevator stop
// nodes hold a back-reference to the same ElevatorEdgePolicy.
public abstract class EdgePolicy {
    // Cost for A* and physical length for locomotion. Nav.Move uses
    //   speed = travelMul * length / cost
    // so don't return zero length.
    public abstract (float cost, float length) GetEdgeInfo(Node from, Node to);

    // True → animal traversing this edge skips the falling check. Default true: every
    // current special edge (waypoint chain, ladder, transit) suppresses falls. Plain
    // horizontal edges have no policy, so they fall through to false in Nav.MoveCore.
    public virtual bool PreventFall => true;

    // True → Nav.MoveCore returns false (no lerp) for this edge. Used by transit edges
    // where an external system (Elevator) drives the animal's position.
    public virtual bool SuspendsLerp => false;

    // Called every Nav.Move where this policy governs the next path step. Idempotent —
    // implementations must handle being invoked every frame the animal is parked
    // before the edge.
    public virtual void OnApproach(Animal a, Node from, Node to) {}

    // Called once when Nav.Navigate commits to a path crossing this edge. Lets dynamic
    // policies (transits) register tentative reservations so simultaneous path planners
    // see realistic queue depth in their cost estimate.
    public virtual void OnPathCommit(Animal a) {}

    // Symmetric release for when an in-flight path is dropped (Nav.EndNavigation).
    public virtual void OnPathRelease(Animal a) {}
}

// ── Constant-cost singletons ──────────────────────────────────────────────

// Ladder vertical edge — direct tile-node to tile-node (no waypoint).
public sealed class LadderPolicy : EdgePolicy {
    public static readonly LadderPolicy Instance = new();
    public override (float cost, float length) GetEdgeInfo(Node from, Node to) => (2.0f, 1.0f);
}

// Cliff-climb middle leg — between two waypoints stacked vertically (~0.25 from base).
// Slow both ways; the approach + exit legs use WaypointApproachPolicy.
public sealed class CliffPolicy : EdgePolicy {
    public static readonly CliffPolicy Instance = new();
    public override (float cost, float length) GetEdgeInfo(Node from, Node to) => (3.0f, 1.0f);
}

// Stair middle leg — between two diagonally-offset waypoints (the Z-shape's diagonal step).
public sealed class StairPolicy : EdgePolicy {
    public static readonly StairPolicy Instance = new();
    public override (float cost, float length) GetEdgeInfo(Node from, Node to) => (1.8f, 1.4142f);
}

// Cliff/stair approach + exit edges (waypoint↔tile) AND workspot connection edges.
// Euclidean so any (dx, dy) offset gets the correct distance — needed for workspot
// waypoints which can be authored at fractional offsets.
public sealed class WaypointApproachPolicy : EdgePolicy {
    public static readonly WaypointApproachPolicy Instance = new();
    public override (float cost, float length) GetEdgeInfo(Node from, Node to) {
        float dx = to.wx - from.wx;
        float dy = to.wy - from.wy;
        float dist = Mathf.Sqrt(dx * dx + dy * dy);
        return (dist, dist);
    }
}

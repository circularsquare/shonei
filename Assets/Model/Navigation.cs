using System.Collections.Generic;
using UnityEngine;
using System;

public class Node {
    public int x, y;       // grid tile position (for array indexing)
    public float wx, wy;   // world position (actual movement target)
    public bool isWaypoint;
    public List<Node> neighbors;
    public bool standable;
    public int componentId = -1;  // set by Graph.RebuildComponents(); -1 = impassable/unvisited
    public Tile tile;
    // Optional per-edge dispatch policy carried on this node. When two nodes share the
    // same EdgePolicy reference (e.g. both elevator stops point to one ElevatorEdgePolicy),
    // Graph.ResolveEdgePolicy returns it for the edge between them — driving cost lookup,
    // OnApproach hooks, etc. IsNeighbor uses the same shared-policy check to preserve the
    // edge across UpdateNeighbors filtering even when the nodes aren't geometrically adjacent.
    // Null on regular nodes; static (constant) policies aren't stored here — they're chosen
    // by ResolveEdgePolicy from node geometry (waypoint flags, dy threshold).
    public EdgePolicy edgePolicy;

    public Node(Tile tile, int x, int y){
        this.tile = tile; this.x = x; this.y = y;
        this.wx = x; this.wy = y;
        neighbors = new List<Node>();
    }
    // Waypoint constructor — float world position, no tile reference
    public Node(float wx, float wy){
        this.wx = wx; this.wy = wy;
        this.x = Mathf.RoundToInt(wx); this.y = Mathf.RoundToInt(wy);
        this.isWaypoint = true;
        neighbors = new List<Node>();
    }

    public void AddNeighbor(Node n, bool reciprocal = false){ 
        if (!neighbors.Contains(n)) {neighbors.Add(n);} 
        if (reciprocal && !n.neighbors.Contains(this)){ n.neighbors.Add(this);}}
    public void RemoveNeighbor(Node n){neighbors.Remove(n);}
}
public class Path {
    public List<Node> nodes;
    public float cost;
    public int length {get {return nodes.Count - 1;}} // number of links in path, not nodes
    public Node end;
    public Tile tile {get {return end.tile;}}
    public Path(List<Node> nodes, float cost = 0){
        this.nodes = nodes;
        this.cost = cost;
        end = nodes[nodes.Count - 1];
    }
}


public class Graph { 
    public World world;
    public Node[,] nodes;
    public static Graph instance { get; protected set; }

    // Reload-Domain-off support — see MaintenanceSystem.ResetStatics for the why.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void ResetStatics() { instance = null; }

    private Dictionary<(int,int),  (Node,Node)> stairWaypoints = new Dictionary<(int,int),  (Node,Node)>();
    // Cliff chain keyed by bottom base (bx, by, dir). Value is the full waypoint column
    // from y=by up to y=by+H (H+1 entries). H is the climb height — the chain reaches the
    // first standable cell on the wall column above. Side ladders along the chain upgrade
    // individual segments to LadderPolicy (cheaper); pure cliff segments use CliffPolicy.
    private Dictionary<(int,int,int), Node[]> cliffWaypoints = new Dictionary<(int,int,int), Node[]>();
    // Upper bound on cliff height we'll scan / store. Sets the cost of UpdateNeighbors
    // refresh propagation when a tile mutates partway up a tall cliff.
    private const int CLIFF_MAX_HEIGHT = 32;

    // Off-grid waypoints created OUTSIDE the stair/cliff machinery above — building interiors,
    // workspots, rope-bridge chains. Unlike stair/cliff waypoints (tracked in the dicts above
    // and reset there) these live on their owning Structure, so RebuildComponents has no other
    // handle on them. They MUST be reset every rebuild: component ids are renumbered from
    // scratch each call, so a waypoint left with a stale id is skipped by the BFS visited-check
    // and frozen out of the colony — see RebuildComponents. Owners register on creation /
    // unregister on teardown. Leak-tolerant: a stale entry just gets reset to -1 and, having no
    // neighbours, is never revisited — unregister only bounds memory across build/destroy churn.
    private readonly HashSet<Node> registeredWaypoints = new HashSet<Node>();
    public void RegisterWaypoint(Node n)   { if (n != null && n.isWaypoint) registeredWaypoints.Add(n); }
    public void UnregisterWaypoint(Node n) { if (n != null) registeredWaypoints.Remove(n); }

    public Graph(World world){
        this.world = world;
        if (instance != null){
            Debug.LogError("there should only be one world graph!");
        }
        instance = this;
    }
    public void Initialize(){ // updates whole network  
        AddNeighborsInitial();
    }
    public Path Navigate(Node start, Node goal){
        if (!SameComponent(start, goal)) return null;  // O(1) fast exit before A*
        AStar astar = new AStar(nodes, start, goal);
        return astar.Search();
    }

    // BFS flood-fill assigning integer component IDs to all standable nodes.
    // Call after any topology change. O(V) — ~5000 nodes, negligible cost.
    public void RebuildComponents() {
        // Reset tile-nodes
        for (int x = 0; x < world.nx; x++)
            for (int y = 0; y < world.ny; y++)
                nodes[x, y].componentId = -1;

        // Reset waypoint nodes (stored off the nodes[,] grid). Three mechanisms: stair/cliff
        // waypoints live in the dicts below (which double as rebuild keys); structure-owned
        // waypoints (interiors, workspots, bridges) live in registeredWaypoints.
        foreach (var (wp1, wp2) in stairWaypoints.Values) { wp1.componentId = -1; wp2.componentId = -1; }
        foreach (Node[] chain in cliffWaypoints.Values) {
            for (int i = 0; i < chain.Length; i++) chain[i].componentId = -1;
        }
        foreach (Node n in registeredWaypoints) n.componentId = -1;

        // BFS from each unvisited standable tile-node; waypoints get IDs transitively via edges
        int nextId = 0;
        var queue = new Queue<Node>();
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Node seed = nodes[x, y];
                if (!seed.standable || seed.componentId >= 0) continue;
                seed.componentId = nextId;
                queue.Enqueue(seed);
                while (queue.Count > 0) {
                    Node cur = queue.Dequeue();
                    foreach (Node nb in cur.neighbors) {
                        if (nb.componentId >= 0) continue;
                        nb.componentId = nextId;
                        queue.Enqueue(nb);
                    }
                }
                nextId++;
            }
        }
    }

    // O(1) reachability query. Returns false if either node is impassable or unvisited.
    public bool SameComponent(Node a, Node b) {
        if (a == null || b == null) return false;
        if (a.componentId < 0 || b.componentId < 0) return false;
        return a.componentId == b.componentId;
    }

    // Full rebuild — only safe to call when all neighbor lists are empty (i.e. on startup).
    // Stair and cliff waypoints must be created before the neighbor pass so adjacent tiles can link to them.
    public void AddNeighborsInitial(){
        for (int x = 0; x < world.nx; x++){
            for (int y = 0; y < world.ny; y++){
                UpdateStandability(x, y);
            }
        }
        for (int x = 0; x < world.nx; x++){
            for (int y = 0; y < world.ny; y++){
                if (nodes[x,y].tile.HasStairRight() || nodes[x,y].tile.HasStairLeft())
                    CreateStairWaypoints(x, y);
            }
        }
        for (int x = 0; x < world.nx; x++){
            for (int y = 0; y < world.ny; y++){
                CreateCliffWaypoints(x, y);
            }
        }
        for (int x = 0; x < world.nx; x++){
            for (int y = 0; y < world.ny; y++){
                UpdateNeighbors(x, y);
            }
        }
        RebuildComponents();
    }
    public void UpdateNeighbors(int x, int y){
        UpdateStandability(x, y);
        Node node = nodes[x,y];

        // Filter existing neighbors. If still valid, also reinforce the reverse edge (keeps
        // bidirectional connections in sync when one side is updated). If invalid, remove both directions.
        List<Node> eligibleNeighbors = new List<Node>();
        foreach (Node neighbor in node.neighbors) {
            if (IsNeighbor(node, neighbor)) {
                eligibleNeighbors.Add(neighbor);
                neighbor.AddNeighbor(node); // reinforce reverse
            } else {
                neighbor.RemoveNeighbor(node);
            }
        }
        node.neighbors = eligibleNeighbors;

        // Add horizontal standable neighbors
        if (x + 1 < world.nx && node.standable && nodes[x+1,y].standable){
            node.AddNeighbor(nodes[x+1,y], true); }
        if (x - 1 >= 0 && node.standable && nodes[x-1,y].standable){
            node.AddNeighbor(nodes[x-1,y], true); }
        // Add vertical neighbors via ladders
        Structure fStruct = node.tile.structs[2];
        if (fStruct != null && fStruct.structType.name == "ladder" && y + 1 < world.ny){
            node.AddNeighbor(nodes[x,y+1], true);
        }
        // Refresh stair waypoints for this tile and any adjacent stair tiles that use this as an endpoint.
        // Candidates: self + 4 positions that could be stairs with (x,y) as their entry/exit.
        // Candidates: self + 4 positions whose far endpoints include (x,y) + (x,y-1) for
        // the new exit↔(sx,sy+1) connection (stair below may now link to this tile).
        int[] scx = { x, x+1, x-1, x-1, x+1, x };
        int[] scy = { y, y,   y,   y-1, y-1, y-1 };
        for (int i = 0; i < 5; i++){
            int cx = scx[i], cy = scy[i];
            if (cx < 0 || cx >= world.nx || cy < 0 || cy >= world.ny) continue;
            Tile candidate = world.GetTileAt(cx, cy);
            if (candidate.HasStairRight() || candidate.HasStairLeft()){
                CreateStairWaypoints(cx, cy);
            }
        }
        // Refresh cliff waypoints. With multi-tile chains the bottom-base is the only
        // entry that owns the chain — a mutation partway up affects whichever base sits
        // below it. Walk downward CLIFF_MAX_HEIGHT tiles refreshing every candidate base
        // for which (x,y) could be: part of the air column, part of the wall column,
        // or the standable exit at the top. Non-base candidates early-return cheaply.
        for (int dy = y; dy >= Math.Max(0, y - CLIFF_MAX_HEIGHT); dy--) {
            CreateCliffWaypointForSide(x,   dy, +1);
            CreateCliffWaypointForSide(x,   dy, -1);
            if (x - 1 >= 0)        CreateCliffWaypointForSide(x - 1, dy, +1);
            if (x + 1 < world.nx)  CreateCliffWaypointForSide(x + 1, dy, -1);
        }
    }

    private void CreateCliffWaypoints(int x, int y) {
        CreateCliffWaypointForSide(x, y, +1);
        CreateCliffWaypointForSide(x, y, -1);
    }

    public bool IsNeighbor(Node node, Node neighbor){
        if (node.isWaypoint || neighbor.isWaypoint) return true; // waypoints manage their own connections
        // Transit edges (e.g. Elevator stops): both nodes share the same EdgePolicy reference.
        // The != null guard is load-bearing — without it, every plain pair would short-circuit
        // on null == null = true and falsely preserve every dead edge across UpdateNeighbors
        // filtering.
        if (node.edgePolicy != null && node.edgePolicy == neighbor.edgePolicy) return true;
        int xDiff = neighbor.x - node.x; int yDiff = neighbor.y - node.y;
        return (
        ((xDiff == 1 || xDiff == -1) && yDiff == 0 && (node.standable && neighbor.standable))
        || (xDiff == 0 && yDiff == 1 && node.tile.HasLadder())
        || (xDiff == 0 && yDiff == -1 && neighbor.tile.HasLadder()));
    }

    // Creates a vertical waypoint chain for scaling a cliff of any height from base (bx,by) in
    // direction dir (+1=right, -1=left). The chain runs up the side of the wall column cx = bx+dir
    // until the first standable cell on cx — that's the exit. Path:
    //   base → chain[0] (short horizontal) → chain[1] → … → chain[H] → cliff_top (short horizontal).
    // Each vertical segment k→k+1 is CliffPolicy by default (slow), upgraded to LadderPolicy when a
    // side ladder is present at tile (bx, by+k) on the matching dir side — encoded by setting
    // chain[k].edgePolicy / chain[k+1].edgePolicy to LadderPolicy.Instance so Graph.ResolveEdgePolicy's
    // shared-policy check returns it for that segment (mixed chains work: only matching pairs match).
    // Fractional X of the chain is the historical 0.24-from-base offset — this avoids the "floating
    // horizontal" bug where intermediate non-standable tiles above gaps connected directly.
    private void CreateCliffWaypointForSide(int bx, int by, int dir) {
        var key = (bx, by, dir);
        if (cliffWaypoints.TryGetValue(key, out Node[] oldChain)) {
            for (int i = 0; i < oldChain.Length; i++)
                foreach (Node n in oldChain[i].neighbors) n.RemoveNeighbor(oldChain[i]);
            cliffWaypoints.Remove(key);
        }
        if (bx < 0 || bx >= world.nx || by < 0 || by >= world.ny) return;
        int cx = bx + dir;
        if (cx < 0 || cx >= world.nx || by + 1 >= world.ny) return;
        if (!nodes[bx, by].standable) return;
        if (!world.GetTileAt(cx, by).type.solid) return;      // wall must exist at base level

        // Walk upward: wall column stays solid, air column stays clear, until we find a standable
        // cell on the wall column. That standable cell is the exit at height by+H.
        int H = 0;
        while (true) {
            H++;
            if (H > CLIFF_MAX_HEIGHT) return;                            // give up — chain too tall
            if (by + H >= world.ny) return;                              // off-map upward
            if (world.GetTileAt(bx, by + H).type.solid) return;          // air column blocked → no climb
            if (GetStandability(cx, by + H)) break;                      // standable exit reached
            if (!world.GetTileAt(cx, by + H).type.solid) return;         // wall ended without exit
        }

        float wpx = bx + dir * 0.24f;
        Node[] chain = new Node[H + 1];
        for (int k = 0; k <= H; k++) chain[k] = new Node(wpx, (float)(by + k));

        // Per-segment policy: segment k → k+1 corresponds to side ladder at tile (bx, by+k).
        // Set both endpoints' edgePolicy so ResolveEdgePolicy's shared-policy check picks it
        // up. A waypoint sitting between a ladder segment and a cliff segment carries the
        // policy — the cliff segment's other end has null policy, so the shared check fails
        // there and falls through to the waypoint-waypoint-vertical CliffPolicy branch.
        for (int k = 0; k <= H; k++) {
            bool ladderBelow = k > 0 && world.GetTileAt(bx, by + k - 1).HasSideLadder(dir);
            bool ladderAbove = k < H && world.GetTileAt(bx, by + k    ).HasSideLadder(dir);
            if (ladderBelow || ladderAbove) chain[k].edgePolicy = LadderPolicy.Instance;
        }

        nodes[bx, by].AddNeighbor(chain[0], true);                 // base → first waypoint
        for (int k = 0; k < H; k++) chain[k].AddNeighbor(chain[k + 1], true);  // vertical climb
        chain[H].AddNeighbor(nodes[cx, by + H], true);             // last waypoint → cliff top

        // Step-off edges into the air column at side-ladder levels. Without these, a
        // mid-air side ladder blueprint (its tile is air with no floor below) has no
        // reachable integer-X node, so the construction task can't path to it. We add
        // an edge whenever the level has a side ladder *or* its blueprint, so a fresh
        // blueprint becomes reachable for the builder; we also add it for any otherwise-
        // standable air tile (e.g. a floating platform) so the chain integrates with
        // existing structures. Bare-air levels stay unconnected so mice can't randomly
        // step off the chain into empty space.
        for (int k = 1; k < H; k++) {
            Tile t = world.GetTileAt(bx, by + k);
            bool hasSideLadder = t.HasSideLadderAny()
                || (t.GetBlueprintAt(2) != null && t.GetBlueprintAt(2).structType.name == "ladder_side");
            if (hasSideLadder || nodes[bx, by + k].standable)
                chain[k].AddNeighbor(nodes[bx, by + k], true);
        }

        cliffWaypoints[key] = chain;
    }
    private void CreateStairWaypoints(int sx, int sy) {
        // Clean up old waypoints if present
        if (stairWaypoints.ContainsKey((sx, sy))) {
            var (oldEntry, oldExit) = stairWaypoints[(sx, sy)];
            foreach (Node n in oldEntry.neighbors) n.RemoveNeighbor(oldEntry);
            foreach (Node n in oldExit.neighbors)  n.RemoveNeighbor(oldExit);
            stairWaypoints.Remove((sx, sy));
        }
        bool right = nodes[sx, sy].tile.HasStairRight();
        if (right) {
            if (sx - 1 < 0 || sx + 1 >= world.nx || sy + 1 >= world.ny) return;
            if (!GetStandability(sx - 1, sy) || !GetStandability(sx + 1, sy + 1)) return;
            Node entry = new Node(sx - 0.5f, (float)sy);
            Node exit  = new Node(sx + 0.5f, sy + 1f);
            entry.AddNeighbor(exit, true);
            entry.AddNeighbor(nodes[sx-1, sy],   true);
            exit.AddNeighbor( nodes[sx+1, sy+1], true);
            // Z-paths: also connect to the stair tile itself and the tile above it,
            // so mice don't have to walk to the far endpoint before using the stair.
            if (nodes[sx, sy].standable)                           entry.AddNeighbor(nodes[sx, sy],     true);
            if (sy + 1 < world.ny && nodes[sx, sy + 1].standable) exit.AddNeighbor( nodes[sx, sy + 1], true);
            stairWaypoints[(sx, sy)] = (entry, exit);
        } else {
            if (sx + 1 >= world.nx || sx - 1 < 0 || sy + 1 >= world.ny) return;
            if (!GetStandability(sx + 1, sy) || !GetStandability(sx - 1, sy + 1)) return;
            Node entry = new Node(sx + 0.5f, (float)sy);
            Node exit  = new Node(sx - 0.5f, sy + 1f);
            entry.AddNeighbor(exit, true);
            entry.AddNeighbor(nodes[sx+1, sy],   true);
            exit.AddNeighbor( nodes[sx-1, sy+1], true);
            // Z-paths: also connect to the stair tile itself and the tile above it.
            if (nodes[sx, sy].standable)                           entry.AddNeighbor(nodes[sx, sy],     true);
            if (sy + 1 < world.ny && nodes[sx, sy + 1].standable) exit.AddNeighbor( nodes[sx, sy + 1], true);
            stairWaypoints[(sx, sy)] = (entry, exit);
        }
    }

    // Returns (cost, physicalLength). Cost is used by A*; locomotion uses both
    // so that speed = maxSpeed * length / cost (correct traversal time regardless of edge length).
    // Road bonus included — A* prefers paths over roads.
    public (float cost, float length) GetEdgeInfo(Node from, Node to)
        => ComputeEdge(from, to, useRoadBonus: true);

    public float GetEdgeCost(Node from, Node to) => GetEdgeInfo(from, to).cost;

    // Edge info without road cost reduction — used by Nav.Move() for runtime movement speed.
    // Road bonus is instead applied per-tile via ModifierSystem.GetTravelSpeedMultiplier().
    // Waypoint, vertical, and water modifiers are kept (they affect traversal physics, not tile bonuses).
    public (float cost, float length) GetRawEdgeInfo(Node from, Node to)
        => ComputeEdge(from, to, useRoadBonus: false);

    // Single source of truth for edge cost. Special edges resolve to an EdgePolicy (transit,
    // ladder, cliff/stair waypoint legs, waypoint approach); plain horizontal edges fall
    // through to the default branch below where road bonus and water modifiers apply.
    private (float cost, float length) ComputeEdge(Node from, Node to, bool useRoadBonus) {
        EdgePolicy policy = ResolveEdgePolicy(from, to);
        if (policy != null) return policy.GetEdgeInfo(from, to);
        // Plain horizontal — water always applies, road bonus only when requested.
        // Broken roads lose their bonus (EffectivePathCostReduction returns 0 when IsBroken).
        float baseCost = 1.0f;
        if (useRoadBonus) {
            float fromR = from.tile?.structs[3]?.EffectivePathCostReduction ?? 0f;
            float toR   = to.tile?.structs[3]?.EffectivePathCostReduction   ?? 0f;
            baseCost = Mathf.Max(0.1f, 1.0f - fromR - toR);
        }
        bool inWater = (from.tile != null && from.tile.water > 0)
                    || (to.tile   != null && to.tile.water   > 0);
        return (inWater ? baseCost * 2f : baseCost, 1.0f);
    }

    // Returns the EdgePolicy governing the edge from→to, or null for plain horizontal.
    // Two dispatch sources:
    //   1. Per-instance: both endpoints carry the same EdgePolicy reference (transit edge).
    //   2. Geometric: waypoint flags + dx/dy classification map to constant singletons
    //      (cliff vertical / stair diagonal / approach / ladder).
    // Note: assumes nodes carrying a per-instance policy are NOT also waypoints. Today only
    // tile-backed nodes (Elevator stops) carry a policy reference, so this holds. If a future
    // case needs a per-instance policy on a waypoint node, the precedence here makes the
    // per-instance policy win — which is probably what you'd want, but worth verifying.
    public static EdgePolicy ResolveEdgePolicy(Node from, Node to) {
        // The != null guard is load-bearing — null == null would short-circuit otherwise.
        if (from.edgePolicy != null && from.edgePolicy == to.edgePolicy) return from.edgePolicy;
        if (from.isWaypoint && to.isWaypoint) {
            return Math.Abs(to.wx - from.wx) < 0.1f
                ? (EdgePolicy)CliffPolicy.Instance
                : StairPolicy.Instance;
        }
        if (from.isWaypoint || to.isWaypoint) return WaypointApproachPolicy.Instance;
        if (Math.Abs(to.wy - from.wy) > 0.1f) return LadderPolicy.Instance;
        return null;  // plain horizontal — handled by ComputeEdge default branch
    }

    public bool GetStandability(int x, int y){ return GetStandability(x, y, allowLadder: true); }

    // allowLadder=false restricts the test to "real" footing: solid ground or a
    // building floor beneath. A tile that's standable only via a ladder / side-ladder
    // (a rung in mid-air, nothing under it) returns false. See IsLadderOnlyFooting,
    // which uses this to tell resting spots from rungs so idle mice climb off.
    public bool GetStandability(int x, int y, bool allowLadder){
        Tile tileHere = world.GetTileAt(x, y);
        Tile tileBelow = world.GetTileAt(x, y-1);
        if (tileBelow == null) {return false;} // need tile below to exist
        if (tileHere.type.solid) {return false;} // need tilehere to not be solid
        // Per-tile internal-floor override: a structure can declare specific tiles
        // inside its footprint as walkable (Elevator's top stop, future partial-top
        // multi-tile buildings, etc.). Wins over the multi-tile-body NOT-standable
        // rule below. Coordinates are local to the structure's anchor.
        for (int d = 0; d < tileHere.structs.Length; d++) {
            Structure s = tileHere.structs[d];
            if (s == null) continue;
            if (s.HasInternalFloorAt(x - s.x, y - s.y)) return true;
        }
        // Multi-tile structure body: if the SAME structure occupies both tileHere and
        // tileBelow, this tile is inside the structure's column — not standable. Top of
        // the column (where structs[depth] is null) and adjacent stacked-but-separate
        // structures (different instance refs) keep their existing standability.
        if (tileHere.structs[0] != null && tileHere.structs[0] == tileBelow.structs[0]
            && tileHere.structs[0].structType.solidTop) return false;
        if (tileHere.structs[1] != null && tileHere.structs[1] == tileBelow.structs[1]
            && tileHere.structs[1].structType.solidTop) return false;
        if (tileBelow.type.solid) {return true;} // tile below is solid
        if (tileBelow.building != null && tileBelow.building.structType.solidTop) {return true;} // tile below is solid-top building
        if (tileBelow.structs[1] != null && tileBelow.structs[1].structType.solidTop) {return true;} // tile below is solid-top mStruct
        // Everything below here is ladder-only footing — a rung in mid-air with no ground beneath.
        if (!allowLadder) return false;
        if (tileHere.HasLadder() || tileBelow.HasLadder()) {return true;}
        // Side ladders: stand ONLY on the ladder's own tile (so a mouse can mount it and a
        // builder can reach mid-air rungs). Unlike a regular ladder, a side ladder does NOT
        // make the tile ABOVE it standable — it's climbed like a cliff face: vertical traversal
        // routes through the cliff chain's fractional-X waypoints, which exit sideways onto the
        // wall column, never onto a phantom floor in the open air above the ladder.
        if (tileHere.HasSideLadderAny()) {return true;}
        return false;
    }
    public void UpdateStandability(int x, int y){ nodes[x,y].standable = GetStandability(x, y); }

    // True when a mouse standing at (x,y) is held up only by a ladder / side-ladder
    // (a rung in mid-air) rather than by solid ground or a building floor. Idle mice
    // path off such tiles — you shouldn't loiter halfway up a ladder.
    public bool IsLadderOnlyFooting(int x, int y){
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) return false;
        return nodes[x, y].standable && !GetStandability(x, y, allowLadder: false);
    }

    // ── Load-time footing rescue ─────────────────────────────────────────────
    // Finds the nearest standable node to an arbitrary world position. Save data
    // persists only the raw (x, y), so a mouse saved mid-traversal (ladder / stairs /
    // cliff / rope bridge) comes back off-grid — it would otherwise drift diagonally
    // toward a horizontal neighbour on its first move (the lerp starts from the off-grid
    // point) and idle in mid-air. Snapping it onto a real node fixes both.
    //
    // Two candidate sources; nearest-by-Euclidean wins:
    //   1. Standable tile nodes in a small box. Handles ladders (the column tiles ARE
    //      standable tile nodes — ladders use direct tile-node edges, not waypoints)
    //      and any case with ground close by.
    //   2. The nearest waypoint of any stair / cliff / bridge chain, then a bounded BFS
    //      along that chain to its standable boundary (cliff base/top, stair entry/exit,
    //      nearer bridge post) — which may be many tiles away.
    // Returns null if nothing standable is reachable (caller falls back).
    public Node FindNearestStandableNode(float x, float y, float r = 3f){
        int ri = Mathf.CeilToInt(r);
        int cx = Mathf.RoundToInt(x);
        int cy = Mathf.RoundToInt(y);

        Node best = null;
        float bestSqr = float.MaxValue;

        // 1. Direct standable tile nodes in the box.
        for (int dx = -ri; dx <= ri; dx++){
            for (int dy = -ri; dy <= ri; dy++){
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || nx >= world.nx || ny < 0 || ny >= world.ny) continue;
                Node n = nodes[nx, ny];
                if (!n.standable) continue;
                float d = (n.wx - x) * (n.wx - x) + (n.wy - y) * (n.wy - y);
                if (d < bestSqr){ bestSqr = d; best = n; }
            }
        }

        // 2. Nearest waypoint of any chain (Chebyshev pre-filter, mirroring Nav.FindPathToCandidate).
        Node nearestWp = null;
        float wpSqr = float.MaxValue;
        foreach (Node wp in AllChainWaypoints()){
            if (wp == null) continue;
            if (Mathf.Max(Mathf.Abs(wp.wx - x), Mathf.Abs(wp.wy - y)) > r) continue;
            float d = (wp.wx - x) * (wp.wx - x) + (wp.wy - y) * (wp.wy - y);
            if (d < wpSqr){ wpSqr = d; nearestWp = wp; }
        }
        if (nearestWp != null){
            Node viaChain = NearestStandableBoundary(nearestWp, x, y);
            if (viaChain != null){
                float d = (viaChain.wx - x) * (viaChain.wx - x) + (viaChain.wy - y) * (viaChain.wy - y);
                if (d < bestSqr){ bestSqr = d; best = viaChain; }
            }
        }
        return best;
    }

    // Every waypoint node the graph knows about, across stair / cliff / bridge chains.
    // Used only by the one-shot load-time snap, so a full sweep is fine.
    private IEnumerable<Node> AllChainWaypoints(){
        foreach (var (wp1, wp2) in stairWaypoints.Values){ yield return wp1; yield return wp2; }
        foreach (Node[] chain in cliffWaypoints.Values)
            for (int i = 0; i < chain.Length; i++) yield return chain[i];
        foreach (RopeBridge bridge in RopeBridge.All){
            if (bridge.waypoints == null) continue;
            foreach (Node wp in bridge.waypoints) yield return wp;
        }
    }

    // BFS from a waypoint through its connected chain to the nearest standable boundary
    // node (by Euclidean distance to (x, y)). Expands only through non-standable waypoint
    // nodes; standable neighbours are recorded as boundary candidates but never expanded —
    // otherwise the flood would escape the chain into the whole walkable component via an
    // endpoint's edges. Returns null if the chain has no standable boundary.
    private Node NearestStandableBoundary(Node startWp, float x, float y){
        var visited = new HashSet<Node>{ startWp };
        var queue = new Queue<Node>();
        queue.Enqueue(startWp);
        Node best = null;
        float bestSqr = float.MaxValue;
        while (queue.Count > 0){
            Node cur = queue.Dequeue();
            foreach (Node nb in cur.neighbors){
                if (nb == null || !visited.Add(nb)) continue;
                if (nb.standable){
                    float d = (nb.wx - x) * (nb.wx - x) + (nb.wy - y) * (nb.wy - y);
                    if (d < bestSqr){ bestSqr = d; best = nb; }
                    // boundary — do not expand past it
                } else {
                    queue.Enqueue(nb);
                }
            }
        }
        return best;
    }
}


public class MinHeap<T> {
    private List<(float priority, T item)> heap = new List<(float, T)>();
    public int Count => heap.Count;

    public void Add(T item, float priority) {
        heap.Add((priority, item));
        BubbleUp(heap.Count - 1);
    }

    public T RemoveMin() {
        var min = heap[0].item;
        heap[0] = heap[heap.Count - 1];
        heap.RemoveAt(heap.Count - 1);
        if (heap.Count > 0) BubbleDown(0);
        return min;
    }

    private void BubbleUp(int i) {
        while (i > 0) {
            int parent = (i - 1) / 2;
            if (heap[i].priority >= heap[parent].priority) break;
            (heap[i], heap[parent]) = (heap[parent], heap[i]);
            i = parent;
        }
    }

    private void BubbleDown(int i) {
        while (true) {
            int smallest = i;
            int left = 2 * i + 1, right = 2 * i + 2;
            if (left < heap.Count && heap[left].priority < heap[smallest].priority) smallest = left;
            if (right < heap.Count && heap[right].priority < heap[smallest].priority) smallest = right;
            if (smallest == i) break;
            (heap[i], heap[smallest]) = (heap[smallest], heap[i]);
            i = smallest;
        }
    }
}

public class AStar {
    private Node start;
    private Node goal;
    private Dictionary<Node, float> gScore;
    private Dictionary<Node, float> fScore;
    private Dictionary<Node, Node> cameFrom;
    private MinHeap<Node> openHeap;
    private HashSet<Node> closedSet;

    public AStar(Node[,] _, Node start, Node goal) {
        this.start = start;
        this.goal = goal;
        gScore = new Dictionary<Node, float>();
        fScore = new Dictionary<Node, float>();
        cameFrom = new Dictionary<Node, Node>();
        openHeap = new MinHeap<Node>();
        closedSet = new HashSet<Node>();
    }

    public Path Search() {
        gScore[start] = 0;
        fScore[start] = Heuristic(start, goal);
        openHeap.Add(start, fScore[start]);

        while (openHeap.Count > 0) {
            Node current = openHeap.RemoveMin();

            if (closedSet.Contains(current)) continue; // skip stale entries
            if (current == goal) { return ReconstructPath(current); }

            closedSet.Add(current);

            foreach (Node neighbor in current.neighbors) {
                if (closedSet.Contains(neighbor)) continue;

                float tentativeG = gScore[current] + Graph.instance.GetEdgeCost(current, neighbor);
                if (tentativeG >= gScore.GetValueOrDefault(neighbor, float.MaxValue)) continue;

                cameFrom[neighbor] = current;
                gScore[neighbor] = tentativeG;
                fScore[neighbor] = tentativeG + Heuristic(neighbor, goal);
                openHeap.Add(neighbor, fScore[neighbor]);
            }
        }
        return null;
    }

    private Path ReconstructPath(Node node) {
        List<Node> nodes = new List<Node>();
        int maxDepth = 200;
        while (node != null && nodes.Count < maxDepth) {
            nodes.Add(node);
            cameFrom.TryGetValue(node, out node);
        }
        if (nodes.Count >= maxDepth) Debug.LogWarning("ReconstructPath hit maxDepth — path may be truncated");
        nodes.Reverse();
        if (nodes.Count == 0) return null;
        return new Path(nodes, gScore.GetValueOrDefault(nodes[nodes.Count - 1], 0));
    }

    private float Heuristic(Node node, Node goal) {
        return (float)Math.Sqrt((node.wx - goal.wx) * (node.wx - goal.wx)
            + (node.wy - goal.wy) * (node.wy - goal.wy));
    }
}
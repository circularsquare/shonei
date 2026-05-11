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
    static void ResetStatics() { instance = null; }

    private Dictionary<(int,int),  (Node,Node)> stairWaypoints = new Dictionary<(int,int),  (Node,Node)>();
    private Dictionary<(int,int,int),(Node,Node)> cliffWaypoints = new Dictionary<(int,int,int),(Node,Node)>();

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

        // Reset waypoint nodes (stored in dictionaries, not in nodes[,])
        foreach (var (wp1, wp2) in stairWaypoints.Values) { wp1.componentId = -1; wp2.componentId = -1; }
        foreach (var (wp1, wp2) in cliffWaypoints.Values)  { wp1.componentId = -1; wp2.componentId = -1; }

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
        // Refresh cliff waypoints for all cliffs that depend on this tile.
        CreateCliffWaypoints(x,   y);              // (x,y) as base
        CreateCliffWaypointForSide(x-1, y,   +1);  // (x,y) is the wall
        CreateCliffWaypointForSide(x+1, y,   -1);
        CreateCliffWaypoints(x,   y-1);            // (x,y) is space above base
        CreateCliffWaypointForSide(x-1, y-1, +1);  // (x,y) is cliff top
        CreateCliffWaypointForSide(x+1, y-1, -1);
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

    // Creates two waypoints for a one-block cliff climb from base (bx,by) in direction dir (+1=right, -1=left).
    // Path: base → wp1 (short horizontal) → wp2 (vertical, slow) → cliff_top (short horizontal).
    // This avoids the "floating horizontal" bug where intermediate non-standable tiles above gaps
    // were directly connected, making a gap appear traversable at height by+1.
    private void CreateCliffWaypointForSide(int bx, int by, int dir) {
        var key = (bx, by, dir);
        if (cliffWaypoints.ContainsKey(key)) {
            var (oldWp1, oldWp2) = cliffWaypoints[key];
            foreach (Node n in oldWp1.neighbors) n.RemoveNeighbor(oldWp1);
            foreach (Node n in oldWp2.neighbors) n.RemoveNeighbor(oldWp2);
            cliffWaypoints.Remove(key);
        }
        if (bx < 0 || bx >= world.nx || by < 0 || by >= world.ny) return;
        int cx = bx + dir;
        if (cx < 0 || cx >= world.nx || by + 1 >= world.ny) return;
        if (!nodes[bx, by].standable) return;
        if (!world.GetTileAt(cx, by).type.solid) return;      // wall must exist
        if (world.GetTileAt(bx, by + 1).type.solid) return;   // space above base must be clear
        if (!GetStandability(cx, by + 1)) return;             // cliff top must be standable

        float wpx = bx + dir * 0.24f;
        Node wp1 = new Node(wpx, (float)by);        // at base height, close to base (0.25 from it)
        Node wp2 = new Node(wpx, (float)(by + 1));  // at cliff-top height, close to base (0.75 from top)

        nodes[bx, by].AddNeighbor(wp1, true);  // short horizontal approach
        wp1.AddNeighbor(wp2, true);             // vertical climb (slow via GetEdgeInfo)
        wp2.AddNeighbor(nodes[cx, by + 1], true); // short horizontal exit

        cliffWaypoints[key] = (wp1, wp2);
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

    public bool GetStandability(int x, int y){
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
        if (tileHere.HasLadder() || tileBelow.HasLadder()) {return true;}
        return false;
    }
    public void UpdateStandability(int x, int y){ nodes[x,y].standable = GetStandability(x, y); }
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
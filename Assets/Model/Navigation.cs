using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class Node {
    public int x, y;       // grid tile position (for array indexing)
    public float wx, wy;   // world position (actual movement target)
    public bool isWaypoint;
    public List<Node> neighbors;
    public bool standable;
    public Tile tile;

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
    public static Graph instance;
    private Dictionary<(int,int),(Node,Node)> stairWaypoints = new Dictionary<(int,int),(Node,Node)>();

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
        AStar astar = new AStar(nodes, start, goal);
        return astar.Search();
    }

    // only call this if all the neighbors are empty, and just initialized.
    public void AddNeighborsInitial(){
        for (int x = 0; x < world.nx; x++){
            for (int y = 0; y < world.ny; y++){
                UpdateStandability(x, y);
            }
        }
        // Create stair waypoints before the neighbor pass so adjacent tiles can connect to them
        for (int x = 0; x < world.nx; x++){
            for (int y = 0; y < world.ny; y++){
                if (nodes[x,y].tile.HasStairRight() || nodes[x,y].tile.HasStairLeft())
                    CreateStairWaypoints(x, y);
            }
        }
        for (int x = 0; x < world.nx; x++){
            for (int y = 0; y < world.ny; y++){
                UpdateNeighbors(x, y);
            }
        }
    }
    public void UpdateNeighbors(int x, int y){
        UpdateStandability(x, y);
        Node node = nodes[x,y];

        List<Node> eligibleNeighbors = new List<Node>();
        foreach (Node neighbor in node.neighbors) {
            if (IsNeighbor(node, neighbor)) {
                eligibleNeighbors.Add(neighbor);
                neighbor.AddNeighbor(node);
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
        Structure fStruct = node.tile.fStruct;
        if (fStruct != null && fStruct.structType.name == "ladder" && y + 1 < world.ny){
            node.AddNeighbor(nodes[x,y+1], true);
        }
        // Stairs add extra diagonal routes on top of normal horizontal connections
        if (node.tile.HasStairRight() || node.tile.HasStairLeft()) {
            CreateStairWaypoints(x, y);
        }
    }

    public bool IsNeighbor(Node node, Node neighbor){
        if (node.isWaypoint || neighbor.isWaypoint) return true; // waypoints manage their own connections
        int xDiff = neighbor.x - node.x; int yDiff = neighbor.y - node.y;
        return (
        ((xDiff == 1 || xDiff == -1) && yDiff == 0 && (node.standable && neighbor.standable))
        || (xDiff == 0 && yDiff == 1 && node.tile.HasLadder())
        || (xDiff == 0 && yDiff == -1 && neighbor.tile.HasLadder()));
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
            stairWaypoints[(sx, sy)] = (entry, exit);
        } else {
            if (sx + 1 >= world.nx || sx - 1 < 0 || sy + 1 >= world.ny) return;
            if (!GetStandability(sx + 1, sy) || !GetStandability(sx - 1, sy + 1)) return;
            Node entry = new Node(sx + 0.5f, (float)sy);
            Node exit  = new Node(sx - 0.5f, sy + 1f);
            entry.AddNeighbor(exit, true);
            entry.AddNeighbor(nodes[sx+1, sy],   true);
            exit.AddNeighbor( nodes[sx-1, sy+1], true);
            stairWaypoints[(sx, sy)] = (entry, exit);
        }
    }

    public bool GetStandability(int x, int y){
        Tile tileHere = world.GetTileAt(x, y);
        Tile tileBelow = world.GetTileAt(x, y-1);
        if (tileBelow == null) {return false;} // need tile below to exist
        else if (tileHere.type.solid) {return false;} // need tilehere to not be solid
        else if (tileBelow.type.solid) {return true;} // tile below is solid
        else if (tileBelow.building != null && tileBelow.building is not Plant) {return true;} // tile below is building
        else if (tileBelow.mStruct != null && tileBelow.mStruct.structType.name == "platform") {return true;} // tile below is platform
        else if (tileHere.HasLadder() || tileBelow.HasLadder()) {return true;}
        else {return false;}
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
    private Node[,] graph;
    private Node start;
    private Node goal;
    private Dictionary<Node, float> gScore;
    private Dictionary<Node, float> fScore;
    private Dictionary<Node, Node> cameFrom;
    private MinHeap<Node> openHeap;
    private HashSet<Node> closedSet;

    public AStar(Node[,] graph, Node start, Node goal) {
        this.graph = graph;
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

                float tentativeG = gScore[current] + 1f;
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
        nodes.Reverse();
        if (nodes.Count == 0) return null;
        return new Path(nodes, gScore.GetValueOrDefault(nodes[nodes.Count - 1], 0));
    }

    private float Heuristic(Node node, Node goal) {
        return (float)Math.Sqrt((node.wx - goal.wx) * (node.wx - goal.wx)
            + (node.wy - goal.wy) * (node.wy - goal.wy));
    }
}
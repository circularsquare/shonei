using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class Node { // note these are shared between all animals who want to navigate
    public int x, y;
    public List<Node> neighbors;
    public bool standable;
    public Tile tile;
    
    public Node(Tile tile, int x, int y){
        this.tile = tile; this.x = x; this.y = y;
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
                UpdateStandability(x, y); // needs to be called before so you can know which neighbors not to add
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
        // the above part only rechecks EXISTING neighbors, doesn't add new potential ones. that happens below
        // consider rewriting below to use IsNeighbor()
        // add horizontal standable neighbors
        if (x + 1 < world.nx && node.standable && nodes[x+1,y].standable){
            node.AddNeighbor(nodes[x+1,y], true); }
        if (x - 1 >= 0 && node.standable && nodes[x-1,y].standable){
            node.AddNeighbor(nodes[x-1,y], true); }
        // add vertical neighbors via ladders or stairs
        Structure fStruct = node.tile.fStruct;
        if (fStruct != null && y + 1 < world.ny){
            if (fStruct.structType.name == "ladder"){
                node.AddNeighbor(nodes[x,y+1], true);
            } else if (fStruct is Stairs && (fStruct as Stairs).right){
                node.AddNeighbor(nodes[x+1,y+1], true);
            } else if (fStruct is Stairs && !(fStruct as Stairs).right){
                node.AddNeighbor(nodes[x-1,y+1], true);
            }
        }
    }

    public bool IsNeighbor(Node node, Node neighbor){
        int xDiff = neighbor.x - node.x; int yDiff = neighbor.y - node.y;
        return (
        ((xDiff == 1 || xDiff == -1) && yDiff == 0 && (node.standable && neighbor.standable))
        || (xDiff == 0 && yDiff == 1 && node.tile.HasLadder())
        || (xDiff == 0 && yDiff == -1 && neighbor.tile.HasLadder())
        || (xDiff == 1 && yDiff == 1 && node.tile.HasStairRight())
        || (xDiff == -1 && yDiff == -1 && neighbor.tile.HasStairRight())
        || (xDiff == -1 && yDiff == 1 && node.tile.HasStairLeft())
        || (xDiff == 1 && yDiff == -1 && neighbor.tile.HasStairLeft()));
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
        return (float)Math.Sqrt((node.x - goal.x) * (node.x - goal.x)
            + (node.y - goal.y) * (node.y - goal.y));
    }
}
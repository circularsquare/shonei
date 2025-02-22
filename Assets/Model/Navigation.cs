using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class Node { // note these are shared between all animals who want to navigate
    public int x, y;
    public float g, h, f; // cost to reach this node, heuristic cost to goal, total cost to goal
    public List<Node> neighbors;
    public Node parent;
    public bool standable;
    public Tile tile;
    
    public Node(Tile tile, int x, int y){
        this.tile = tile; this.x = x; this.y = y;
        neighbors = new List<Node>();
    }

    // note: adds reciprocally
    public void AddNeighbor(Node n){ if (!neighbors.Contains(n)) {neighbors.Add(n); n.neighbors.Add(this);}} 
    public void RemoveNeighbor(Node n){neighbors.Remove(n); n.neighbors.Remove(this);}
}
public class Path {
    public List<Node> nodes;
    public float cost;
    public int length {get {return nodes.Count - 1;}} // number of links in path, not nodes
    public Node end;
    public Tile tile {get {return end.tile;}}
    public Path(List<Node> nodes, float cost){
        this.nodes = nodes;
        this.cost = cost;
        end = nodes[nodes.Count - 1];
    }
}


public class Graph {
    public World world;
    public Node[,] nodes;

    public Graph(World world){
        this.world = world;
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

        // this part removes no longer viable neighbors
        foreach (Node neighbor in node.neighbors){
            int xDiff = neighbor.x - node.x; int yDiff = neighbor.y - node.y;
            if ((xDiff == 1 || xDiff == -1) && yDiff == 0 && !(node.standable && neighbor.standable)){
                node.RemoveNeighbor(neighbor); }
            else if (xDiff == 0 && yDiff == 1 && !node.tile.HasLadder()){
                node.RemoveNeighbor(neighbor); }
            else if (xDiff == 0 && yDiff == -1 && !neighbor.tile.HasLadder()){
                node.RemoveNeighbor(neighbor);}
            else if (xDiff == 1 && yDiff == 1 && !node.tile.HasStairRight()){
                node.RemoveNeighbor(neighbor); }
            else if (xDiff == -1 && yDiff == -1 && !neighbor.tile.HasStairRight()){
                node.RemoveNeighbor(neighbor); }
            else if (xDiff == -1 && yDiff == 1 && !node.tile.HasStairLeft()){
                node.RemoveNeighbor(neighbor); }
            else if (xDiff == 1 && yDiff == -1 && !neighbor.tile.HasStairLeft()){
                node.RemoveNeighbor(neighbor); }
        }

        // add horizontal standable neighbors
        if (x + 1 < world.nx && node.standable && nodes[x+1,y].standable){
            node.AddNeighbor(nodes[x+1,y]); }
        if (x - 1 >= 0 && node.standable && nodes[x-1,y].standable){
            node.AddNeighbor(nodes[x-1,y]); }
        // add vertical neighbors via ladders or stairs
        Structure fStruct = node.tile.fStruct;
        if (fStruct != null && y + 1 < world.ny){
            if (fStruct.structType.name == "ladder"){
                node.AddNeighbor(nodes[x,y+1]);
            } else if (fStruct is Stairs && (fStruct as Stairs).right){
                node.AddNeighbor(nodes[x+1,y+1]);
            } else if (fStruct is Stairs && !(fStruct as Stairs).right){
                node.AddNeighbor(nodes[x-1,y+1]);
            }
        }
    }
    public bool GetStandability(int x, int y){
        Tile tileHere = world.GetTileAt(x, y);
        Tile tileBelow = world.GetTileAt(x, y-1);
        if (tileBelow == null) {return false;} // need tile below to exist
        else if (tileHere.type.solid) {return false;} // need tilehere to not be solid

        else if (tileBelow.type.solid) {return true;} // tile below is solid
        else if (tileBelow.building != null) {return true;} // tile below is building
        else if (tileBelow.mStruct != null && tileBelow.mStruct.structType.name == "platform") {return true;} // tile below is platform
        else {return false;}
    }
    public void UpdateStandability(int x, int y){ nodes[x,y].standable = GetStandability(x, y); }
}


public class AStar {
    private Node[,] graph;
    private Node start;
    private Node goal;
    private List<Node> openList;
    private List<Node> closedList;

    public AStar(Node[,] graph, Node start, Node goal) {
        this.graph = graph;
        this.start = start;
        this.goal = goal;
        openList = new List<Node>();
        closedList = new List<Node>();
    }

    public Path Search() {
        Reset();
        start.g = 0;     // Initialize the start node
        start.h = Heuristic(start, goal);
        start.f = start.g + start.h;
        openList.Add(start);

        while (openList.Count > 0) { // Get the node with the lowest f score
            Node current = openList[0];

            // remove current from open, add to cloesd
            openList.RemoveAt(0);
            closedList.Add(current);

            if (current == goal) { return ReconstructPath(current); }

            // Explore the neighbors of the current node
            foreach (Node neighbor in current.neighbors) {
                if (closedList.Contains(neighbor)) { continue; } // skip if closed

                float tentativeG = current.g + 1f; // 1 is cost to move!! make different for ladders?
                
                //if (!neighbor.standable || !current.standable){tentativeG = 1000;}

                // Check if the neighbor is already in the open list
                if (openList.Contains(neighbor)) {
                    // If the tentative g score is lower, update the neighbor's g score
                    if (tentativeG < neighbor.g) {
                        neighbor.g = tentativeG;
                        neighbor.f = neighbor.g + neighbor.h;
                        neighbor.parent = current;
                    }
                } else { 
                    neighbor.g = tentativeG; // Add the neighbor to the open list
                    neighbor.h = Heuristic(neighbor, goal);
                    neighbor.f = neighbor.g + neighbor.h;
                    neighbor.parent = current;
                    openList.Add(neighbor);
                }
            }
        }
        return null; // If we reach this point, there is no path to the goal
    }
    private float Heuristic(Node node, Node goal) {
        // Simple Manhattan distance heuristic
        return (float)Math.Sqrt((node.x - goal.x)*(node.x - goal.x) + (node.y - goal.y)*(node.y - goal.y));
    }
    public void Reset(){
        foreach (Node node in graph){
            node.parent = null;
            node.g = 0; node.h = 0; node.f = 0;
        }
        openList.Clear(); closedList.Clear();
    }

    private Path ReconstructPath(Node node) {
        int maxDepth = 60;
        int depth = 0;
        List<Node> nodes = new List<Node>();
        while (node != null) {
            depth++;
            if (depth > maxDepth) {Debug.LogError("path too long"); break;}
            nodes.Add(node);
            node = node.parent;
        }
        nodes.Reverse();
        if (nodes == null || nodes.Count == 0) {return null;}
        Path path = new Path(nodes, nodes[nodes.Count-1].g); // does this work as distance? 
        return path;
    }
}
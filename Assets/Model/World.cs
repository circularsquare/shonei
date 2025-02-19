using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class World : MonoBehaviour
{
    Tile[,] tiles;
    public Graph graph;
    public int nx;
    public int ny;
    public WorldController worldController;
    public InventoryController invController;
    public AnimalController animalController;
    public PlantController plantController;
    
    public static World instance;
    public float timer = 0f;


    public void Awake(){
        if (instance != null){
            Debug.LogError("there should only be one world?");}
        instance = this;

        graph = new Graph(this);

        nx = 50;
        ny = 100;
        tiles = new Tile[nx, ny];
        graph.nodes = new Node[nx, ny];
        for (int x = 0; x < nx; x++){
            for (int y = 0; y < ny; y++){
                tiles[x, y] = new Tile(this, x, y);
                graph.nodes[x, y] = tiles[x, y].node;
            }
        }
        invController = InventoryController.instance;
        worldController = WorldController.instance;
        animalController = AnimalController.instance;
        plantController = PlantController.instance;
    }

    public void Update(){
        if (Math.Floor(timer + Time.deltaTime) - Math.Floor(timer) > 0){ // every 1 sec
            animalController.TickUpdate(); 
            plantController.TickUpdate();
        }        
        float period = 0.2f;
        if (Math.Floor((timer + Time.deltaTime) / period) - Math.Floor(timer / period) > 0){  // every 0.2 sec
            invController.TickUpdate(); // update itemdisplay, add controller instances
            InfoPanel.instance.UpdateInfo();
        }
        timer += Time.deltaTime;
    }
        

    // ---------------------------------
    // TILE STUFF
    // ---------------------------------
    public Tile GetTileAt(int x, int y){
        if (x >= nx || x < 0 || y >= ny || y < 0){
            // Debug.Log("tile " + x + "," + y +  " out of range");
            return null;
        }
        return tiles[x,y];
    }
    public Tile GetTileAt(float x, float y){
        int xi = Mathf.FloorToInt(x + 0.5f);
        int yi = Mathf.FloorToInt(y + 0.5f);
        return GetTileAt(xi, yi);
    }


    // ---------------------------------
    // CALLBACKS
    // ---------------------------------


}


public class Node {
    public int x, y;
    public float g, h, f; // cost to reach this node, heuristic cost to goal, total cost to goal
    public List<Node> neighbors;
    public Node parent;
    public bool standable;
    public Tile tile;
    
   
    public Node(Tile tile, int x, int y, bool standable){
        this.tile = tile; this.x = x; this.y = y; this.standable = standable;
        neighbors = new List<Node>();
    }
    public void AddNeighbor(Node n){neighbors.Add(n); n.neighbors.Add(this);}  // also adds reciprocally
    public void RemoveNeighbor(Node n){neighbors.Remove(n); n.neighbors.Remove(this);}
}




public class Graph {
    public World world;
    public Node[,] nodes;

    public Graph(World world){
        this.world = world;
    }
    public void Initialize(){ // updates whole network
        CalculateTileStandability();
        AddNeighborsInitial();
        
    }
    public List<Node> Navigate(Node start, Node goal){
        AStar astar = new AStar(nodes, start, goal);
        List<Node> path = astar.Search();
        return path;
    }

    // only call this if all the neighbors are empty, and just initialized.
    public void AddNeighborsInitial(){
        for (int x = 0; x < world.nx; x++){
            for (int y = 0; y < world.ny; y++){
                // add horizontal neighbors
                if (x + 1 < world.nx) {nodes[x,y].AddNeighbor(nodes[x+1,y]);}
                // add vertical neighbors via ladders or stairs
                Structure fStruct = nodes[x,y].tile.fStruct;
                if (fStruct != null && y + 1 < world.ny){
                    if (fStruct.structType.name == "ladder"){
                        nodes[x,y].AddNeighbor(nodes[x,y+1]);
                    } else if (fStruct is Stairs && (fStruct as Stairs).right){
                        nodes[x,y].AddNeighbor(nodes[x+1,y+1]);
                    } else if (fStruct is Stairs && !(fStruct as Stairs).right){
                        nodes[x,y].AddNeighbor(nodes[x-1,y+1]);
                    }
                }
            }
        }
    }
    public bool GetTileStandability(int x, int y){
        Tile tileHere = world.GetTileAt(x, y);
        Tile tileBelow = world.GetTileAt(x, y-1);
        if (tileBelow == null) {return false;} // need tile below to exist
        else if (tileHere.type.solid) {return false;} // need tilehere to not be solid

        else if (tileBelow.type.solid) {return true;} // tile below is solid
        else if (tileBelow.building != null) {return true;} // tile below is building
        else if (tileBelow.fStruct != null && tileBelow.fStruct.structType.name == "platform") {return true;} // tile below is platform
        else {return false;}
        
    }
    public void CalculateTileStandability(Tile tile){CalculateTileStandability(tile.x, tile.y);}
    public void CalculateTileStandability(int x, int y){world.GetTileAt(x, y).node.standable = GetTileStandability(x, y);}
    public void CalculateTileStandability(){
        for (int x = 0; x < world.nx; x++){
            for (int y = 0; y < world.ny; y++){
                CalculateTileStandability(x, y);
            }
        }
    }
    
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

    public List<Node> Search() {
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
    
    private List<Node> ReconstructPath(Node node) {
        int maxDepth = 30;
        int depth = 0;
        List<Node> path = new List<Node>();
        while (node != null) {
            depth++;
            if (depth > maxDepth) {Debug.LogError("path too long"); break;}
            path.Add(node);
            node = node.parent;
        }
        path.Reverse();
        return path;
    }
}
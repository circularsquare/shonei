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


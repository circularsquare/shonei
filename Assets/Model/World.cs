using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class World 
{
    Tile[,] tiles;
    public bool[,] standableTiles;
    public int nx;
    public int ny;
    public WorldController worldController;
    public InventoryController invController;
    public int na = 0;
    public int maxna = 1000;
    public Animal[] animals;

    public float timer = 0f;

    public World(int nx = 50, int ny = 100){
        this.nx = nx;
        this.ny = ny;
        tiles = new Tile[nx,ny];
        standableTiles = new bool[nx,ny];
        for (int x = 0; x < nx; x++){
            for (int y = 0; y < ny; y++){
                tiles[x,y] = new Tile(this, x, y);
                standableTiles[x,y] = false;
            }
        }

        animals = new Animal[maxna];
        invController = InventoryController.instance;
        worldController = WorldController.instance;


    }

    public void Start(){
        for (int i = 0; i < 10; i++){
            AddAnimal();
        }
    }

    public void Update(){
        if (Math.Floor(timer + Time.deltaTime) - Math.Floor(timer) > 0.5){
            for (int a = 0; a < na; a++){ // later, change the animal work method to not be a timer and instead track individual animal workloads
                animals[a].Work(); 
            }
        }
        timer += Time.deltaTime;
    }
        


    // ---------------------------
    // ANIMAL STUFF 
    // ---------------------------
    public void AddAnimal(int x = 10, int y = 2, Job job = null){
        animals[na] = new Animal(this, x, y, job);
        animals[na].RegisterCbAnimalChanged(AnimalController.instance.OnAnimalChanged);
        if (job == null) {
            animals[na].job = Db.getJobByName("none");
            AnimalController.instance.jobCounts[Db.jobs[0]] += 1;
        }
        na += 1;
    }
    public void AddJob(string jobstr, int n = 1){
        if (n > 0) {
            for (int a = 0; a < maxna; a++){
                if (n == 0){return;}
                if (animals[a] != null && animals[a].job.id == 0){ // if null
                    animals[a].SetJob(jobstr);
                    n -= 1;
                }
            }
            Debug.Log("no free mice!"); // only fires if doesn't return early
        } else if (n < 0) {
            for (int a = 0; a < maxna; a++){
                if (n == 0){return;}
                if (animals[a] != null && animals[a].job.name == jobstr){
                    animals[a].SetJob("none");
                    n += 1;
                }
            }
            Debug.Log("no more mice to fire!");
        }
        

    }

    // ---------------------------------
    // TILE STUFF
    // ---------------------------------

    public void RandomizeTiles(){

    }

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

    public void CalculateTileStandability(){
        for (int x = 0; x < nx; x++){
            for (int y = 0; y < ny; y++){
                if (y == 0){
                    SetStandability(x, y, true);
                } else if (GetTileAt(x, y-1).Solid() && !GetTileAt(x, y).Solid()){
                    SetStandability(x, y, true);
                } else {
                    SetStandability(x, y, false);
                }
            }
        }
    }
    public void CalculateTileStandability(int x, int y){
        for (int i = 0; i < 2; i++){
            if (y == 0){
                SetStandability(x, y, true);
            } else if (GetTileAt(x, y-1).Solid() && !GetTileAt(x, y).Solid()){
                SetStandability(x, y, true);
            } else {
                SetStandability(x, y, false); 
            }
            if (y < ny-1){ y += 1; }
        }
    }
    void SetStandability(int x, int y, bool val){
        standableTiles[x,y] = val;
        tiles[x,y].standable = val;
    }
    
    // ---------------------------------
    // CALLBACKS
    // ---------------------------------


}

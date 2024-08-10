using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using Pathfinding;

public class Animal : MonoBehaviour      
{
    public string aName;
    public float x;
    public float y;
    public float timerOffset = 0f;
    public float maxSpeed = 1f;

    public Tile target;
    public Tile workTile;

    public Job job;
    public Inventory inventory; 
    public GlobalInventory ginv;
    // list of skills goes here?
    // maybe one for each job?

    public enum AnimalState {Idle, Walking, Working, Retrieving, Hauling}

    public AnimalState state;
    public GameObject go;
    public SpriteRenderer sr;
    public Astar astar;

    public int[][] map;

    public Sprite sprite;
    public Bounds bounds; // a box to click on to select the animal
    World world;
    
    Action<Animal, Job> cbAnimalChanged;

    public void Start(){
        world = World.instance;
        this.aName = "mouse";
        this.state = AnimalState.Idle;
        this.job = Db.jobs[0];
        this.go = this.gameObject;
        this.go.name = "animal" + aName;
        this.inventory = new Inventory(5, (int)Math.Round(x), (int)Math.Round(y));
        ginv = GlobalInventory.instance;
    }

    public void SetJob(Job newJob){
        Job oldJob = this.job;
        this.job = newJob;
        if (cbAnimalChanged != null){
            cbAnimalChanged(this, oldJob);} 
        FindWork();
    }
    public void SetJob(string jobStr){
        SetJob(Db.GetJobByName(jobStr));
    }
    public void FastUpdate(){
        if (state == AnimalState.Working){ // haulers are never Working
            switch (job.name) {
            case "none":
                Debug.LogError("working without a job!");
                break;
            case "logger":
                Produce("wood", 1);
                break;
            case "miner":
                Produce("stone", 1);
                break;
            case "farmer":
                Produce("wheat", 1);
                break;
            case "digger":
                Produce("soil", 1);
                break;
            default:
                Debug.LogError("unknown job!");
                break;
            }
        }

        if (state == AnimalState.Idle) {
            FindWork(); // want to move this into slow update, once i make it
        }
        if (state == AnimalState.Idle){
            MoveTo(x + (float)UnityEngine.Random.Range(-2, 2), y);
        }
        
    }
    public void Produce(string itemName, int amount = 1){
        inventory.AddItem(itemName, amount);
        ginv.AddItem(itemName, amount);
    }

    public void Update(){
        if ((state == AnimalState.Walking) || (state == AnimalState.Retrieving) || (state == AnimalState.Hauling)){
            // arrived at target
            if (Vector3.Distance(this.go.transform.position, target.go.transform.position) < 0.02f){
                this.go.transform.position = target.go.transform.position;
                SyncPosition(); 
                

                if (state == AnimalState.Walking){ // have reached destination.
                    if (target == workTile){
                        state = AnimalState.Working;
                    } else {
                        state = AnimalState.Idle;
                    }
                }
                if (state == AnimalState.Retrieving){
                    HaulBack();
                    state = AnimalState.Hauling;
                }
                if (state == AnimalState.Hauling){
                    DropOff();
                    state = AnimalState.Idle;
                }

            }
            else {
                this.go.transform.position = Vector3.MoveTowards(this.go.transform.position, 
                    target.go.transform.position, maxSpeed * Time.deltaTime);
                SyncPosition();
            }
        }
    }
    public void SyncPosition(){
        this.x = this.go.transform.position.x;
        this.y = this.go.transform.position.y;
        bounds.center = go.transform.position;
    }
    
    public void MoveTo(float x, float y){
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) { return; }
        if (/*world.GetTileAt(x, y).type.solid |*/ true){
            MoveTo(world.GetTileAt(x, y));
        }
    }
    public void MoveTo(Tile t){
        target = t;
        this.state = AnimalState.Walking;
    }

    // hauler method. finds ANY floor item
    public void FindHaul(){ // called in FindWork() for haulers
        Tile itemTile = FindFloorItem();
        if (itemTile == null){return;} // didn't find any floor items
        Item item = itemTile.itemStack.item;
        Tile storageTile = FindStorage(item);
        target = itemTile;
        state = AnimalState.Retrieving; // on arrival, will HaulBack()
    }
    // hauler method
    public void HaulBack(){
        // pick up
        // then set target to storageTile
        // and set state to Hauling
        return;
    }
    public void DropOff(){
        return;
    }

    // finds ANY item on floor
    public Tile FindFloorItem(int searchRadius = 30){ 
        Tile closestTile = null;
        float closestDistance = float.MaxValue;
        for (int x = -searchRadius; x <= searchRadius; x++) {
            for (int y = -searchRadius; y <= searchRadius; y++) {
                Tile tile = world.GetTileAt(this.x + x, this.y + y);
                if (tile.ContainsFloorItem()) {
                    float distance = SquareDistance((float)tile.x, this.x, (float)tile.y, this.y);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestTile = tile;
                    }
                }
            }
        }
        return closestTile;
    }
    public Tile FindStorage(Item item, int searchRadius = 50){ // finds item in building
        Tile closestTile = null;
        float closestDistance = float.MaxValue;
        for (int x = -searchRadius; x <= searchRadius; x++) {
            for (int y = -searchRadius; y <= searchRadius; y++) {
                Tile tile = world.GetTileAt(this.x + x, this.y + y);
                // note: change when you have configurable inventories!s
                if (tile.building != null && tile.building.HasSpaceForItem(item)) { 
                    float distance = SquareDistance((float)tile.x, this.x, (float)tile.y, this.y);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestTile = tile;
                    }
                }
            }
        }
        return closestTile;
    }

    public Tile FindTile(TileType tileType, int searchRadius = 30){ 
        Tile closestTile = null;
        float closestDistance = float.MaxValue;

        for (int x = -searchRadius; x <= searchRadius; x++) {
            for (int y = -searchRadius; y <= searchRadius; y++) {
                Tile tile = world.GetTileAt(this.x + x, this.y + y);
                if (tile != null && tile.type == tileType && ! tile.reserved) {
                    float distance = SquareDistance((float)tile.x, this.x, (float)tile.y, this.y);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestTile = tile;
                    }
                }
            }
        }
        return closestTile;
    }
    public float SquareDistance(float x1, float x2, float y1, float y2){
        return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);
    }
    public Tile FindTile(string tileTypeStr, int searchRadius = 30){
        if (Db.tileTypeByName.ContainsKey(tileTypeStr)){
            return FindTile(Db.tileTypeByName[tileTypeStr], searchRadius);
        } else {Debug.Log("tile type doesn't exist"); return null;}
    }
    public void FindWork(){
        Tile t = null;
        if (job.name == "none"){
            if (workTile != null){
                workTile.reserved = false;}
            workTile = null;
            state = AnimalState.Idle;
            return;
        } else if (job.name == "hauler"){
            FindHaul();
            return;
        } else if (job.name == "logger"){
            t = FindTile("tree");
        } else if (job.name == "digger"){
            t = FindTile("soil");
        } else if (job.name == "miner"){ 
            t = FindTile("stone");
        } else if (job.name == "farmer"){
            t = FindTile("wheat");
        } 
        if (t != null){
            workTile = t;
            workTile.reserved = true;
            MoveTo(workTile);
        }
    }

    public void RegisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged += callback;}
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged -= callback;}
}

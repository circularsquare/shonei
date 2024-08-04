using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using Pathfinding;

public class Animal : MonoBehaviour      
{
    // public enum Jobs {
    //     None, Woodcutter, Miner, Farmer
    // }
    public enum AnimalState {Idle, Walking, Working}

    public string aName;
    public float x;
    public float y;
    public float maxSpeed = 1f;

    public Tile target;

    public Job job;
    public Inventory inventory; // just points to the global inv right now
    // list of skills goes here?
    // maybe one for each job?

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
        Debug.Log("start");
    }

    public void SetJob(Job newJob){
        Job oldJob = this.job;
        this.job = newJob;
        if (cbAnimalChanged != null){
            cbAnimalChanged(this, oldJob);} 
    }
    public void SetJob(string jobStr){
        SetJob(Db.getJobByName(jobStr));
    }
    public void Work(){
        if (inventory == null){
            inventory = InventoryController.instance.inventory;
        }
        switch (job.name) {
            case "none":
                break;
            case "logger":
                inventory.AddItem("wood", 1);
                break;
            case "miner":
                inventory.AddItem("stone", 1);
                break;
            case "farmer":
                inventory.AddItem("wheat", 1);
                break;
            case "digger":
                inventory.AddItem("soil", 1);
                break;
            default:
                Debug.LogError("unknown job!");
                break;
        }

        if (state == AnimalState.Idle)  {
            MoveTo(x + (float)UnityEngine.Random.Range(-3, 3), y);
        }
        
    }

    public void Update(){
        if (state == AnimalState.Walking){
            if (Vector3.Distance(this.go.transform.position, target.go.transform.position) < 0.02f){
                this.go.transform.position = target.go.transform.position;
                SyncPosition(); 
                this.state = AnimalState.Idle;
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
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) {
            Debug.Log("destination out of range!" + x.ToString() + "," + y.ToString());
            return;
        }
        if (/*world.GetTileAt(x, y).type.solid |*/ true){
            target = world.GetTileAt(x, y);
            this.state = AnimalState.Walking;
        }
    }

    public void FetchItem(){
        // huhhh
    }



    public void RegisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged += callback;}
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged -= callback;}
}

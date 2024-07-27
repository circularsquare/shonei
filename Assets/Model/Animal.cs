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

        //MoveTo(x + (float)UnityEngine.Random.Range(-1, 1), y);
    }
    
    public void MoveTo(float x, float y){
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) {
            Debug.Log("destination out of range!" + x.ToString() + "," + y.ToString());
            return;
        }
        if (/*world.GetTileAt(x, y).type.solid |*/ true){
            this.state = AnimalState.Walking;
            Vector2Int start = new Vector2Int{x=(int)this.x, y=(int)this.y};
            Vector2Int end = new Vector2Int{x=(int)x, y=(int)y};
            // a star stuff
            //     List<Vector2Int> result = new Astar(world.standableTiles, start, end).Result;
            //     if( result.Count>1){
            //         this.x = (float)result[1].x;
            //         this.y = (float)result[1].y; // this is kinda stupid... having two different sets of coords.
            //         go.transform.position = new Vector3((float)result[1].x, (float)result[1].y, go.transform.position.z);
            //     }
            // }
            this.x = x;
            this.y = y;
            go.transform.position = new Vector3(x, y, go.transform.position.z);
            bounds.center = go.transform.position;

    
            // when done, call some stuff.
            
            this.state = AnimalState.Idle;
        }
    }
    public void MoveToward(float x, float y){
        
    }


    public void RegisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged += callback;}
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged -= callback;}
}

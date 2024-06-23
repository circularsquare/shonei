using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using Pathfinding;

public class Animal      
{
    // public enum Jobs {
    //     None, Woodcutter, Miner, Farmer
    // }
    public enum animalState {Idle, Walking, Working}

    public string aName;
    public float x;
    public float y;
    public float timeToFinish;
    public Job job;
    public Inventory inventory;
    public animalState state;
    public GameObject go;
    public SpriteRenderer sr;
    public Astar astar;

    public int[][] map;

    public Sprite sprite;
    World world;
    
    Action<Animal, Job> cbAnimalChanged;

    public Animal(World world, float x = 0f, float y = 0f, Job job = null, string aName = "mouse"){
        this.world = world;
        this.x = x;
        this.y = y;
        this.aName = aName;
        if(job == null){
            this.job = Db.jobs[0];
        } else {
            this.job = job;
        }
        this.state = animalState.Idle;
        go = new GameObject();
        go.name = "An" + aName;
        go.transform.SetParent(world.worldController.transform, true);
        go.transform.position = new Vector3(x, y, 0);
        sr = go.AddComponent<SpriteRenderer>();
        sprite = Resources.Load<Sprite>("Sprites/Mushrooms/redMushSmall");
        sr.sprite = sprite;

        // go.AddComponent<CharacterController>();
        // seeker = go.AddComponent<Seeker>();
        // go.AddComponent<AstarAI>();

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
            case "woodcutter":
                inventory.AddItem("wood", 1);
                break;
            case "miner":
                inventory.AddItem("stone", 1);
                break;
            case "farmer":
                inventory.AddItem("wheat", 1);
                break;
            default:
                Debug.LogError("unknown job!");
                break;
        }

        MoveTo(x + (float)UnityEngine.Random.Range(-1, 1), y);
    }
    
    public void MoveTo(float x, float y){
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) {
            Debug.Log("destination out of range!" + x.ToString() + "," + y.ToString());
            return;
        }
        if (world.GetTileAt(x, y).standable | true){
            this.state = animalState.Walking;
            Vector2Int start = new Vector2Int{x=(int)this.x, y=(int)this.y};
            Vector2Int end = new Vector2Int{x=(int)x, y=(int)y};
            //     List<Vector2Int> result = new Astar(world.standableTiles, start, end).Result;
            //     if( result.Count>1){
            //         this.x = (float)result[1].x;
            //         this.y = (float)result[1].y; // this is kinda stupid... having two different sets of coords.
            //         go.transform.position = new Vector3((float)result[1].x, (float)result[1].y, go.transform.position.z);
            //     }
            // }
            // NOTE this a star stuff doesnt work right now... idk why.
            this.x = x;
            this.y = y;
            go.transform.position = new Vector3(x, y, go.transform.position.z);

            this.state = animalState.Idle;
        }
    }
    public void MoveToward(float x, float y){
        
    }


    public void RegisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged += callback;}
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged -= callback;}
}

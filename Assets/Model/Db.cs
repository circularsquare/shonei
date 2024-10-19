using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Runtime.Serialization;

// this loads before anything else. 

public class Db : MonoBehaviour { // should detach from game object (or make it a conrtrolelr?)
    public static Db instance {get; protected set;}
    // itemById or jobById is just the array items or jobs.
    public static Dictionary<string, int> iidByName {get; protected set;}
    public static Dictionary<string, Item> itemByName {get; protected set;}
    public static Dictionary<string, Job> jobByName {get; protected set;}
    public static Dictionary<string, BuildingType> buildingTypeByName {get; protected set;}
    public static Dictionary<string, TileType> tileTypeByName {get; protected set;}

    // int maxJobs = 40;
    // int maxRecipes = 5000;
    public static Item[] items = new Item[500];
    public static Job[] jobs = new Job[100];
    public static Recipe[] recipes = new Recipe[500];
    public static BuildingType[] buildingTypes = new BuildingType[300];
    public static TileType[] tileTypes = new TileType[100];


    // items: stored in csv and accessible through here
    // jobs: stored in Animal.Jobs enum
    // recipes: stored in json
        // should there just be one recipe per possible (inputs, outputs)?

    private Db()
    {
        if (instance != null){
            Debug.LogError("tried to create two instances of database"); }
        instance = this;
        
        iidByName = new Dictionary<string, int>();
        itemByName = new Dictionary<string, Item>();
        jobByName = new Dictionary<string, Job>();
        buildingTypeByName = new Dictionary<string, BuildingType>();
        tileTypeByName = new Dictionary<string, TileType>();
    } 

    void Awake(){ // this runs before Start() like in world
        ReadJson();
        Debug.Log("db loaded");
    } 

    void ReadJson(){
        // read Items
        string jsonTextItems = File.ReadAllText(Application.dataPath + "/Resources/itemsDb.json");
        Item[] itemsUnplaced = JsonConvert.DeserializeObject<Item[]>(jsonTextItems);
        foreach (Item item in itemsUnplaced){
            if (items[item.id] != null){Debug.LogError("error!! multiple items with same id");}
            items[item.id] = item;
            if (item.name != null){
                itemByName.Add(item.name, item);
                iidByName.Add(item.name, item.id);
            }
        }

        // read Jobs
        string jsonTextJobs = File.ReadAllText(Application.dataPath + "/Resources/jobsDb.json");
        Job[] jobsUnplaced = JsonConvert.DeserializeObject<Job[]>(jsonTextJobs);
        foreach (Job job in jobsUnplaced){
            if (jobs[job.id] != null){Debug.LogError("error!! multiple jobs with same id");}
            jobs[job.id] = job;
            if (job.name != null){
                jobByName.Add(job.name, job);
            }
        }
        Debug.Log("loaded jobs");
        // read Recipes
        string jsonTextRecipes = File.ReadAllText(Application.dataPath + "/Resources/recipesDb.json");
        Recipe[] recipesUnplaced = JsonConvert.DeserializeObject<Recipe[]>(jsonTextRecipes);
        foreach (Recipe recipe in recipesUnplaced){
            if (recipes[recipe.id] != null){Debug.LogError("error!! multiple recipes with same id");}
            recipes[recipe.id] = recipe;
            if (jobByName.ContainsKey(recipe.job)){
                jobByName[recipe.job].recipes.Add(recipe);
            }
        }
        foreach (Recipe recipe in recipes){
            if (recipe != null){
                Debug.Log(recipe.inputs[0].item.name);
                Debug.Log(recipe.outputs[0].item.name);
            }
        }

        // read Buildings
        string jsonBuildingTypes = File.ReadAllText(Application.dataPath + "/Resources/buildingsDb.json");
        BuildingType[] buildingTypesUnplaced = JsonConvert.DeserializeObject<BuildingType[]>(jsonBuildingTypes);
        foreach (BuildingType buildingType in buildingTypesUnplaced){
            if (buildingTypes[buildingType.id] != null){Debug.LogError("error!! multiple building types with same id");}
            buildingTypes[buildingType.id] = buildingType;
            buildingTypeByName.Add(buildingType.name, buildingType);
        }

        // read Tiles
        string jsonTileTypes = File.ReadAllText(Application.dataPath + "/Resources/tilesDb.json");
        TileType[] tileTypesUnplaced = JsonConvert.DeserializeObject<TileType[]>(jsonTileTypes);
        foreach (TileType tileType in tileTypesUnplaced){
            if (tileTypes[tileType.id] != null){Debug.LogError("error!! multiple tile types with same id");}
            tileTypes[tileType.id] = tileType;
            tileTypeByName.Add(tileType.name, tileType);
        }
        

    }

    void Update(){  }
    public static Job GetJobByName(string name){
        if (jobByName.ContainsKey(name)){
            return jobByName[name];
        } else {
            return null;
        }
    }
}

// theres already an Item class in another file
public class Job {
    public int id {get; set;} // for some reason the set can't be protected. for the json deserialize to work
    public string name {get; set;}
    public string jobType {get; set;}
    public List<Recipe> recipes = new List<Recipe>();
}

public class Recipe {
    public int id {get; set;}
    public string job {get; set;}
    public string description {get; set;} // optional (maybe make the getter return something other than null?)
    public ItemQuantity[] inputs {get; set;}
    public ItemQuantity[] outputs {get; set;}
}

public class BuildingType {
    public int id {get; set;}
    public string name {get; set;}
    public int nx {get; set;}
    public int ny {get; set;}
    public ItemQuantity[] costs {get; set;}
    public bool isTile {get; set;}
}

public class TileType {
    public int id {get; set;}
    public string name {get; set;}
    public bool solid {get; set;}
}


// for stuff like input costs.
public class ItemQuantity {
    public int id {get; set;}
    public int quantity {get; set;}
    public Item item;
    public ItemQuantity(){}
    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        item = Db.items[id];
    }

    public ItemQuantity(int id, int quantity){
        this.item = Db.items[id];
        this.quantity = quantity;
    }
    public ItemQuantity(Item item, int quantity){
        this.item = item;
        this.quantity = quantity;
    }
    public ItemQuantity(string name, int quantity){
        this.item = Db.itemByName[name];
        this.quantity = quantity;
    }
    public override string ToString(){
        return item.name + ": " + quantity.ToString();}
    public string ItemName(){
        return item.name;
    }
}
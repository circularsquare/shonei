using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Runtime.Serialization;

// Loads before anything else — Awake() runs before World.Start().
public class Db : MonoBehaviour {
    public static Db instance {get; protected set;}
    // itemById or jobById is just the array items or jobs.
    public static Dictionary<string, int> iidByName {get; protected set;}
    public static Dictionary<string, Item> itemByName {get; protected set;}
    public static Dictionary<string, Job> jobByName {get; protected set;}
    public static Dictionary<string, StructType> structTypeByName {get; protected set;}
    public static Dictionary<string, PlantType> plantTypeByName {get; protected set;}
    public static Dictionary<string, TileType> tileTypeByName {get; protected set;}

    public static Item[] items = new Item[500];
    public static Item[] itemsFlat = new Item[500];
    public static int itemsCount = 0;
    public static List<Item> edibleItems;
    public static List<Item> equipmentItems;
    public static Job[] jobs = new Job[100];
    public static Recipe[] recipes = new Recipe[500];
    public static StructType[] structTypes = new StructType[600];
    public static PlantType[] plantTypes = new PlantType[600];
    public static TileType[] tileTypes = new TileType[100];

    // sprite sorting orders
        // 100: blueprint
        // 80: fStruct
        // 70: item on floor
        // 60: plant
        // 40: animal
        // 30: item in storage
        // 20: building
        // 0: tile

    // items: stored in csv and accessible through here
    // jobs: stored in Animal.Jobs enum
    // recipes: stored in json
        // should there just be one recipe per possible (inputs, outputs)?

    private Db(){
        if (instance != null){
            Debug.LogError("tried to create two instances of database"); }
        instance = this;
        
        iidByName = new Dictionary<string, int>();
        itemByName = new Dictionary<string, Item>();
        jobByName = new Dictionary<string, Job>();
        structTypeByName = new Dictionary<string, StructType>();
        tileTypeByName = new Dictionary<string, TileType>();
        plantTypeByName = new Dictionary<string, PlantType>();
    } 

    void Awake(){ // this runs before Start() like in world
        ReadJson();
        itemsFlat = itemsFlat.Take(itemsCount).ToArray();
        edibleItems = itemsFlat.Where(i => i.foodValue > 0).OrderByDescending(i => i.foodValue).ToList();
        equipmentItems = itemsFlat.Where(i => { Item cur = i; while (cur != null) { if (cur.name == "tools") return true; cur = cur.parent; } return false; }).ToList();
        Debug.Log("db loaded");
    } 

    void ReadJson(){
        // read Items
        string jsonTextItems = File.ReadAllText(Application.dataPath + "/Resources/itemsDb.json");
        foreach (Item item in JsonConvert.DeserializeObject<Item[]>(jsonTextItems))
            AddItemToDb(item);

        // read Tiles
        string jsonTileTypes = File.ReadAllText(Application.dataPath + "/Resources/tilesDb.json");
        TileType[] tileTypesUnplaced = JsonConvert.DeserializeObject<TileType[]>(jsonTileTypes);
        foreach (TileType tileType in tileTypesUnplaced){
            if (tileTypes[tileType.id] != null){Debug.LogError("error!! multiple tile types with same id");}
            tileTypes[tileType.id] = tileType;
            tileTypeByName.Add(tileType.name, tileType);
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

        // read Structures
        string jsonStructTypes = File.ReadAllText(Application.dataPath + "/Resources/buildingsDb.json");
        StructType[] structTypesUnplaced = JsonConvert.DeserializeObject<StructType[]>(jsonStructTypes);
        foreach (StructType structType in structTypesUnplaced){
            if (structTypes[structType.id] != null){Debug.LogError("error!! multiple struct types with same id");}
            structType.isPlant = false;
            structTypes[structType.id] = structType;
            structTypeByName.Add(structType.name, structType);
        } 

        string jsonPlantTypes = File.ReadAllText(Application.dataPath + "/Resources/plantsDb.json");
        PlantType[] plantTypesUnplaced = JsonConvert.DeserializeObject<PlantType[]>(jsonPlantTypes);
        foreach (PlantType plantType in plantTypesUnplaced){
            if (plantTypes[plantType.id] != null){Debug.LogError("error!! multiple plant types with same id");}
            plantTypes[plantType.id] = plantType;
            plantTypeByName.Add(plantType.name, plantType);
            // also add each plant as a building so you can build it
            if (structTypes[plantType.id] != null){Debug.LogError("error!! multiple struct types with same id");}
            structTypes[plantType.id] = plantType;
            plantType.isPlant = true;
            structTypeByName.Add(plantType.name, plantType);
        } 


        // read Recipes
        string jsonTextRecipes = File.ReadAllText(Application.dataPath + "/Resources/recipesDb.json");
        Recipe[] recipesUnplaced = JsonConvert.DeserializeObject<Recipe[]>(jsonTextRecipes);
        foreach (Recipe recipe in recipesUnplaced){
            if (recipes[recipe.id] != null){Debug.LogError("error!! multiple recipes with same id");}
            recipes[recipe.id] = recipe;
            if (jobByName.ContainsKey(recipe.job)){ // add recipe to job's array of recipes
                Job job = jobByName[recipe.job];
                job.recipes[job.nRecipes] = recipe;
                job.nRecipes += 1;                
            }
        }        
    }

    public static Job GetJobByName(string name) {
        jobByName.TryGetValue(name, out Job job);
        return job;
    }

    void AddItemToDb(Item item) {
        if (items[item.id] != null)        Debug.LogError("error!! multiple items with same id");
        if (itemByName.ContainsKey(item.name)) Debug.LogError("error!! multiple items with same name");
        items[item.id] = item;
        if (item.name != null) {
            itemByName.Add(item.name, item);
            iidByName.Add(item.name, item.id);
            itemsFlat[itemsCount++] = item;
        }
        if (item.children != null) {
            foreach (Item child in item.children) {
                AddItemToDb(child);
                child.parent = item;
                if (child.decayRate == 0f) child.decayRate = item.decayRate;
            }
        }
    }
}

public class Job {
    public int id {get; set;} // set must be public for JSON deserialization
    public string name {get; set;}
    public string jobType {get; set;}
    public int nRecipes = 0;
    public static int maxRecipes = 100;
    public Recipe[] recipes = new Recipe[maxRecipes]; // max 100 recipes per job
}

public class Recipe {
    public int id {get; set;}
    public string job {get; set;}
    public string description {get; set;} // optional (maybe make the getter return something other than null?)
    public string tile {get; set;} // actually is a tile or a building...
    public float workload {get; set;}
    public string research   { get; set; }   // optional: research name to advance per cycle
    public float  skillPoints { get; set; }  // progress added to that research per cycle
    public TileType tileType;
    public ItemNameQuantity[] ninputs {get; set;}
    public ItemNameQuantity[] noutputs {get; set;}
    public ItemQuantity[] inputs;
    public ItemQuantity[] outputs;
    public InventoryController inventoryController;
    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        inputs = new ItemQuantity[ninputs.Length];
        outputs = new ItemQuantity[noutputs.Length];
        for (int i = 0; i < ninputs.Length; i++){
            inputs[i] = new ItemQuantity(ninputs[i].name, (int)Math.Round(ninputs[i].quantity * 100));
        }
        for (int i = 0; i < noutputs.Length; i++){
            outputs[i] = new ItemQuantity(noutputs[i].name, (int)Math.Round(noutputs[i].quantity * 100));
            outputs[i].chance = noutputs[i].chance;
        }
    }
    public float Score(){ // only takes into account global quantity / target. nothing about recipe ratios.
        if (inventoryController == null){inventoryController = InventoryController.instance;}
        if (inventoryController.targets == null){return 0;}
        float score = 1;
        foreach (ItemQuantity iq in inputs){
            score *= ((float)inventoryController.globalInventory.Quantity(iq.item.id) / 
                inventoryController.targets[iq.item.id]);
        }
        foreach (ItemQuantity iq in outputs){
            score /= ((float)inventoryController.globalInventory.Quantity(iq.item.id) / 
                inventoryController.targets[iq.item.id]);
        }
        return score;
    }
}


public class HaulInfo {
    public Item item;
    public int quantity;
    public Tile itemTile;
    public Tile storageTile;
    public ItemStack itemStack;
    
    public HaulInfo(Item item, int quantity, Tile itemTile, Tile storageTile, ItemStack itemStack) {
        this.item = item;
        this.quantity = quantity;
        this.itemTile = itemTile;
        this.storageTile = storageTile;
        this.itemStack = itemStack;
    }
}
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
    public static List<Item> clothingItems;
    public static Job[] jobs = new Job[100];
    public static Recipe[] recipes = new Recipe[500];
    public static StructType[] structTypes = new StructType[600];
    public static PlantType[] plantTypes = new PlantType[600];
    public static TileType[] tileTypes = new TileType[100];

    // Mouse name pools loaded from Resources/Misc/names.csv.
    public static List<string> chineseNames = new List<string>();
    public static List<string> inventedNames = new List<string>();

    // Sprite sorting orders: see SPEC-rendering.md § "Sorting orders" for the authoritative list.

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
        clothingItems = itemsFlat.Where(i => { Item cur = i; while (cur != null) { if (cur.name == "clothing") return true; cur = cur.parent; } return false; }).ToList();
        ValidateNoGroupOutputs();
        LoadItemIcons();
        LoadNames();
        Debug.Log("db loaded");
    }

    void LoadNames() {
        TextAsset csv = Resources.Load<TextAsset>("Misc/names");
        if (csv == null) { Debug.LogError("Db: names.csv not found at Resources/Misc/names"); return; }
        foreach (string line in csv.text.Split('\n')) {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("name")) continue; // skip header
            string[] parts = trimmed.Split(',');
            if (parts.Length != 2) continue;
            string n = parts[0].Trim();
            string pool = parts[1].Trim();
            if (pool == "chinese") chineseNames.Add(n);
            else if (pool == "invented") inventedNames.Add(n);
            else Debug.LogWarning($"Db: unknown pool '{pool}' for name '{n}'");
        }
    }

    // Draw a random mouse name from the combined pool.
    // Pools are kept separate so frequency weighting can be added later.
    public static string DrawName() {
        int total = chineseNames.Count + inventedNames.Count;
        if (total == 0) { Debug.LogError("Db: name pool is empty, falling back to 'mouse'"); return "mouse"; }
        int i = UnityEngine.Random.Range(0, total);
        return i < chineseNames.Count ? chineseNames[i] : inventedNames[i - chineseNames.Count];
    }

    void LoadItemIcons() {
        Sprite fallback = Resources.Load<Sprite>("Sprites/Items/split/default/icon");
        if (fallback == null) Debug.LogError("Db: missing default item icon at Sprites/Items/split/default/icon");
        foreach (Item item in itemsFlat) {
            string iName = item.name.Trim().Replace(" ", "");
            Sprite loaded = Resources.Load<Sprite>($"Sprites/Items/split/{iName}/icon");
            if (loaded != null) {
                item.icon = loaded;
            } else if (item.children == null) {
                // Leaf items always get at least the fallback icon.
                item.icon = fallback;
            }
            // Group items with no dedicated sprite stay null — ItemIcon resolves
            // them dynamically to the most-owned child leaf at display time.
        }
    }

    // Validates that no group/parent items appear as recipe outputs, plant products, or tile drops.
    // Group items (those with children) are only valid as recipe *inputs* (where they act as wildcards).
    void ValidateNoGroupOutputs(){
        foreach (Recipe recipe in recipes){
            if (recipe == null) continue;
            foreach (ItemQuantity iq in recipe.outputs)
                if (iq.item.children != null)
                    Debug.LogError($"Db validation: recipe '{recipe.description}' output '{iq.item.name}' is a group item. Only leaf items may be produced.");
        }
        foreach (PlantType pt in plantTypes){
            if (pt == null || pt.products == null) continue;
            foreach (ItemQuantity iq in pt.products)
                if (iq.item.children != null)
                    Debug.LogError($"Db validation: plant '{pt.name}' product '{iq.item.name}' is a group item. Only leaf items may be produced.");
        }
        foreach (TileType tt in tileTypes){
            if (tt == null || tt.products == null) continue;
            foreach (ItemQuantity iq in tt.products)
                if (iq.item.children != null)
                    Debug.LogError($"Db validation: tile '{tt.name}' product '{iq.item.name}' is a group item. Only leaf items may be produced.");
        }
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

        // Resolve skill weight string keys to Skill enum values for fast runtime lookup.
        foreach (Job job in jobsUnplaced) {
            if (job.skillWeights == null) continue;
            job.resolvedSkillWeights = new Dictionary<Skill, float>();
            foreach (var kv in job.skillWeights) {
                if (System.Enum.TryParse<Skill>(kv.Key, true, out Skill s))
                    job.resolvedSkillWeights[s] = kv.Value;
                else
                    Debug.LogWarning($"Job '{job.name}': unknown skill '{kv.Key}' in skillWeights");
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

        // Propagate each job's defaultSkill to any recipe that didn't specify its own skill.
        foreach (Recipe recipe in recipesUnplaced) {
            if (recipe.skill != null) continue;
            if (jobByName.TryGetValue(recipe.job, out Job j))
                recipe.skill = j.defaultSkill;
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
                if (!child.discrete) child.discrete = item.discrete;
                if (!child.isLiquid) child.isLiquid = item.isLiquid;
            }
        }
    }
}

public class Job {
    public int id {get; set;} // set must be public for JSON deserialization
    public string name {get; set;}
    public string jobType {get; set;}
    public string defaultSkill {get; set;} // optional; skill domain used for recipes of this job (e.g. "woodworking")
    public int nRecipes = 0;
    public static int maxRecipes = 100;
    public Recipe[] recipes = new Recipe[maxRecipes]; // max 100 recipes per job

    // Job-skill affinity weights for automatic job swapping (e.g. farmer → farming:0.8, construction:0.2).
    // Authored in JSON as string keys; resolved to Skill enum at load time.
    public Dictionary<string, float> skillWeights {get; set;}
    [JsonIgnore] public Dictionary<Skill, float> resolvedSkillWeights;
}

public class Recipe {
    public int id {get; set;}
    public string job {get; set;}
    public string description {get; set;} // optional (maybe make the getter return something other than null?)
    public string tile {get; set;} // actually is a tile or a building...
    public float workload {get; set;}
    public string research   { get; set; }   // optional: research name to advance per cycle
    public float  skillPoints { get; set; }  // progress added to that research per cycle
    public string skill      { get; set; }   // optional: overrides job.defaultSkill for this recipe (e.g. "mining")
    public TileType tileType;
    public ItemNameQuantity[] ninputs {get; set;}
    public ItemNameQuantity[] noutputs {get; set;}
    public ItemQuantity[] inputs;
    public ItemQuantity[] outputs;
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
    public float Score(Dictionary<int, int> targets){ // only takes into account global quantity / target. nothing about recipe ratios.
        if (targets == null) return 1;
        float score = 1;
        foreach (ItemQuantity iq in inputs){
            int target = targets[iq.item.id];
            if (target == 0) continue; // no target set — treat as neutral
            score *= ((float)GlobalInventory.instance.Quantity(iq.item) / target);
        }
        foreach (ItemQuantity iq in outputs){
            int target = targets[iq.item.id];
            if (target == 0) continue; // no target set — treat as neutral
            score /= ((float)GlobalInventory.instance.Quantity(iq.item) / target);
        }
        return score;
    }

    // Returns true if every tracked output is already at or above its target,
    // meaning this recipe should not be chosen (production is unneeded).
    // target=0 means "produce none" — any quantity ≥ 0 satisfies it.
    // Items missing from the targets dict are skipped as a safe fallback.
    public bool AllOutputsSatisfied(Dictionary<int, int> targets) {
        if (targets == null) return false;
        bool anyTracked = false;
        foreach (var iq in outputs) {
            if (!targets.TryGetValue(iq.item.id, out int target)) continue;
            anyTracked = true;
            if (GlobalInventory.instance.Quantity(iq.item) < target) return false;
        }
        return anyTracked; // only suppress if at least one output was tracked
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
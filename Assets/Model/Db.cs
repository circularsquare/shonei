using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// this loads before anything else. 

public class Db : MonoBehaviour { // should detach from game object (or make it a conrtrolelr?)
    public static Db instance {get; protected set;}
    // itemById or jobById is just the array items or jobs.
    public static Dictionary<string, int> iidByName {get; protected set;}
    public static Dictionary<string, Item> itemByName {get; protected set;}
    public static Dictionary<string, Job> jobByName {get; protected set;}

    // int maxJobs = 40;
    // int maxRecipes = 5000;
    public static Item[] items = new Item[5000];
    public static Job[] jobs = new Job[100];
    public static Recipe[] recipes = new Recipe[5000];


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
    } 

    void Awake(){ // this runs before Start() like in world
        readCsv();
        Debug.Log("db loaded");
    } 

    void readCsv(){
        // var path = Application.dataPath + "/Resources/itemsDb.csv"; 
        // List<int> iids = new List<int>();
        // List<Item> items = new List<Item>();
        // using(var reader = new StreamReader(path)){
        //     reader.ReadLine(); // column names
        //     while (!reader.EndOfStream){
        //         string line = reader.ReadLine();
        //         string[] values = line.Split(new char[] {',', ' '}, StringSplitOptions.RemoveEmptyEntries);
        //         iids.Add(Int32.Parse(values[0]));
        //         items.Add(new Item(Int32.Parse(values[0]), values[1]));
        //         // Debug.Log(values[0] + values[1]);
        //     }
        // }
        // itemById = iids.Zip(items, (k, v) => new { k, v })
        //       .ToDictionary(x => x.k, x => x.v);
        // iidByName = iids.Zip(items, (k, v) => new {k, v})
        //       .ToDictionary(x => x.v.iName, x => x.k); 

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
        

    }

    void Update(){  }
    public static Job getJobByName(string name){
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
    public List<Recipe> recipes = new List<Recipe>();
}

public class Recipe {
    public int id {get; set;}
    public string job {get; set;}
    public string name {get; set;} // optional (maybe make the getter return something other than null?)
    public ItemQuantity[] inputs { get; set; }
    public ItemQuantity[] outputs { get; set; }
}

public class ItemQuantity{
    public int id {get; set;}
    public int quantity {get; set;}
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using System.Runtime.Serialization;

public class Plant : Building { 
    public PlantType plantType;

    public float timer;
    public int age;
    public int growthStage;
    public int size;
    public int yield;

    public bool harvestable;


    public Plant(PlantType plantType, int x, int y) : base (plantType, x, y){ // call parent constructor
        this.plantType = plantType;

        PlantController.instance.AddPlant(this);
        go.name = "plant_" + plantType.name;

        sprite = Resources.Load<Sprite>("Sprites/Plants/" + plantType.name.Replace(" ", ""));
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Plants/default");}
        sr.sprite = sprite; 
        sr.sortingOrder = 60;
    }
    
    public void Grow(int t){
        age += t;
        // hardcoded 4 growth stages
        growthStage = Math.Min(1 + (age * 3 / plantType.growthTime), 4);
        if (growthStage >= 4){ // probably wanna make this start at 0 (and change the sprite file names too).
            harvestable = true;
        }
        UpdateSprite();
    }
    public void Mature(){
        Grow(plantType.growthTime);
    }
    public ItemQuantity[] Harvest(){
        harvestable = false;
        age = 0; // autoreplant
        growthStage = 0;
        return plantType.products;
    }

    public void UpdateSprite(){
        sprite = Resources.Load<Sprite>("Sprites/Plants/" + plantType.name + growthStage.ToString());
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Plants/" + plantType.name);} 
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Plants/default");}
        sr.sprite = sprite;
    }
}



public class PlantType : StructType {
    public ItemNameQuantity[] nproducts {get; set;}
    public ItemQuantity[] products;

    public int maxSize;
    public int maxYieldPerSize;
    public int harvestProgress;
    public int growthTime;
    public float harvestTime {get; set;}
    // public string njob {get; set;}
    // public Job job;

    [OnDeserialized]
    new internal void OnDeserialized(StreamingContext context){
        // costs = new ItemQuantity[ncosts.Length];
        // for (int i = 0; i < ncosts.Length; i++){
        //     costs[i] = new ItemQuantity(ncosts[i].name, ncosts[i].quantity);
        // }
        costs = ncosts.Select(iq => new ItemQuantity(iq.name, iq.quantity)).ToArray();
        products = nproducts.Select(iq => new ItemQuantity(iq.name, iq.quantity)).ToArray();
        if (njob != null){
            job = Db.jobByName[njob]; // this is duplicated in plant...
        }
        // handle null or 0 growthTime?
    }

}
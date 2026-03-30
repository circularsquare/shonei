using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using System.Runtime.Serialization;

public class Plant : Structure {
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
        go.transform.SetParent(PlantController.instance.transform, true);
        go.name = "plant_" + plantType.name;

        sprite = plantType.LoadSprite() ?? Resources.Load<Sprite>("Sprites/Plants/default");
        sr.sprite = sprite;
        sr.sortingOrder = 60;
    }

    public override void OnPlaced() {
        // Standing harvest order — isActive suppresses it between grow cycles.
        WorkOrderManager.instance?.RegisterHarvest(this);
    }

    public void Grow(int t){
        age += t;
        // hardcoded 4 growth stages
        growthStage = Math.Min(age * 3 / plantType.growthTime, 3);
        if (growthStage >= 3 && !harvestable){
            harvestable = true;
        }
        UpdateSprite();
    }
    public void Mature(){
        Grow(plantType.growthTime);
    }
    public ItemQuantity[] Harvest(){
        if (!harvestable) { Debug.LogError($"Harvest() called on {plantType.name} but harvestable=false"); return Array.Empty<ItemQuantity>(); }
        harvestable = false;
        age = 0; // autoreplant
        growthStage = 0;
        return plantType.products;
    }

    public override void Destroy() {
        PlantController.instance.Remove(this);
        base.Destroy();
    }

    public void UpdateSprite(){
        string n = plantType.name.Replace(" ", "");
        sprite = Resources.Load<Sprite>("Sprites/Plants/Split/" + n + "/g" + growthStage.ToString());
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Plants/Split/" + n + "/g0");}
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Plants/default");}
        sr.sprite = sprite;
    }
}



public class PlantType : StructType {
    public ItemNameQuantity[] nproducts {get; set;}
    public ItemQuantity[] products;

    public override Sprite LoadSprite() {
        string n = name.Replace(" ", "");
        Sprite s = Resources.Load<Sprite>("Sprites/Plants/Split/" + n + "/g0");
        return s != null && s.texture != null ? s : null;
    }

    public int maxSize;
    public int maxYieldPerSize;
    public int harvestProgress;
    public int growthTime;
    public float harvestTime {get; set;}
    // public string njob {get; set;}
    // public Job job;

    [OnDeserialized]
    new internal void OnDeserialized(StreamingContext context){
        costs = ncosts.Select(iq => new ItemQuantity(iq.name, (int)Math.Round(iq.quantity * 100))).ToArray();
        products = nproducts.Select(iq => new ItemQuantity(iq.name, (int)Math.Round(iq.quantity * 100))).ToArray();
        if (njob != null){
            job = Db.jobByName[njob]; 
        }
        // handle null or 0 growthTime?
    }

}
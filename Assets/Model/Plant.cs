using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using System.Runtime.Serialization;

public class Plant {
    public GameObject go;
    public int x;
    public int y;
    public PlantType plantType;
    public Sprite sprite;
    public Tile tile; 

    public float timer;
    public int growthStage;
    public int size;
    public int yield;

    // public int capacity = 1;
    // public int reserved = 0;

    public Plant(PlantType plantType, int x, int y){
        this.plantType = plantType;
        this.x = x;
        this.y = y;
        this.tile = World.instance.GetTileAt(x, y);

        go = new GameObject();
        go.transform.position = new Vector3(x, y, 0);
        go.transform.SetParent(WorldController.instance.transform, true);
        go.name = "Plant_" + plantType.name;
        
        sprite = Resources.Load<Sprite>("Sprites/Plants/" + plantType.name);
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Plants/default");}
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite; 

    }


}

public class PlantType {
    public int id {get; set;}
    public string name {get; set;}
    public ItemNameQuantity[] ncosts {get; set;}
    public ItemNameQuantity[] nproducts {get; set;}
    public ItemQuantity[] costs;
    public ItemQuantity[] products;
    

    public int maxSize;
    public int maxYieldPerSize;
    public int harvestProgress;

    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        // costs = new ItemQuantity[ncosts.Length];
        // for (int i = 0; i < ncosts.Length; i++){
        //     costs[i] = new ItemQuantity(ncosts[i].name, ncosts[i].quantity);
        // }
        costs = ncosts.Select(iq => new ItemQuantity(iq.name, iq.quantity)).ToArray();
        products = nproducts.Select(iq => new ItemQuantity(iq.name, iq.quantity)).ToArray();
    }

}
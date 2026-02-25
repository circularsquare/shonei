using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;
using System.Runtime.Serialization;


public class Structure {
    public GameObject go;
    public int x;
    public int y;
    public StructType structType;
    public Sprite sprite;
    public SpriteRenderer sr;
    public Tile tile; // not really sure how this will work for multi-tile buildings...
    public Reservable res;
    public string depth;
    
    public Structure(StructType st, int x, int y){
        this.structType = st;
        this.x = x;
        this.y = y;
        this.tile = World.instance.GetTileAt(x, y);

        go = new GameObject();
        go.transform.position = new Vector3(x, y, 0);
        go.transform.SetParent(WorldController.instance.transform, true);
        go.name = "structure_" + structType.name;
        
        sprite = Resources.Load<Sprite>("Sprites/Buildings/" + structType.name.Replace(" ", "")); // removes spaces in name
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Buildings/default");
        }
        sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        res = new Reservable(structType.capacity);
    }

    public void Destroy(){
        if (this is Plant plant) {
            PlantController.instance.Remove(plant);
        } 
        StructController.instance.Remove(this);
        if (depth == "b"){
            tile.building = null;
        } else if (depth == "f"){
            tile.fStruct = null;
        } else if (depth == "m"){
            tile.mStruct = null;
        }
        GameObject.Destroy(go);
    }

}


public class StructType {
    public int id {get; set;}
    public string name {get; set;}
    public int nx {get; set;}
    public int ny {get; set;}
    public ItemNameQuantity[] ncosts {get; set;}
    public ItemQuantity[] costs;
    public float constructionCost {get; set;}
    public bool isTile {get; set;}
    public bool isPlant; // is this used?
    public string depth {get; set;}
    public string njob {get; set;}
    public Job job;
    public int capacity {get; set;} // number of animals that can reserve this struct at once
    public string requiredTileName {get; set;} // tile that this struct must be built on

    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        if (capacity == 0){ capacity = 1; } // default, can be used by one animal at a time
        if (depth == null){ depth = "b"; }
        costs = new ItemQuantity[ncosts.Length];
        for (int i = 0; i < ncosts.Length; i++){
            costs[i] = new ItemQuantity(ncosts[i].name, ncosts[i].quantity);
        }
        if (njob != null){
            job = Db.jobByName[njob]; 
        } else {
            job = Db.jobByName["hauler"]; // default if no njob provided
        }
    }
}
 




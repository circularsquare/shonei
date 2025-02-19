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
    public int capacity = 1;
    public int reserved = 0;
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
        
        sprite = Resources.Load<Sprite>("Sprites/Buildings/" + structType.name);
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Buildings/default");
        }
        sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
    }

    public void Destroy(){
        GameObject.Destroy(go);
        if (depth == "b"){
            tile.building = null;
        } if (depth == "f"){
            tile.fStruct = null;
        } if (depth == "m"){
            tile.mStruct = null;
        }
    }

}


public class StructType {
    public int id {get; set;}
    public string name {get; set;}
    public int nx {get; set;}
    public int ny {get; set;}
    public ItemNameQuantity[] ncosts {get; set;}
    public ItemQuantity[] costs;
    public bool isTile {get; set;}
    public bool isPlant; // is this used?
    public string depth {get; set;}
    public string njob {get; set;}
    public Job job;
    public int capacity {get; set;} // animal capacity, like max # of workers or eepers

    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        if (capacity == 0){ capacity = 20; } // default, basically infinite capacity 
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
 




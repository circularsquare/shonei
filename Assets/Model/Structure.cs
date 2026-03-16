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
    public Tile workTile => World.instance.GetTileAt(x + structType.workTileX, y + structType.workTileY);
    public Reservable res;

    public Structure(StructType st, int x, int y){
        this.structType = st;
        this.x = x;
        this.y = y;

        go = new GameObject();
        float visualX = st.nx > 1 ? x + (st.nx - 1) / 2.0f : x;
        go.transform.position = st.depth == "r"
            ? new Vector3(visualX, y - 1, 0)
            : new Vector3(visualX, y, 0);
        go.transform.SetParent(WorldController.instance.transform, true);
        go.name = "structure_" + structType.name;
        
        sprite = structType.LoadSprite() ?? Resources.Load<Sprite>("Sprites/Buildings/default");
        sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        if (structType.depth == "r") sr.sortingOrder = 1; // above tile (order 0), below buildings
        res = new Reservable(structType.capacity);

        if (structType.name == "torch") {
            go.AddComponent<LightSource>();
        }
    }

    public void Destroy(){
        if (this is Plant plant) {
            PlantController.instance.Remove(plant);
        }
        StructController.instance.Remove(this);
        string depth = structType.depth;
        for (int i = 0; i < structType.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t == null) continue;
            if (depth == "b") t.building = null;
            else if (depth == "f") t.fStruct = null;
            else if (depth == "m") t.mStruct = null;
            else if (depth == "r") t.road = null;
        }
        GameObject.Destroy(go);
        World world = World.instance;
        int tileCount = structType.nx > 1 ? structType.nx : 1;
        for (int i = 0; i < tileCount; i++) {
            world.graph.UpdateNeighbors(x + i, y);
            world.graph.UpdateNeighbors(x + i, y + 1);
            world.FallIfUnstandable(x + i, y + 1);
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
    public float constructionCost {get; set;}
    public bool isTile {get; set;}
    public bool isPlant; 
    public string depth {get; set;} // 'b', 'm', 'f', 'r'
    public string njob {get; set;}
    public Job job;
    public int capacity {get; set;} // number of animals that can reserve this struct at once
    public string requiredTileName {get; set;} // tile that this struct must be built on
    public bool isStorage {get; set;} // true for storage buildings (drawers, crates, etc.)
    public int nStacks {get; set;} // number of item stacks in storage
    public int storageStackSize {get; set;} // max items per stack in storage
    public string category {get; set;} // build menu category: "structures", "plants", "production", "storage"
    public bool defaultLocked {get; set;} // true = locked; hidden from build menu until unlocked via research
    public int depleteAt {get; set;} // 0 = never depletes; >0 = deplete after this many uses
    public float pathCostReduction {get; set;} // subtracted from edge cost for horizontal moves (roads: 0.1)
    public bool solidTop {get; set;} // can animals stand on top of this structure?
    public int workTileX {get; set;} // tile offset to the interaction/nav tile (default 0,0 = anchor)
    public int workTileY {get; set;}
    public int storageTileX {get; set;} // tile offset to the storage inventory tile (default 0,0 = anchor)
    public int storageTileY {get; set;}

    public virtual Sprite LoadSprite() {
        if (isTile) {
            if (name == "empty") return null;
            Sprite s = Resources.Load<Sprite>("Sprites/Tiles/" + name.Replace(" ", ""));
            return s != null && s.texture != null ? s : null;
        }
        Sprite s2 = Resources.Load<Sprite>("Sprites/Buildings/" + name.Replace(" ", ""));
        return s2 != null && s2.texture != null ? s2 : null;
    }

    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        if (capacity == 0){ capacity = 1; } // default, can be used by one animal at a time
        if (depth == null){ depth = "b"; }
        if (nx == 0){ nx = 1; }
        if (ny == 0){ ny = 1; }
        if (storageStackSize > 0){ storageStackSize *= 100; } // convert liang → fen
        costs = new ItemQuantity[ncosts.Length];
        for (int i = 0; i < ncosts.Length; i++){
            costs[i] = new ItemQuantity(ncosts[i].name, (int)Math.Round(ncosts[i].quantity * 100));
        }
        if (njob != null){
            job = Db.jobByName[njob]; 
        } else {
            job = Db.jobByName["hauler"]; // default if no njob provided
        }
    }
}
 




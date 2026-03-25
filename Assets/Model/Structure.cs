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
    public Tile tile => World.instance.GetTileAt(x, y);
    public Tile workTile => World.instance.GetTileAt(x + structType.workTileX, y + structType.workTileY);
    public Reservable res;

    // Returns false to suppress the WOM craft order for this building without removing it.
    // Override in subclasses to add runtime conditions (e.g. pump needs water below).
    public virtual bool IsActive() => true;

    public Structure(StructType st, int x, int y){
        this.structType = st;
        this.x = x;
        this.y = y;

        go = new GameObject();
        float visualX = st.nx > 1 ? x + (st.nx - 1) / 2.0f : x;
        go.transform.position = st.depth == 3
            ? new Vector3(visualX, y - 1f/8f, 0)
            : new Vector3(visualX, y, 0);
        go.transform.SetParent(StructController.instance.transform, true);
        go.name = "structure_" + structType.name;
        
        Sprite loadedSprite = structType.LoadSprite();
        sprite = loadedSprite ?? Resources.Load<Sprite>("Sprites/Buildings/default");
        sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        if (structType.depth == 3) sr.sortingOrder = 1; // above tile (order 0), below buildings
        if (loadedSprite == null)
            go.transform.localScale = new Vector3(structType.nx, Mathf.Max(1, structType.ny), 1f);
        // Workstations don't use Structure.res — their WOM Craft order owns the reservation.
        res = (structType.capacity > 0 && !structType.isWorkstation) ? new Reservable(structType.capacity) : null;

        if (structType.name == "clock") {
            go.AddComponent<ClockHand>();
        }
    }

    public virtual void Destroy(){
        if (this is Plant plant) {
            PlantController.instance.Remove(plant);
        }
        StructController.instance.Remove(this);
        int depth = structType.depth;
        for (int i = 0; i < structType.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t == null) continue;
            t.structs[depth] = null;
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


/// <summary>
/// Per-tile constraint checked by StructPlacement.CanPlaceHere before allowing placement.
/// dx/dy offsets are relative to the placement anchor tile.
/// </summary>
public class TileRequirement {
    public int dx {get; set;}
    public int dy {get; set;}
    public bool mustBeStandable {get; set;}
    public bool mustHaveWater {get; set;}    // tile.water > 0
    public bool mustBeEmpty {get; set;}      // structs[0] (building layer) must be null
    public string requiredTileName {get; set;}
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
    public int depth {get; set;} // 0=building, 1=platform, 2=foreground, 3=road
    public string njob {get; set;}
    public Job job;
    public int capacity {get; set;} // number of animals that can reserve this struct at once
    public string requiredTileName {get; set;} // tile that this struct must be built on
    public bool isStorage {get; set;} // true for storage buildings (drawers, crates, etc.)
    public bool liquidStorage {get; set;} // true = creates InvType.Liquid instead of InvType.Storage (use on tank-type buildings)
    public int nStacks {get; set;} // number of item stacks in storage
    public int storageStackSize {get; set;} // max items per stack in storage
    public string category {get; set;} // build menu category: "structures", "plants", "production", "storage"
    public bool defaultLocked {get; set;} // true = locked; hidden from build menu until unlocked via research
    public int depleteAt {get; set;} // 0 = never depletes; >0 = deplete after this many uses
    public float pathCostReduction {get; set;} // subtracted from edge cost for horizontal moves (roads: 0.1)
    public bool solidTop {get; set;} // can animals stand on top of this structure?
    public bool isWorkstation {get; set;} // true = registers a WOM Craft order when placed; use IsActive() to gate it
    public int workTileX {get; set;} // tile offset to the interaction/nav tile (default 0,0 = anchor)
    public int workTileY {get; set;}
    public int storageTileX {get; set;} // tile offset to the storage inventory tile (default 0,0 = anchor)
    public int storageTileY {get; set;}
    public TileRequirement[] tileRequirements {get; set;} // extra per-tile constraints checked at placement
    // isBuilding: true = use Building class regardless of depth (default false = depth 0 uses Building; others don't).
    // Allows foreground/other-depth structures to have full Building features (fuelInv, uses, storage, etc.).
    public bool isBuilding {get; set;}
    // Fuel inventory: internal resource consumed over time (torch burns wood; furnace burns coal, etc.).
    // hasFuelInv = true → Building creates a fuelInv and WOM registers a standing supply order on placement.
    public bool hasFuelInv {get; set;}
    public string fuelItemName {get; set;} // group or leaf item name (e.g. "wood"); resolved to fuelItem on load
    public Item fuelItem;                  // resolved from fuelItemName in OnDeserialized
    public int fuelCapacity {get; set;}    // max stack size in fen (JSON in liang, converted in OnDeserialized)
    public int fuelTarget {get; set;}      // WOM keeps fuelInv.Quantity >= this (JSON in liang, converted in OnDeserialized)
    public float fuelBurnRate {get; set;}  // liang/day consumed; LightSource converts to fen/s at runtime

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
        // Fuel inventory: convert liang → fen; resolve fuel item reference.
        if (hasFuelInv) {
            if (fuelCapacity > 0) fuelCapacity = (int)Math.Round(fuelCapacity * 100f);
            if (fuelTarget  > 0) fuelTarget  = (int)Math.Round(fuelTarget  * 100f);
            if (fuelItemName != null && Db.itemByName.ContainsKey(fuelItemName))
                fuelItem = Db.itemByName[fuelItemName];
            else
                Debug.LogError($"StructType '{name}': hasFuelInv=true but fuelItemName '{fuelItemName}' not found in Db");
        }
    }
}
 




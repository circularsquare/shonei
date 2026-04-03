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
    public Tile workTile => World.instance.GetTileAt(
        x + (mirrored ? (structType.nx - 1 - structType.workTileX) : structType.workTileX),
        y + structType.workTileY);
    public Reservable res;
    // Per-seat reservables for leisure buildings. Each work tile gets its own Reservable(1)
    // so two mice won't path to the same seat. Null for non-leisure buildings.
    public Reservable[] seatRes;

    // Returns true if any seat reservable is available. Only meaningful for leisure buildings.
    public bool AnySeatAvailable() {
        if (seatRes == null) return false;
        for (int i = 0; i < seatRes.Length; i++)
            if (seatRes[i].Available()) return true;
        return false;
    }

    // Returns the tile for a specific work tile index (from structType.nworkTiles), with mirroring applied.
    public Tile WorkTileAt(int index) {
        var wt = structType.nworkTiles[index];
        return World.instance.GetTileAt(
            x + (mirrored ? (structType.nx - 1 - wt.dx) : wt.dx),
            y + wt.dy);
    }

    // Local pixel offsets (bottom-left origin, unmirrored) of WaterMarkerColor pixels in this
    // sprite. Null when none found. Registered with WaterController by StructController.Place().
    public List<Vector2Int> waterPixelOffsets { get; private set; }

    // Returns false to suppress the WOM craft order for this building without removing it.
    // Override in subclasses to add runtime conditions (e.g. pump needs water below).
    public virtual bool IsActive() => true;

    // Called by StructController after Place(). Override to register WOM orders or other post-placement setup.
    // Not called during load or world generation — Reconcile() handles order registration on both paths.
    public virtual void OnPlaced() { }

    // Whether this structure is horizontally mirrored (flipped left-right).
    // Affects sprite flipX, workTile/storageTile offsets, tileRequirement offsets, and stair pathfinding.
    public bool mirrored = false;

    public Structure(StructType st, int x, int y, bool mirrored = false){
        this.structType = st;
        this.x = x;
        this.y = y;
        this.mirrored = mirrored;

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
        sr.flipX = mirrored;
        if (loadedSprite == null) {
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.size = new Vector2(structType.nx, Mathf.Max(1, structType.ny));
        }
        // Workstations don't use Structure.res — their WOM Craft order owns the reservation.
        // Leisure buildings use per-seat seatRes[] instead of a single res.
        if (structType.isLeisure && structType.capacity > 0) {
            res = null;
            seatRes = new Reservable[structType.nworkTiles.Length];
            for (int i = 0; i < seatRes.Length; i++)
                seatRes[i] = new Reservable(1);
        } else {
            res = (structType.capacity > 0 && !structType.isWorkstation) ? new Reservable(structType.capacity) : null;
        }

        if (structType.name == "clock") {
            go.AddComponent<ClockHand>();
        }

        // Register on tiles at the appropriate depth layer.
        int depth = st.depth;
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.structs[depth] != null)
                Debug.LogError($"Already a depth-{depth} structure at {x+i},{y}!");
            t.structs[depth] = this;
        }
        // Sort order by depth: 0=building(10), 1=platform(11), 2=foreground(40), 3=road(1).
        if (depth == 0) sr.sortingOrder = 10;
        else if (depth == 1) sr.sortingOrder = 11;
        else if (depth == 2) sr.sortingOrder = 40;
        else if (depth == 3) sr.sortingOrder = 1;

        // Scan sprite for water-marker pixels. Registration happens in StructController.Place().
        waterPixelOffsets = WaterController.ScanWaterPixels(sprite);
    }

    // Shared factory: dispatches to the correct subclass based on StructType properties.
    // Used by both StructController.Construct (gameplay) and SaveSystem.RestoreStructure (load).
    // When adding a new Structure subclass, add its case here — no other dispatch site needed.
    public static Structure Create(StructType st, int x, int y, bool mirrored = false) {
        if (st.isPlant)
            return new Plant(st as PlantType, x, y);
        if (st.depth == 0 || st.isBuilding) {
            return st.name == "pump"
                ? new PumpBuilding(st, x, y, mirrored)
                : new Building(st, x, y, mirrored);
        }
        return new Structure(st, x, y, mirrored); // platforms, ladders, stairs, foreground, roads
    }

    public virtual void Destroy(){
        if (waterPixelOffsets != null)
            WaterController.instance?.UnregisterDecorativeWater(this);
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
// Work tile offset: a position within a multi-tile building where an animal can stand to interact.
// Used by nworkTiles[] on StructType. Mirroring is applied at runtime by Structure.WorkTileAt().
public class WorkTileOffset {
    public int dx {get; set;}
    public int dy {get; set;}
}

public class TileRequirement {
    public int dx {get; set;}
    public int dy {get; set;}
    public bool mustBeStandable {get; set;}
    public bool mustHaveWater {get; set;}    // tile.water > 0
    public bool mustBeEmpty {get; set;}      // structs[0] (building layer) must be null
    public bool mustBeSolidTile {get; set;}  // tile.type.solid must be true (ground tiles only, not solidTop buildings)
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
    public bool liquidStorage {get; set;} // true = sets Inventory.isLiquidStorage, enforcing liquid-only constraint (use on tank-type buildings)
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
    public WorkTileOffset[] nworkTiles {get; set;} // multiple work positions (e.g. fireplace seats); populated from legacy workTileX/Y if absent
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
    public int fuelCapacity {get; set;}    // max stack size in fen (JSON in liang, converted in OnDeserialized); supply triggers below half capacity
    public float fuelBurnRate {get; set;}  // liang/day consumed; LightSource converts to fen/s at runtime

    // Decoration: nearby animals gain a happiness point when within decorRadius (Chebyshev) of this building.
    // A decoration with hasFuelInv=true only counts when its reservoir has fuel (e.g. fountain needs water).
    // decorationNeed identifies which happiness satisfaction this decoration targets (e.g. "fountain").
    public bool isDecoration {get; set;}
    public int decorRadius {get; set;}     // Chebyshev radius; 0 means not a decoration
    public string decorationNeed {get; set;}

    // Leisure: mice actively visit this building during leisure time (fireplace, tea house, etc.).
    // A leisure building with hasFuelInv=true only attracts mice when its reservoir has fuel.
    // leisureNeed identifies which happiness satisfaction this building targets (e.g. "fireplace").
    public bool isLeisure {get; set;}
    public string leisureNeed {get; set;}
    public bool socialWhenShared {get; set;} // true = grant half social happiness to both mice when one finishes and another is still seated
    // When activeStartHour >= 0, this building is only active during [activeStartHour, activeEndHour).
    // Hours 0–24; end < start wraps midnight (e.g. 16→6 = 4pm–6am). -1 = always active.
    public float activeStartHour {get; set;} = -1f;
    public float activeEndHour   {get; set;}

    // Light source: building emits point light and passively burns fuel while torchFactor > 0.
    // lightIntensity is the baseIntensity passed to LightSource (default 0.80).
    public bool isLightSource {get; set;}
    public float lightIntensity {get; set;}

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
        if (isLightSource && lightIntensity == 0f) lightIntensity = 0.80f;
        if (nworkTiles == null || nworkTiles.Length == 0)
            nworkTiles = new[] { new WorkTileOffset { dx = workTileX, dy = workTileY } };
        // Fuel inventory: convert liang → fen; resolve fuel item reference.
        if (hasFuelInv) {
            if (fuelCapacity > 0) fuelCapacity = (int)Math.Round(fuelCapacity * 100f);
            if (fuelItemName != null && Db.itemByName.ContainsKey(fuelItemName))
                fuelItem = Db.itemByName[fuelItemName];
            else
                Debug.LogError($"StructType '{name}': hasFuelInv=true but fuelItemName '{fuelItemName}' not found in Db");
        }
    }
}
 




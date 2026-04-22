using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;
using System.Runtime.Serialization;

// One cell of the world grid. Owns its tile type, depth-indexed structure/blueprint
// slots, optional floor inventory, water level, and pathfinding Node. Fires callbacks
// on change so controllers can re-render without polling. Static grid coordinates
// (x, y) are immutable after construction.
public class Tile {
    Action<Tile> cbTileTypeChanged;
    Action<Tile> cbBackgroundChanged;

    World world;
    public int x, y;
    public GameObject go;
    private TileType _type;
    public TileType type{
        get{return _type;}
        set{
            _type = value;
            if (cbTileTypeChanged != null){
                cbTileTypeChanged(this);
            }
        }
    }
    private bool _hasBackground;
    public bool hasBackground {
        get { return _hasBackground; }
        set {
            if (_hasBackground == value) return;
            _hasBackground = value;
            cbBackgroundChanged?.Invoke(this);
        }
    }
    public Structure[] structs = new Structure[4];   // indexed by depth: 0=building, 1=platform, 2=foreground, 3=road
    public Blueprint[] blueprints = new Blueprint[4]; // indexed by depth
    public Building building => structs[0] as Building; // alias for depth-0 Building (does NOT match Plant)
    public Plant plant => structs[0] as Plant;           // alias for depth-0 Plant
    public Inventory inv; // this encapsulates all inventory types
    public ushort water; // 0–160 internal fixed-point (10 units = 1 display unit); 160 = fully filled tile
    public byte moisture; // 0–100 soil wetness percent. Only meaningful on SOLID tiles (dirt/stone) — air tiles stay 0. Plants above read moisture from the soil tile directly below them.
    public Node node;

    
    public Tile(World world, int x, int y){
        this.world = world;
        this.x = x;
        this.y = y;
        type = Db.tileTypes[0];
        node = new Node(this, x, y);
    }
    
    public void RegisterCbTileTypeChanged(Action<Tile> callback){cbTileTypeChanged += callback;}
    public void UnregisterCbTileTypeChanged(Action<Tile> callback){cbTileTypeChanged -= callback;}
    public void RegisterCbBackgroundChanged(Action<Tile> callback){cbBackgroundChanged += callback;}
    public void UnregisterCbBackgroundChanged(Action<Tile> callback){cbBackgroundChanged -= callback;}
    public bool ContainsAvailableItem(Item item){return inv != null && inv.ContainsAvailableItem(item);}
    public ItemStack GetItemToHaul(){
        if (inv == null){return null;}
        else{return inv.GetItemToHaul();}
    }
    public bool HasItemToHaul(Item item){return inv != null && inv.HasItemToHaul(item);} // can be null for any item

    // space: floor allowed
    public bool HasSpaceForItem(Item item){return (inv == null || inv.HasSpaceForItem(item));}
    public bool HasLadder(){ return structs[2]?.structType.name == "ladder"; }
    public bool HasStairRight(){ return structs[2]?.structType.name == "stairs" && !structs[2].mirrored; }
    public bool HasStairLeft(){ return structs[2]?.structType.name == "stairs" && structs[2].mirrored; }

    public Blueprint GetAnyBlueprint(){
        foreach (var bp in blueprints) if (bp != null) return bp;
        return null;
    }
    public Blueprint GetMatchingBlueprint(Func<Blueprint, bool> predicate){
        foreach (var bp in blueprints) if (bp != null && predicate(bp)) return bp;
        return null;
    }
    public Blueprint GetBlueprintAt(int depth) => blueprints[depth];
    public void SetBlueprintAt(int depth, Blueprint bp) => blueprints[depth] = bp;

    public Tile[] GetAdjacents(){ // not the same as graph neighbors
        Tile[] adjacents = new Tile[8];
        adjacents[0] = world.GetTileAt(x + 1, y);
        adjacents[1] = world.GetTileAt(x, y - 1);
        adjacents[2] = world.GetTileAt(x - 1, y);
        adjacents[3] = world.GetTileAt(x, y + 1);
        adjacents[4] = world.GetTileAt(x + 1, y - 1);
        adjacents[5] = world.GetTileAt(x - 1, y - 1);
        adjacents[6] = world.GetTileAt(x - 1, y + 1);
        adjacents[7] = world.GetTileAt(x + 1, y + 1);
        return adjacents;
    }

    override public string ToString(){
        return ("tile " + x.ToString() + "," + y.ToString());
    }

    public Inventory EnsureFloorInventory() {
        if (inv == null) { inv = new Inventory(n: 1, x: x, y: y); }
        return inv;
    }
}


public class TileType {
    public int id {get; set;}
    public string name {get; set;}
    public bool solid {get; set;}
    public ItemNameQuantity[] nproducts {get; set;}
    public ItemQuantity[] products;


    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        if (nproducts != null){
            products = new ItemQuantity[nproducts.Length];
            for (int i = 0; i < nproducts.Length; i++){
                products[i] = new ItemQuantity(nproducts[i].name, ItemStack.LiangToFen(nproducts[i].quantity));
            }
        }
    }
}
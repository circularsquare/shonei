using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;
using System.Runtime.Serialization;

public class Tile {
    Action<Tile> cbTileTypeChanged;

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
    public Structure[] structs = new Structure[4];   // indexed by depth: 0=building, 1=platform, 2=foreground, 3=road
    public Blueprint[] blueprints = new Blueprint[4]; // indexed by depth
    public Building building => structs[0] as Building; // alias for depth 0 (Plant extends Building, so this covers both)
    public Inventory inv; // this encapsulates all inventory types
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
    public bool ContainsAvailableItem(Item item){return inv != null && inv.ContainsAvailableItem(item);}
    public ItemStack GetItemToHaul(){
        if (inv == null){return null;}
        else{return inv.GetItemToHaul();}
    }
    public bool HasItemToHaul(Item item){return inv != null && inv.HasItemToHaul(item);} // can be null for any item
    public int GetStorageForItem(Item item){
        if (inv != null && inv.invType == Inventory.InvType.Storage){
            return inv.GetStorageForItem(item);
        }
        return 0;
    }
    // storage: floor not allowed
    public bool HasStorageForItem(Item item){return (inv != null && inv.GetStorageForItem(item) > 0); }

    // space: floor allowed
    public bool HasSpaceForItem(Item item){return (inv == null || inv.HasSpaceForItem(item));}
    public bool HasLadder(){ return (structs[2] != null && structs[2] is Ladder); }
    public bool HasStairRight(){ return (structs[2] != null && structs[2] is Stairs && (structs[2] as Stairs).right); }
    public bool HasStairLeft(){ return (structs[2] != null && structs[2] is Stairs && !(structs[2] as Stairs).right); }

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
                products[i] = new ItemQuantity(nproducts[i].name, (int)Math.Round(nproducts[i].quantity * 100));
            }
        }
    }
}
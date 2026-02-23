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
    public TileType type
    {
        get{return _type;}
        set{
            _type = value;
            if (cbTileTypeChanged != null){
                cbTileTypeChanged(this);
            }
        }
    }
    public Building building; // can be a plant... should probably rename at some point
    public Blueprint bBlueprint; // background blueprint
    public Blueprint mBlueprint; // midground blueprint
    public Blueprint fBlueprint; // foreground blueprint
    public Structure mStruct; // midground: platforms for moving horizontally in front of buildings
    public Structure fStruct; // foreground: ladders and stairs and stuff
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
    public bool ContainsItem(Item item){return inv != null && inv.ContainsItem(item);}
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
    public bool HasStorageForItem(Item item){return (inv != null && inv.HasStorageForItem(item)); }

    // space: floor allowed
    public bool HasSpaceForItem(Item item){return (inv == null || inv.HasSpaceForItem(item));}
    public bool HasLadder(){ return (fStruct != null && fStruct is Ladder); }
    public bool HasStairRight(){ return (fStruct != null && fStruct is Stairs && (fStruct as Stairs).right); }
    public bool HasStairLeft(){ return (fStruct != null && fStruct is Stairs && !(fStruct as Stairs).right); }

    public Blueprint GetAnyBlueprint(){
        return bBlueprint ?? mBlueprint ?? fBlueprint;
    }
    public Blueprint GetMatchingBlueprint(Func<Blueprint, bool> predicate){
        if (bBlueprint != null && predicate(bBlueprint)) return bBlueprint;
        if (mBlueprint != null && predicate(mBlueprint)) return mBlueprint;
        if (fBlueprint != null && predicate(fBlueprint)) return fBlueprint;
        return null;
    }
    public Blueprint GetBlueprintAt(string depth){
        if (depth == "b") return bBlueprint;
        if (depth == "m") return mBlueprint;
        if (depth == "f") return fBlueprint;
        return null;
    }
    public void SetBlueprintAt(string depth, Blueprint bp){
        if (depth == "b") bBlueprint = bp;
        else if (depth == "m") mBlueprint = bp;
        else if (depth == "f") fBlueprint = bp;
    }

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
                products[i] = new ItemQuantity(nproducts[i].name, nproducts[i].quantity);
            }
        }
    }



}
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;
using System.Runtime.Serialization;

public class Tile 
{
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
    public Building building; // in future, have like "front level" "back level" building slots? like wall level?
    public Blueprint blueprint; // not sure how this would interact with levels of building
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
    public bool ContainsItem(Item item){return (inv != null && inv.ContainsItem(item));}
    public Item GetItemToHaul(){
        if (inv == null){return null;}
        else{return inv.GetItemToHaul();}
    }
    public bool HasItemToHaul(Item item){return (inv != null && inv.HasItemToHaul(item));} // can be null for any item
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
}


public class TileType {
    public int id {get; set;}
    public string name {get; set;}
    public bool solid {get; set;}
}
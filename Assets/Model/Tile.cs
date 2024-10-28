using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;

public class Tile 
{
    Action<Tile> cbTileTypeChanged;

    World world;
    public int x;
    public int y;
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
    public Inventory inv; // this encapsulates all inventory types
    public int capacity = 1;    // unused rn
    public int reserved = 0;    // unused maybe?

    
    public Tile(World world, int x, int y){
        this.world = world;
        this.x = x;
        this.y = y;
        type = Db.tileTypes[0];
    }
    
    public void RegisterCbTileTypeChanged(Action<Tile> callback){cbTileTypeChanged += callback;}
    public void UnregisterCbTileTypeChanged(Action<Tile> callback){cbTileTypeChanged -= callback;}
    public bool ContainsItem(Item item){return (inv != null && inv.ContainsItem(item));}
    public Item GetItemToHaul(){
        if (inv == null){return null;}
        else{return inv.GetItemToHaul();}
    }
    public bool HasItemToHaul(Item item){return (inv != null && inv.HasItemToHaul(item));}
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


}

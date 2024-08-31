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
    public Inventory inv;
    public bool reserved;

    
    public Tile(World world, int x, int y){
        this.world = world;
        this.x = x;
        this.y = y;
        type = Db.tileTypes[0];
    }
    
    public void RegisterCbTileTypeChanged(Action<Tile> callback){
        cbTileTypeChanged += callback;
    }
    public void UnregisterCbTileTypeChanged(Action<Tile> callback){
        cbTileTypeChanged -= callback;
    }
    public bool ContainsItem(Item item){
        return (inv != null && inv.ContainsItem(item));
    }
    public bool ContainsFloorItem(Item item = null){
        return ContainsItem(item) && inv.invType == Inventory.InvType.Floor;
    }
    public bool HasStorageForItem(Item item){
        return (inv != null && inv.invType == Inventory.InvType.Storage && 
            inv.HasSpaceForItem(item));
    }
    // should change below so that not all tiles have floor room. 
    public bool HasSpaceForItem(Item item){ 
        return (inv == null || inv.HasSpaceForItem(item));
    }


}

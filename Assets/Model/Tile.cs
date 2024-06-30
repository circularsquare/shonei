using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;

public class Tile 
{
    Action<Tile> cbTileTypeChanged;

    public enum TileType {Empty, Soil, Stone, Built, Tree, Structure};
    public Dictionary<TileType, bool> SolidDict = new Dictionary<TileType, bool>(){
                                {TileType.Empty, false},
                                {TileType.Tree, false},
                                {TileType.Soil, true},
                                {TileType.Stone, true},
                                {TileType.Structure, true}}; 

    TileType type = TileType.Empty;
    public TileType Type {
        get{return type;}
        set{
            type = value;
            if (cbTileTypeChanged != null){
                cbTileTypeChanged(this);
            }
        }
    }

    World world;
    public int x;
    public int y;
    public bool standable;
    
    public Tile(World world, int x, int y){
        this.world = world;
        this.x = x;
        this.y = y;
    }
    public bool Solid(){
        return SolidDict[type];
    }
    
    public void RegisterCbTileTypeChanged(Action<Tile> callback){
        cbTileTypeChanged += callback;
    }
    public void UnregisterCbTileTypeChanged(Action<Tile> callback){
        cbTileTypeChanged -= callback;
    }

}

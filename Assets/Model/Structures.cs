using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Building : Structure {
    public BuildingType buildingType;
    public Building(StructType st, int x, int y) : base(st, x, y){
        if (tile.building != null){Debug.LogError("already a building here!");}
        tile.building = this;
        go.name = "building_" + structType.name;
        sr.sortingOrder = 10;
    
        if (structType.name == "drawer"){
            tile.inv = new Inventory(4, 20, Inventory.InvType.Storage, x, y); 
            // TODO: don't overwrite existing floor inventory!!
        }

        capacity = structType.capacity;
        buildingType = structType as BuildingType;
    }
}
public class BuildingType : StructType {

}

public class Platform : Structure {
    public Platform(StructType st, int x, int y) : base(st, x, y){
        if (tile.mStruct != null){Debug.LogError("already a mid structure here!");}
        tile.mStruct = this; 
        sr.sortingOrder = 11;
    }
}
public class Ladder: Structure {
    public Ladder(StructType st, int x, int y) : base(st, x, y){
        if (tile.fStruct != null){Debug.LogError("already a foreground structure here!");}
        tile.fStruct = this; 
        sr.sortingOrder = 80;
    }
}
public class Stairs: Structure {
    public bool right = true;
    public Stairs(StructType st, int x, int y) : base(st, x, y){
        if (tile.fStruct != null){Debug.LogError("already a foreground structure here!");}
        tile.fStruct = this;
        sr.sortingOrder = 80;
        if (right){
            sprite = Resources.Load<Sprite>("Sprites/Buildings/stairRight");
        } else { 
            sprite = Resources.Load<Sprite>("Sprites/Buildings/stairLeft");
        }
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Buildings/default");}
        sr.sprite = sprite;
    }   

}

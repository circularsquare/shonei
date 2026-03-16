using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Building : Structure {
    public int uses = 0;
    public Tile storageTile => World.instance.GetTileAt(x + structType.storageTileX, y + structType.storageTileY);
    public Building(StructType st, int x, int y) : base(st, x, y){
        // Register building on all occupied tiles
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.building != null) Debug.LogError("already a building at " + (x+i) + "," + y + "!");
            t.building = this;
        }

        go.name = "building_" + structType.name;
        sr.sortingOrder = 10;

        if (structType.isStorage){
            Tile storageTile = World.instance.GetTileAt(x + st.storageTileX, y + st.storageTileY);
            Inventory oldInv = storageTile.inv;
            var invType = structType.name == "market" ? Inventory.InvType.Market : Inventory.InvType.Storage;
            storageTile.inv = new Inventory(structType.nStacks, structType.storageStackSize, invType, storageTile.x, storageTile.y);
            storageTile.inv.displayName = structType.name;
            if (oldInv != null && oldInv.invType == Inventory.InvType.Floor) {
                foreach (Item item in oldInv.GetItemsList()) {
                    oldInv.MoveItemTo(storageTile.inv, item, oldInv.Quantity(item));
                }
                oldInv.Destroy();
            }
        }
    }
}

public class Platform : Structure {
    public Platform(StructType st, int x, int y) : base(st, x, y){
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.mStruct != null) Debug.LogError("already a mid structure at " + (x+i) + "," + y + "!");
            t.mStruct = this;
        }
        sr.sortingOrder = 11;
    }
}
public class Ladder: Structure {
    public Ladder(StructType st, int x, int y) : base(st, x, y){
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.fStruct != null) Debug.LogError("already a foreground structure at " + (x+i) + "," + y + "!");
            t.fStruct = this;
        }
        sr.sortingOrder = 80;
    }
}
public class Stairs: Structure {
    public bool right = true;
    public Stairs(StructType st, int x, int y) : base(st, x, y){
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.fStruct != null) Debug.LogError("already a foreground structure at " + (x+i) + "," + y + "!");
            t.fStruct = this;
        }
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

public class Road : Structure {
    public Road(StructType st, int x, int y) : base(st, x, y){
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.road != null) Debug.LogError("already a road at " + (x+i) + "," + y + "!");
            t.road = this;
        }
    }
}

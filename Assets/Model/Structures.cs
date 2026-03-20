using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Building : Structure {
    public int uses = 0;
    public Tile storageTile => World.instance.GetTileAt(x + structType.storageTileX, y + structType.storageTileY);
    public Inventory storage { get; private set; }
    public Building(StructType st, int x, int y) : base(st, x, y){
        // Register building on all occupied tiles
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.structs[0] != null) Debug.LogError("already a building at " + (x+i) + "," + y + "!");
            t.structs[0] = this;
        }

        go.name = "building_" + structType.name;
        sr.sortingOrder = 10;

        if (structType.isStorage){
            Tile storageTile = World.instance.GetTileAt(x + st.storageTileX, y + st.storageTileY);
            Inventory oldInv = storageTile.inv;
            var invType = structType.name == "market"  ? Inventory.InvType.Market
                        : structType.liquidStorage      ? Inventory.InvType.Liquid
                        :                                 Inventory.InvType.Storage;
            storageTile.inv = new Inventory(structType.nStacks, structType.storageStackSize, invType, storageTile.x, storageTile.y);
            storageTile.inv.displayName = structType.name;
            storage = storageTile.inv;
            if (oldInv != null && oldInv.invType == Inventory.InvType.Floor) {
                foreach (Item item in oldInv.GetItemsList()) {
                    oldInv.ForceMoveItemTo(storageTile.inv, item, oldInv.Quantity(item));
                }
                oldInv.Destroy();
            }
        }
    }
    public override void Destroy() {
        if (structType.isWorkstation)
            WorkOrderManager.instance?.RemoveWorkstationOrders(this);
        if (structType.isStorage && storage != null) {
            Tile st = storageTile;
            if (!storage.IsEmpty() && !WorldController.isClearing)
                Debug.LogError($"Destroying building storage with items in it at ({x},{y})!");
            storage.Destroy();
            if (st != null) st.inv = null;
        }
        base.Destroy();
    }
}

public class Platform : Structure {
    public Platform(StructType st, int x, int y) : base(st, x, y){
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.structs[1] != null) Debug.LogError("already a mid structure at " + (x+i) + "," + y + "!");
            t.structs[1] = this;
        }
        sr.sortingOrder = 11;
    }
}
public class Ladder: Structure {
    public Ladder(StructType st, int x, int y) : base(st, x, y){
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.structs[2] != null) Debug.LogError("already a foreground structure at " + (x+i) + "," + y + "!");
            t.structs[2] = this;
        }
        sr.sortingOrder = 80;
    }
}
public class Stairs: Structure {
    public bool right = true;
    public Stairs(StructType st, int x, int y) : base(st, x, y){
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.structs[2] != null) Debug.LogError("already a foreground structure at " + (x+i) + "," + y + "!");
            t.structs[2] = this;
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

public class ForegroundStructure : Structure {
    public ForegroundStructure(StructType st, int x, int y) : base(st, x, y){
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.structs[2] != null) Debug.LogError("already a foreground structure at " + (x+i) + "," + y + "!");
            t.structs[2] = this;
        }
        sr.sortingOrder = 80;
    }
}

public class Road : Structure {
    public Road(StructType st, int x, int y) : base(st, x, y){
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.structs[3] != null) Debug.LogError("already a road at " + (x+i) + "," + y + "!");
            t.structs[3] = this;
        }
    }
}

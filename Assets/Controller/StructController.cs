using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class StructController : MonoBehaviour {
    public static StructController instance;
    private List<Structure> structures = new List<Structure>();
    private Dictionary<StructType, List<Structure>> structsByType = new Dictionary<StructType, List<Structure>>();
    private List<Blueprint> blueprints = new List<Blueprint>();
    public int n = 0;
    private World world;
    public Dictionary<Job, int> jobCounts;

    // this class keeps track of all the structures
    void Start() {    
        if (instance != null) {
            Debug.LogError("there should only be one structure controller");}
        instance = this;   
    }

    public void Remove(Structure structure) {
        structures.Remove(structure);
        n -= 1;
        if (structsByType.TryGetValue(structure.structType, out var list))
            list.Remove(structure);
    }

    public List<Structure> GetStructures() => new List<Structure>(structures);

    // Place a structure that was already created directly (load path, world generation).
    // Unlike Construct(), this does not touch GlobalInventory costs.
    public void Place(Structure structure) {
        structures.Add(structure);
        n += 1;
        if (!structsByType.TryGetValue(structure.structType, out var list)) {
            list = new List<Structure>();
            structsByType[structure.structType] = list;
        }
        list.Add(structure);
    }

    public bool Construct(StructType st, Tile tile){        
        Structure structure = null;
        if (st.isTile){ // tiles are not real structures, should just turn into tile
            if (st.name == "empty"){
                // need to spawn the mined resources
                if (tile.type.products != null && tile.type.products.Length > 0){
                    if (tile.inv == null){ tile.inv = new Inventory(4, 1000, Inventory.InvType.Floor, tile.x, tile.y); }
                    tile.inv.Produce(tile.type.products[0].item, tile.type.products[0].quantity);
                }
            }
            tile.type = Db.tileTypeByName[st.name];}
        else if (st.isPlant){
            if (tile.building != null){Debug.LogError("already a building or plant here!"); return false;}
            structure = new Plant(st as PlantType, tile.x, tile.y);}
        else {
            // Generic multi-tile collision check for all depths
            for (int i = 0; i < st.nx; i++) {
                Tile t = World.instance.GetTileAt(tile.x + i, tile.y);
                if (t == null) { Debug.LogError("tile out of bounds at " + (tile.x+i) + "," + tile.y); return false; }
                if (st.depth == "b" && t.building != null) { Debug.LogError("building occupied at " + (tile.x+i) + "," + tile.y); return false; }
                if (st.depth == "m" && t.mStruct != null) { Debug.LogError("mStruct occupied at " + (tile.x+i) + "," + tile.y); return false; }
                if (st.depth == "f" && t.fStruct != null) { Debug.LogError("fStruct occupied at " + (tile.x+i) + "," + tile.y); return false; }
                if (st.depth == "r" && t.road != null) { Debug.LogError("road occupied at " + (tile.x+i) + "," + tile.y); return false; }
            }
            if (st.depth == "r") {
                Tile below = World.instance.GetTileAt(tile.x, tile.y - 1);
                if (below == null || !below.type.solid) { Debug.LogError("road requires solid tile below!"); return false; }
            }
            // Dispatch to subclass
            if (st.depth == "b") { structure = new Building(st, tile.x, tile.y); }
            else if (st.name == "platform") { structure = new Platform(st, tile.x, tile.y); }
            else if (st.name == "stairs") { structure = new Stairs(st, tile.x, tile.y); }
            else if (st.name == "ladder") { structure = new Ladder(st, tile.x, tile.y); }
            else if (st.depth == "r") { structure = new Road(st, tile.x, tile.y); }
            else { Debug.LogError("unknown type of structure? " + st.depth); return false; }
        }
        

        if (st.requiredTileName != null){ // if building inside a tile (like for quarry), remove the tile
            tile.type = Db.tileTypeByName["empty"];
        }
        if (!st.isTile){
            Place(structure);
        }
        if (world == null) {world = World.instance;}
        world.graph.UpdateNeighbors(tile.x, tile.y);
        world.graph.UpdateNeighbors(tile.x, tile.y + 1);
        if (st.name == "stairs") {
            int nx = world.nx, ny = world.ny;
            if (tile.x - 1 >= 0)              world.graph.UpdateNeighbors(tile.x - 1, tile.y);
            if (tile.x + 1 < nx)              world.graph.UpdateNeighbors(tile.x + 1, tile.y);
            if (tile.x - 1 >= 0 && tile.y + 1 < ny) world.graph.UpdateNeighbors(tile.x - 1, tile.y + 1);
            if (tile.x + 1 < nx && tile.y + 1 < ny) world.graph.UpdateNeighbors(tile.x + 1, tile.y + 1);
        }
        if (st.isTile) {
            int nx = world.nx, ny = world.ny;
            for (int dx = -1; dx <= 1; dx++) {
                for (int dy = -1; dy <= 1; dy++) {
                    if (dx == 0 && dy == 0) continue; // already updated above
                    if (dx == 0 && dy == 1) continue; // (tile.x, tile.y+1) already updated above
                    int tx = tile.x + dx, ty = tile.y + dy;
                    if (tx >= 0 && tx < nx && ty >= 0 && ty < ny)
                        world.graph.UpdateNeighbors(tx, ty);
                }
            }
        }
        // After any tile/building change, check if items on the tile above are now floating.
        world.FallIfUnstandable(tile.x, tile.y + 1);
        return true;
    }

    public void AddBlueprint(Blueprint bp) { blueprints.Add(bp); }
    public void RemoveBlueprint(Blueprint bp) { blueprints.Remove(bp); }
    public List<Blueprint> GetBlueprints() => blueprints;

    public List<Structure> GetByType(StructType st) {
        return structsByType.TryGetValue(st, out var list) ? list : null;
    }

    public int TotalHousingCapacity() {
        int total = 0;
        var houses = GetByType(Db.structTypeByName["house"]);
        if (houses != null)
            foreach (Structure s in houses) total += s.res.capacity;
        return total;
    }

    public void TickUpdate(){
        foreach (Structure structure in structures){
            //
        }
    }

}
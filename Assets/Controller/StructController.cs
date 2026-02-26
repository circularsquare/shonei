using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class StructController : MonoBehaviour {
    public static StructController instance;
    private List<Structure> structures = new List<Structure>(); // list of structures
    public int n = 0; 
    public GameObject jobsPanel;

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
    }

    public List<Structure> GetStructures() => new List<Structure>(structures);

    // Place a structure that was already created directly (load path, world generation).
    // Unlike Construct(), this does not touch GlobalInventory costs.
    public void Place(Structure structure) {
        structures.Add(structure);
        n += 1;
    }

    public bool Construct(StructType st, Tile tile){        
        Structure structure = null;
        if (st.isTile){ // tiles are not real structures, should just turn into tile
            if (st.name == "empty"){
                // need to spawn the mined resources
                if (tile.type.products != null && tile.type.products.Length > 0){
                    if (tile.inv == null){ tile.inv = new Inventory(1, 20, Inventory.InvType.Floor, tile.x, tile.y); }
                    tile.inv.Produce(tile.type.products[0].item, tile.type.products[0].quantity);
                }
            }
            tile.type = Db.tileTypeByName[st.name];}
        else if (st.isPlant){
            if (tile.building != null){Debug.LogError("already a building or plant here!"); return false;}
            structure = new Plant(st as PlantType, tile.x, tile.y);}
        else if (st.depth == "b"){
            if (tile.building != null){Debug.LogError("already a building or plant here!"); return false;}
            structure = new Building(st, tile.x, tile.y);}
        else if (st.name == "platform"){
            if (tile.mStruct != null){Debug.LogError("already a mid structure here!"); return false;}
            structure = new Platform(st, tile.x, tile.y);}   
        else if (st.name == "stairs"){
            if (tile.fStruct != null){Debug.LogError("already a foreground structure here!"); return false;}
            structure = new Stairs(st, tile.x, tile.y);}
        else if (st.name == "ladder"){
            if (tile.fStruct != null){Debug.LogError("already a foreground structure here!"); return false;}
            structure = new Ladder(st, tile.x, tile.y);}
        else { Debug.LogError("unknown type of structure?"); Debug.Log(st.depth);return false; }
        

        if (st.requiredTileName != null){ // if building inside a tile (like for quarry), remove the tile
            tile.type = Db.tileTypeByName["empty"];
        }
        if (!st.isTile){
            structures.Add(structure);
        }
        GlobalInventory.instance.AddItems(st.costs, true);
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
        return true;
    }

    public void TickUpdate(){
        foreach (Structure structure in structures){
            //
        }
    }

}
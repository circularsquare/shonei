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

    public void AddStructure(Structure structure){
        structures.Add(structure);
        n += 1;
    }

    public bool Construct(StructType st, Tile tile){        
        Structure structure = null;
        if (st.isTile){ // tiles are not real structures, should just turn into tile
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
        
        if (!st.isTile){
            structures.Add(structure);
        }
        GlobalInventory.instance.AddItems(st.costs, true);
        if (world == null) {world = World.instance;}
        world.graph.UpdateNeighbors(tile.x, tile.y);
        world.graph.UpdateNeighbors(tile.x, tile.y + 1); // it may become standable?
        return true;
    }

    public void TickUpdate(){
        foreach (Structure structure in structures){
            //
        }
    }

}
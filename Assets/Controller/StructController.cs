using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class StructController : MonoBehaviour {
    public static StructController instance { get; protected set; }
    private List<Structure> structures = new List<Structure>();
    private Dictionary<StructType, List<Structure>> structsByType = new Dictionary<StructType, List<Structure>>();
    private List<Blueprint> blueprints = new List<Blueprint>();
    private List<Building> leisureBuildings = new List<Building>();
    private int _seatResExpireTick = 0;
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
        if (structure is Building b && b.structType.isLeisure)
            leisureBuildings.Remove(b);
    }

    public List<Structure> GetStructures() => new List<Structure>(structures);
    public List<Building> GetLeisureBuildings() => leisureBuildings;

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
        if (structure is Building b && b.structType.isLeisure)
            leisureBuildings.Add(b);
        if (structure.waterPixelOffsets != null)
            WaterController.instance?.RegisterDecorativeWater(structure);
    }

    public bool Construct(StructType st, Tile tile, bool mirrored = false, int rotation = 0, int shapeIndex = 0){
        Structure structure = null;
        // Visual footprint for non-tile, non-plant structures. Matches the full-footprint
        // claim in Structure / Blueprint, so the defense-in-depth collision check below
        // covers every tile a multi-tile structure will occupy (e.g. all 8 tiles of a 2×4
        // windmill, not just the bottom row).
        Shape shape = st.GetShape(shapeIndex);
        bool shapeAware = st.HasShapes;
        int fnx = shapeAware ? shape.nx : st.nx;
        int fny = shapeAware ? shape.ny : Mathf.Max(1, st.ny);
        if (st.isTile){ // tiles are not real structures, should just turn into tile
            if (st.name == "empty"){
                // Mining output is captured by Blueprint.Complete() into pendingOutput before this is called.
                // (No floor produce here — if called outside a blueprint context, caller handles output.)
            }
            tile.type = Db.tileTypeByName[st.name];}
        else if (st.isPlant){
            if (tile.structs[0] != null){Debug.LogError("already a building or plant here!"); return false;}
            structure = Structure.Create(st, tile.x, tile.y, mirrored, rotation, shapeIndex);}
        else {
            // Generic multi-tile collision check across the full footprint.
            for (int dy = 0; dy < fny; dy++) {
                for (int dx = 0; dx < fnx; dx++) {
                    Tile t = World.instance.GetTileAt(tile.x + dx, tile.y + dy);
                    if (t == null) { Debug.LogError("tile out of bounds at " + (tile.x+dx) + "," + (tile.y+dy)); return false; }
                    if (t.structs[st.depth] != null) { Debug.LogError("depth " + st.depth + " occupied at " + (tile.x+dx) + "," + (tile.y+dy)); return false; }
                }
            }
            structure = Structure.Create(st, tile.x, tile.y, mirrored, rotation, shapeIndex);
            if (structure == null) return false;
        }


        // Capture the original tile type BEFORE it's replaced below — the quarry
        // needs this to pick its extraction distribution per cycle.
        if (structure is Quarry q) q.CaptureOriginalTile(tile.type);

        // Mining trigger. Two paths converge here:
        //   - `requiredTileName != null` (quarry / dirt pit): the structure replaces a specific tile group.
        //   - `requiresSolidTilePlacement` (mineshaft): the structure occupies any solid tile, mining it.
        if (st.requiredTileName != null || st.requiresSolidTilePlacement){
            tile.type = Db.tileTypeByName["empty"];
        }
        if (!st.isTile){
            Place(structure);
            structure.OnPlaced();
        }
        // Optional follow-up structure placed on the same tile (mineshaft → ladder). Lifted out of
        // the isTile branch so it fires for any StructType. Done before the standability/neighbor
        // sweep below so the new structure's edges enter the nav graph in the same call. Defaults for
        // mirror/rotation/shape — current consumers (ladder) don't have variants; revisit if a future
        // placesStructureOnComplete needs them.
        if (st.placesStructureOnComplete != null){
            StructType extraType = Db.structTypeByName[st.placesStructureOnComplete];
            Structure extra = Structure.Create(extraType, tile.x, tile.y);
            if (extra != null){
                Place(extra);
                extra.OnPlaced();
            }
        }
        if (world == null) {world = World.instance;}
        // Refresh standability across the footprint and the row directly above the top —
        // every footprint tile may have changed standability via the same-structure-body
        // rule, and the row above may have become standable on the new solidTop surface.
        for (int dx = 0; dx < fnx; dx++) {
            for (int dy = 0; dy < fny; dy++)
                world.graph.UpdateNeighbors(tile.x + dx, tile.y + dy);
            world.graph.UpdateNeighbors(tile.x + dx, tile.y + fny);
        }
        if (st.name == "stairs") {
            int nx = world.nx, ny = world.ny;
            if (tile.x - 1 >= 0)              world.graph.UpdateNeighbors(tile.x - 1, tile.y);
            if (tile.x + 1 < nx)              world.graph.UpdateNeighbors(tile.x + 1, tile.y);
            if (tile.x - 1 >= 0 && tile.y + 1 < ny) world.graph.UpdateNeighbors(tile.x - 1, tile.y + 1);
            if (tile.x + 1 < nx && tile.y + 1 < ny) world.graph.UpdateNeighbors(tile.x + 1, tile.y + 1);
        }
        // 8-neighbor sweep around any tile that *changes type* during construction. Covers both
        // isTile blueprints (tile.type swap) and structures that mine their placement tile
        // (mineshaft via requiresSolidTilePlacement). Diagonal neighbours can have cliff/stair
        // edges that depend on this tile's solidity.
        if (st.isTile || st.requiresSolidTilePlacement) {
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
        // After any tile/building change, check if items on the tile above the top are now floating.
        for (int dx = 0; dx < fnx; dx++)
            world.FallIfUnstandable(tile.x + dx, tile.y + fny);
        world.graph.RebuildComponents();
        // Refresh any blueprints stacked directly above the top of the footprint — they may
        // have just become unsuspended, in which case they need both a tint update and
        // (re)registration of their WOM orders.
        for (int dx = 0; dx < fnx; dx++) {
            Tile above = world.GetTileAt(tile.x + dx, tile.y + fny);
            if (above == null) continue;
            for (int d = 0; d < Tile.NumDepths; d++) {
                Blueprint bp = above.GetBlueprintAt(d);
                if (bp == null) continue;
                bp.RefreshColor();
                bp.RegisterOrdersIfUnsuspended();
            }
        }
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
        if (++_seatResExpireTick >= 120) {
            _seatResExpireTick = 0;
            foreach (Building b in leisureBuildings)
                if (b.seatRes != null)
                    foreach (var seat in b.seatRes)
                        seat.ExpireIfStale(60f, $"{b.structType.name} seat");
        }
        // Furnishing slot decay. Called every 0.2s from World.Tick; FurnishingSlots converts
        // elapsed seconds → in-game days using World.ticksInDay. Empties slots whose lifetime
        // crosses 0 and fires onSlotChanged so Happiness + visuals refresh.
        foreach (Structure structure in structures){
            if (structure is Building b && b.furnishingSlots != null)
                b.furnishingSlots.TickDecay(0.2f);
        }
    }

}
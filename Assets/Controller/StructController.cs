using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

// Central runtime registry for every placed Structure in the world. Anything that
// needs to enumerate, look up, or react to structures (Animal AI, UI panels,
// happiness, housing, work dispatch, save/load) goes through here rather than
// scanning tiles. The registry mirrors what's authoritatively stored on Tiles —
// keeping it in sync is this class's main job.
//
// ── Registries ─────────────────────────────────────────────────────────
//   structures        — flat list of every live Structure. The "iterate everything"
//                       path (tick decay, housing totals, save gather).
//   structsByType     — same set bucketed by StructType for cheap "all benches"
//                       style lookups (GetByType).
//   blueprints        — separate list of in-progress Blueprints. They are not
//                       Structures yet; promoted via Blueprint.Complete().
//   leisureBuildings  — narrow subset used by the seat-reservation expiry sweep
//                       so we don't walk every structure each tick.
//
// ── Creation paths ─────────────────────────────────────────────────────
// Two entry points, both routed through Structure.Create() (the shared factory
// that dispatches to the right subclass). See CLAUDE.md "Structure creation rules".
//   Construct(st, tile, ...) — the gameplay path. Called from Blueprint.Complete()
//                              after construction work finishes. Handles tile-type
//                              swaps for isTile blueprints, multi-tile footprint
//                              collision checks, mining-trigger tile clearing,
//                              optional follow-up structures (mineshaft → ladder),
//                              and the standability / nav-graph refresh sweep.
//                              Returns false on placement failure (collision,
//                              out of bounds) — callers must handle that.
//   Place(structure)         — the load / worldgen path. The Structure already
//                              exists; we just file it into the registries and
//                              register any decorative water. No cost side-effects,
//                              no failure mode.
// Construct() calls Place() internally for the non-tile branch, so Place() is the
// single funnel for "structure becomes live and tracked".
//
// ── Other contracts ────────────────────────────────────────────────────
//   - Remove() must be called when a structure is destroyed; it mirrors the
//     additions Place() does. Tile-side cleanup is the caller's responsibility.
//   - TickUpdate() drives two things: a slow (~every 120 ticks) leisure-seat
//     reservation expiry sweep, and a per-tick furnishing-slot decay pass.
//     Called from World.Tick — not Unity Update.
//   - Decorative water offsets are registered with WaterController in Place(),
//     so any structure with waterPixelOffsets contributes to the water field
//     the moment it's tracked.
public class StructController : MonoBehaviour {
    public static StructController instance { get; protected set; }
    private List<Structure> structures = new List<Structure>();
    private Dictionary<StructType, List<Structure>> structsByType = new Dictionary<StructType, List<Structure>>();
    private List<Blueprint> blueprints = new List<Blueprint>();
    private List<Building> leisureBuildings = new List<Building>();
    private int _seatResExpireTick = 0;
    public int n = 0;
    private World world;

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

    // partnerX / partnerY thread through to Structure.Create so two-click placements
    // (rope bridge posts) can hand each post its partner's coords. -1 sentinels mean
    // "single-tile placement; ignore." Called twice in succession by Blueprint.Complete
    // for two-click blueprints — once per post with the coords swapped.
    // `materials` is the actual leaf items + fen the structure was built from (set by
    // Blueprint.Complete from its delivered, leaf-locked costs). Stored on the Structure for
    // exact deconstruct refunds + future tinting. null on non-blueprint paths (worldgen, load,
    // mineshaft-ladder follow-up) → deconstruct falls back to first-leaf of each cost.
    public bool Construct(StructType st, Tile tile, bool mirrored = false, int rotation = 0, int shapeIndex = 0, int partnerX = -1, int partnerY = -1, List<ItemQuantity> materials = null){
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
            structure = Structure.Create(st, tile.x, tile.y, mirrored, rotation, shapeIndex, partnerX, partnerY);
            if (structure == null) return false;
        }


        // ── Mining + tile-type swap ──────────────────────────────────────
        // Capture the original tile type BEFORE it's replaced below — quarry and
        // digging pit both produce their substrate's material per cycle, so they
        // need the tile they were built on remembered.
        if (structure is ExtractionBuilding eb) eb.CaptureOriginalTile(tile.type);

        // Mining trigger. Two paths converge here:
        //   - `requiredTileName != null` (quarry / digging pit): the structure replaces a specific tile group.
        //   - `requiresSolidTilePlacement` (mineshaft): the structure occupies any solid tile, mining it.
        // Mining loops the full footprint so multi-tile excavation buildings clear every claimed
        // tile, not just the anchor. Single-tile buildings (mineshaft, quarry) run the loop once.
        // `preservesTile` opts out: the structure renders over the tile as if it were a hole, but
        // the tile stays its original type (burrow). Yield is still captured in Blueprint.Complete.
        if (st.OccupiesSolidTile && !st.preservesTile){
            TileType empty = Db.tileTypeByName["empty"];
            World w = World.instance;
            for (int dy = 0; dy < fny; dy++) {
                for (int dx = 0; dx < fnx; dx++) {
                    Tile t = w.GetTileAt(tile.x + dx, tile.y + dy);
                    if (t != null) t.type = empty;
                }
            }
        }
        // ── Place + optional follow-up ───────────────────────────────────
        if (!st.isTile){
            structure.materials = materials; // null on non-blueprint paths; deconstruct falls back to first-leaf
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
        // ── Nav graph refresh ────────────────────────────────────────────
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
        // 8-neighbor sweep around any tile that *changes type* during construction. Covers
        // isTile blueprints (tile.type swap), single-tile excavators (mineshaft, quarry),
        // and multi-tile excavators that mine their footprint. For multi-tile, the sweep
        // walks the full footprint; per-footprint-tile sweeps overlap inside the rectangle,
        // which is fine (UpdateNeighbors is idempotent). Diagonal neighbours can have
        // cliff/stair edges that depend on this tile's solidity, so the diagonals matter.
        // `preservesTile` opts out — solidity is unchanged, so no diagonal refresh needed.
        if (st.isTile || (st.OccupiesSolidTile && !st.preservesTile)) {
            int nx = world.nx, ny = world.ny;
            for (int fdy = 0; fdy < fny; fdy++) {
                for (int fdx = 0; fdx < fnx; fdx++) {
                    int cx = tile.x + fdx, cy = tile.y + fdy;
                    for (int dx = -1; dx <= 1; dx++) {
                        for (int dy = -1; dy <= 1; dy++) {
                            if (dx == 0 && dy == 0) continue; // anchor footprint already updated
                            int tx = cx + dx, ty = cy + dy;
                            if (tx >= 0 && tx < nx && ty >= 0 && ty < ny)
                                world.graph.UpdateNeighbors(tx, ty);
                        }
                    }
                }
            }
        }
        // After any tile/building change, check if items on the tile above the top are now floating.
        for (int dx = 0; dx < fnx; dx++)
            world.FallIfUnstandable(tile.x + dx, tile.y + fny);
        world.graph.RebuildComponents();

        // ── Refresh suspended blueprints above ───────────────────────────
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

        // ── Refresh suspended shaft blueprints beside a new shaft ─────────
        // Shafts gain support by connecting to an existing shaft (PowerShaft.ConnectsToShaft),
        // so building one may un-suspend a shaft blueprint on ANY orthogonal side — not just the
        // tile above the footprint handled by the loop above. Re-evaluate all four neighbours.
        // (RegisterOrdersIfUnsuspended is idempotent, so the overlap with the 'above' tile is
        // harmless.)
        if (PowerShaft.IsShaft(st)) {
            foreach ((int dx, int dy) in ShaftNeighbourOffsets) {
                Tile n = world.GetTileAt(tile.x + dx, tile.y + dy);
                Blueprint bp = n?.GetBlueprintAt(st.depth);
                if (bp == null) continue;
                bp.RefreshColor();
                bp.RegisterOrdersIfUnsuspended();
            }
        }
        return true;
    }

    static readonly (int dx, int dy)[] ShaftNeighbourOffsets = { (-1, 0), (1, 0), (0, -1), (0, 1) };

    public void AddBlueprint(Blueprint bp) { blueprints.Add(bp); }
    public void RemoveBlueprint(Blueprint bp) { blueprints.Remove(bp); }
    public List<Blueprint> GetBlueprints() => blueprints;

    public List<Structure> GetByType(StructType st) {
        return structsByType.TryGetValue(st, out var list) ? list : null;
    }

    public int TotalHousingCapacity() {
        int total = 0;
        // Walks all placed structures and sums capacity over anything flagged isHousing.
        // Replaces the legacy "look up house by name" scan — now picks up shack, future
        // burrow, and any other housing tier without code changes.
        foreach (Structure s in GetStructures()) {
            if (s == null || s.res == null) continue;
            // Broken housing isn't usable shelter, so it doesn't count toward available housing.
            if (s.structType.isHousing && !s.IsBroken) total += s.res.capacity;
        }
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
        // Per-building 0.2s updates. Called every 0.2s from World.Tick.
        //  - Furnishing slot decay: FurnishingSlots converts elapsed seconds → in-game days
        //    via World.ticksInDay, empties expired slots, fires onSlotChanged.
        //  - Processor ferment: Processor.Tick advances an UNTENDED batch's `progress` (seconds)
        //    while Working, scaled by ambient temperature. Tended batches advance via their worker.
        foreach (Structure structure in structures){
            if (!(structure is Building b)) continue;
            if (b.furnishingSlots != null)
                b.furnishingSlots.TickDecay(0.2f);
            // Drain reservoirs NOT burned by a LightSource (e.g. fountain water evaporating, foundry
            // fuel). LightSource buildings (torch/fireplace) burn per-frame in LightSource, gated to
            // night, so burning them here too would double-consume — skip those. Disabled/broken don't
            // drain. Burn FIRST so a foundry's fuel→heat lands before it melts this frame. An IDLE
            // foundry (nothing to melt, target unmakeable) skips burning so its heat decays.
            int burnedFen = 0;
            bool foundryIdle = b is Foundry idleChk && !idleChk.WantsHeat();
            if (b.reservoir != null && !b.structType.isLightSource && !b.disabled && !b.IsBroken && !foundryIdle)
                burnedFen = b.reservoir.Burn(0.2f);
            float ambientTemp = WeatherSystem.instance != null ? WeatherSystem.instance.temperature : 17.5f;
            if (b.processor != null) {
                b.processor.Tick(0.2f, ambientTemp);
            } else if (b is Foundry fdy) {
                // The foundry stokes its OWN heat from burned fuel before melting (so the heat lands
                // the same frame it's gated on), then advances its chunks + auto-alloy.
                fdy.AddFuelHeat(burnedFen, b.reservoir?.HeldLeaf());
                fdy.Tick(0.2f, ambientTemp);
            }
        }
    }

}
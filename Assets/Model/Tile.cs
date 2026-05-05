using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;
using System.Runtime.Serialization;

// One cell of the world grid. Owns its tile type, depth-indexed structure/blueprint
// slots, optional floor inventory, water level, and pathfinding Node. Fires callbacks
// on change so controllers can re-render without polling. Static grid coordinates
// (x, y) are immutable after construction.

// Health state of the tile's overlay decoration. Drives an atlas-suffix swap in
// WorldController.OnTileOverlayChanged: e.g. "grass" → "grass_dying" → "grass_dead".
// Default Live so legacy saves and freshly-grown grass start healthy. Underlying
// byte ordering matters (saved as int): inserting a new value would break old saves.
public enum OverlayState : byte { Live = 0, Dying = 1, Dead = 2 }

public class Tile {
    Action<Tile> cbTileTypeChanged;
    Action<Tile> cbBackgroundChanged;
    Action<Tile> cbOverlayChanged;
    Action<Tile> cbSnowChanged;

    World world;
    public int x, y;
    public GameObject go;
    private TileType _type;
    public TileType type{
        get{return _type;}
        set{
            // If we're moving to a tile type that doesn't carry an overlay (e.g. dirt → empty
            // on mining), drop the mask so we don't keep stale bits on a non-overlay tile.
            // The visual would already be hidden (the overlay renderer checks for a non-null
            // overlay name), but persisting these bits would dirty saves and confuse a future
            // re-promotion of the tile to a different overlay-bearing type.
            TileType prev = _type;
            _type = value;
            if (_overlayMask != 0 && (value == null || value.overlay == null)
                && prev != null && prev.overlay != null) {
                _overlayMask = 0;
                _overlayState = OverlayState.Live;
                cbOverlayChanged?.Invoke(this);
            }
            // Mining a snowy tile (or any transition to a non-solid type) clears
            // the snow flag — there's no surface left for it to rest on.
            if (_snow && (value == null || !value.solid)) {
                _snow = false;
                cbSnowChanged?.Invoke(this);
            }
            if (cbTileTypeChanged != null){
                cbTileTypeChanged(this);
            }
        }
    }
    // Per-side decoration bits for overlay materials (grass on dirt, future moss on stone, …).
    // Bit 0=L, 1=R, 2=D, 3=U (matches the cMask layout used by tile-sprite baking).
    // The bit semantics are generic — "side is decorated" — and the *atlas* selecting how that
    // looks is tile.type.overlay (e.g. "grass"). Worldgen seeds bits on every exposed cardinal
    // edge of overlay-bearing tiles; mining never auto-sets bits, so freshly exposed sides
    // stay bare. The renderer further AND-masks with neighbor-emptiness, so a side that becomes
    // visually buried (e.g. a structure goes adjacent) hides its grass without touching data.
    private byte _overlayMask;
    public byte overlayMask {
        get { return _overlayMask; }
        set {
            if (_overlayMask == value) return;
            _overlayMask = value;
            cbOverlayChanged?.Invoke(this);
        }
    }
    // Health state of the overlay decoration (Live / Dying / Dead). Driven by
    // OverlayGrowthSystem from temperature + moisture. Renderer appends a suffix
    // to the atlas name based on this value. Reset to Live when the tile transitions
    // to a non-overlay type (alongside overlayMask).
    private OverlayState _overlayState;
    public OverlayState overlayState {
        get { return _overlayState; }
        set {
            if (_overlayState == value) return;
            _overlayState = value;
            cbOverlayChanged?.Invoke(this);
        }
    }
    // Weather-driven snow cover. Orthogonal to the grass overlay system above —
    // snow is ephemeral, can land on any solid tile (dirt, stone, …), and
    // accumulating snow kills the underlying grass. Driven by SnowAccumulationSystem
    // from temperature + WeatherSystem.snowAmount; renderer subscribes to the
    // change callback for sprite swaps. Cleared when the tile becomes non-solid
    // (mining) — same pattern as overlayMask.
    private bool _snow;
    public bool snow {
        get { return _snow; }
        set {
            if (_snow == value) return;
            _snow = value;
            cbSnowChanged?.Invoke(this);
        }
    }
    // Background wall behind the tile (rendered by BackgroundTile.cs).
    // Type is fixed at world-gen and never changes after mining: top DirtDepth
    // rows below surface get Dirt walls, deeper get Stone. `hasBackground` is
    // a derived getter for callers that just want presence (SkyExposure etc.).
    private BackgroundType _backgroundType;
    public BackgroundType backgroundType {
        get { return _backgroundType; }
        set {
            if (_backgroundType == value) return;
            _backgroundType = value;
            cbBackgroundChanged?.Invoke(this);
        }
    }
    public bool hasBackground => _backgroundType != BackgroundType.None;
    // Depth slots — slot index is independent of visual sortingOrder.
    // 0=building, 1=platform, 2=foreground, 3=road, 4=power shaft.
    // Slot 4 (shafts) renders behind buildings via a low sortingOrder; see Structure.cs.
    public const int NumDepths = 5;
    public Structure[] structs = new Structure[NumDepths];
    public Blueprint[] blueprints = new Blueprint[NumDepths];
    public Building building => structs[0] as Building; // alias for depth-0 Building (does NOT match Plant)
    public Plant plant => structs[0] as Plant;           // alias for depth-0 Plant
    public Inventory inv; // this encapsulates all inventory types
    public ushort water; // 0–160 internal fixed-point (10 units = 1 display unit); 160 = fully filled tile
    public byte moisture; // 0–100 soil wetness percent. Only meaningful on SOLID tiles (dirt/stone) — air tiles stay 0. Plants above read moisture from the soil tile directly below them.
    public Node node;

    
    public Tile(World world, int x, int y){
        this.world = world;
        this.x = x;
        this.y = y;
        type = Db.tileTypes[0];
        node = new Node(this, x, y);
    }
    
    public void RegisterCbTileTypeChanged(Action<Tile> callback){cbTileTypeChanged += callback;}
    public void UnregisterCbTileTypeChanged(Action<Tile> callback){cbTileTypeChanged -= callback;}
    public void RegisterCbBackgroundChanged(Action<Tile> callback){cbBackgroundChanged += callback;}
    public void UnregisterCbBackgroundChanged(Action<Tile> callback){cbBackgroundChanged -= callback;}
    public void RegisterCbOverlayChanged(Action<Tile> callback){cbOverlayChanged += callback;}
    public void UnregisterCbOverlayChanged(Action<Tile> callback){cbOverlayChanged -= callback;}
    public void RegisterCbSnowChanged(Action<Tile> callback){cbSnowChanged += callback;}
    public void UnregisterCbSnowChanged(Action<Tile> callback){cbSnowChanged -= callback;}
    // Fire the overlay callback without changing data — used when external state
    // that the renderer reads (e.g. structs[3] for road suppression) flips, but
    // the tile's own overlay fields haven't.
    public void NotifyOverlayDirty(){ cbOverlayChanged?.Invoke(this); }
    public bool ContainsAvailableItem(Item item){return inv != null && inv.ContainsAvailableItem(item);}
    public ItemStack GetItemToHaul(){
        if (inv == null){return null;}
        else{return inv.GetItemToHaul();}
    }
    public bool HasItemToHaul(Item item){return inv != null && inv.HasItemToHaul(item);} // can be null for any item

    // space: floor allowed
    public bool HasSpaceForItem(Item item){return (inv == null || inv.HasSpaceForItem(item));}
    public bool HasLadder(){ return structs[2]?.structType.name == "ladder"; }
    public bool HasStairRight(){ return structs[2]?.structType.name == "stairs" && !structs[2].mirrored; }
    public bool HasStairLeft(){ return structs[2]?.structType.name == "stairs" && structs[2].mirrored; }

    public Blueprint GetAnyBlueprint(){
        foreach (var bp in blueprints) if (bp != null) return bp;
        return null;
    }
    public Blueprint GetMatchingBlueprint(Func<Blueprint, bool> predicate){
        foreach (var bp in blueprints) if (bp != null && predicate(bp)) return bp;
        return null;
    }
    public Blueprint GetBlueprintAt(int depth) => blueprints[depth];
    public void SetBlueprintAt(int depth, Blueprint bp) => blueprints[depth] = bp;

    public Tile[] GetAdjacents(){ // not the same as graph neighbors
        Tile[] adjacents = new Tile[8];
        adjacents[0] = world.GetTileAt(x + 1, y);
        adjacents[1] = world.GetTileAt(x, y - 1);
        adjacents[2] = world.GetTileAt(x - 1, y);
        adjacents[3] = world.GetTileAt(x, y + 1);
        adjacents[4] = world.GetTileAt(x + 1, y - 1);
        adjacents[5] = world.GetTileAt(x - 1, y - 1);
        adjacents[6] = world.GetTileAt(x - 1, y + 1);
        adjacents[7] = world.GetTileAt(x + 1, y + 1);
        return adjacents;
    }

    override public string ToString(){
        return ("tile " + x.ToString() + "," + y.ToString());
    }

    public Inventory EnsureFloorInventory() {
        if (inv == null) { inv = new Inventory(n: 1, x: x, y: y); }
        return inv;
    }
}


// Wall type behind a tile. Saved per-tile and authoritative for which
// background texture renders at that grid cell. Decided at world-gen by
// position (top DirtDepth rows = Dirt, deeper = Stone) and never changes.
public enum BackgroundType {
    None  = 0,
    Stone = 1,
    Dirt  = 2,
}


public class TileType {
    public int id {get; set;}
    public string name {get; set;}
    public bool solid {get; set;}
    // Optional logical family ("stone" for limestone/granite/slate, etc.) — used by
    // StructPlacement so that a building's `requiredTileName` can match by group.
    public string group {get; set;}
    // Optional name of an overlay sprite sheet (e.g. "grass" for dirt). When non-null,
    // tiles of this type can have a per-side overlayMask whose bits are rendered as
    // edge art from Sprites/Tiles/Sheets/<overlay>.png. See Tile.overlayMask.
    public string overlay {get; set;}
    public ItemNameQuantity[] nproducts {get; set;}         // tile-break drops (authored in liang)
    public ItemQuantity[] products;                         // fen, populated on deserialize
    // Quarry / extraction-building yields, distinct from tile-break drops.
    // Tile breaking = "clear the area"; extraction = "deliberate resource harvesting".
    public ItemNameQuantity[] nExtractionProducts {get; set;}
    public ItemQuantity[] extractionProducts;


    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        if (nproducts != null){
            products = new ItemQuantity[nproducts.Length];
            for (int i = 0; i < nproducts.Length; i++){
                products[i] = new ItemQuantity(nproducts[i].name, ItemStack.LiangToFen(nproducts[i].quantity));
            }
        }
        if (nExtractionProducts != null){
            extractionProducts = new ItemQuantity[nExtractionProducts.Length];
            for (int i = 0; i < nExtractionProducts.Length; i++){
                var src = nExtractionProducts[i];
                var iq = new ItemQuantity(src.name, ItemStack.LiangToFen(src.quantity));
                iq.chance = src.chance;
                extractionProducts[i] = iq;
            }
        }
    }
}
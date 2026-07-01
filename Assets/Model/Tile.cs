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
    Action<Tile> cbBodyChanged;
    // Per-side rim-suppression bits (L=1, R=2, D=4, U=8 — same convention as the
    // body renderer's cMask). When a bit is set, the body bake treats that side as
    // if a solid neighbour were present, hiding the 2-pixel air-edge bevel. Used by
    // doored `preservesTile` buildings (burrow) so the entrance doesn't draw a rim
    // through the doorway. Maintained by Structure ctor / Destroy.
    public byte bodyEdgeSuppressMask;
    // When true, this tile skips emitting its own body + overlay quads in
    // TileMeshController — the cell reads as an open hole and whatever sits
    // behind it (background wall, or sky where backgroundType==None) shows
    // through. Solidity is UNCHANGED, so neighbouring tiles still bake their
    // normals as if this cell were solid earth. Set by `preservesTile`
    // excavation buildings (digging pit) that draw their own receding-substrate
    // sprite over the cell. Fire NotifyBodyDirty() after mutating.
    public bool bodyRenderSuppressed;
    // When true, the chunk skips this cell's own body quad (like bodyRenderSuppressed), but —
    // unlike bodyRenderSuppressed — neighbours and the grass/snow overlay still treat the cell
    // as normal SOLID terrain (neighbours bake buried, no jagged edge toward it; the overlay
    // still renders on top). Set by structures that re-draw the cell's body themselves at a
    // lower sort order so they sit in FRONT of it — the burrow, whose dirt bank shows behind
    // the facade with grass still growing over the top. Fire NotifyBodyDirty() after mutating.
    public bool bodyDrawnByStructure;
    // When true, neighbours treat this cell as open AIR for their normal-map
    // (lighting) bake, so their edges facing it light as exposed cliff faces
    // rather than buried seams. Separate from bodyRenderSuppressed: an excavation
    // pit suppresses its body immediately, but only flips this once it's dug
    // enough that the cell reads as mostly-open (see ExtractionBuilding.UpdateHoleLighting).
    // Solidity itself is unchanged. Fire NotifyBodyDirty() after mutating.
    public bool lightAsAir;
    Action<Tile> cbBackgroundChanged;
    Action<Tile> cbOverlayChanged;
    Action<Tile> cbSnowChanged;
    // Fired whenever this tile's structs[] contents change (Structure.Place /
    // Destroy, Plant extension claim/release). The array is public so we can't
    // intercept writes at the property setter — callers fire NotifyStructChanged
    // after mutating. Subscribers re-derive whatever they need (depth-0 building,
    // road suppression, etc.) themselves.
    Action<Tile> cbStructChanged;

    World world;
    public int x, y;
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
            // the snow — there's no surface left for it to rest on. The overlayMask
            // is cleared above in the same setter, so the under-snow grass goes with
            // the tile too.
            if (_snowAmount > 0 && (value == null || !value.solid)) {
                _snowAmount = 0;
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
    // Weather-driven snow cover, tracked as a continuous depth 0..SnowMax (byte).
    // Orthogonal to the grass overlay system above — snow is ephemeral and can land
    // on any solid tile (dirt, stone, …). It *preserves* the underlying grass with
    // no snapshot: the renderer skips the grass overlay quad while snowAmount > 0
    // (the snow mesh draws on top, picking a depth texture via
    // SnowAccumulationSystem.SnowLevel), and OverlayGrowthSystem freezes snowed
    // tiles, so the live overlayMask/State sit untouched and reappear on melt.
    // Accumulation raises the depth, melt lowers it gradually — both in
    // SnowAccumulationSystem (temperature + WeatherSystem.snowAmount). Renderer
    // subscribes to cbSnowChanged for the overlay/snow rebuild. Cleared when the
    // tile becomes non-solid (mining) — same pattern as overlayMask.
    private byte _snowAmount;
    public byte snowAmount {
        get { return _snowAmount; }
        set {
            if (_snowAmount == value) return;
            _snowAmount = value;
            cbSnowChanged?.Invoke(this);
        }
    }
    // Presence shorthand for the readers that only care "is there snow" (renderer
    // overlay-skip, OverlayGrowthSystem freeze, flower eligibility, save gate).
    // Depth-aware consumers read snowAmount / SnowAccumulationSystem.SnowLevel.
    public bool snow => _snowAmount > 0;
    // Farmland: tilling swaps the dirt tile's TYPE to "dirttilled" (its own tile type, with no
    // grass overlay and its own body sprite), so the state is the type itself — no separate flag,
    // and it persists/renders/clears-grass for free through the normal tile-type machinery. Till-
    // requiring crops (wheat/rice/soybean) need this below them; persistent, so a replant reuses it.
    public bool tilled => _type != null && _type.name == "dirttilled";
    // Background wall behind the tile — the MATERIAL (TileType) whose wall art renders
    // at this cell (drawn by BackgroundTileMeshController, grouped by TileType.backgroundAtlas).
    // Set at world-gen to mirror the stone/earth that spawned in front (WorldGen.SetBackgrounds),
    // so mining a tile out reveals the matching wall. null = no wall (open to sky / void).
    // `hasBackground` is the presence shorthand for callers that don't care which material
    // (SkyExposure, flower placement, save gate).
    private TileType _backgroundTile;
    public TileType backgroundTile {
        get { return _backgroundTile; }
        set {
            if (_backgroundTile == value) return;
            _backgroundTile = value;
            cbBackgroundChanged?.Invoke(this);
        }
    }
    public bool hasBackground => _backgroundTile != null;
    // True once a quarry/pit has fully extracted this cell's wall (its depth is spent).
    // The wall is exhausted: it can't host another quarry (StructPlacement) and renders
    // darkened. Set on depletion, persisted, reset only by ClearWorld. Fires
    // cbBackgroundChanged so the renderer can redraw.
    private bool _backgroundQuarriedOut;
    public bool backgroundQuarriedOut {
        get { return _backgroundQuarriedOut; }
        set {
            if (_backgroundQuarriedOut == value) return;
            _backgroundQuarriedOut = value;
            cbBackgroundChanged?.Invoke(this);
        }
    }
    // Depth slots — slot index is independent of visual sortingOrder.
    // 0=building, 1=platform, 2=foreground, 3=road, 4=power shaft, 5=enclosure (greenhouse).
    // Slot 4 (shafts) renders behind buildings via a low sortingOrder; see Structure.cs.
    // Slot 5 (greenhouse) is its own layer so the glass frame coexists on a tile with the plant
    // it covers (slot 0) AND any foreground ladder/rope (slot 2) without contending for a slot.
    public const int NumDepths = 6;
    public Structure[] structs = new Structure[NumDepths];
    public Blueprint[] blueprints = new Blueprint[NumDepths];
    public Building building => structs[0] as Building; // alias for depth-0 Building (does NOT match Plant)
    public Plant plant => structs[0] as Plant;           // alias for depth-0 Plant
    // Non-null when this tile is a hollow interior tile of a building with declared
    // interiorTiles (burrow, doored housing). Set/cleared by Structure interior-node
    // setup/teardown. Animal.insideBuilding is derived from this — don't cache it elsewhere.
    public Building interiorBuilding;
    // Non-null when this tile is covered by a greenhouse frame (isGreenhouse structure, at a
    // foreground depth so it doesn't contest structs[0]). Set across the greenhouse footprint in
    // the Structure tile-registration loop, cleared in Structure.Destroy. A plant rooted on a
    // greenhouse-covered tile bypasses the temperature growth-gate, grows faster, and can't grow
    // taller than the frame. Reference identity is load-bearing: Plant.CanExtendTo compares an
    // extension tile's greenhouse against the anchor's to enforce the height cap. See Plant.Grow.
    public Structure greenhouse;
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
    public void RegisterCbBodyChanged(Action<Tile> callback){cbBodyChanged += callback;}
    public void UnregisterCbBodyChanged(Action<Tile> callback){cbBodyChanged -= callback;}
    public void RegisterCbBackgroundChanged(Action<Tile> callback){cbBackgroundChanged += callback;}
    public void UnregisterCbBackgroundChanged(Action<Tile> callback){cbBackgroundChanged -= callback;}
    public void RegisterCbOverlayChanged(Action<Tile> callback){cbOverlayChanged += callback;}
    public void UnregisterCbOverlayChanged(Action<Tile> callback){cbOverlayChanged -= callback;}
    public void RegisterCbSnowChanged(Action<Tile> callback){cbSnowChanged += callback;}
    public void UnregisterCbSnowChanged(Action<Tile> callback){cbSnowChanged -= callback;}
    public void RegisterCbStructChanged(Action<Tile> callback){cbStructChanged += callback;}
    public void UnregisterCbStructChanged(Action<Tile> callback){cbStructChanged -= callback;}
    // Fire the overlay callback without changing data — used when external state
    // that the renderer reads (e.g. structs[3] for road suppression) flips, but
    // the tile's own overlay fields haven't.
    public void NotifyOverlayDirty(){ cbOverlayChanged?.Invoke(this); }
    // Fire after mutating structs[]. Centralised here so the four call sites
    // (Structure.Place, Structure.Destroy, Plant.ClaimExtensionTile,
    // Plant.ReleaseAllExtensionTiles) share a single firing point.
    public void NotifyStructChanged(){ cbStructChanged?.Invoke(this); }
    // Fire after mutating bodyEdgeSuppressMask. Body-only refresh — doesn't disturb
    // overlay/snow/structs callbacks.
    public void NotifyBodyDirty(){ cbBodyChanged?.Invoke(this); }
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
    // Side-ladder presence test, scoped by which side the wall is on.
    // dir = +1 → wall on right (sprite flipX, mirrored=true).
    // dir = -1 → wall on left  (sprite as-authored, mirrored=false).
    // Navigation uses this per-segment to upgrade a cliff-scaling segment to ladder cost.
    public bool HasSideLadder(int dir){
        Structure s = structs[2];
        if (s == null || s.structType.name != "ladder_side") return false;
        return dir > 0 ? s.mirrored : !s.mirrored;
    }
    // Direction-agnostic "is there any side ladder on this tile?" — used by
    // Navigation.GetStandability so a side ladder's tile is walkable (lets builders
    // reach mid-air stack levels and stand atop the ladder), without committing to
    // a particular wall direction. Vertical navigation through the ladder still
    // happens via the cliff/side-ladder waypoint chain at fractional X — this
    // standability extension does NOT add a direct integer-X vertical edge.
    public bool HasSideLadderAny(){
        Structure s = structs[2];
        return s != null && s.structType.name == "ladder_side";
    }

    // Direction-scoped lookup for ANY side-mounted structure leaning on a wall to its `dir`
    // side (side ladder, bracket, side torch). dir = +1 → wall on right (mirrored=true);
    // dir = -1 → wall on left (mirrored=false). Scans all depths since side-mounts vary by
    // depth (ladder/torch at 2, bracket at 1). Used by Blueprint to drop a mount whose wall
    // was mined out. Returns the structure or null.
    public Structure GetSideMount(int dir){
        for (int d = 0; d < NumDepths; d++){
            Structure s = structs[d];
            if (s == null || !s.structType.sideMounted) continue;
            if (dir > 0 ? s.mirrored : !s.mirrored) return s;
        }
        return null;
    }

    // Any ceiling-mounted structure (hanging lantern) on this tile — it hangs from the tile
    // above. Mirror-invariant. Used by Blueprint to drop a mount whose ceiling was mined out.
    public Structure GetCeilingMount(){
        for (int d = 0; d < NumDepths; d++){
            Structure s = structs[d];
            if (s != null && s.structType.mountTo == MountTo.Ceiling) return s;
        }
        return null;
    }

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


public class TileType {
    public int id {get; set;}
    public string name {get; set;}                  // internal lookup key — never shown to the player.
    public string displayName {get; set;}           // optional player-facing name; falls back to `name`. See DisplayName.
    // Player-facing name (info panel). Lets a placed variant (e.g. "limestone_placed") read as
    // its base material ("limestone"). Mirrors StructType.DisplayName.
    public string DisplayName => string.IsNullOrEmpty(displayName) ? name : displayName;
    public bool solid {get; set;}
    // Optional logical family ("stone" for limestone/granite/slate, etc.) — used by
    // StructPlacement so that a building's `requiredTileName` can match by group.
    public string group {get; set;}
    // True when this tile type is farmable soil — the ground a bare plant may root in and a
    // ground-mode greenhouse draws moisture from. The diggable earth group (dirt/sand/clay) plus
    // player-placed dirt (which drops its group per the _placed convention but is still soil).
    // Stone and everything else are NOT soil: bare crops can't grow on them, and a greenhouse
    // built on them runs self-contained. See StructPlacement (planting) and Greenhouse.OnPlaced.
    public bool isSoil => solid && (group == "earth" || name == "dirt_placed");
    // Max soil moisture this material holds (0..100; = MoistureSystem.MoistureMax for earth). Stone
    // holds less — slate/granite are near-impermeable. Drives the moisture clamp, the diffusion
    // conductivity (low cap = poor conductor = barrier), the well pull threshold, and (future)
    // saturation-based plant comfort. Default 100; only set lower for stone in tilesDb.json.
    public int moistureCapacity {get; set;} = 100;
    // Optional override: borrow another tile type's texture for rendering instead of `name`
    // (cache + per-type arrays stay keyed by `name`). Usually unnecessary — the "_placed"
    // convention (below) auto-borrows the base art. Set this only to point at some OTHER stem.
    public string spriteName {get; set;}

    // Suffix marking a player-built, non-harvestable variant of a base tile ("dirt" → "dirt_placed").
    public const string PlacedSuffix = "_placed";

    // Art stem for a tile name — the single source of truth for placed-variant art reuse, shared by
    // TileSpriteCache (final tile render) AND StructType.LoadSprite (blueprint / build-ghost / menu
    // icon), so all four agree. Resolution order: explicit `spriteName` override, else the base of a
    // "<base>_placed" convention name, else the name itself. Static + null-guarded so it's safe to
    // call before Db finishes loading.
    public static string SpriteStem(string tileName) {
        if (Db.tileTypeByName != null && Db.tileTypeByName.TryGetValue(tileName, out TileType tt)
            && !string.IsNullOrEmpty(tt.spriteName))
            return tt.spriteName;
        if (tileName != null && tileName.Length > PlacedSuffix.Length && tileName.EndsWith(PlacedSuffix))
            return tileName.Substring(0, tileName.Length - PlacedSuffix.Length);
        return tileName;
    }
    // Optional name of an overlay sprite sheet (e.g. "grass" for dirt). When non-null,
    // tiles of this type can have a per-side overlayMask whose bits are rendered as
    // edge art from Sprites/Tiles/Sheets/<overlay>.png. See Tile.overlayMask.
    public string overlay {get; set;}
    public ItemNameQuantity[] nproducts {get; set;}         // tile-break drops (authored in liang)
    public ItemQuantity[] products;                         // fen, populated on deserialize
    // Extraction-building (quarry / digging pit) yields, distinct from tile-break drops.
    // Tile breaking = "clear the area"; extraction = "deliberate resource harvesting".
    public ItemNameQuantity[] nExtractionProducts {get; set;}
    public ItemQuantity[] extractionProducts;
    // Optional stem of this material's background-wall atlas under Resources/Sprites/Tiles/Sheets/
    // (e.g. "limestoneback"). Non-null only on materials that can appear as a revealed wall
    // (stone + earth groups). BackgroundTileMeshController groups walls by this atlas; null
    // means the material never renders as a wall. See SPEC-rendering (background walls).
    public string backgroundAtlas {get; set;}


    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        if (nproducts != null){
            products = new ItemQuantity[nproducts.Length];
            for (int i = 0; i < nproducts.Length; i++){
                products[i] = new ItemQuantity(nproducts[i]);
            }
        }
        if (nExtractionProducts != null){
            extractionProducts = new ItemQuantity[nExtractionProducts.Length];
            for (int i = 0; i < nExtractionProducts.Length; i++){
                extractionProducts[i] = new ItemQuantity(nExtractionProducts[i]); // chance carried by the constructor
            }
        }
    }
}
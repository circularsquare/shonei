using UnityEngine;
using System.Runtime.Serialization;

// JSON schema for a structure kind (building, plant, tile, road, etc.). Loaded by
// Db.cs on startup from buildingsDb.json / plantsDb.json. Runtime Structure instances
// hold a reference to their StructType. Quantities are authored in liang (float) and
// converted to fen (int) in OnDeserialized.

// How a structure attaches to the world. None = rests on the ground (default).
// Side = hangs against a vertical wall face (terrain or a building's body), the player
// picking left/right via the mirror toggle. Ceiling = hangs from the solid underside of
// the tile above. Side and Ceiling share the mount-attach placement branch in
// StructPlacement and skip the generic standability check.
public enum MountTo { None, Side, Ceiling }

// A face of a footprint tile, queried against the baked edge-solidity masks via
// StructType.EdgeSolid ("does this tile present body on that face?"). Left/Right feed
// side-mounts; Bottom feeds ceiling-mounts. Top is NOT baked — top-face support is the
// authored `solidTop` flag, which also carries occlusion semantics a sprite bake can't.
public enum MountFace { Left, Right, Bottom }

// Work tile offset: a position within a multi-tile building where an animal can stand to interact.
// Used by nworkTiles[] on StructType. Mirroring is applied at runtime by Structure.WorkTileAt().
public class WorkTileOffset {
    public int dx {get; set;}
    public int dy {get; set;}
}

// Per-shape standable tile offset: declares that this specific (dx, dy) tile inside the
// footprint is a walkable surface, beyond what the default solidTop / multi-tile-body
// rules in Navigation.GetStandability would conclude. Used by Shape.standableOffsets[]
// to give non-subclassed multi-tile buildings partial-top patterns from JSON. Mirroring
// is applied at runtime by Structure.HasInternalFloorAt() — author offsets against the
// un-flipped shape (the way the sprite reads at mirrored=false).
public class StandableOffset {
    public int dx {get; set;}
    public int dy {get; set;}
}

// Door declaration: a tile in the footprint (dx, dy) + which side of that tile the
// doorway opens out of. Used by housing (and future production) buildings whose
// residents enter the footprint through a designated edge rather than standing on
// top. The approach tile (where the mouse stands before stepping in) is derived from
// dx/dy/side at runtime by Structure; mirroring flips dx and swaps left↔right.
//
// side ∈ "left" | "right" | "top" | "bottom" — relative to the un-mirrored footprint.
public class Door {
    public int dx {get; set;}
    public int dy {get; set;}
    public string side {get; set;}
}

// Interior tile: a footprint tile that should have interior walking space. Each
// entry causes Structure to register an off-grid Node inside that tile (visually
// below the roof line), edged to neighboring interior nodes so mice can walk
// between tiles inside the building, and edged through any matching door to the
// exterior approach tile. Pure graph topology — no Task code knows about doors.
// Mirroring flips dx via nx-1-dx at lookup time.
public class InteriorTile {
    public int dx {get; set;}
    public int dy {get; set;}
}

// Furnishing slot position: where in the building footprint to render an
// installed furnishing item. Parallel to StructType.furnishingSlotNames —
// entry i gives the render offset for slot i. Mirroring flips dx via nx-1-dx
// at lookup time (FurnishingVisuals.Refresh).
public class FurnishingSlotPos {
    public int dx {get; set;}
    public int dy {get; set;}
}

// Ladder: an explicit vertical connection between two interior tiles within a
// building's footprint. (dx, dy) is the BOTTOM tile; the edge connects to the
// interior node at (dx, dy+1). Interior nodes auto-edge horizontal neighbors
// by default but NOT vertical ones, so vertical access requires a declaration.
// Lets authors choose where mice climb up inside a building (e.g. "ladder on
// the left of a 2×2 house") instead of mice climbing through walls. Mirroring
// flips dx via nx-1-dx; dy is unaffected.
public class Ladder {
    public int dx {get; set;}
    public int dy {get; set;}
}

public class TileRequirement {
    public int dx {get; set;}
    public int dy {get; set;}
    public bool mustBeStandable {get; set;}
    public bool mustHaveWater {get; set;}    // tile.water > 0
    public bool mustBeEmpty {get; set;}      // structs[0] (building layer) must be null
    public bool mustNotBePlant {get; set;}   // if structs[0] is a Plant, reject. Lets a structure permit non-plant occupants while still refusing rooted plants — e.g. digging pit on a dirt tile rejects when a plant grows in the air tile above, since hollowing out the dirt would orphan it.
    public bool allowSelfSupporting {get; set;}  // relaxes mustBeEmpty: a preservesTile structure (burrow/digging pit/quarry, dug into its own solid footprint) is allowed here — it won't fall when this tile is removed. Used by the "empty" mining action's tile-above check so you can mine beneath a burrow.
    public bool mustBeSolidTile {get; set;}  // tile.type.solid must be true (ground tiles only, not solidTop buildings)
    public bool mustBeOpenSkyAbove {get; set;}  // World.IsExposedAbove(tx, ty) — used by windmill on each top-row tile
    public string requiredTileName {get; set;}
}

// Shape variant: an alternate footprint a StructType can take. When a StructType
// declares a `shapes` array, the player cycles between them with Q/E during build
// placement and the chosen shape's nx/ny override the StructType's base nx/ny on
// the placed structure & blueprint. Cost scales linearly with tile count vs shape[0]
// (the "authored" baseline). v1 only handles vertical extension (nx=1, ny>=1) for
// the variable-height platform; future buildings can add wider shapes as needed.
public class Shape {
    public int nx {get; set;}
    public int ny {get; set;}
    public int TileCount => Mathf.Max(1, nx) * Mathf.Max(1, ny);

    // Optional partial-top / internal-floor pattern for this shape variant. When set, the
    // base Structure.HasInternalFloorAt iterates these and reports any matching tile as
    // standable, letting non-subclassed multi-tile buildings declare walkable surfaces
    // inside their footprint without writing a Structure subclass. Each offset is in
    // un-mirrored shape-local coordinates (0 ≤ dx < nx, 0 ≤ dy < ny); mirroring is applied
    // at lookup time. Null = no internal floor (the default).
    public StandableOffset[] standableOffsets {get; set;}

    // Optional sprite-name override for this variant (a 1×1 visual swap). When set, the
    // structure loads "Sprites/Buildings/{sprite}" instead of the StructType's base name —
    // so one build-menu entry can offer several looks (e.g. roof / roof2) cycled with Q/E,
    // no second StructType. Resolved in StructureVisuals.ResolveAnchorSprite. Null = base name.
    public string sprite {get; set;}
}

public class StructType {
    public int id {get; set;}
    public string name {get; set;}                  // internal lookup key — referenced by recipes, saves, etc. Never shown to the player.
    public string displayName {get; set;}           // optional player-facing name; falls back to `name`. See DisplayName.
    public string description {get; set;}            // optional build-menu tooltip body
    // Player-facing name: prefer displayName, fall back to the internal name.
    public string DisplayName => string.IsNullOrEmpty(displayName) ? name : displayName;
    public int nx {get; set;}
    public int ny {get; set;}
    public ItemNameQuantity[] ncosts {get; set;}
    public ItemQuantity[] costs;
    public float constructionCost {get; set;}
    // When true, constructionCost is authored PER TILE: the blueprint multiplies it by the
    // placed footprint's tile count (shape tile count for shape-aware structures, span length
    // for two-click placements). Lets a variable-size structure (elevator, rope bridge) cost
    // proportionally more to build the bigger it is. Default false = constructionCost is a flat total.
    public bool constructionCostPerTile {get; set;}
    public bool isTile {get; set;}
    public bool isPlant;
    public int depth {get; set;} // 0=building, 1=platform, 2=foreground, 3=road, 4=power shaft, 5=enclosure (greenhouse)
    // Optional sprite sortingOrder override. -1 = use depth-based default (see Structure ctor).
    // Used for e.g. torches/fireplaces that need to sort above plants (60) and animals (48-57) so
    // LightSource's auto-detected sort bucket front-lights those receivers. Also changes draw order.
    public int sortingOrder {get; set;} = -1;
    // Optional lighting-bucket override (0..5; -1 = derive from sortingOrder). Decouples the
    // light-shaping depth plane from draw order — for ground-plane structures (e.g. roads) that
    // draw at a high sortingOrder so the raised tile body (78..74) doesn't bury them, yet must
    // still light as the Tiles plane (bucket 1), not as creatures. See SortBucketUtil + the
    // GroundPlaneLightingBucket note in TileMeshController.
    public int lightingBucket {get; set;} = -1;
    // ── Logistics job (NOT the operator job!) ─────────────────────────
    // `njob` (JSON) → `job` (resolved Job ref): the job responsible for the structure's
    // BUILD/SUPPLY/DECONSTRUCT lifecycle work — not who operates it once built. Specifically:
    //   - SupplyBlueprintTask / ConstructTask / DeconstructTask all gate canDo on `a.job == structType.job`
    //   - Defaults to "hauler" if unspecified (see OnDeserialized below)
    //   - Plants overload this to mean the HARVEST job (logger/farmer) — see WorkOrderManager.RegisterDeconstruct comment
    // For workstation OPERATOR eligibility (who crafts at this building), use Recipe.job:
    //   `Array.Exists(animal.job.recipes, r => r != null && r.tile == buildingName)`
    // (See CLAUDE.md "Craft order job check" anti-pattern.)
    public string njob {get; set;}
    public Job job;
    public int capacity {get; set;} // number of animals that can reserve this struct at once
    public string requiredTileName {get; set;} // tile that this struct must be built on
    public bool isStorage {get; set;} // true for storage buildings (drawers, crates, etc.)
    public ItemClass storageClass {get; set;} = ItemClass.Default; // category of items this storage accepts; matched against Item.itemClass in Inventory.ItemTypeCompatible. Liquid = tanks; Book = bookshelves; Default = normal dry storage.
    public bool isLiquidStorage => storageClass == ItemClass.Liquid; // convenience — for existing tank-specific rendering code
    public int nStacks {get; set;} // number of item stacks in storage
    public int storageStackSize {get; set;} // max items per stack in storage
    public string category {get; set;} // build menu category: "structures", "plants", "production", "storage"
    public bool defaultLocked {get; set;} // true = locked; hidden from build menu until unlocked via research
    public int depleteAt {get; set;} // 0 = never depletes; >0 = deplete after this many uses
    // Maintenance opt-out. Set true on purely-structural nav pieces (platform, stairs, ladder)
    // so they never break — otherwise neglected infrastructure would cut mice off from the world.
    // All other structures with construction costs auto-opt-in (see Structure.NeedsMaintenance).
    public bool noMaintenance {get; set;}
    public float pathCostReduction {get; set;} // subtracted from edge cost for horizontal moves (roads: 0.1)
    public bool solidTop {get; set;} // can animals stand on top of this structure?
    // Lets sunlight through despite being solidTop — slatted / mostly-open structures (platforms)
    // and any future see-through floors. Excludes the structure from BlocksSun so it doesn't shade
    // plants below/around it. Greenhouses are excluded separately (isGreenhouse). See BlocksSun.
    public bool sunPermeable {get; set;}
    // Does this structure cast shade — block sunlight from reaching plants? Sun-blocking keys off
    // solidTop (roofs, floors, buildings) but skips greenhouses (glass) and sunPermeable structures
    // (slatted platforms). Read by World.BlocksSun for the plant sun-exposure raycast. Distinct from
    // the rain/overhead predicate (World.BlocksSky), which also counts blocksRain (tarps).
    public bool BlocksSun => solidTop && !isGreenhouse && !sunPermeable;
    // Decoupled rain-shelter flag. solidTop also blocks rain (a roofed-over tile is by
    // definition sheltered), but some structures want to block rain WITHOUT being walkable
    // on top — e.g. tarps. Authors set blocksRain=true on those. World.IsExposedAbove and
    // MoistureSystem.CapsSoilFromAbove honour either flag.
    public bool blocksRain {get; set;}
    // "Edge-supported" footprint: only the leftmost and rightmost columns of the bottom
    // row need standable support beneath them — the middle can hang in mid-air. Used by
    // tarps (cloth strung between two posts) where the middle is unsupported by design.
    // Affects placement validation (StructPlacement.CanPlaceHere requires both ends to
    // be standable, instead of just one body tile) AND blueprint suspension
    // (Blueprint.IsSuspended only checks the two ends, not every bottom tile).
    public bool edgeSupported {get; set;}
    public bool isWorkstation {get; set;} // true = registers a WOM Craft order when placed; use ConditionsMet() to gate it
    public int workTileX {get; set;} // tile offset to the interaction/nav tile (default 0,0 = anchor)
    public int workTileY {get; set;}
    public WorkTileOffset[] nworkTiles {get; set;} // multiple work positions (e.g. fireplace seats); populated from legacy workTileX/Y if absent

    // Optional off-grid worker pose. When both are set, Structure registers a Graph waypoint
    // at anchor + (mirrored)workSpotX, workSpotY and edges it to the nearest standable bottom-row
    // tile-node. Workers walk to and stand at the waypoint instead of the integer workTile center
    // — used by the mouse-wheel runner to stand centred between the 2x2 footprint columns,
    // slightly above ground. Null = use workTile center (today's behaviour).
    public float? workSpotX {get; set;}
    public float? workSpotY {get; set;}

    // Body pose the worker strikes while crafting at this building (mirrors leisurePose).
    // Read by WorkObjective.PoseOverride. AnimationController routes the special value
    // "walk" through the state Animator int (reusing the existing walk clip — no extra
    // Animator state needed). Other names map via PoseToInt. Null = default Working state.
    public string workPose {get; set;}
    // Facing-view the worker strikes while crafting here ("back"/"front"). Read by
    // WorkObjective.ViewOverride, mapped by AnimationController.ViewNameToFacing. Null =
    // default nav/state-driven (side) facing. See SPEC-rendering.md §Animal Paper-Doll.
    public string workView {get; set;}
    public int storageTileX {get; set;} // tile offset to the storage inventory tile (default 0,0 = anchor)
    public int storageTileY {get; set;}
    public TileRequirement[] tileRequirements {get; set;} // extra per-tile constraints checked at placement
    // Cached: true if `tileRequirements` declares `mustBeSolidTile` on the placement tile (dx=0, dy=0).
    // Populated in OnDeserialized so callers don't rescan the array. This flag is the single signal that
    // (a) StructPlacement bypasses its default "reject placement on non-empty tiles" rule, and
    // (b) StructController.Construct() mines the tile to empty after placement (alongside the
    // existing `requiredTileName` mining trigger). Used by mineshaft.
    public bool requiresSolidTilePlacement;

    // Placement requires an OPEN tile with a revealed background wall whose material group matches
    // `requiredTileName` (quarry → "stone", digging pit → "earth"). The structure digs the wall's
    // depth over time (WallQuarry) instead of mining a solid tile. Mutually exclusive with the
    // solid-tile occupancy path below — a wall structure sits on empty air, not in solid rock.
    public bool requiresBackgroundWall {get; set;}

    // True when this structure is built INTO a solid tile rather than resting on open ground — either it
    // targets a specific tile group (`requiredTileName`: burrow / mineshaft variants) or any solid tile
    // (`requiresSolidTilePlacement`: mineshaft). Such structures are self-supported by the tile they occupy,
    // so they're exempt from the "tile is not empty" placement rejection AND the generic bottom-row support
    // check. (Mining the tile out at build additionally requires `!preservesTile` — a burrow occupies but keeps it.)
    // Wall-quarry types opt OUT: they use `requiredTileName` to match the WALL group on an open tile, not to
    // occupy solid rock, so they must not take the solid-tile exemptions.
    public bool OccupiesSolidTile => !requiresBackgroundWall && (requiredTileName != null || requiresSolidTilePlacement);

    // Cached: true if `tileRequirements` declares `mustBeStandable` on any tile. Signals that the
    // author controls support explicitly (which columns must rest on something solid) — so the
    // generic bottom-row support check in StructPlacement / Blueprint.IsSuspended is skipped and
    // these reqs alone decide it. Used by the pump (only the building tile needs support; the
    // spout tile overhangs water). Without an explicit req, buildings default to all-columns-supported.
    public bool hasStandableRequirement;
    // Skip the `tile.type = empty` swap in StructController.Construct(). The footprint
    // tiles keep their original type — grass continues, snow accumulates, water still
    // blocked, tile-graph solidity unchanged — and the structure renders in front of
    // them as if it were a hole carved out. Yield path still fires: Blueprint.Complete
    // captures each footprint tile's tile.type.products into pendingOutput so the
    // player still receives the materials. Used by burrow (a hole into a dirt bank).
    // Authors set this on structures with `requiredTileName` or `requiresSolidTilePlacement`
    // that should preserve the underlying tile visually + physically.
    public bool preservesTile {get; set;}
    // Suppress the one-time mining yield at construction (Blueprint.Complete skips the
    // pendingOutput capture). For structures that extract the underlying tile's material
    // GRADUALLY through work instead — quarry, digging pit — where dumping the tile's full
    // products on completion would double up on what the worker mines out over time. The
    // burrow, by contrast, leaves this false: its dirt really is dug out at construction.
    public bool extractsTileOverTime {get; set;}
    // A 1-wide structure dug straight DOWN into solid ground (well). The bottom ny-1 tiles of
    // the chosen shape must be solid + mineable (the shaft); the TOP tile (dy=ny-1) must be an
    // OPEN surface tile — that's the wellhead a hauler walks up to and operates, rendered like a
    // normal surface building. requiresSolidTilePlacement only validates dx=0,dy=0, so this is
    // the variable-height generalisation StructPlacement enforces. OnDeserialized turns on
    // requiresSolidTilePlacement too, so the empty-tile / bottom-support exemptions and the
    // stone-mining tech gate all apply without authoring a per-dy tileRequirements entry.
    public bool digsSolidColumn {get; set;}
    // Cursor maps to the TOP tile, not the bottom anchor — so a downward-dug structure (well)
    // places its surface piece under the cursor and Q/E extends the shaft DOWNWARD. MouseController
    // offsets the anchor by -(ny-1) in Y; everything downstream still uses the bottom (dy=0) anchor.
    public bool anchorAtTop {get; set;}
    // The structure draws its own facade over its footprint tiles, so the surface grass/dirt overlay
    // must NOT render on them (it would draw at order 80, on top of the facade). Unlike the burrow —
    // which keeps grass growing over its dug top on purpose — a well's sky-exposed shaft mouth would
    // otherwise sprout grass over the shaft. Same effect roads get via their depth-3 layer.
    public bool suppressOverlay {get; set;}
    // Optional name of an additional Structure to place on the tile after Construct() completes.
    // Resolved via Db.structTypeByName at construction time. Used by structures that bundle a
    // follow-up structure with their placement (mineshaft → ladder). Null = no extra placement.
    public string placesStructureOnComplete {get; set;}
    // isBuilding: true = use Building class regardless of depth (default false = depth 0 uses Building; others don't).
    // Allows foreground/other-depth structures to have full Building features (fuelInv, uses, storage, etc.).
    public bool isBuilding {get; set;}
    // isGreenhouse: true = this structure is a climate frame plants grow inside. It lives at a non-zero
    // depth (foreground) so it never contests the plant's structs[0] slot; the footprint tiles back-point
    // to it via Tile.greenhouse. A plant whose anchor tile is greenhouse-covered grows in a regulated
    // climate (below), grows faster, draws less moisture, and is height-capped to the frame.
    // See Plant.Grow / SPEC-systems §Plant Growth.
    public bool isGreenhouse {get; set;}

    // ── Greenhouse climate tuning (only read when isGreenhouse) ───────────────
    // A plant rooted on a greenhouse-covered tile reads these off tile.greenhouse.structType.
    // Defaults model the starter greenhouse; a future larger/stronger greenhouse just overrides
    // them in JSON (e.g. greenhouseTempPull 1.0 for perfect regulation). All read in Plant.Grow
    // and MoistureSystem's per-plant draw.
    public float greenhouseTargetTempC {get; set;} = 25f;   // interior temp (°C) the frame pulls toward
    public float greenhouseTempPull {get; set;} = 0.5f;     // 0..1 fraction of (target − ambient) applied; 0.5 = halfway, imperfect
    public float greenhouseGrowthMult {get; set;} = 1.1f;   // growth-rate multiplier inside (+10%)
    public float greenhouseMoistureMult {get; set;} = 0.5f; // scales transpiration draw AND stage moisture cost (half)

    // The regulated temperature a greenhouse presents to a plant inside it: ambient pulled a
    // fraction of the way toward the target. Single source of truth shared by the growth gate
    // (Plant.Grow) and the InfoPanel comfort bar (StructureInfoView) so the two never drift.
    public float RegulatedTemp(float ambient) => ambient + (greenhouseTargetTempC - ambient) * greenhouseTempPull;
    // Fuel inventory: internal resource consumed over time (torch burns wood; foundry burns any fuel, etc.).
    // hasFuelInv = true → Building creates a fuelInv and WOM registers a standing supply order on placement.
    public bool hasFuelInv {get; set;}
    public string fuelItemName {get; set;} // OPTIONAL restriction: group/leaf name (e.g. "wood"); absent = accept any fuel (fuelValue>0)
    public Item fuelItem;                  // resolved from fuelItemName in OnDeserialized; null = any fuel
    public int fuelCapacity {get; set;}    // max stack size in fen (JSON in liang, converted in OnDeserialized); supply triggers below refill fraction of capacity
    public float fuelBurnRate {get; set;}  // ENERGY/day consumed; divided by the stocked fuel's fuelValue at burn (wood=1 → unchanged)
    // Fuel level (fraction of capacity) below which the supply order fires. Default 0.5; the foundry runs
    // it higher (its fueller is the busy smith, who also feeds + casts), so it refuels with more margin.
    public float fuelRefillFraction {get; set;} = 0.5f;

    // ── Processor ──────────────────────────────────────────────────────
    // A batch converter (see Processor.cs). hasProcessor=true → Building creates a Processor
    // component that runs the building's recipes (Recipe entries with tile==this.name and a
    // `duration`; see Db.GetProcessorRecipes). processorTended chooses how the Working phase
    // advances: false = UNTENDED (brewery — ferments passively over `duration` seconds, scaled
    // by ambient temperature, then a worker taps); true = TENDED (cauldron — a worker stands and
    // labours for `duration` seconds, then the batch auto-taps). The only processor data kept
    // here is footprint geometry (processorTileX/Y) — where the processor's inventory tile sits.
    public bool hasProcessor {get; set;}
    public bool processorTended {get; set;}  // true = worker-tended Working; false = passive ferment
    // The pot's liquid capacity in LIANG — sets how full the liquid renders (one batch against a
    // bigger pot reads partially full) and how much `output` can buffer. 0/omit → sized to one
    // batch (reads full when holding a batch). Authored in liang; ×100 → fen in the Processor ctor.
    public int processorCapacityLiang {get; set;}
    public int processorTileX {get; set;}  // tile offset of the processor's (or foundry's) inventory tile
    public int processorTileY {get; set;}

    // Foundry (melt pool, the dedicated `Foundry` Building subclass — NOT a Processor): the total
    // ore + molten capacity in LIANG (chunks awaiting melt + the molten pool). Authored in liang;
    // ×100 → fen in the Foundry ctor. See SPEC-systems §Foundry.
    public int foundryCapacityLiang {get; set;}

    // Furnishing slots: when set, Building creates a FurnishingSlots sub-component with one
    // slot inventory per name in `furnishingSlotNames`. Mice auto-haul matching items into
    // empty slots via WOM SupplyFurnishing orders; installed items grant happiness to residents.
    // See SPEC-systems.md and FurnishingSlots.cs.
    public bool hasFurnishingSlots {get; set;}
    public string[] furnishingSlotNames {get; set;}
    // Per-slot render offsets within the building footprint. Parallel to
    // furnishingSlotNames — entry i is where slot i's installed item draws.
    // Null/short → that slot defaults to (0,0) (the anchor tile). Authoring
    // these lets multi-tile housing place cloth/stool on different interior
    // tiles instead of stacked at the origin.
    public FurnishingSlotPos[] furnishingSlotPositions {get; set;}

    // ── Door + interior ───────────────────────────────────────────────
    // Housing (and future enterable production buildings) declare interior tiles and
    // doors. Structure registers one off-grid Node per interior tile, edges adjacent
    // interior nodes to each other, and edges each door's interior node to its
    // exterior approach tile's existing graph node. From there pathfinding routes
    // mice through doors naturally — Task code stays ignorant of door semantics.
    // Mirror handling lives in Structure (see Door / InteriorTile comments above).
    public Door[] doors {get; set;}
    public InteriorTile[] interiorTiles {get; set;}
    public Ladder[] ladders {get; set;}

    // Canonical "this building is a place mice live." Replaces the legacy hardcoded
    // `structType.name == "house"` check that's sprinkled across Animal AI, info panels,
    // and capacity queries. Set on every housing tier (house, shack, future burrow).
    public bool isHousing {get; set;}

    // Canonical "this building is a work flag" — a marker mice can be assigned to so it becomes
    // their work anchor (they gather and work around it instead of home). Drives the assignment
    // widget in the info panel and Animal.AssignToFlag validation. See plans/work-anchors-and-housing.
    public bool isWorkFlag {get; set;}

    // Decoration: nearby animals gain a happiness point when within decorRadius (Chebyshev) of this building.
    // A decoration with hasFuelInv=true only counts when its reservoir has fuel (e.g. fountain needs water).
    // decorationNeed identifies which happiness satisfaction this decoration targets (e.g. "fountain").
    public bool isDecoration {get; set;}
    public int decorRadius {get; set;}     // Chebyshev radius; 0 means not a decoration
    public string decorationNeed {get; set;}

    // Leisure: mice actively visit this building during leisure time (fireplace, tea house, etc.).
    // A leisure building with hasFuelInv=true only attracts mice when its reservoir has fuel.
    // leisureNeed identifies which happiness satisfaction this building targets (e.g. "fireplace").
    public bool isLeisure {get; set;}
    public string leisureNeed {get; set;}
    // Per-session leisure satisfaction multiplier (applied to Happiness.activityGrant when NoteLeisure fires).
    // Default 1.0. Lower values (e.g. 0.5 on benches) reflect buildings that are cheaper / always-on /
    // require less investment than premium leisure like fireplaces.
    public float leisureGrant {get; set;} = 1f;
    // Body pose an animal strikes while seated/working at this building (e.g. "sit" on a bench).
    // Mapped to an Animator int by AnimationController.PoseToInt. null = default state-driven animation.
    public string leisurePose {get; set;}
    public bool socialWhenShared {get; set;} // true = grant half social happiness to both mice when one finishes and another is still seated
    // Leisure-hours window: mice only use this building for leisure during
    // [activeStartHour, activeEndHour) (see Building.CanHostLeisureNow — e.g. the
    // fireplace is an evening/night warmth spot). Hours 0–24; end < start wraps
    // midnight (e.g. 16→6 = 4pm–6am). -1 = always available. Note: this is a fixed
    // CLOCK window and does NOT gate light sources — sun-modulated lights track
    // dusk independently (LightSource.UpdateLitState).
    public float activeStartHour {get; set;} = -1f;
    public float activeEndHour   {get; set;}

    // Light source: building emits point light and passively burns fuel while torchFactor > 0.
    // lightIntensity is the baseIntensity passed to LightSource (default 0.80).
    // lightOuterRadius is the radial reach in world units (default 10). Smaller =
    // tighter lit area and ~quadratic GPU savings on the light-circle pass.
    // Enclosed interior building (burrow, dug-in housing): its sprites — and any mouse
    // standing inside — render on the Interior layer (sun + ambient only, no point/torch
    // light), so torchlight from above doesn't bleed into the buried interior. The mouse
    // swap is driven by Animal.insideBuilding. See InteriorLayer / LightFeature.
    public bool enclosed {get; set;}

    public bool isLightSource {get; set;}
    // When true, the building emits a craft-gated light + fire (LightSource.craftGated): lit only
    // while a mouse is actively crafting here, day or night, with no fuel. Reuses the light* fields
    // below. Cauldron uses it; foundry/crucible can opt in. Distinct from isLightSource (fuel + night).
    public bool lightWhileCrafting {get; set;}
    public float lightIntensity {get; set;}
    public float lightOuterRadius {get; set;}
    public float lightInnerRadius {get; set;} = 4f; // flat-bright core radius; falloff ramps innerRadius → outerRadius
    // 0 = raw NdotL hot-spot under the light; 1 = flat-center fill that still respects normals.
    // Maps to LightSource.centerFlatten. See LightCircle.shader.
    public float lightCenterFlatten {get; set;} = 0f;

    // Subtle intensity flicker for fire lights (torches, fireplaces). Amplitude as a fraction of
    // intensity — 0 = steady, 0.06 = ±6%. Applied in LightSource.Update via a cheap per-instance
    // Perlin sample (phase from position so neighbours don't pulse in unison). 0 by default.
    public float lightFlicker {get; set;}

    // Per-building self-emission multiplier (the bright pixels of the body/_e glow), independent of
    // the radial point light. 1 = full (default); <1 dims the glow without touching the light reach.
    // Lets a soft-glow building (lantern) read dimmer than a hot flame (torch) on the same global
    // _EmissionStrength. Feeds LightSource.emissionMult → LightFeature's per-emitter _EmissionScale.
    public float emissionStrength {get; set;} = 1f;

    // ── Fire art ──────────────────────────────────────────────────────
    // Toggleable flame child sprite (Structure ctor) for light-emitting buildings. The art is a
    // small flame, positioned at the wick end via the offset below rather than baked into the
    // building sprite — so one shared sheet serves both the floor and wall-mounted torch.
    //   fireSprite : flame sheet name (default "{name}_f"). Set to share a sheet, e.g. the side
    //                torch points at "torch_f". A multi-frame sliced sheet animates (FrameAnimator);
    //                the sheet self-references as its own emission map (atlas-safe — see Structure.cs).
    //   fireOffsetX/Y : flame child local offset in tile units from the structure origin (tile
    //                centre). X is mirror-flipped so a wall torch's flame tracks the leaning arm.
    //   fireFps    : animation speed for a multi-frame sheet (default 7).
    public string fireSprite {get; set;}
    public float fireOffsetX {get; set;}
    public float fireOffsetY {get; set;}
    public float fireFps {get; set;}
    //   emberRate : rising-spark emission rate (sparks/sec at full glow) for EmberManager.
    //               0 = no embers (default). Sparks only appear while the fire is lit (night),
    //               scaled by emission glow and the player's particle-density setting.
    //   emberOffsetX/Y : spawn point of the embers relative to the fire child's transform
    //               (tile units), set to the flame's painted centre so sparks rise from the
    //               flame. X is mirror-flipped with the fire sprite's flipX (side torches).
    //   emberSpreadMult : multiplies the horizontal spawn spread — wide fires (the
    //               fireplace) scatter sparks across a broader strip than a torch. Default 1.
    public float emberRate {get; set;}
    public float emberOffsetX {get; set;}
    public float emberOffsetY {get; set;}
    public float emberSpreadMult {get; set;} = 1f;

    // ── Mechanical power ──────────────────────────────────────────────
    // powerBoost > 1.0 turns this StructType into a power consumer at runtime: when the
    // built instance has its (single) port connected to a powered network, the operator's
    // craft work-tick rate multiplies by this value. Default 1.0 means "not power-aware".
    // Implementation: AnimalStateManager.HandleWorking gates on PowerSystem.IsBuildingPowered.
    // The consumer registration is auto-wired from Building.OnPlaced. See SPEC-power.md.
    public float powerBoost {get; set;} = 1f;

    // When true, the player can press R during placement to cycle through 4 rotation states
    // (0/90/180/270 clockwise). The rotation persists on the placed structure / blueprint.
    // For v1, intended for 1×1 buildings only — rotating a multi-tile sprite would break
    // tile-occupancy and placement math (footprint stays nx×ny). Rotation is purely visual
    // for most types; PowerShaft additionally derives its connectivity axis from rotation.
    public bool rotatable {get; set;}

    // How this structure attaches instead of resting on the ground. Side = against a vertical
    // wall face (terrain or a building's body), player picks the side via the mirror toggle (F);
    // Ceiling = hangs from the solid underside of the tile above. Both route through the
    // mount-attach branch in StructPlacement and skip the generic standability check. Default
    // None = ground-supported. Used by ladder_side / bracket / torch_side (Side) and lantern (Ceiling).
    public MountTo mountTo {get; set;} = MountTo.None;
    public bool isMounted => mountTo != MountTo.None;          // hangs on a surface, not the ground
    public bool sideMounted => mountTo == MountTo.Side;        // convenience for the many side-only sites

    // Name of the side-mounted variant this build tool resolves to when the cursor hovers near
    // a tile edge during placement (e.g. "ladder" → "ladder_side", "torch" → "torch_side").
    // The tool still shows/charges THIS type; only the placed structure swaps. Null = no side
    // variant (always placed centred). Resolved via Db.structTypeByName in BuildPanel.ResolveSideVariant.
    public string sideVariant {get; set;}

    // ── Two-click placement (rope bridge) ─────────────────────────────
    // When `placementMethod == "twoClick"`, the player clicks two tiles to define
    // a span — the first click drops the post here, the second click drops the
    // partner post at the second tile and links them via a `RopeBridgeSystem`
    // entity (waypoint chain + visuals). Costs scale linearly with horizontal
    // delta: each post pays `ncosts × (dx / minDx)`. minDx / maxDx clamp the
    // bridge length and maxDy clamps the vertical drop. See SPEC-systems.md
    // §Rope bridges and `Catenary.cs` for the curve math.
    public string placementMethod {get; set;}
    public int minDx {get; set;}
    public int maxDx {get; set;}
    public int maxDy {get; set;}
    public float sagFraction {get; set;}

    // Variable-shape variants. When non-null, the player cycles them with Q/E during
    // placement. shapes[0] is the "authored" baseline — `costs` are sized for it, and
    // larger shapes scale linearly with tile count. Null = single fixed shape (nx,ny).
    // See Shape comment above. Sprite composition for vertical shapes uses
    // `{name}_b` (anchor), `{name}_m` (middles), `{name}_t` (top) — falling back
    // to the base sprite if any are missing.
    public Shape[] shapes {get; set;}
    public bool HasShapes => shapes != null && shapes.Length > 0;
    // Returns the chosen shape, clamped to a valid index. Falls back to a Shape mirroring
    // the StructType's base nx/ny when `shapes` is unset, so callers don't need to branch.
    public Shape GetShape(int index) {
        if (!HasShapes) return new Shape { nx = nx, ny = ny };
        if (index < 0) index = 0;
        if (index >= shapes.Length) index = shapes.Length - 1;
        return shapes[index];
    }

    public virtual Sprite LoadSprite() {
        if (isTile) {
            if (name == "empty") return null;
            // Tile art lives only in the baked TileSpriteCache (32px sheets under
            // Sprites/Tiles/Sheets/), not as a flat sprite — so pull the representative
            // fully-surrounded block (cardinal mask 0xF = no rim) for the blueprint /
            // build-ghost / menu icon. SpriteStem resolves "<base>_placed" to the base art.
            // Returns null if the cache has no sheet for the stem → caller falls back to default.
            return TileSpriteCache.Get(TileType.SpriteStem(name), 0xF, 0, 0);
        }
        string path = "Sprites/Buildings/" + name.Replace(" ", "");
        Sprite s2 = Resources.Load<Sprite>(path);
        if (s2 != null && s2.texture != null) return s2;
        // Multi-sliced sprite-sheet fallback: Resources.Load<Sprite> returns null on a
        // sheet imported in "Multiple" mode (it can't pick a single sub-sprite). Fall
        // back to the first sliced frame so animated buildings still have a static
        // default before FrameAnimator takes over at Update time.
        Sprite[] all = Resources.LoadAll<Sprite>(path);
        if (all != null && all.Length > 0 && all[0].texture != null) return all[0];
        // Shape-only buildings (e.g. well) ship art solely as a sliced "{name}_s" sheet with
        // no flat "{name}.png" — the in-world render assembles it via LoadShapeSprite, but a
        // flat icon/preview still wants a representative frame. Prefer the "_b" anchor slice.
        Sprite[] sheet = Resources.LoadAll<Sprite>(path + "_s");
        if (sheet != null && sheet.Length > 0) {
            string anchor = name.Replace(" ", "") + "_b";
            foreach (Sprite sp in sheet) if (sp != null && sp.name == anchor) return sp;
            return sheet[0];
        }
        return null;
    }

    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        if (nx == 0){ nx = 1; }
        if (ny == 0){ ny = 1; }
        if (storageStackSize > 0){ storageStackSize *= 100; } // convert liang → fen
        costs = new ItemQuantity[ncosts.Length];
        for (int i = 0; i < ncosts.Length; i++){
            costs[i] = new ItemQuantity(ncosts[i]);
        }
        if (njob != null){
            job = Db.jobByName[njob];
        } else {
            job = Db.jobByName["hauler"]; // default if no njob provided
        }
        if ((isLightSource || lightWhileCrafting) && lightIntensity == 0f) lightIntensity = 0.80f;
        if ((isLightSource || lightWhileCrafting) && lightOuterRadius == 0f) lightOuterRadius = 10f;
        if (nworkTiles == null || nworkTiles.Length == 0)
            nworkTiles = new[] { new WorkTileOffset { dx = workTileX, dy = workTileY } };
        // Fuel inventory: convert liang → fen; resolve fuel item reference.
        if (hasFuelInv) {
            if (fuelCapacity > 0) fuelCapacity = ItemStack.LiangToFen(fuelCapacity);
            // fuelItemName is OPTIONAL: present = restrict to that item (group or leaf);
            // absent = accept ANY fuel (any fuelValue>0 item), chosen at refuel via PickFuel.
            // Only error on a name that's specified-but-unknown (typo guard).
            if (!string.IsNullOrEmpty(fuelItemName)) {
                if (Db.itemByName.TryGetValue(fuelItemName, out Item fi)) fuelItem = fi;
                else Debug.LogError($"StructType '{name}': fuelItemName '{fuelItemName}' not found in Db");
            }
        }
        // Cache placement-tile solid requirement so StructPlacement / StructController don't rescan.
        if (tileRequirements != null) {
            for (int i = 0; i < tileRequirements.Length; i++) {
                TileRequirement r = tileRequirements[i];
                if (r.dx == 0 && r.dy == 0 && r.mustBeSolidTile) requiresSolidTilePlacement = true;
                if (r.mustBeStandable) hasStandableRequirement = true;
            }
        }
        // A column-digging structure occupies solid tiles, so reuse the same placement
        // exemptions + mining tech gate as a single-tile solid placement (mineshaft). The
        // per-column solidity is validated separately in StructPlacement.
        if (digsSolidColumn) requiresSolidTilePlacement = true;
    }

    // ── Sprite body-edge solidity masks (mount support) ───────────────────
    // Per-footprint-tile bitmasks (bit dy*nx+dx) of which tiles present body on their left /
    // right / bottom edge — so a mount won't attach against a visually-empty footprint tile
    // (e.g. a windmill's blade tiles, claimed in tile.structs[] but with no sprite body). Baked
    // offline from sprite alpha by Tools/Bake Building Edge Solidity and applied at Db load via
    // SetEdgeMasks. -1 = unbaked → permissive (treat the edge as solid) so we never wrongly reject
    // a mount before the bake has run. Authored un-mirrored; EdgeSolid applies mirror. Left/Right
    // feed side-mounts; Bottom feeds ceiling-mounts. (No top mask — see solidTop.)
    int leftEdgeMask   = -1;
    int rightEdgeMask  = -1;
    int bottomEdgeMask = -1;
    public void SetEdgeMasks(int left, int right, int bottom) {
        leftEdgeMask = left; rightEdgeMask = right; bottomEdgeMask = bottom;
    }

    // True if footprint tile (dx,dy) presents body on the given face — i.e. a mount can attach
    // there. structMirrored flips both the column lookup and (for Left/Right) which authored side
    // we read; Bottom is mirror-invariant. Out-of-range or unbaked → permissive (true).
    public bool EdgeSolid(int dx, int dy, MountFace face, bool structMirrored) {
        int lx = structMirrored ? (nx - 1 - dx) : dx;
        if (lx < 0 || lx >= nx || dy < 0 || dy >= ny) return true;
        int mask;
        if (face == MountFace.Bottom) {
            mask = bottomEdgeMask;
        } else {
            bool wantRight = face == MountFace.Right;
            bool readRight = structMirrored ? !wantRight : wantRight;
            mask = readRight ? rightEdgeMask : leftEdgeMask;
        }
        if (mask < 0) return true;                           // unbaked → permissive
        return ((mask >> (dy * nx + lx)) & 1) != 0;
    }
}

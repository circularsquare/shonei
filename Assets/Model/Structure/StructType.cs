using UnityEngine;
using System.Runtime.Serialization;

// JSON schema for a structure kind (building, plant, tile, road, etc.). Loaded by
// Db.cs on startup from buildingsDb.json / plantsDb.json. Runtime Structure instances
// hold a reference to their StructType. Quantities are authored in liang (float) and
// converted to fen (int) in OnDeserialized.

// Work tile offset: a position within a multi-tile building where an animal can stand to interact.
// Used by nworkTiles[] on StructType. Mirroring is applied at runtime by Structure.WorkTileAt().
public class WorkTileOffset {
    public int dx {get; set;}
    public int dy {get; set;}
}

public class TileRequirement {
    public int dx {get; set;}
    public int dy {get; set;}
    public bool mustBeStandable {get; set;}
    public bool mustHaveWater {get; set;}    // tile.water > 0
    public bool mustBeEmpty {get; set;}      // structs[0] (building layer) must be null
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
}

public class StructType {
    public int id {get; set;}
    public string name {get; set;}
    public int nx {get; set;}
    public int ny {get; set;}
    public ItemNameQuantity[] ncosts {get; set;}
    public ItemQuantity[] costs;
    public float constructionCost {get; set;}
    public bool isTile {get; set;}
    public bool isPlant;
    public int depth {get; set;} // 0=building, 1=platform, 2=foreground, 3=road
    // Optional sprite sortingOrder override. -1 = use depth-based default (see Structure ctor).
    // Used for e.g. torches/fireplaces that need to sort above plants (60) and animals (48-57) so
    // LightSource's auto-detected sort bucket front-lights those receivers. Also changes draw order.
    public int sortingOrder {get; set;} = -1;
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
    public int storageTileX {get; set;} // tile offset to the storage inventory tile (default 0,0 = anchor)
    public int storageTileY {get; set;}
    public TileRequirement[] tileRequirements {get; set;} // extra per-tile constraints checked at placement
    // isBuilding: true = use Building class regardless of depth (default false = depth 0 uses Building; others don't).
    // Allows foreground/other-depth structures to have full Building features (fuelInv, uses, storage, etc.).
    public bool isBuilding {get; set;}
    // Fuel inventory: internal resource consumed over time (torch burns wood; furnace burns coal, etc.).
    // hasFuelInv = true → Building creates a fuelInv and WOM registers a standing supply order on placement.
    public bool hasFuelInv {get; set;}
    public string fuelItemName {get; set;} // group or leaf item name (e.g. "wood"); resolved to fuelItem on load
    public Item fuelItem;                  // resolved from fuelItemName in OnDeserialized
    public int fuelCapacity {get; set;}    // max stack size in fen (JSON in liang, converted in OnDeserialized); supply triggers below half capacity
    public float fuelBurnRate {get; set;}  // liang/day consumed; LightSource converts to fen/s at runtime

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
    // When activeStartHour >= 0, this building is only active during [activeStartHour, activeEndHour).
    // Hours 0–24; end < start wraps midnight (e.g. 16→6 = 4pm–6am). -1 = always active.
    public float activeStartHour {get; set;} = -1f;
    public float activeEndHour   {get; set;}

    // Light source: building emits point light and passively burns fuel while torchFactor > 0.
    // lightIntensity is the baseIntensity passed to LightSource (default 0.80).
    public bool isLightSource {get; set;}
    public float lightIntensity {get; set;}

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
            Sprite s = Resources.Load<Sprite>("Sprites/Tiles/" + name.Replace(" ", ""));
            return s != null && s.texture != null ? s : null;
        }
        string path = "Sprites/Buildings/" + name.Replace(" ", "");
        Sprite s2 = Resources.Load<Sprite>(path);
        if (s2 != null && s2.texture != null) return s2;
        // Multi-sliced sprite-sheet fallback: Resources.Load<Sprite> returns null on a
        // sheet imported in "Multiple" mode (it can't pick a single sub-sprite). Fall
        // back to the first sliced frame so animated buildings still have a static
        // default before FrameAnimator takes over at Update time.
        Sprite[] all = Resources.LoadAll<Sprite>(path);
        return (all != null && all.Length > 0 && all[0].texture != null) ? all[0] : null;
    }

    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        if (nx == 0){ nx = 1; }
        if (ny == 0){ ny = 1; }
        if (storageStackSize > 0){ storageStackSize *= 100; } // convert liang → fen
        costs = new ItemQuantity[ncosts.Length];
        for (int i = 0; i < ncosts.Length; i++){
            costs[i] = new ItemQuantity(ncosts[i].name, ItemStack.LiangToFen(ncosts[i].quantity));
        }
        if (njob != null){
            job = Db.jobByName[njob];
        } else {
            job = Db.jobByName["hauler"]; // default if no njob provided
        }
        if (isLightSource && lightIntensity == 0f) lightIntensity = 0.80f;
        if (nworkTiles == null || nworkTiles.Length == 0)
            nworkTiles = new[] { new WorkTileOffset { dx = workTileX, dy = workTileY } };
        // Fuel inventory: convert liang → fen; resolve fuel item reference.
        if (hasFuelInv) {
            if (fuelCapacity > 0) fuelCapacity = ItemStack.LiangToFen(fuelCapacity);
            if (fuelItemName != null && Db.itemByName.ContainsKey(fuelItemName))
                fuelItem = Db.itemByName[fuelItemName];
            else
                Debug.LogError($"StructType '{name}': hasFuelInv=true but fuelItemName '{fuelItemName}' not found in Db");
        }
    }
}

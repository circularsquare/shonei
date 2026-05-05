using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;
using System.Runtime.Serialization;


public class Structure {
    // ── Maintenance constants ──────────────────────────────────────────
    // Condition is a 0-1 float tracked on every non-plant structure with construction costs.
    // Below BreakThreshold the structure is non-functional (craft halts, road bonus lost,
    // light sources go dark, etc.); below RegisterThreshold a WOM Maintenance order becomes
    // active so menders will come repair it. A single visit restores up to MaxRepairPerTask.
    public const float BreakThreshold     = 0.5f;   // below → IsBroken, functionality gated off
    public const float RegisterThreshold  = 0.75f;  // below → WOM order active, mender may come
    public const float MaxRepairPerTask   = 0.40f;  // cap on condition restored in one mender visit
    public const float RepairWorkPerTick  = 0.05f;  // base condition gained per tick while working
    public const float RepairCostFraction = 0.25f;  // full 0→1 repair = ¼ of construction cost
    public const int   DaysToBreak        = 30;     // in-game days from 1.0 → BreakThreshold (0.5)

    public GameObject go;
    public int x;
    public int y;
    public StructType structType;
    public Sprite sprite;
    public SpriteRenderer sr;

    // ── Maintenance state ──────────────────────────────────────────────
    // 0.0 = fully broken, 1.0 = pristine. Decays in MaintenanceSystem.Tick(); restored by menders.
    // Persisted via StructureSaveData.condition. Default 1.0 also covers old saves.
    public float condition = 1.0f;

    // Opt-in gate: plants, nav-only structures (platform/stairs/ladder, via noMaintenance JSON flag),
    // and zero-cost structures are excluded from the maintenance system entirely.
    public bool NeedsMaintenance =>
        !structType.noMaintenance
        && !structType.isPlant
        && structType.ncosts != null
        && structType.ncosts.Length > 0;

    // Non-functional when broken. Gates craft/research/supply orders, road bonuses, light emission,
    // decoration happiness, leisure seat availability, and house sleep assignment.
    public bool IsBroken => NeedsMaintenance && condition < BreakThreshold;

    // WOM Maintenance order's isActive lambda reads this — order goes idle once repaired past 75%.
    public bool WantsMaintenance => NeedsMaintenance && condition < RegisterThreshold;

    // Used by Navigation.cs and ModifierSystem.cs for road speed bonus — returns 0 when broken
    // so neglected roads stop giving a path-cost discount / movement bonus.
    public float EffectivePathCostReduction => IsBroken ? 0f : structType.pathCostReduction;

    // The project-default material this SpriteRenderer was created with (URP 2D's
    // Sprite-Lit-Default, which carries the `LightMode = Universal2D` tag needed for
    // the NormalsCapture render-layer filter). Captured once in RefreshTint before
    // we ever swap to the cracked material, so we can restore it verbatim on repair
    // without hardcoding a shader name. Restoring via `Shader.Find("Sprites/Default")`
    // would silently substitute the *legacy* sprite shader and drop the structure out
    // of the lighting pipeline (no ambient, no sun). See SPEC-rendering.md §Lighting.
    Material defaultMat;

    // Applies a multiplicative tint colour to every spawned SR — anchor,
    // shape-aware extensions, AND custom-visual children (tarp's cloth + posts).
    // Used by Blueprint.RefreshColor for the deconstruct overlay so the whole
    // structure glows red, not just the anchor. The flat `tintableSrs` list is
    // populated by StructureVisualBuilder.Build so this method doesn't need to
    // know about subclass-specific child arrangements. Safe to call with
    // Color.white to clear. Independent of RefreshTint's material swap, so
    // broken + deconstructing composites correctly.
    public void SetTint(Color c) {
        if (tintableSrs == null) return;
        for (int i = 0; i < tintableSrs.Length; i++)
            if (tintableSrs[i] != null) tintableSrs[i].color = c;
    }

    // Re-applies the sprite material based on broken state. Called from
    // MaintenanceSystem on threshold crossings. Broken structures swap to the shared
    // CrackedSprite material which composites a tileable crack texture on top of the
    // base sprite, alpha-masked by the sprite so cracks only appear on visible pixels.
    // That shader is URP 2D-tagged so the NormalsCapture pipeline still picks up the
    // renderer (preserving ambient/sun/torch lighting on broken buildings).
    // Deconstruct blueprints override tint via SetTint in Blueprint.cs — independent
    // of the material swap, so broken + deconstructing composites correctly.
    public virtual void RefreshTint() {
        if (sr == null) return;
        if (defaultMat == null) defaultMat = sr.sharedMaterial;
        sr.sharedMaterial = IsBroken ? (GetCrackedMaterial() ?? defaultMat) : defaultMat;
    }

    // Loaded once per process: the material that drives the cracked look. See
    // Assets/Resources/Materials/CrackedSprite.mat. Null if the asset is missing —
    // callers fall back to the captured default material rather than crashing.
    static Material _crackedMaterialCache;
    static bool     _crackedMaterialProbed;
    static Material GetCrackedMaterial() {
        if (_crackedMaterialProbed) return _crackedMaterialCache;
        _crackedMaterialProbed = true;
        _crackedMaterialCache = Resources.Load<Material>("Materials/CrackedSprite");
        if (_crackedMaterialCache == null)
            Debug.LogWarning("CrackedSprite material not found at Resources/Materials/CrackedSprite — broken structures will render untinted.");
        return _crackedMaterialCache;
    }
    public Tile tile => World.instance.GetTileAt(x, y);
    public Tile workTile => World.instance.GetTileAt(
        x + (mirrored ? (structType.nx - 1 - structType.workTileX) : structType.workTileX),
        y + structType.workTileY);

    // Path target for tasks that walk to this structure to work (CraftTask, FindPathToStruct,
    // CanReachBuilding). For most structures this equals workTile.node — the worker stands on
    // the integer tile. For structures whose StructType declares workSpotX/Y, a float-coord
    // waypoint Node is created in the Graph at construction time and edged to the nearest
    // standable bottom-row tile-node; workNode points at that waypoint so paths end off-grid.
    // Set in Structure ctor; cleaned up in Destroy(). Mirroring uses the same nx-1-x formula
    // as workTile, which works identically for floats.
    public Node workNode { get; private set; }

    public Reservable res;
    // Per-seat reservables for leisure buildings. Each work tile gets its own Reservable(1)
    // so two mice won't path to the same seat. Null for non-leisure buildings.
    public Reservable[] seatRes;

    // Returns true if any seat reservable is available. Only meaningful for leisure buildings.
    public bool AnySeatAvailable() {
        if (seatRes == null) return false;
        for (int i = 0; i < seatRes.Length; i++)
            if (seatRes[i].Available()) return true;
        return false;
    }

    // Returns the tile for a specific work tile index (from structType.nworkTiles), with mirroring applied.
    public Tile WorkTileAt(int index) {
        var wt = structType.nworkTiles[index];
        return World.instance.GetTileAt(
            x + (mirrored ? (structType.nx - 1 - wt.dx) : wt.dx),
            y + wt.dy);
    }

    // Fire art child GO — a separate SpriteRenderer showing the fire/flame portion of
    // the building sprite. Loaded from `{name}_f.png` companion. LightSource toggles
    // visibility based on isLit so fire disappears when the light is off.
    // Null for buildings without a fire art companion.
    public GameObject fireGO;
    public SpriteRenderer fireSR;

    // Local pixel offsets (bottom-left origin, unmirrored) of the structure's water zone —
    // the opaque pixels of its `{name}_w.png` companion mask. Null when no companion exists.
    // Registered with WaterController by StructController.Place().
    public List<Vector2Int> waterPixelOffsets { get; private set; }

    // "World conditions allow this structure to be worked on" gate for Structure subclasses.
    // Returns false to suppress the WOM craft order without removing it. Combined with
    // `Building.disabled` at the call site as `!disabled && ConditionsMet()`.
    // Override in subclasses for runtime conditions (e.g. PumpBuilding requires water below).
    // Blueprint mirrors this method by convention (it's a sibling class, not a Structure subclass).
    public virtual bool ConditionsMet() => true;

    // Called by StructController after Place(). Override to register WOM orders or other post-placement setup.
    // Not called during load or world generation — Reconcile() handles order registration on both paths.
    public virtual void OnPlaced() { }

    // Called once from the Structure constructor (after the SpriteRenderer is set up)
    // for both gameplay and load paths. Override to attach FrameAnimator(s) or any other
    // visual-only behaviour. Default is no-op. Use AttachFrameAnimator() for the standard
    // sliced-sheet pattern.
    public virtual void AttachAnimations() { }

    // Per-tile standability override for structures that have walkable surfaces inside
    // their footprint that the default solidTop rule can't express. localDx/localDy are
    // world-relative offsets from the structure's anchor (this.x, this.y) — i.e. what
    // Navigation.GetStandability passes in (`x - s.x, y - s.y`). The base impl looks up
    // the current Shape.standableOffsets[] (JSON-driven, un-mirrored coordinates) and
    // applies mirroring to the input before comparing — see StandableOffset's comment.
    // Default null offsets → false; standability falls through to the normal solidTop /
    // support-from-below rules in Navigation.GetStandability.
    //
    // Subclasses that override this typically have a fixed structural rule (e.g. Elevator
    // exposes top + bottom of a 1-wide column) and don't need to call base. JSON-driven
    // patterns are for non-subclassed structures.
    public virtual bool HasInternalFloorAt(int localDx, int localDy) {
        var offs = Shape.standableOffsets;
        if (offs == null || offs.Length == 0) return false;
        // Author offsets are un-mirrored. For a mirrored placement, flip the input dx
        // back to the authored frame before comparing. Mirroring of dy is a no-op (we
        // only flip horizontally — see Structure.mirrored).
        int authoredDx = mirrored ? (Shape.nx - 1 - localDx) : localDx;
        for (int i = 0; i < offs.Length; i++) {
            if (offs[i].dx == authoredDx && offs[i].dy == localDy) return true;
        }
        return false;
    }

    // Helper for the common "load a sliced sheet, attach a FrameAnimator" pattern. Returns
    // null (and adds nothing) when the sheet has only a single sprite — animation is opt-in
    // per asset, so an unsliced PNG just keeps rendering as the static sprite assigned in
    // the constructor.
    protected FrameAnimator AttachFrameAnimator(string spriteName, Func<bool> isActive,
                                                float baseFps = 8f, Func<float> speedMul = null) {
        Sprite[] frames = Resources.LoadAll<Sprite>("Sprites/Buildings/" + spriteName);
        if (frames == null || frames.Length <= 1) return null;
        FrameAnimator anim = go.AddComponent<FrameAnimator>();
        anim.frames = frames;
        anim.baseFps = baseFps;
        anim.isActive = isActive;
        anim.speedMultiplier = speedMul;
        return anim;
    }

    // Helper for power producers/consumers/storage to spawn conditional shaft-stub
    // child SpriteRenderers at each port offset. Stubs render behind the building and
    // toggle visibility based on whether a compatible shaft is wired up at the port —
    // see PortStubVisuals for the full contract. Subclasses call this from
    // AttachAnimations(), passing their own Ports collection.
    protected void AttachPortStubs(IEnumerable<PowerSystem.PowerPort> ports) {
        if (ports == null) return;
        var stubs = go.AddComponent<PortStubVisuals>();
        stubs.Init(this, ports);
    }

    // Whether this structure is horizontally mirrored (flipped left-right).
    // Affects sprite flipX, workTile/storageTile offsets, tileRequirement offsets, and stair pathfinding.
    public bool mirrored = false;

    // Rotation in 90° clockwise steps (0..3). Set during placement when StructType.rotatable
    // is true; persisted via StructureSaveData. Currently only PowerShaft uses this beyond
    // pure visual rotation — it derives its connectivity axis from (name, rotation).
    public int rotation = 0;

    // Variable-shape index. Selects which entry of `structType.shapes` this instance uses.
    // 0 (default) means "first authored shape" or, when shapes is null, the StructType's
    // base nx/ny. Set during placement (Q/E in build mode) and persisted in save data.
    // Drives the multi-tile footprint claim and per-tile sprite composition for shape-aware
    // structures — see Shape POCO in StructType.cs.
    public int shapeIndex = 0;
    public Shape Shape => structType.GetShape(shapeIndex);

    // Per-tile child SRs spawned for shape-aware multi-tile structures (v1: vertical only,
    // shape.nx==1, shape.ny>1). Index 0 is the tile directly above the anchor; the topmost
    // is at index ny-2. The anchor tile renders through `sr` itself. Released by Destroy()
    // via cascading GameObject teardown when `go` is destroyed.
    private SpriteRenderer[] extensionSrs;

    // Flattened union of every spawned SR (main + extensions + custom-visual children).
    // Walked by SetTint so the deconstruct overlay reaches all renderers without needing
    // per-subclass knowledge of where the children live. Initially populated by
    // StructureVisualBuilder.Build during construction; subclasses with dynamic visual
    // children (e.g. Plant's growth-stage extensions) keep it in sync as their visual
    // changes — see Plant.RefreshTintableSrs().
    protected SpriteRenderer[] tintableSrs;

    public Structure(StructType st, int x, int y, bool mirrored = false, int rotation = 0, int shapeIndex = 0){
        this.structType = st;
        this.x = x;
        this.y = y;
        this.mirrored = mirrored;
        this.rotation = rotation;
        this.shapeIndex = shapeIndex;

        Shape shape = Shape;
        bool shapeAware = st.HasShapes;

        go = new GameObject();
        // Shape-aware structures anchor at (x, y) so per-tile child SRs at local (0, dy)
        // line up with their tile's centre. Legacy multi-tile (windmill etc.) renders one
        // big sprite with a centred pivot — keep the centred-position helper for those.
        go.transform.position = shapeAware
            ? new Vector3(x, y + (st.depth == 3 ? -1f/8f : 0f), 0f)
            : StructureVisuals.PositionFor(st, x, y);
        go.transform.SetParent(StructController.instance.transform, true);
        go.name = "structure_" + structType.name;

        // Sort order by depth: 0=building(10), 1=platform(15), 2=foreground(40), 3=road(1), 4=shaft(5).
        // Slot index ≠ visual layering — shafts are slot 4 but render behind buildings via
        // sortingOrder 5 (between roads at 1 and buildings at 10).
        // StructType.sortingOrder overrides this when >= 0 (e.g. light-source buildings at 64).
        int baseSortingOrder = ResolveBaseSortingOrder(st);

        // Spawn the primary visual via the shared builder. Standard path resolves the
        // anchor sprite + optional vertical extensions; custom-visual types (tarp) take
        // a per-name branch in Build that spawns cloth/posts/etc. instead. Caller stores
        // back the references it cares about — sr (anchor SR), extensionSrs (for shape-
        // aware vertical Plant subclasses to extend), and tintableSrs (walked by SetTint).
        var refs = StructureVisualBuilder.Build(go, st, shape, mirrored, rotation, baseSortingOrder, Color.white);
        sr = refs.mainSr;
        sprite = sr.sprite;            // null for custom-visual types — fine; WaterController.ScanWaterPixels handles null
        extensionSrs = refs.extensionSrs;
        tintableSrs = refs.tintableSrs;

        // Workstations don't use Structure.res — their WOM Craft order owns the reservation.
        // Leisure buildings use per-seat seatRes[] instead of a single res.
        if (structType.isLeisure && structType.capacity > 0) {
            res = null;
            seatRes = new Reservable[structType.nworkTiles.Length];
            for (int i = 0; i < seatRes.Length; i++)
                seatRes[i] = new Reservable(1);
        } else {
            res = (structType.capacity > 0 && !structType.isWorkstation) ? new Reservable(structType.capacity) : null;
        }

        if (structType.name == "clock") {
            var ch = go.AddComponent<ClockHand>();
            ch.structure = this;
        }

        // Register on tiles at the appropriate depth layer. Every tile in the visual
        // footprint claims the structure — so a 2×4 windmill writes itself to all 8 tiles
        // and a 1×3 platform writes to all 3 tiles. This keeps tile→structure lookup
        // (selection, collision, tile.building) symmetric with the rendered footprint.
        // Mathf.Max(1, st.ny) guards against StructTypes that omit ny (default 0).
        int depth = st.depth;
        int claimNx = shapeAware ? shape.nx : st.nx;
        int claimNy = shapeAware ? shape.ny : Mathf.Max(1, st.ny);
        for (int dy = 0; dy < claimNy; dy++) {
            for (int dx = 0; dx < claimNx; dx++) {
                Tile t = World.instance.GetTileAt(x + dx, y + dy);
                if (t == null) continue;
                if (t.structs[depth] != null)
                    Debug.LogError($"Already a depth-{depth} structure at {x+dx},{y+dy}!");
                t.structs[depth] = this;
                // Roads (depth 3) suppress tile overlays — see WorldController.OnTileOverlayChanged.
                if (depth == 3) t.NotifyOverlayDirty();
            }
        }
        // Subclass hook for visuals that need the parent's final sortingOrder — rotating
        // wheels, frame-animated overlays, port stubs, etc. Constructor-time call is safe
        // because Update-driven callbacks (FrameAnimator.isActive, RotatingPart.speedSource,
        // PowerSystem.HasCompatibleShaftAt) are invoked lazily — by then OnPlaced has run
        // and any registries (PowerSystem, WOM) have the structure recorded.
        AttachAnimations();

        // Refresh floor-item sort for tiles directly above this structure's footprint —
        // any pile resting there now sees a new surface (building/platform) below it.
        for (int dx = 0; dx < claimNx; dx++)
            Inventory.RefreshFloorAt(x + dx, y + claimNy);

        // Fire art companion — toggleable child GO for flame/fire visuals.
        // LightSource.Update toggles this based on isLit + emission intensity.
        Sprite fireSprite = Resources.Load<Sprite>("Sprites/Buildings/" + st.name.Replace(" ", "") + "_f");
        if (fireSprite != null) {
            fireGO = new GameObject("fire");
            fireGO.transform.SetParent(go.transform, false);
            fireSR = SpriteMaterialUtil.AddSpriteRenderer(fireGO);
            fireSR.sprite = fireSprite;
            fireSR.sortingOrder = sr.sortingOrder;
            fireSR.flipX = mirrored;
            LightReceiverUtil.SetSortBucket(fireSR);
            // Bind fire texture as _EmissionMap via MPB — secondary textures from
            // the sprite importer may not survive DrawRenderers with an override
            // material (EmissionWriter). Explicit MPB binding ensures it's always
            // available as a per-renderer property.
            var mpb = new MaterialPropertyBlock();
            fireSR.GetPropertyBlock(mpb);
            mpb.SetTexture(Shader.PropertyToID("_EmissionMap"), fireSprite.texture);
            fireSR.SetPropertyBlock(mpb);
            fireGO.SetActive(false);
        }

        // Scan sprite for water-marker pixels. Registration happens in StructController.Place().
        waterPixelOffsets = WaterController.ScanWaterPixels(sprite);

        // Workspot waypoint: optional off-grid path target. When the StructType declares
        // workSpotX/workSpotY, register a Graph waypoint at that fractional position and
        // edge it bidirectionally to the nearest standable tile-node in the footprint's
        // bottom row. Mice walking to "this structure" end up standing at the waypoint's
        // wx/wy instead of the integer workTile centre — used for the wheel's centred-and-
        // elevated runner pose. Falls back to workTile.node when no spot is declared.
        //
        // Critical: this MUST run from the constructor (not OnPlaced) because OnPlaced is
        // skipped on the load path — Structure.Create() runs the constructor on both the
        // gameplay and load paths, and Graph.AddNeighborsInitial's RebuildComponents at
        // load picks up the waypoint via its neighbour edges automatically.
        if (st.workSpotX.HasValue && st.workSpotY.HasValue) {
            float spotX = mirrored ? (st.nx - 1 - st.workSpotX.Value) : st.workSpotX.Value;
            float spotY = st.workSpotY.Value;
            workNode = new Node(x + spotX, y + spotY);
            // Edge to the bottom-row tile-node closest to the workspot's x (tie → lower x).
            // Standability isn't checked here: at load time, Structure constructors run in
            // SaveSystem Phase 2 BEFORE graph.Initialize (Phase 4) sets node.standable, so a
            // standability-gated pick would always fail on load. The chosen tile is implicitly
            // standable post-placement — buildings can only be placed on standable ground —
            // and Phase 4's RebuildComponents picks up the waypoint via its neighbour edge.
            var graph = World.instance.graph;
            int nearestDx = -1;
            float nearestDist = float.MaxValue;
            for (int dx = 0; dx < st.nx; dx++) {
                int nxIdx = x + dx;
                if (nxIdx < 0 || nxIdx >= World.instance.nx) continue;
                float dist = Mathf.Abs(spotX - dx);
                if (dist < nearestDist) { nearestDist = dist; nearestDx = dx; }
            }
            if (nearestDx >= 0) {
                workNode.AddNeighbor(graph.nodes[x + nearestDx, y], reciprocal: true);
            } else {
                Debug.LogError($"{st.name} at ({x},{y}): workSpot declared but no in-bounds bottom-row tile to edge to");
            }
        } else {
            // No workspot — path target is the integer workTile's node (today's behaviour).
            // workTile getter may return null (out-of-bounds), in which case workNode stays null.
            workNode = workTile?.node;
        }
    }

    // Shared factory: dispatches to the correct subclass based on StructType properties.
    // Used by both StructController.Construct (gameplay) and SaveSystem.RestoreStructure (load).
    // When adding a new Structure subclass, add its case here — no other dispatch site needed.
    // Subclasses without their own ctor signature ignore shapeIndex (only base Structure
    // currently supports shape variants — Plant has its own multi-tile system).
    // Resolves the depth-based sortingOrder for a structure's anchor SR.
    // Sort order by depth: 0=building(10), 1=platform(15), 2=foreground(40), 3=road(1), 4=shaft(5).
    // Slot index ≠ visual layering — shafts are slot 4 but render behind buildings via
    // sortingOrder 5 (between roads at 1 and buildings at 10).
    // StructType.sortingOrder overrides this when >= 0 (e.g. light-source buildings at 64).
    // Used by the Structure ctor to compute the baseSortingOrder it passes into
    // StructureVisualBuilder.Build. Not used by Blueprint (always 100) or build preview
    // (always 200) — those are layer-fixed regardless of depth.
    public static int ResolveBaseSortingOrder(StructType st) {
        if (st.sortingOrder >= 0) return st.sortingOrder;
        switch (st.depth) {
            case 0: return 10;
            case 1: return 15;
            case 2: return 40;
            case 3: return 1;
            case 4: return 5;
            default: return 10;
        }
    }

    public static Structure Create(StructType st, int x, int y, bool mirrored = false, int rotation = 0, int shapeIndex = 0) {
        if (st.isPlant)
            return new Plant(st as PlantType, x, y);
        // Power shafts are foreground (depth 2). One subclass for straight, turning, and
        // 4-way junction; axis is derived from (name, rotation) inside PowerShaft.
        if (st.name == "power shaft" || st.name == "power shaft turn" || st.name == "power shaft 4")
            return new PowerShaft(st, x, y, mirrored, rotation);
        if (st.depth == 0 || st.isBuilding) {
            if (st.name == "pump")     return new PumpBuilding(st, x, y, mirrored);
            if (st.name == "market")   return new MarketBuilding(st, x, y, mirrored);
            if (st.name == "quarry")   return new Quarry(st, x, y, mirrored);
            if (st.name == "wheel")    return new MouseWheel(st, x, y, mirrored);
            if (st.name == "windmill") return new Windmill(st, x, y, mirrored);
            if (st.name == "flywheel") return new Flywheel(st, x, y, mirrored);
            if (st.name == "elevator") return new Elevator(st, x, y, mirrored, shapeIndex);
            if (st.name == "tarp")     return new Tarp(st, x, y, mirrored, shapeIndex);
            return new Building(st, x, y, mirrored);
        }
        return new Structure(st, x, y, mirrored, rotation, shapeIndex); // platforms, ladders, stairs, foreground, roads
    }

    public virtual void Destroy(){
        if (waterPixelOffsets != null)
            WaterController.instance?.UnregisterDecorativeWater(this);
        WorkOrderManager.instance?.RemoveMaintenanceOrders(this);
        MaintenanceSystem.instance?.ForgetStructure(this);
        // Clear workspot waypoint edges so neighbour tile-nodes don't carry a dangling
        // reference to this structure's now-defunct Node. Tile-backed workNodes are owned
        // by the Graph itself (don't touch them); only waypoints created by this structure
        // need teardown here.
        if (workNode != null && workNode.isWaypoint) {
            foreach (Node n in workNode.neighbors) n.RemoveNeighbor(workNode);
            workNode.neighbors.Clear();
            workNode = null;
        }
        // Defense-in-depth: PowerShaft/MouseWheel/Windmill clean themselves up in their
        // own Destroy overrides, but a future subclass that forgets won't leak stale
        // references through this central call. Cheap on non-power structures.
        PowerSystem.instance?.ForgetStructure(this);
        StructController.instance.Remove(this);
        int depth = structType.depth;
        bool shapeAware = structType.HasShapes;
        Shape shape = Shape;
        int claimNx = shapeAware ? shape.nx : structType.nx;
        int claimNy = shapeAware ? shape.ny : Mathf.Max(1, structType.ny);
        World world = World.instance;
        for (int dy = 0; dy < claimNy; dy++) {
            for (int dx = 0; dx < claimNx; dx++) {
                Tile t = world.GetTileAt(x + dx, y + dy);
                if (t == null) continue;
                if (t.structs[depth] == this) {
                    t.structs[depth] = null;
                    // Removing a road un-suppresses any overlay on this tile.
                    if (depth == 3) t.NotifyOverlayDirty();
                }
            }
        }
        GameObject.Destroy(go);
        // Refresh standability for every footprint tile and the row above the top —
        // the column was blocking those tiles via the same-structure-body rule.
        for (int dx = 0; dx < claimNx; dx++) {
            for (int dy = 0; dy < claimNy; dy++)
                world.graph.UpdateNeighbors(x + dx, y + dy);
            world.graph.UpdateNeighbors(x + dx, y + claimNy);
            world.FallIfUnstandable(x + dx, y + claimNy);
            // Floor-item sort follows the surface below; any pile that didn't fall
            // (e.g. because of a ladder or alternate support) needs to re-sort.
            Inventory.RefreshFloorAt(x + dx, y + claimNy);
        }
    }

}

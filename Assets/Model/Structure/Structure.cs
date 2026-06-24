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
    // Repair labour and materials both scale with the structure's build cost: a full 0→1 repair
    // takes ¼ of the build's labour (RepairLaborFraction × constructionCost ticks) and ¼ of its
    // materials (RepairCostFraction × ncosts). So a costlier/slower-to-build structure is also
    // costlier/slower to repair — and grants proportionally more research per repair.
    public const float RepairLaborFraction = 0.25f; // full 0→1 repair labour = ¼ of construction labour
    public const float RepairCostFraction  = 0.25f; // full 0→1 repair materials = ¼ of construction cost
    public const int   DaysToBreak        = 30;     // in-game days from 1.0 → BreakThreshold (0.5), when sheltered
    public const float ExposedDecayFactor = 1.5f;   // no overhead cover → 1.5× the sheltered decay rate; see MaintenanceSystem

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

    // ── Build materials ────────────────────────────────────────────────
    // The actual leaf items + fen this structure was built from (a group cost like "wood"
    // resolved to the delivered leaf "pine"). Set on the blueprint-construction path, persisted
    // via StructureSaveData.materialItems/materialFen, and read by Blueprint.Deconstruct to
    // refund the exact leaf. Also the data source for future wood-type tinting.
    // null = no record (legacy save, or a non-blueprint creation path: worldgen, mined tile,
    // mineshaft-ladder follow-up) → deconstruct falls back to first-leaf of each cost.
    public List<ItemQuantity> materials;

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

    // Reload-Domain-off support — see SpriteMaterialUtil.ResetStatics for the why.
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetCrackedMaterialCache() {
        _crackedMaterialCache = null;
        _crackedMaterialProbed = false;
    }

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

    // Interior waypoints — one off-grid Graph node per StructType.interiorTiles entry,
    // registered in the ctor. Each node sits at the tile's center (same world coords as
    // standing on an exterior tile would land).
    // Edges are wired so the graph alone routes mice in and out:
    //   - Adjacent interior nodes within the footprint are edged to each other (walk inside).
    //   - Each door's interior node is edged to the exterior approach tile's graph node
    //     (transit through the doorway).
    // Mirror handling: dx is flipped at ctor time; callers read these fields raw.
    // Null on buildings without interiorTiles. Cleaned up in Destroy alongside workNode.
    public Node[] interiorNodes;
    // Tiles whose bodyEdgeSuppressMask we OR'd bits into during ctor (for doored
    // `preservesTile` buildings). Tracked so Destroy can XOR the same bits back out.
    // Stored as parallel arrays (tile + bits) rather than a tuple list to keep alloc
    // small — most structures contribute zero entries here.
    private List<Tile> edgeSuppressTiles;
    private List<byte> edgeSuppressBits;

    // First door's approach tile (the tile just outside the door, on the door's side).
    // Used by Animal.FindHome to set homeTile as a tile-level "where my home is" hint
    // for UI / debug. Pathing doesn't go through this field — A* uses the door edge
    // wired up between interiorNodes and approach tile nodes. Null on doorless buildings.
    public Tile doorApproachTile { get; private set; }

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

        // Depth-based default order, overridden by an authored StructType.sortingOrder
        // (the stock road is 79, just above the dirt body band). See ResolveBaseSortingOrder.
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
                t.NotifyStructChanged();
                // Greenhouse frames back-point every covered tile to themselves so a plant rooted
                // here can detect the climate frame in O(1) (mirrors interiorBuilding). Set in this
                // shared loop so it lands on the gameplay, worldgen, AND load paths (all run the ctor).
                if (st.isGreenhouse) t.greenhouse = this;
                // Roads (depth 3) suppress tile overlays — on this tile and on the
                // top grass edge of the tile directly below (grass overhangs the tile
                // edge up over the road; see TileMeshController.BuildOverlayGeometry).
                if (depth == 3) NotifyRoadOverlayDirty(t);
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

        // Fire art companion — toggleable child GO for flame/fire visuals, toggled by
        // LightSource.Update based on isLit + emission intensity. The flame sheet is authored at the
        // host's tile size with the flame at the wick position (see overlay note below); a multi-frame
        // sheet animates. fireSprite overrides the default "{name}_f" lookup if a host wants to share.
        string flameName = !string.IsNullOrEmpty(st.fireSprite) ? st.fireSprite : st.name.Replace(" ", "") + "_f";
        Sprite[] fireFrames = Resources.LoadAll<Sprite>("Sprites/Buildings/" + flameName);
        if (fireFrames != null && fireFrames.Length > 0) {
            fireGO = new GameObject("fire");
            fireGO.transform.SetParent(go.transform, false);
            fireSR = SpriteMaterialUtil.AddSpriteRenderer(fireGO);
            fireSR.sprite = fireFrames[0];
            // +1 so the flame draws in front of the body/stick (equal order is an ambiguous tie).
            // Stays within the same lighting bucket (Mid 18..47 for a depth-2 torch), so emission
            // and sort-aware shaping are unaffected — see SortBucketUtil.
            fireSR.sortingOrder = sr.sortingOrder + 1;
            fireSR.flipX = mirrored;
            LightReceiverUtil.SetSortBucket(fireSR);
            // The flame sheet is authored at the host's full tile size (16x16) with the flame painted
            // at the wick position, so it renders as a plain overlay on the building — identical sprite
            // size, centre pivot, and transform as the body. That guarantees the flame shares the body's
            // exact pixel-snap rounding at every zoom; a smaller offset sprite rounds independently and
            // drifts by a pixel under pixel-perfect snapping. flipX mirrors it with the host like the
            // body. fireOffsetX/Y stay available for whole-pixel nudges (safe at the even tile size).
            fireGO.transform.localPosition = new Vector3(st.fireOffsetX, st.fireOffsetY, 0f);
            // Bind the flame's own texture as _EmissionMap via MPB so its painted pixels glow
            // (white-masked → colour preserved). Self-reference is atlas-safe: the SR's UVs and
            // the bound texture are the same atlas page, so each frame samples its own pixels.
            // (A separate emission sheet wouldn't survive atlasing here — different page/UVs.)
            var mpb = new MaterialPropertyBlock();
            fireSR.GetPropertyBlock(mpb);
            mpb.SetTexture(Shader.PropertyToID("_EmissionMap"), fireFrames[0].texture);
            fireSR.SetPropertyBlock(mpb);
            // Animate multi-frame sheets; phase-offset the start frame per instance (position
            // hash, deterministic + save-safe) so a row of torches doesn't flicker in lockstep.
            if (fireFrames.Length > 1) {
                FrameAnimator anim = fireGO.AddComponent<FrameAnimator>();
                anim.frames = fireFrames;
                anim.baseFps = st.fireFps > 0f ? st.fireFps : 7f;
                anim.startFrame = ((x * 31 + y) % fireFrames.Length + fireFrames.Length) % fireFrames.Length;
                anim.randomWalk = true; // fire flickers via a ±1 random walk, not a fixed cycle

            }
            fireGO.SetActive(false);
        }

        // Enclosed buildings (burrow, dug-in housing) render on the Interior layer so they
        // receive sun + ambient only — torchlight from above doesn't bleed into the buried
        // interior. Applied after all child SRs (main, extensions, fire) exist. Mice standing
        // inside get the same treatment dynamically (Animal.RefreshInteriorRendering).
        if (structType.enclosed) InteriorLayer.SetSpriteLayers(go, InteriorLayer.Interior);

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
            // Register so RebuildComponents resets this off-grid waypoint's componentId.
            World.instance.graph.RegisterWaypoint(workNode);
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

        // Interior nodes + door edges. Same load-time contract as workNode: runs from the
        // ctor so the load path picks it up before graph.Initialize. Doors are pure graph
        // topology — each interior tile gets a waypoint Node, adjacent interior nodes are
        // edged together, and each door is an edge from its interior node to the existing
        // approach-tile node. A* then routes mice in / out without Task code involvement.
        if (st.interiorTiles != null && st.interiorTiles.Length > 0) {
            Building selfBuilding = this as Building;
            interiorNodes = new Node[st.interiorTiles.Length];
            // Cache per-entry mirrored dx so adjacency math (below) uses world tile coords.
            int[] worldX = new int[st.interiorTiles.Length];
            int[] worldY = new int[st.interiorTiles.Length];
            for (int i = 0; i < st.interiorTiles.Length; i++) {
                InteriorTile it = st.interiorTiles[i];
                int mdx = mirrored ? (st.nx - 1 - it.dx) : it.dx;
                worldX[i] = x + mdx;
                worldY[i] = y + it.dy;
                // Off-grid waypoint at the tile's center — same world position a mouse
                // would stand at when on top of an exterior tile. With the standard
                // sprite pivot this lands the rendered mouse aligned with the building's
                // floor line, visually inside the silhouette.
                Node n = new Node(worldX[i] + 0f, worldY[i]);
                interiorNodes[i] = n;
                // Register so RebuildComponents resets this off-grid waypoint's componentId.
                World.instance.graph.RegisterWaypoint(n);
                // Tile-level back-ref — the authoritative "this tile is inside a
                // building" marker that Animal.insideBuilding derives from.
                Tile interiorTile = World.instance.GetTileAt(worldX[i], worldY[i]);
                if (interiorTile != null) interiorTile.interiorBuilding = selfBuilding;
            }
            // Auto-edge horizontally-adjacent interior nodes only. Vertical access is
            // intentional: it requires an explicit ladder declaration (below), so authors
            // pick where mice climb up — they don't pass through walls or floors.
            for (int i = 0; i < interiorNodes.Length; i++) {
                for (int j = i + 1; j < interiorNodes.Length; j++) {
                    if (worldY[i] == worldY[j] && Mathf.Abs(worldX[i] - worldX[j]) == 1)
                        interiorNodes[i].AddNeighbor(interiorNodes[j], reciprocal: true);
                }
            }
            // Ladder edges: each entry connects the interior node at (dx, dy) up to
            // (dx, dy+1). Author can place multiple ladders for taller stacks or
            // multiple climb points within one building.
            if (st.ladders != null) {
                for (int l = 0; l < st.ladders.Length; l++) {
                    Ladder lad = st.ladders[l];
                    int mdx = mirrored ? (st.nx - 1 - lad.dx) : lad.dx;
                    int botX = x + mdx, botY = y + lad.dy;
                    int topX = botX,     topY = botY + 1;
                    Node bot = null, top = null;
                    for (int i = 0; i < interiorNodes.Length; i++) {
                        if (worldX[i] == botX && worldY[i] == botY) bot = interiorNodes[i];
                        if (worldX[i] == topX && worldY[i] == topY) top = interiorNodes[i];
                    }
                    if (bot != null && top != null) {
                        bot.AddNeighbor(top, reciprocal: true);
                    } else {
                        Debug.LogError($"{st.name} at ({x},{y}): ladder at ({lad.dx},{lad.dy}) requires interiorTiles at ({lad.dx},{lad.dy}) and ({lad.dx},{lad.dy+1})");
                    }
                }
            }
            // Door edges — each door anchors to one interior node (by matching dx/dy)
            // and one approach tile (by side). The result is a single graph edge bridging
            // outside and inside.
            if (st.doors != null) {
                for (int d = 0; d < st.doors.Length; d++) {
                    Door door = st.doors[d];
                    int doorDx = mirrored ? (st.nx - 1 - door.dx) : door.dx;
                    string side = door.side;
                    if (mirrored) {
                        if (side == "left") side = "right";
                        else if (side == "right") side = "left";
                    }
                    // Find the interior node whose tile matches the door's tile.
                    int doorWorldX = x + doorDx, doorWorldY = y + door.dy;
                    Node interiorAtDoor = null;
                    for (int i = 0; i < interiorNodes.Length; i++) {
                        if (worldX[i] == doorWorldX && worldY[i] == doorWorldY) {
                            interiorAtDoor = interiorNodes[i]; break;
                        }
                    }
                    if (interiorAtDoor == null) {
                        Debug.LogError($"{st.name} at ({x},{y}): door at ({door.dx},{door.dy}) has no matching interiorTile entry");
                        continue;
                    }
                    WireDoorEdge(interiorAtDoor, doorWorldX, doorWorldY, side);
                }
            }
        }

        // Workstation-with-interior: when no explicit workSpotX/Y was declared, repoint
        // workNode away from the (non-standable) tile-node at workTile and onto the
        // matching interior node instead. Door edges connect the interior node to outside
        // approach tiles, so CraftTask's path-to-workNode resolves through the door — the
        // worker enters the building and stands inside it while crafting. Currently used
        // by digging pit (preservesTile, workTile sits inside the original solid tile);
        // safe for any future workstation with an interior layer.
        if (st.isWorkstation && interiorNodes != null && interiorNodes.Length > 0 && !st.workSpotX.HasValue) {
            int wxTile = x + (mirrored ? (st.nx - 1 - st.workTileX) : st.workTileX);
            int wyTile = y + st.workTileY;
            for (int i = 0; i < interiorNodes.Length; i++) {
                if (interiorNodes[i].x == wxTile && interiorNodes[i].y == wyTile) {
                    workNode = interiorNodes[i];
                    break;
                }
            }
        }

        // Preserved-tile structures dug out fully at construction (burrow) keep their footprint
        // SOLID but render their tile bodies behind the facade — see BuildPreservedTileBackdrop.
        // Built AFTER door wiring so the door tile's bodyEdgeSuppressMask is already set and the
        // backdrop hides that edge too. Runs in the ctor → applies on live + load (OnPlaced is
        // skipped on load).
        if (structType.preservesTile && !structType.extractsTileOverTime) BuildPreservedTileBackdrop();
    }

    // Wires one door edge: connects an interior node to the approach tile on `side`
    // (already mirror-resolved), records the first as doorApproachTile (FindHome's
    // homeTile hint), and for preservesTile buildings suppresses the door tile's
    // air-side rim so the entrance reads as a clean hole. Shared by the ctor's
    // JSON-door loop and by ExtractionBuilding, which picks its single door's side at
    // runtime from neighbour openness rather than from JSON.
    protected void WireDoorEdge(Node interiorNode, int doorWorldX, int doorWorldY, string side) {
        int approachX = doorWorldX, approachY = doorWorldY;
        switch (side) {
            case "left":   approachX -= 1; break;
            case "right":  approachX += 1; break;
            case "top":    approachY += 1; break;
            case "bottom": approachY -= 1; break;
            default:
                Debug.LogError($"{structType.name} at ({x},{y}): unknown door side '{side}'"); return;
        }
        Tile approach = World.instance.GetTileAt(approachX, approachY);
        if (approach == null) {
            Debug.LogError($"{structType.name} at ({x},{y}): door approach tile ({approachX},{approachY}) is out of bounds");
            return;
        }
        interiorNode.AddNeighbor(approach.node, reciprocal: true);
        if (doorApproachTile == null) doorApproachTile = approach;
        if (structType.preservesTile) {
            Tile doorTile = World.instance.GetTileAt(doorWorldX, doorWorldY);
            if (doorTile != null) {
                byte bit = side switch {
                    "left"   => (byte)1,
                    "right"  => (byte)2,
                    "bottom" => (byte)4,
                    "top"    => (byte)8,
                    _        => (byte)0,
                };
                if (bit != 0) SuppressDoorRim(doorTile, bit);
            }
        }
    }

    // ORs a rim-suppression bit into a tile and records it so Structure.Destroy can
    // clear it. Kept as a matched pair with that teardown loop so set and clear can't
    // drift — any code path that suppresses a rim goes through here.
    protected void SuppressDoorRim(Tile tile, byte bit) {
        tile.bodyEdgeSuppressMask |= bit;
        tile.NotifyBodyDirty();
        if (edgeSuppressTiles == null) {
            edgeSuppressTiles = new List<Tile>();
            edgeSuppressBits  = new List<byte>();
        }
        edgeSuppressTiles.Add(tile);
        edgeSuppressBits.Add(bit);
    }

    // Shared factory: dispatches to the correct subclass based on StructType properties.
    // Used by both StructController.Construct (gameplay) and SaveSystem.RestoreStructure (load).
    // When adding a new Structure subclass, add its case here — no other dispatch site needed.
    // Subclasses without their own ctor signature ignore shapeIndex (only base Structure
    // currently supports shape variants — Plant has its own multi-tile system).
    // Resolves the depth-based DEFAULT sortingOrder for a structure's anchor SR — the
    // fallback used only when StructType.sortingOrder < 0:
    //   0=building(10), 1=platform(15), 2=foreground(40), 3=road(1), 4=shaft(5).
    // Slot index ≠ visual layering — shafts are slot 4 but render behind buildings via
    // sortingOrder 5 (between the road default 1 and buildings at 10).
    // Most types author their own StructType.sortingOrder, which overrides the default
    // (the stock road is 79 — just above the dirt body band; light-source buildings 64).
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

    // Preserved-tile structures dug out fully at construction (burrow) keep their footprint
    // SOLID (graph + neighbour lighting treat it as earth), but the raised tile-body draw order
    // (78..74) would render the dirt in FRONT of the facade and bury it. The chunked mesh can't
    // sort a single tile differently, so for each footprint cell we flag bodyDrawnByStructure
    // (the chunk skips that cell's body, but neighbours + the grass/snow overlay still treat it
    // as normal solid terrain) and re-draw the cell's body as a child sprite at the OLD low
    // order — behind the facade, with the overlay still drawing over the top at order 80.
    // Gradual-extraction types (digging pit, quarry) are excluded: they draw their own receding
    // dish via bodyRenderSuppressed.
    void BuildPreservedTileBackdrop() {
        const int backdropOrder = 0;   // dirt's pre-reorder body order: behind the facade (10), Tiles bucket (≤8)
        World world = World.instance;
        for (int dy = 0; dy < structType.ny; dy++) {
            for (int dx = 0; dx < structType.nx; dx++) {
                int tx = x + dx, ty = y + dy;
                Tile t = world.GetTileAt(tx, ty);
                if (t == null || !t.type.solid) continue;
                t.bodyDrawnByStructure = true;
                t.NotifyBodyDirty();

                // Cardinal (4-bit) picks the body slice; 8-bit picks the normal map. Solid
                // neighbours (incl. this structure's other footprint cells) read as buried, so
                // the dirt between cells is seamless and only true air edges get a rim.
                int cMask = 0, mask8 = 0;
                if (SolidBody(world, tx - 1, ty    )) { cMask |= 1; mask8 |= 1; }
                if (SolidBody(world, tx + 1, ty    )) { cMask |= 2; mask8 |= 2; }
                if (SolidBody(world, tx,     ty - 1)) { cMask |= 4; mask8 |= 4; }
                if (SolidBody(world, tx,     ty + 1)) { cMask |= 8; mask8 |= 8; }
                if (SolidBody(world, tx - 1, ty - 1)) mask8 |= 16;
                if (SolidBody(world, tx + 1, ty - 1)) mask8 |= 32;
                if (SolidBody(world, tx - 1, ty + 1)) mask8 |= 64;
                if (SolidBody(world, tx + 1, ty + 1)) mask8 |= 128;
                // Door-facing side(s) (flagged by SuppressDoorRim in bodyEdgeSuppressMask) must
                // read as an actual OPENING, not a dirt wall. Bury the side first so the body is
                // flat Main with no cliff rim, then carve the outer strip to transparent (below)
                // so the burrow mouth is open. Other sides keep their normal solidity edge.
                byte doorSides = (byte)(t.bodyEdgeSuppressMask & 0xF);
                cMask |= doorSides;

                Sprite body = TileSpriteCache.Get(t.type.name, cMask, tx, ty);
                if (body == null) continue;
                if (doorSides != 0 && body.texture != null) {
                    body = CarveDoorEdges(body, doorSides, out Texture2D carvedTex);
                    RegisterBackdropDisposable(carvedTex);
                    RegisterBackdropDisposable(body);
                }
                GameObject bgo = new GameObject("tilebackdrop");
                bgo.transform.SetParent(go.transform, false);
                // Set WORLD position to the tile centre — the GO origin is the building's
                // (possibly centred) anchor, not tile (x,y), so a local (dx,dy) would be off.
                bgo.transform.position = new Vector3(tx, ty, 0f);
                SpriteRenderer bsr = SpriteMaterialUtil.AddSpriteRenderer(bgo);
                bsr.sprite       = body;
                bsr.sortingOrder = backdropOrder;
                LightReceiverUtil.SetSortBucket(bsr); // order ≤ 8 → Tiles bucket, like the chunk body
                // Enclosed buildings (burrow): the dirt backdrop behind the facade joins the
                // facade on the Interior layer, so the lighting pipeline treats the burrow's own
                // body as a non-occluding interior (directional-only tier) rather than a solid
                // shadow-caster wall. With wall-shadows on it's promoted to lit-only and receives
                // torches; the surrounding REAL tiles still occlude. See InteriorLayer / SPEC-rendering.
                if (structType.enclosed && InteriorLayer.Interior >= 0) bgo.layer = InteriorLayer.Interior;
                Texture2D nrm = TileSpriteCache.GetNormalMap(t.type.name, mask8, tx, ty);
                if (nrm != null) {
                    var mpb = new MaterialPropertyBlock();
                    bsr.GetPropertyBlock(mpb);
                    mpb.SetTexture(Shader.PropertyToID("_NormalMap"), nrm);
                    bsr.SetPropertyBlock(mpb);
                }
            }
        }
    }

    static bool SolidBody(World w, int tx, int ty) {
        if (tx < 0 || ty < 0 || tx >= w.nx || ty >= w.ny) return true; // off-map reads solid, matching the chunk bake
        Tile t = w.GetTileAt(tx, ty);
        return t != null && t.type.solid;
    }

    // Returns a copy of `src` with the outer DoorStripPx of each flagged side cleared to
    // transparent, so a burrow door reads as an open mouth rather than dirt up to the edge.
    // Bits: 1=left 2=right 4=bottom(down) 8=top(up) — matches bodyEdgeSuppressMask. GetPixels32
    // is bottom-up (y=0 = bottom), so "top" is the high rows. The new texture is returned via
    // outTex so the caller can register both it and the Sprite for disposal.
    const int DoorStripPx = 4; // texture px cleared inward from each door edge (tune for opening width)
    static Sprite CarveDoorEdges(Sprite src, byte sides, out Texture2D outTex) {
        int w = src.texture.width, h = src.texture.height;
        Color32[] px = src.texture.GetPixels32();
        Color32 clear = new Color32(0, 0, 0, 0);
        for (int r = 0; r < h; r++) {
            for (int c = 0; c < w; c++) {
                bool cut =
                    ((sides & 1) != 0 && c < DoorStripPx)          ||   // left
                    ((sides & 2) != 0 && c >= w - DoorStripPx)     ||   // right
                    ((sides & 4) != 0 && r < DoorStripPx)          ||   // bottom
                    ((sides & 8) != 0 && r >= h - DoorStripPx);         // top
                if (cut) px[r * w + c] = clear;
            }
        }
        outTex = new Texture2D(w, h, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
        };
        outTex.SetPixels32(px);
        outTex.Apply();
        return Sprite.Create(outTex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 16f);
    }

    // Carved backdrop textures/sprites we allocate (door tiles) — destroyed in Destroy so they
    // don't leak (unlike the child GOs, which die with `go`).
    List<UnityEngine.Object> _backdropDisposables;
    void RegisterBackdropDisposable(UnityEngine.Object o) {
        if (o == null) return;
        if (_backdropDisposables == null) _backdropDisposables = new List<UnityEngine.Object>();
        _backdropDisposables.Add(o);
    }

    // partnerX / partnerY are only consulted by two-click placement types (today,
    // "rope bridge post"). Other StructTypes ignore them. Default -1 keeps every
    // existing caller (worldgen, AnimalStateManager's quarry-depletion path,
    // single-click Blueprint.Complete) source-compatible.
    public static Structure Create(StructType st, int x, int y, bool mirrored = false, int rotation = 0, int shapeIndex = 0, int partnerX = -1, int partnerY = -1) {
        if (st.isPlant)
            return new Plant(st as PlantType, x, y);
        // Power shafts are foreground (depth 2). One subclass for straight, turning, and
        // 4-way junction; axis is derived from (name, rotation) inside PowerShaft.
        if (st.name == "power shaft" || st.name == "power shaft turn" || st.name == "power shaft 4")
            return new PowerShaft(st, x, y, mirrored, rotation);
        // Two-click placement: rope bridge posts come in pairs, each remembering the
        // other's coords. RopeBridge is materialised in BridgePost.OnPlaced once both
        // ends exist, or in RopeBridge.PairAllAfterLoad on the load path.
        if (st.name == "rope bridge post")
            return new BridgePost(st, x, y, mirrored, partnerX, partnerY);
        if (st.depth == 0 || st.isBuilding) {
            if (st.name == "pump")     return new PumpBuilding(st, x, y, mirrored);
            if (st.name == "market")   return new MarketBuilding(st, x, y, mirrored);
            // quarry (stone) and digging pit (earth) share one class; they differ only in JSON data.
            if (st.name == "quarry" || st.name == "digging pit") return new ExtractionBuilding(st, x, y, mirrored);
            if (st.name == "wheel")    return new MouseWheel(st, x, y, mirrored);
            if (st.name == "windmill") return new Windmill(st, x, y, mirrored);
            if (st.name == "flywheel") return new Flywheel(st, x, y, mirrored);
            if (st.name == "elevator") return new Elevator(st, x, y, mirrored, shapeIndex);
            if (st.name == "tarp")     return new Tarp(st, x, y, mirrored, shapeIndex);
            if (st.name == "clock")    return new Clock(st, x, y, mirrored);
            if (st.name == "foundry")  return new Foundry(st, x, y, mirrored);
            if (st.isWorkFlag)         return new WorkFlag(st, x, y, mirrored);
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
            World.instance.graph.UnregisterWaypoint(workNode);
            foreach (Node n in workNode.neighbors) n.RemoveNeighbor(workNode);
            workNode.neighbors.Clear();
            workNode = null;
        }
        // Clear any rim-suppression bits we OR'd into door tiles during ctor.
        if (edgeSuppressTiles != null) {
            for (int i = 0; i < edgeSuppressTiles.Count; i++) {
                Tile t = edgeSuppressTiles[i];
                if (t == null) continue;
                t.bodyEdgeSuppressMask &= (byte)~edgeSuppressBits[i];
                t.NotifyBodyDirty();
            }
            edgeSuppressTiles = null;
            edgeSuppressBits  = null;
        }
        // Restore chunk body rendering for footprint cells whose body we took over in the ctor
        // (burrow). The backdrop SRs are children of `go` and die with it; we just clear the flag
        // so the chunk redraws the cell normally. Gated as in the ctor so digging-pit/quarry
        // cells (which clear their own bodyRenderSuppressed) aren't touched here.
        if (structType.preservesTile && !structType.extractsTileOverTime) {
            for (int dy = 0; dy < structType.ny; dy++) {
                for (int dx = 0; dx < structType.nx; dx++) {
                    Tile t = World.instance?.GetTileAt(x + dx, y + dy);
                    if (t == null || !t.bodyDrawnByStructure) continue;
                    t.bodyDrawnByStructure = false;
                    t.NotifyBodyDirty();
                }
            }
        }
        // Free carved backdrop textures/sprites (door tiles); the child GOs die with `go`.
        if (_backdropDisposables != null) {
            for (int i = 0; i < _backdropDisposables.Count; i++)
                if (_backdropDisposables[i] != null) UnityEngine.Object.Destroy(_backdropDisposables[i]);
            _backdropDisposables = null;
        }
        // Tear down all interior waypoints. Edges to neighboring interior nodes and
        // to the door-approach tile node would otherwise dangle and pull A* into
        // dead ends. Clearing each tile's interiorBuilding back-ref makes mice on
        // those tiles derive insideBuilding == null, so the fall integration evicts
        // them naturally once the interior reverts to empty air.
        if (interiorNodes != null) {
            for (int i = 0; i < interiorNodes.Length; i++) {
                Node n = interiorNodes[i];
                if (n == null) continue;
                World.instance.graph.UnregisterWaypoint(n);
                foreach (Node m in n.neighbors) m.RemoveNeighbor(n);
                n.neighbors.Clear();
                Tile t = World.instance.GetTileAt((int)n.wx, (int)n.wy);
                if (t != null && t.interiorBuilding == this) t.interiorBuilding = null;
            }
            interiorNodes = null;
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
                    t.NotifyStructChanged();
                    // Removing a road un-suppresses the overlay on this tile and the
                    // top edge of the tile below (mirror of the place path above).
                    if (depth == 3) NotifyRoadOverlayDirty(t);
                }
                // Clear the greenhouse back-ref (guarded by identity so a re-used tile that some
                // other greenhouse now covers isn't wrongly cleared). A plant left inside simply
                // un-caps and resumes normal growth from the next stage crossing.
                if (t.greenhouse == this) t.greenhouse = null;
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

    // A road's presence on a tile suppresses that tile's own overlay AND the top
    // (U) grass edge of the tile directly below it (roads pave a top surface; the
    // grass below overhangs up over the road — see TileMeshController). Re-bake
    // both whenever a road is placed or removed.
    static void NotifyRoadOverlayDirty(Tile t) {
        t.NotifyOverlayDirty();
        World.instance.GetTileAt(t.x, t.y - 1)?.NotifyOverlayDirty();
    }
}

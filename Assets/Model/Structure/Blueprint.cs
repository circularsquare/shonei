using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class Blueprint {
    public GameObject go;
    public int x;
    public int y;
    public StructType structType;
    public Sprite sprite;
    public Tile tile; // anchor tile (bottom-left of footprint)
    // Center tile of the footprint — used as the pathfinding target for delivery/construction.
    // Uses the chosen shape's nx so tall/wide variants pathfind to the right place.
    public Tile centerTile => World.instance.GetTileAt(x + (Shape.nx - 1) / 2, y);

    // Tiles claimed by this blueprint at structType.depth. Default: every tile in
    // the anchor rectangle. Two-click placements (rope bridges) override to yield
    // ONLY the two post tiles — the catenary visually passes through other tiles
    // but doesn't claim them (rope is mid-air, not a tile structure).
    //
    // Used by Nav.PathToOrAdjacentBlueprint to enumerate approach candidates so a
    // hauler can deliver to whichever post is closer for a bridge.
    public System.Collections.Generic.IEnumerable<Tile> FootprintTiles() {
        if (IsTwoClick) {
            Tile a = World.instance.GetTileAt(x, y);
            Tile b = World.instance.GetTileAt(x2.Value, y2.Value);
            if (a != null) yield return a;
            if (b != null) yield return b;
            yield break;
        }
        Shape shape = Shape;
        int fnx = structType.HasShapes ? shape.nx : structType.nx;
        int fny = structType.HasShapes ? shape.ny : Mathf.Max(1, structType.ny);
        for (int dy = 0; dy < fny; dy++)
            for (int dx = 0; dx < fnx; dx++) {
                Tile t = World.instance.GetTileAt(x + dx, y + dy);
                if (t != null) yield return t;
            }
    }

    // Primary path targets for a hauler delivering to this blueprint. Single-click
    // bps yield just centerTile; two-click bps yield both post tiles so the hauler
    // walks to whichever has the cheaper path. Falls through to footprint-neighbour
    // candidates in Nav.PathToOrAdjacentBlueprint if neither direct path exists.
    public System.Collections.Generic.IEnumerable<Tile> CenterApproachTiles() {
        yield return centerTile;
        if (IsTwoClick) {
            Tile partner = World.instance.GetTileAt(x2.Value, y2.Value);
            if (partner != null) yield return partner;
        }
    }

    public Inventory inv;  // holds delivered materials; InvType.Blueprint keeps it out of haul/consolidate searches
    public ItemQuantity[] costs;
    public float constructionCost;
    public float constructionProgress = 0f;
    public enum BlueprintState { Receiving, Constructing, Deconstructing}
    public BlueprintState state = BlueprintState.Receiving;
    public bool cancelled = false;
    public bool disabled = false;
    public int priority = 0;
    // Leaf item ids the player has banned from THIS blueprint (e.g. "don't build this
    // foundry with gypsum"). Only meaningful for group-item costs (e.g. "stone"); a leaf
    // belongs to exactly one group root (single parent chain), so a flat set is unambiguous
    // and the ban is intentionally blueprint-wide. The supply path skips these leaves
    // (SupplyBlueprintTask → ResolveConsumeLeaf), delivery refuses them (DeliverToBlueprint-
    // Objective), and locking won't re-pick them (LockGroupCostsAfterDelivery). Persisted by
    // name via BlueprintSaveData.disallowedLeafNames.
    public HashSet<int> disallowedLeaves = new HashSet<int>();
    // Whether this blueprint (and the structure it builds) is horizontally mirrored.
    public bool mirrored = false;
    // Rotation in 90° clockwise steps (0..3). Set by BuildPanel during placement when
    // structType.rotatable is true. Carried through Complete() into the constructed Structure.
    public int rotation = 0;
    // Shape variant index. Set by BuildPanel (Q/E during placement) when structType.HasShapes;
    // carried through Complete() into the constructed Structure so the built thing matches the
    // ghost preview. Defaults to 0 (first authored shape, or base nx/ny when shapes is null).
    public int shapeIndex = 0;
    public Shape Shape => structType.GetShape(shapeIndex);

    // Sort order for the translucent ghost body. Sits just below the animal band
    // (mice are SortingGroup 50 + id%15, so 50..64) so blueprints read in front of
    // most structures (buildings 10, platforms 15, ladders 40) but tuck behind mice.
    // Plants (60) / torches (64) still draw in front — acceptable. The always-on-top
    // frame overlay is unaffected (separate Unlit camera). See SPEC-rendering §Sorting orders.
    private const int GhostSortingOrder = 49;

    // Two-click placement (rope bridge): nullable second endpoint coords. When
    // set, the blueprint claims BOTH posts' tiles (at structType.depth), scales
    // its cost linearly with horizontal delta, and on Complete materialises
    // both posts atomically via two Construct calls.
    public int? x2;
    public int? y2;
    public bool IsTwoClick => x2.HasValue && y2.HasValue;
    // Items to give to the completing animal after construction/deconstruction finishes.
    // Set by StructController.Construct (mining output) or Deconstruct (refunded materials).
    public List<ItemQuantity> pendingOutput;

    // Child SR rendering a sliced frame around the blueprint footprint. Always unlit so it
    // stays visible at night and reads as an overlay. Tint/alpha is updated by RefreshColor.
    private SpriteRenderer frameSr;
    // Partner-tile frame for two-click placements (rope bridges). Null on single-click
    // blueprints. Updated in tandem with frameSr by RefreshColor.
    private SpriteRenderer frameSrPartner;

    // ── Frame overlay asset cache ─────────────────────────────────────────
    // Mirrors the pattern used in Plant.cs for its harvest overlay. If a future third user
    // appears, extract a shared UnlitOverlayUtil helper.
    // Two sprites: blueprintframe (blue — construct/supply) and bpdeconstructframe (red).
    private static Sprite _constructFrameSprite;
    private static bool   _constructFrameLoaded;
    private static Sprite GetConstructFrameSprite() {
        if (_constructFrameLoaded) return _constructFrameSprite;
        _constructFrameSprite = Resources.Load<Sprite>("Sprites/Misc/blueprintframe");
        if (_constructFrameSprite == null)
            Debug.LogError("Blueprint: missing Resources/Sprites/Misc/blueprintframe — construct frame overlay will be invisible");
        _constructFrameLoaded = true;
        return _constructFrameSprite;
    }
    private static Sprite _deconstructFrameSprite;
    private static bool   _deconstructFrameLoaded;
    private static Sprite GetDeconstructFrameSprite() {
        if (_deconstructFrameLoaded) return _deconstructFrameSprite;
        _deconstructFrameSprite = Resources.Load<Sprite>("Sprites/Misc/bpdeconstructframe");
        if (_deconstructFrameSprite == null)
            Debug.LogError("Blueprint: missing Resources/Sprites/Misc/bpdeconstructframe — deconstruct frame overlay will be invisible");
        _deconstructFrameLoaded = true;
        return _deconstructFrameSprite;
    }
    private static Material _unlitOverlayMaterial;
    private static Material GetUnlitOverlayMaterial() {
        if (_unlitOverlayMaterial != null) return _unlitOverlayMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        if (shader == null) {
            Debug.LogError("Blueprint: URP Sprite-Unlit-Default shader not found — frame overlay will render black");
            return null;
        }
        _unlitOverlayMaterial = new Material(shader) { name = "BlueprintFrameUnlit" };
        return _unlitOverlayMaterial;
    }
    private static int  _unlitLayer = -1;
    private static bool _unlitLayerLookedUp;
    private static int GetUnlitLayer() {
        if (_unlitLayerLookedUp) return _unlitLayer;
        _unlitLayer = LayerMask.NameToLayer("Unlit");
        if (_unlitLayer < 0) Debug.LogError("Blueprint: 'Unlit' layer not found — frame overlay will be lit");
        _unlitLayerLookedUp = true;
        return _unlitLayer;
    }

    public Blueprint(StructType structType, int x, int y, bool mirrored = false, bool autoRegister = true, int rotation = 0, int shapeIndex = 0, int? x2 = null, int? y2 = null){
        this.structType = structType;
        this.x = x;
        this.y = y;
        this.mirrored = mirrored;
        this.rotation = rotation;
        this.shapeIndex = shapeIndex;
        this.x2 = x2;
        this.y2 = y2;
        this.tile = World.instance.GetTileAt(x, y);

        Shape shape = Shape;
        bool shapeAware = structType.HasShapes;
        int claimNx = shapeAware ? shape.nx : structType.nx;
        int claimNy = shapeAware ? shape.ny : Mathf.Max(1, structType.ny);
        // Multi-tile blueprint claim: every tile in the visual footprint at this depth
        // points at the same blueprint instance. Mirrors Structure's full-footprint claim
        // so blueprint and structure tile-lookup are symmetric.
        for (int dy = 0; dy < claimNy; dy++)
            for (int dx = 0; dx < claimNx; dx++)
                World.instance.GetTileAt(x + dx, y + dy).SetBlueprintAt(structType.depth, this);
        // Two-click placement: also claim the second post's tile. The claim is the
        // same blueprint reference so selection / collision / right-click lookup on
        // EITHER tile resolves to this one blueprint.
        if (IsTwoClick)
            World.instance.GetTileAt(x2.Value, y2.Value)?.SetBlueprintAt(structType.depth, this);

        // Side-ladder blueprints need the nav graph to refresh so the cliff chain
        // adds a step-off edge onto this tile — otherwise a mid-air placement is
        // unreachable for the builder and construction never starts. RebuildComponents
        // is required because the new step-off may connect a previously-impassable
        // (componentId=-1) air tile into a reachable component; without it, the
        // SameComponent fast-exit would still report the blueprint as unreachable.
        // Other blueprints don't affect navigation, so we keep the trigger scoped.
        if (structType.name == "ladder_side") {
            World.instance.graph.UpdateNeighbors(x, y);
            World.instance.graph.RebuildComponents();
        }

        // constructionCost = ticks of one builder's labor (10 ticks ~ 1 in-game hour). Default 2.
        // For variable-size structures (constructionCostPerTile), the authored value is per-tile and
        // we multiply by the placed footprint's tile count — shape tile count for shape-aware types,
        // span length for two-click placements. Mirrors the material-cost scaling just below.
        float baseCost = structType.constructionCost == 0f ? 2f : structType.constructionCost;
        if (structType.constructionCostPerTile) {
            int tiles = shapeAware ? shape.TileCount
                      : IsTwoClick ? Math.Max(1, (int)Catenary.HorizontalDelta(x, x2.Value))
                      : Math.Max(1, structType.nx * Mathf.Max(1, structType.ny));
            constructionCost = baseCost * tiles;
        } else {
            constructionCost = baseCost;
        }

        go = new GameObject();
        // Shape-aware blueprints anchor at (x, y) so per-tile child SRs at local (0, dy)
        // line up with their tile's centre — same convention as Structure.cs uses.
        go.transform.position = shapeAware
            ? new Vector3(x, y + (structType.depth == 3 ? -1f/8f : 0f), 0f)
            : StructureVisuals.PositionFor(structType, x, y);
        go.transform.SetParent(StructController.instance.transform, true);
        go.name = "blueprint_" + structType.name;

        // Spawn the blueprint's primary visual via the shared builder. Standard path
        // resolves the anchor sprite + optional vertical extensions; custom-visual types
        // (tarp) take their own branch in Build that spawns cloth/posts/etc. instead.
        // The translucent blueprint tint is applied uniformly to every spawned SR.
        var refs = StructureVisualBuilder.Build(go, structType, shape, mirrored, rotation, GhostSortingOrder, new Color(0.8f, 0.9f, 1f, 0.5f));
        sprite = refs.mainSr.sprite;  // null for custom-visual types — fine; nothing external reads this

        // Two-click placement: spawn a SECOND ghost at the partner tile, mirrored
        // OPPOSITE to the anchor. BuildPanel sets the anchor's `mirrored` flag
        // from geometry (anchor-is-left → mirrored=true), so flipping it gives
        // the partner the correct orientation. Parented to `go` so the standard
        // GameObject.Destroy(go) in Complete/Destroy/Deconstruct tears it down
        // alongside everything else — no extra cleanup paths needed.
        if (IsTwoClick) {
            GameObject partnerGo = new GameObject("blueprint_partner");
            partnerGo.transform.SetParent(go.transform, false);
            partnerGo.transform.localPosition = new Vector3(x2.Value - x, y2.Value - y, 0f);
            StructureVisualBuilder.Build(partnerGo, structType, shape, !mirrored, rotation, GhostSortingOrder,
                                         new Color(0.8f, 0.9f, 1f, 0.5f));
        }

        CreateFrameOverlay();

        // Deep-copy costs so LockGroupCostsAfterDelivery only affects this blueprint,
        // not every blueprint sharing the same StructType. Cost scales linearly with
        // shape footprint relative to shapes[0] (the authored baseline) — for the
        // platform's [1×1, 1×2, 1×3] this gives 1×, 2×, 3× the wood per height step.
        // Two-click placements (rope bridges) scale by horizontal delta instead:
        // ncosts is authored per-tile-of-span, total is ncosts × dx.
        int costMul = 1, costDiv = 1;
        if (shapeAware && structType.shapes.Length > 0) {
            costMul = shape.TileCount;
            costDiv = structType.shapes[0].TileCount;
        } else if (IsTwoClick) {
            costMul = (int)Catenary.HorizontalDelta(x, x2.Value);
            costDiv = 1;
        }
        costs = new ItemQuantity[structType.costs.Length];
        for (int i = 0; i < costs.Length; i++) {
            var src = structType.costs[i];
            int scaled = (int)Math.Round((double)src.quantity * costMul / costDiv);
            costs[i] = new ItemQuantity(src.item, scaled);
        }
        // One stack per cost item, capacity capped to exactly that item's cost quantity.
        // slotConstraints binds each stack to its cost item (group or leaf) so AddItem
        // routes deliveries to the right slot regardless of arrival order — without this,
        // a small-quantity item delivered first could squat in a slot sized for a
        // larger-quantity cost, capping the larger cost at the smaller stack's size.
        inv = new Inventory(Math.Max(1, costs.Length), 0, Inventory.InvType.Blueprint, x, y);
        if (costs.Length > 0) {
            inv.slotConstraints = new Item[costs.Length];
            for (int i = 0; i < costs.Length; i++) {
                inv.itemStacks[i].stackSize = costs[i].quantity;
                inv.slotConstraints[i] = costs[i].item;
            }
        }

        StructController.instance.AddBlueprint(this);
        if (autoRegister) {
            if (costs.Length == 0) {
                state = BlueprintState.Constructing;
                if (!IsSuspended())
                    WorkOrderManager.instance?.RegisterConstruct(this);
            } else if (!IsSuspended()) {
                WorkOrderManager.instance?.RegisterSupplyBlueprint(this);
            }
            // If suspended, no order is registered — RegisterOrdersIfUnsuspended()
            // will pick it up when the support below is built.
            RefreshColor(); // apply suspended tint if placed without solid support below
        }
        // For autoRegister: false (load path), RestoreBlueprint calls RefreshColor() separately
        // after restoring inventory and state, so we don't register stale orders here.
    }
    // Disable or re-enable this blueprint. Removes or re-registers WOM orders accordingly.
    public void SetDisabled(bool value) {
        disabled = value;
        RefreshColor();
        if (disabled)
            WorkOrderManager.instance?.RemoveForBlueprint(this);
        else
            RegisterOrdersIfUnsuspended();
    }

    // Spawns a sliced-sprite frame overlay around the blueprint footprint on an Unlit child GO.
    // Always visible regardless of lighting — serves as a persistent "this is a blueprint" cue.
    // Colour is driven by RefreshColor below; see SPEC-rendering.md for the Unlit layer pipeline.
    private void CreateFrameOverlay() {
        // Centre the frame on the footprint, independent of the main blueprint GO's pivot
        // (which for legacy multi-tile buildings is the visual centre, and for depth-3 floor
        // tiles is offset by -1/8 y, and for shape-aware structures is the anchor tile).
        // The anchor tile is (x, y); the footprint extends by the chosen shape's nx,ny.
        Shape shape = Shape;
        int fnx = structType.HasShapes ? shape.nx : structType.nx;
        int fny = structType.HasShapes ? shape.ny : structType.ny;
        frameSr = SpawnFrameSr(name: "frame", fnx: fnx, fny: fny, worldX: x, worldY: y);

        // Two-click placements (rope bridges) need a second frame at the partner
        // post's tile so the player can see BOTH endpoints are claimed.
        if (IsTwoClick) {
            frameSrPartner = SpawnFrameSr(name: "frame_partner", fnx: fnx, fny: fny,
                                          worldX: x2.Value, worldY: y2.Value);
        }
    }

    // Helper: spawns one sliced-frame SR at the given world tile, parented under `go`
    // and centred on the (fnx × fny) footprint. Returns the SR so RefreshColor can
    // update its sprite/colour later.
    private SpriteRenderer SpawnFrameSr(string name, int fnx, int fny, int worldX, int worldY) {
        GameObject frameGo = new GameObject(name);
        frameGo.transform.SetParent(go.transform, false);
        float fx = (fnx - 1) / 2f;
        float fy = Mathf.Max(0, fny - 1) / 2f;
        frameGo.transform.position = new Vector3(worldX + fx, worldY + fy, 0);

        int unlitLayer = GetUnlitLayer();
        if (unlitLayer >= 0) frameGo.layer = unlitLayer;
        SpriteRenderer sr = frameGo.AddComponent<SpriteRenderer>();
        Material unlitMat = GetUnlitOverlayMaterial();
        if (unlitMat != null) sr.sharedMaterial = unlitMat;
        // Default to the construct frame so the SR always has a valid sliced sprite.
        // RefreshColor swaps to the deconstruct sprite when appropriate.
        sr.sprite = GetConstructFrameSprite();
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = new Vector2(fnx, Mathf.Max(1, fny));
        sr.sortingOrder = 101; // above the blueprint sprite (100)
        return sr;
    }

    // Multiplicative red tint applied to the underlying structure's sprite while a deconstruct
    // blueprint sits on this tile. Keeps the visual in sync with live sprite changes (plant
    // growth stages, harvest cycles) without duplicating the sprite on the blueprint itself.
    private static readonly Color DeconstructStructureTint = new Color(1f, 0.5f, 0.5f);

    // Returns the structure that a deconstruct blueprint targets, or null if none is present.
    // structType.depth maps 1:1 to the slot index in tile.structs[], so we always return
    // the structure that this bp was created against — even on multi-structure tiles.
    // (The construct paths can't call this meaningfully — there's no target structure yet.)
    private Structure GetDeconstructTarget() {
        return tile.structs[structType.depth];
    }

    // Pure visual refresh — updates the sprite tint, frame colour, and underlying structure
    // tint to reflect current state (disabled / deconstructing / suspended / normal).
    // Does NOT touch WOM. Callers that also need to (re)register orders after a state
    // transition must call RegisterOrdersIfUnsuspended() explicitly.
    public void RefreshColor() {
        Color color;
        if (disabled)
            color = new Color(0.6f, 0.55f, 0.5f, 0.4f); // warm grey: disabled
        else if (state == BlueprintState.Deconstructing)
            color = new Color(1f, 0.3f, 0.3f, 0.5f);
        else if (IsSuspended())
            color = new Color(0.6f, 0.6f, 0.7f, 0.4f); // greyed-out: waiting for support below
        else
            color = new Color(0.8f, 0.9f, 1f, 0.5f);
        SpriteRenderer mainSr = go.GetComponent<SpriteRenderer>();
        mainSr.color = color;
        // For deconstruct, the underlying structure is already visible — we don't need a ghost
        // copy on top, just the frame + a red tint on the structure itself.
        mainSr.enabled = state != BlueprintState.Deconstructing;

        // Frame: red sprite for deconstruct, blue sprite otherwise. Half alpha when suspended
        // or disabled so the inactive state still reads but doesn't compete with active blueprints.
        Sprite frameSprite = state == BlueprintState.Deconstructing
            ? GetDeconstructFrameSprite()
            : GetConstructFrameSprite();
        float frameAlpha = (disabled || IsSuspended()) ? 0.5f : 1f;
        Color frameColor = new Color(1f, 1f, 1f, frameAlpha);
        if (frameSr != null)        { frameSr.sprite        = frameSprite; frameSr.color        = frameColor; }
        if (frameSrPartner != null) { frameSrPartner.sprite = frameSprite; frameSrPartner.color = frameColor; }

        // Tint the underlying structure red for deconstruct blueprints. Applied every RefreshColor
        // so it's idempotent and works on the load path too. Restored in Destroy() on cancel.
        // SetTint walks anchor + extension SRs so shape-aware multi-tile structures (e.g. a
        // 1×3 platform) tint across all their tiles, not just the bottom row.
        if (state == BlueprintState.Deconstructing) {
            Structure target = GetDeconstructTarget();
            target?.SetTint(DeconstructStructureTint);
        }
    }

    // If this blueprint is not suspended and has no work order yet, register one.
    // Called by StructController.Construct() when the structure below completes and
    // may have unsuspended blueprints stacked above, and by SetDisabled() when
    // re-enabling a blueprint.
    public void RegisterOrdersIfUnsuspended() {
        if (IsSuspended() || cancelled || disabled) return;
        if (state == BlueprintState.Receiving) {
            // After save/load, LockGroupCostsAfterDelivery() is not re-run so cost.item reverts to
            // the group ("wood"). IsFullyDelivered() uses MatchesItem so it still works correctly.
            // If the blueprint is already fully supplied, heal the state rather than registering
            // a supply order that would immediately fail in SupplyBlueprintTask.Initialize().
            if (IsFullyDelivered()) {
                state = BlueprintState.Constructing;
                WorkOrderManager.instance?.RegisterConstruct(this);
            } else {
                WorkOrderManager.instance?.RegisterSupplyBlueprint(this);
            }
        } else if (state == BlueprintState.Constructing)
            WorkOrderManager.instance?.RegisterConstruct(this);
    }

    // Mirrors Structure.ConditionsMet by convention (Blueprint is a sibling of Structure, not a
    // subclass — no polymorphism, just a shared name so WOM gates read the same on both: the order
    // is live iff `!disabled && ConditionsMet()`). For blueprints, the only runtime condition is
    // non-suspension. IsSuspended remains the named predicate for blueprint-specific paths (UI
    // tinting, RegisterOrdersIfUnsuspended) where the reason is clearer than the abstract gate.
    public bool ConditionsMet() => !IsSuspended();

    // True when this blueprint is waiting for world conditions to be met before it can be worked on.
    // Suspended blueprints are placed validly but mice should not supply or construct them yet.
    //
    // Two layers, both of which must pass:
    //   1. Authored `tileRequirements` (dynamic world-state flags: mustBeStandable, mustHaveWater,
    //      mustBeSolidTile). Lets buildings like the pump add domain-specific preconditions —
    //      "water below me", etc.
    //   2. Bottom-row support: every tile in the base of the footprint must be standable
    //      (solid ground or a built solidTop structure beneath it). A blueprint below doesn't
    //      count — pure blueprints don't bear weight, so a windmill on platform blueprints
    //      stays suspended until those platforms are actually built.
    public bool IsSuspended() {
        // Structures placed *inside* a solid tile (mine-tile, quarry/dirt pit via requiredTileName,
        // mineshaft via requiresSolidTilePlacement) are exempt from the standard support check —
        // their placement tile is non-standable by design, but they're authored to occupy it.
        if (structType.isTile || structType.name == "empty"
            || structType.requiredTileName != null
            || structType.requiresSolidTilePlacement)
            return false;

        // Side-mounted structures (ladder_side, bracket) lean on a wall, not a floor, so they
        // bypass the bottom-row support check entirely. Support is the mounting wall, validated
        // by the SAME predicate placement uses — suspended only if that wall is gone.
        if (structType.sideMounted)
            return !StructPlacement.SideMountWallPresent(tile, mirrored);

        if (structType.tileRequirements != null) {
            foreach (TileRequirement req in structType.tileRequirements) {
                int effectiveDx = mirrored ? (structType.nx - 1 - req.dx) : req.dx;
                Tile t = World.instance.GetTileAt(tile.x + effectiveDx, tile.y + req.dy);
                if (t == null) return true;
                if (req.mustBeStandable && !World.instance.graph.nodes[t.x, t.y].standable) return true;
                if (req.mustHaveWater && t.water == 0) return true;
                if (req.mustBeSolidTile && !t.type.solid) return true;
            }
            // When the type declares explicit mustBeStandable reqs, those REPLACE the generic
            // bottom-row check below (the author named exactly which columns bear weight, e.g.
            // the pump). Otherwise the reqs are additive and the generic check still runs.
        }

        // Generic bottom-row support — every column's base must rest on something solid (the
        // rest of the column stacks above). edgeSupported types check only the two end columns,
        // the middle may hang (tarp). Skipped entirely for types with explicit standable reqs.
        // Mirrors the placement rule in StructPlacement.GetPlacementFailReason.
        // Power shafts can also be supported by connecting to a built shaft (see
        // StructPlacement / PowerShaft.ConnectsToShaft). includeBlueprints is false here so the
        // run builds outward from a real, load-bearing shaft — a shaft hooked only onto another
        // *blueprint* stays suspended until that neighbour is actually built. (StructController
        // re-checks adjacent shaft blueprints whenever a shaft completes.)
        bool shaftConnected = PowerShaft.IsShaft(structType)
                && PowerShaft.ConnectsToShaft(structType, tile, rotation, mirrored, includeBlueprints: false);

        if (!structType.hasStandableRequirement && !shaftConnected) {
            int bottomNx = structType.HasShapes ? Shape.nx : structType.nx;
            if (structType.edgeSupported) {
                Node leftNode  = World.instance.graph.nodes[tile.x, tile.y];
                Node rightNode = World.instance.graph.nodes[tile.x + bottomNx - 1, tile.y];
                if (leftNode  != null && !leftNode.standable)  return true;
                if (rightNode != null && !rightNode.standable) return true;
            } else {
                for (int i = 0; i < bottomNx; i++) {
                    Node node = World.instance.graph.nodes[tile.x + i, tile.y];
                    if (node != null && !node.standable) return true;
                }
            }
        }
        // Two-click placements (rope bridges) own a second tile that the anchor
        // footprint loop above doesn't cover. The partner post needs the same
        // standability gate or a mined-out support there would silently let the
        // blueprint complete and float a post on empty air.
        if (IsTwoClick) {
            Node partner = World.instance.graph.nodes[x2.Value, y2.Value];
            if (partner != null && !partner.standable) return true;
        }
        return false;
    }

    // target: which structure on the tile to deconstruct. When null (right-click in
    // BuildPanel), pick the first non-null slot — building beats road, etc. When
    // provided (InfoPanel deconstruct on a specific tab), target that structure
    // directly so multi-structure tiles deconstruct the slot the player selected.
    public static Blueprint CreateDeconstructBlueprint(Tile tile, Structure target = null) {
        Structure structure = target;
        if (structure == null) {
            for (int d = 0; d < Tile.NumDepths; d++)
                if (tile.structs[d] != null) { structure = tile.structs[d]; break; }
        }
        if (structure == null) return null;
        // Anchor the bp at the structure's origin, NOT the clicked tile. For multi-tile
        // buildings every footprint tile points at the same Structure, so right-clicking
        // the middle/right cell of a 3-wide burrow would otherwise spawn a footprint-shifted bp.
        Blueprint bp = new Blueprint(structure.structType, structure.x, structure.y, structure.mirrored, autoRegister: false, rotation: structure.rotation, shapeIndex: structure.shapeIndex);
        bp.state = BlueprintState.Deconstructing;
        // RefreshColor hides the blueprint's duplicate sprite and applies a red multiplicative tint
        // to the underlying structure's SR — so growth stages and other live sprite changes keep
        // rendering through the tint. No per-deconstruct sprite copy needed.
        bp.RefreshColor();
        WorkOrderManager.instance?.RegisterDeconstruct(bp);
        // Storage-lock only applies when the deconstruct targets the building itself.
        // Deconstructing a road/foreground on the same tile must not touch the
        // co-located building's storage.
        if (structure is Building b && b.storage != null)
            b.storage.locked = true;
        // If the player is looking at this tile, switch them to the new deconstruct bp tab
        // rather than leaving the structure tab active (it's about to go away anyway).
        if (InfoPanel.instance?.obj == tile)
            InfoPanel.instance.RebuildSelection(preferBlueprint: bp);
        return bp;
    }

    // Locks each group cost (e.g. "wood") to the specific leaf first delivered (e.g. "pine").
    // Prevents future haulers from bringing a mismatched type that won't fit the occupied slot.
    // No-op if all costs are already leaves.
    public void LockGroupCostsAfterDelivery() {
        foreach (ItemQuantity cost in costs) {
            if (cost.item.children == null) continue; // already locked to a leaf
            foreach (ItemStack stack in inv.itemStacks) {
                if (stack.item == null) continue;
                if (disallowedLeaves.Contains(stack.item.id)) continue; // banned variant — never lock to it
                // Walk up the delivered item's ancestry to see if it belongs to this group cost.
                for (Item cur = stack.item.parent; cur != null; cur = cur.parent) {
                    if (cur != cost.item) continue;
                    cost.item = stack.item;
                    cost.id   = stack.item.id;
                    break;
                }
                if (cost.item.children == null) break; // locked — move on to next cost
            }
        }
    }

    // Player banned `leaf` (a group cost's variant, e.g. "gypsum") for this blueprint. Drops any
    // already-delivered units of it onto the floor (a hauler returns them to storage via the normal
    // floor-haul path), reverts every cost slot that was locked to it back to its authored group so
    // a different leaf can be supplied, and re-derives the WOM orders. Banning resolves through
    // disallowedLeaves in the supply/deliver/lock paths.
    public void DisallowLeaf(Item leaf) {
        if (leaf == null || leaf.children != null) return; // only leaves can be banned
        if (!disallowedLeaves.Add(leaf.id)) return;        // already banned

        bool revertedAnything = false;
        for (int i = 0; i < costs.Length; i++) {
            if (costs[i].item != leaf) continue; // only slots currently locked to this exact leaf
            // Drop delivered units to the floor at the anchor tile. inv.Produce(-qty) decrements
            // GlobalInventory; ProduceAtTile re-adds it on the floor and registers the haul — net
            // global change is zero (mirrors Reservoir.DropToFloor).
            int qty = inv.Quantity(leaf);
            if (qty > 0) {
                inv.Produce(leaf, -qty);
                World.instance.ProduceAtTile(leaf, qty, tile);
            }
            // Revert the slot to its authored group (the deep-copy source is still the group).
            costs[i].item = structType.costs[i].item;
            costs[i].id   = structType.costs[i].item.id;
            revertedAnything = true;
        }
        if (!revertedAnything) return; // leaf wasn't in use (pre-emptive ban) — orders unaffected

        // Rolling back to Receiving: a fully-delivered bp that was Constructing is now short the
        // reverted cost. Zero progress and fail any builder mid-construction so ReceiveConstruction
        // doesn't LogError against the now-Receiving state.
        if (state == BlueprintState.Constructing) {
            state = BlueprintState.Receiving;
            constructionProgress = 0f;
            FailActiveConstructTask();
        }
        WorkOrderManager.instance?.RemoveForBlueprint(this);
        RegisterOrdersIfUnsuspended();
        InfoPanel.instance?.UpdateInfo();
    }

    // Player un-banned `leaf`. No item movement — just re-allow it and refresh orders (a previously
    // un-suppliable blueprint may now be suppliable again).
    public void AllowLeaf(Item leaf) {
        if (leaf == null || !disallowedLeaves.Remove(leaf.id)) return;
        RegisterOrdersIfUnsuspended();
        InfoPanel.instance?.UpdateInfo();
    }

    // Fails the builder (if any) currently running a ConstructTask against this blueprint, so a
    // mid-construction rollback to Receiving doesn't leave a task calling ReceiveConstruction on
    // the wrong state. Cheap one-off scan; only called from DisallowLeaf's Constructing rollback.
    void FailActiveConstructTask() {
        AnimalController ac = AnimalController.instance;
        if (ac == null) return;
        for (int i = 0; i < ac.na; i++) {
            Animal a = ac.animals[i];
            if (a?.task is ConstructTask ct && ct.blueprint == this) {
                ct.Fail();
                return;
            }
        }
    }

    public bool IsFullyDelivered() {
        foreach (var cost in costs)
            if (inv.Quantity(cost.item) < cost.quantity) return false;
        return true;
    }

    public bool ReceiveConstruction(float progress){ // returns whether you just finished
        if (state == BlueprintState.Receiving) { Debug.LogError("Blueprint is not in Constructing state"); return true;}
        constructionProgress += progress;
        if (constructionProgress >= constructionCost){
            if (state == BlueprintState.Constructing) {
                Complete();
                return true;
            } else if (state == BlueprintState.Deconstructing) {
                Deconstruct();
                return true;
            }
        }
        return false;
    }

    public void Complete(){
        StructController.instance.RemoveBlueprint(this);
        WorkOrderManager.instance?.RemoveForBlueprint(this);
        // Consume delivered materials — removes them from globalInv now that they're used up.
        // Use the actual stack items (not cost.item) because group costs (e.g. "wood") are only locked
        // to their delivered leaf ("pine") in-memory; after a save/load cost.item reverts to the group
        // and Produce("wood", ...) would fail the group-item guard in AddItem.
        foreach (var stack in inv.itemStacks)
            if (stack.item != null && stack.quantity > 0)
                inv.Produce(stack.item, -stack.quantity);
        // Capture tile products before Construct() either changes or hides the tile. Three trigger
        // paths: (a) the legacy isTile mine-tile (`empty`); (b) any structure placed inside a solid
        // tile (mineshaft); (c) a `preservesTile` structure that leaves the tile alone visually but
        // still yields its materials (burrow). The first two consume a single anchor tile. The
        // preserve path walks the full footprint so multi-tile excavators (burrow: 3× dirt) yield
        // one tile's worth of products per footprint tile.
        // `extractsTileOverTime` (quarry, digging pit) opts out entirely — those structures mine the
        // tile's material gradually through work, so dumping it all on completion would double up.
        bool minesTile = !structType.extractsTileOverTime
            && ((structType.isTile && structType.name == "empty") || structType.requiresSolidTilePlacement);
        if (minesTile && tile.type.products != null) {
            pendingOutput = new List<ItemQuantity>(tile.type.products);
        } else if (structType.preservesTile && !structType.extractsTileOverTime) {
            int fnx = structType.HasShapes ? Shape.nx : structType.nx;
            int fny = structType.HasShapes ? Shape.ny : Mathf.Max(1, structType.ny);
            pendingOutput = new List<ItemQuantity>();
            World w = World.instance;
            for (int dy = 0; dy < fny; dy++) {
                for (int dx = 0; dx < fnx; dx++) {
                    Tile t = w.GetTileAt(tile.x + dx, tile.y + dy);
                    if (t == null || t.type.products == null) continue;
                    foreach (var p in t.type.products)
                        pendingOutput.Add(new ItemQuantity(p.item, p.quantity));
                }
            }
        }
        // Record the exact leaf items this structure is built from, for an exact deconstruct
        // refund (and future wood-type tinting). By now costs are locked to their delivered
        // leaves (the blueprint is fully delivered); FirstLeaf() is a no-op on those but also
        // resolves a still-group cost defensively on the instant-build debug path (empty inv,
        // no LockGroupCostsAfterDelivery), so `materials` is always leaf-valued.
        List<ItemQuantity> mats = new List<ItemQuantity>(costs.Length);
        foreach (var c in costs)
            if (c.quantity > 0) mats.Add(new ItemQuantity(c.item.FirstLeaf(), c.quantity));

        // Two-click placement: materialise BOTH posts atomically, each handed
        // the OTHER's coords as its partner. The second Construct's OnPlaced
        // finds the first BridgePost already in place and spins up the
        // RopeBridge linking them.
        //
        // Mirror is geometry-driven, not player-driven: the LEFT post (smaller
        // x) is mirrored so its pole faces right toward the bridge; the RIGHT
        // post stays un-mirrored so its pole faces left. Overrides whatever
        // `mirrored` field the BuildPanel had set — for bridges that flag is
        // meaningless since the rope dictates orientation.
        //
        // Both posts record the FULL bridge cost (not a split): on teardown only one post runs
        // through the refunding Deconstruct — the partner is direct-Destroyed without refund —
        // so storing the whole cost on each yields half back, identical to any building.
        if (IsTwoClick) {
            Tile tileB = World.instance.GetTileAt(x2.Value, y2.Value);
            bool aIsLeft = x <= x2.Value;
            StructController.instance.Construct(structType, tile,  aIsLeft,  rotation, shapeIndex, x2.Value, y2.Value, mats);
            StructController.instance.Construct(structType, tileB, !aIsLeft, rotation, shapeIndex, x,         y,        mats);
        } else {
            StructController.instance.Construct(structType, tile, mirrored, rotation, shapeIndex, materials: mats);
        }
        // A tile mined to empty drops any side-mount (side ladder, bracket, side torch) that was
        // leaning on the now-removed wall; its materials route to the mining mouse (via
        // pendingOutput → animal.Produce fallbacks).
        if (minesTile) DestroyDependentSideMounts();
        // Passive research gain from constructing a tech-gated building.
        // No-op for ungated structures (floors, walls, etc.).
        ResearchSystem.instance?.AddConstructionProgress(structType.name);
        // One-way building gate: reveals jobs like woodworker (sawmill) / scientist (laboratory).
        // No-op unless some job's unlockedByBuilding matches this type.
        AnimalController.instance?.RegisterBuildingBuilt(structType.name);
        ClearBlueprintFromTiles();
        GameObject.Destroy(go);
        if (InfoPanel.instance?.obj == tile) {
            // Auto-select the newly constructed structure if one was placed (non-tile blueprints only).
            Structure newStructure = structType.isTile ? null : tile.structs[structType.depth];
            InfoPanel.instance.RebuildSelection(newStructure);
        }
    }

    // Side-mounts (side ladders, brackets, side torches) hang against a wall (a solid tile or a
    // building's body). When this completion mines a footprint tile to empty, any side-mount leaning
    // on it has lost its wall — destroy it instantly and route its half-cost materials to the mining
    // mouse via pendingOutput (animal.Produce already falls back to a floor drop, then
    // vanishing-with-log, if the mouse can't hold it).
    void DestroyDependentSideMounts() {
        World w = World.instance;
        int fnx = structType.HasShapes ? Shape.nx : structType.nx;
        int fny = structType.HasShapes ? Shape.ny : Mathf.Max(1, structType.ny);
        bool anyDestroyed = false;
        for (int dy = 0; dy < fny; dy++)
            for (int dx = 0; dx < fnx; dx++) {
                Tile wall = w.GetTileAt(tile.x + dx, tile.y + dy);
                if (wall == null || wall.type.solid) continue;          // still a wall → nothing lost
                Structure wb = wall.structs[0];
                if (wb != null && !(wb is Plant)) continue;             // a building still provides a wall
                // Mount to the RIGHT of the wall leans on its left face (mirrored=false → dir -1);
                // mount to the LEFT leans on its right face (mirrored=true → dir +1).
                anyDestroyed |= TryDestroySideMount(w.GetTileAt(wall.x + 1, wall.y), -1);
                anyDestroyed |= TryDestroySideMount(w.GetTileAt(wall.x - 1, wall.y), +1);
            }
        if (anyDestroyed) w.graph.RebuildComponents();
    }

    bool TryDestroySideMount(Tile mountTile, int wallDir) {
        if (mountTile == null) return false;
        Structure mount = mountTile.GetSideMount(wallDir);
        if (mount == null) return false;
        // Half-cost refund (mirrors Deconstruct), group cost resolved to a concrete leaf.
        if (pendingOutput == null) pendingOutput = new List<ItemQuantity>();
        foreach (ItemQuantity cost in mount.structType.costs) {
            int amt = Mathf.FloorToInt(cost.quantity / 2f);
            if (amt <= 0) continue;
            pendingOutput.Add(new ItemQuantity(cost.item.FirstLeaf(), amt));
        }
        mount.Destroy();
        World.instance.graph.UpdateNeighbors(mountTile.x, mountTile.y);
        World.instance.graph.UpdateNeighbors(mountTile.x, mountTile.y + 1);
        World.instance.graph.UpdateNeighbors(mountTile.x, mountTile.y - 1);
        return true;
    }

    public void Deconstruct() {
        StructController.instance.RemoveBlueprint(this);
        WorkOrderManager.instance?.RemoveForBlueprint(this);
        // Structure being removed. structType.depth maps 1:1 to tile.structs[] index, so we
        // always target the structure that matches this bp — even on multi-structure tiles.
        // Fetched up-front so plant removal yields can read live growth state before Destroy.
        Structure removed = tile.structs[structType.depth];
        pendingOutput = new List<ItemQuantity>(); // given in asm.handleworking
        if (removed is Plant plant) {
            // Plants yield their removal products (chopped wood / crop), not a build-cost refund.
            pendingOutput.AddRange(plant.RemovalYield());
        } else if (removed?.materials != null && removed.materials.Count > 0) {
            // Refund half of the exact leaf items this structure was built from.
            foreach (ItemQuantity m in removed.materials) {
                int amount = Mathf.FloorToInt(m.quantity / 2f);
                if (amount > 0) pendingOutput.Add(new ItemQuantity(m.item, amount));
            }
        } else {
            // No material record (legacy save, or a non-blueprint structure: worldgen, mined
            // tile, mineshaft-ladder follow-up). Refund half of the first leaf of each cost.
            Debug.Log($"Deconstruct: {structType.name} at ({x},{y}) has no material record; refunding first-leaf of each cost.");
            foreach (ItemQuantity cost in costs) {
                int amount = Mathf.FloorToInt(cost.quantity / 2f);
                if (amount <= 0) continue;
                pendingOutput.Add(new ItemQuantity(cost.item.FirstLeaf(), amount));
            }
        }
        // Capture `preservesTile` before Destroy clears the slot — for burrow (and any future
        // hole-style building) the dirt tiles get rewritten to empty AFTER destroy so the
        // visual result matches "the roof was dug away and the hole collapsed inward".
        bool preservesTile = removed != null && removed.structType.preservesTile;
        int preservedFnx = structType.HasShapes ? Shape.nx : structType.nx;
        int preservedFny = structType.HasShapes ? Shape.ny : Mathf.Max(1, structType.ny);
        removed?.Destroy();
        if (preservesTile) {
            World.instance.SetFootprintTileType(tile.x, tile.y, preservedFnx, preservedFny,
                Db.tileTypeByName["empty"]);
        }
        // remove blueprint
        ClearBlueprintFromTiles();
        GameObject.Destroy(go);
        if (InfoPanel.instance?.obj == tile) InfoPanel.instance.RebuildSelection();
    }

    // Instantly finishes this blueprint with no worker — backs the Ctrl+Shift+F dev shortcut
    // (hover in MouseController, selected Blueprint tab in InfoPanel). Dispatches by state:
    // Deconstructing tears the structure down, anything else builds it. The pendingOutput a
    // worker would normally receive (deconstruct refund, or mining yield on Complete) is dropped
    // onto the floor at the anchor tile instead, since there's no worker to hand it to. Overflow
    // that won't fit on nearby floor is discarded (ProduceAtTile logs the shortfall).
    public void InstantFinish() {
        Tile dropTile = tile;
        if (state == BlueprintState.Deconstructing)
            Deconstruct();
        else
            Complete();
        if (pendingOutput != null && dropTile != null)
            foreach (ItemQuantity iq in pendingOutput)
                World.instance.ProduceAtTile(iq.item, iq.quantity, dropTile);
    }

    // Returns true if this is a deconstruct blueprint on a storage building and the storage still has items.
    // Deconstruction must wait until the storage is emptied by haulers.
    // Only relevant when this bp targets slot 0 — deconstructing a co-located road or
    // foreground decoration must not block on the building's storage.
    public bool StorageNeedsEmptying() {
        return state == BlueprintState.Deconstructing
            && structType.depth == 0
            && tile.building?.storage != null
            && !tile.building.storage.IsEmpty();
    }

    // Returns true if completing this blueprint would cause items on the tile(s) above to lose
    // standability and fall. Uses the same logic as Navigation.GetStandability().
    public bool WouldCauseItemsFall() {
        World world = World.instance;
        // Items rest one tile above the visual top of the structure — `tile.y + ny`. Uses the
        // same full-footprint convention as the tile-claim, so a future solidTop multi-height
        // building (e.g. a 2×4 windmill marked solidTop) checks above its actual top, not above
        // its anchor row.
        int fnx = structType.HasShapes ? Shape.nx : structType.nx;
        int fny = structType.HasShapes ? Shape.ny : Mathf.Max(1, structType.ny);
        int topY = tile.y + (fny - 1);
        for (int i = 0; i < fnx; i++) {
            int bx = tile.x + i, by = topY;
            Tile above = world.GetTileAt(bx, by + 1);
            if (above == null || above.inv == null || above.inv.IsEmpty()) continue;
            if (!world.graph.nodes[bx, by + 1].standable) continue;

            Tile tileBelow = world.GetTileAt(bx, by);

            // Predict the tile's post-construction solidity. Three cases:
            //   - isTile blueprint: tile becomes whatever StructType.name resolves to (e.g. "empty").
            //   - non-isTile that mines its tile (mineshaft): tile becomes empty (non-solid).
            //   - regular non-isTile: tile is unchanged.
            bool solidTileAfter = structType.isTile
                ? Db.tileTypeByName[structType.name].solid
                : structType.requiresSolidTilePlacement
                    ? false
                    : tileBelow.type.solid;

            bool anySolidTopAfter = false;
            for (int d = 0; d < Tile.NumDepths; d++) {
                bool solidTop = structType.depth == d
                    ? (state == BlueprintState.Constructing && structType.solidTop)
                    : (tileBelow.structs[d] != null && tileBelow.structs[d].structType.solidTop);
                if (solidTop) { anySolidTopAfter = true; break; }
            }

            bool ladderSupport = above.HasLadder() || tileBelow.HasLadder();

            if (!(solidTileAfter || anySolidTopAfter || ladderSupport))
                return true;
        }
        return false;
    }

    public void Destroy() {
        StructController.instance.RemoveBlueprint(this);
        WorkOrderManager.instance?.RemoveForBlueprint(this);
        cancelled = true;
        // Mirror the slot-0 gate from CreateDeconstructBlueprint: only unlock the
        // building's storage if this bp was the one that locked it (i.e. it targeted
        // the building, not a co-located road/foreground).
        if (state == BlueprintState.Deconstructing && structType.depth == 0 && tile.building?.storage != null)
            tile.building.storage.locked = false;
        // Restore the underlying structure's sprite tint if we were colouring it red. Safe
        // to call unconditionally: structures default to white and we're the only writer.
        // Skipped on world clear since Destroy() below tears the structure GO down anyway.
        if (state == BlueprintState.Deconstructing && !WorldController.isClearing) {
            Structure target = GetDeconstructTarget();
            target?.SetTint(Color.white);
        }
        ClearBlueprintFromTiles();
        GameObject.Destroy(go);
        if (InfoPanel.instance?.obj == tile) InfoPanel.instance.RebuildSelection();
    }

    private void ClearBlueprintFromTiles() {
        Shape shape = Shape;
        int fnx = structType.HasShapes ? shape.nx : structType.nx;
        int fny = structType.HasShapes ? shape.ny : Mathf.Max(1, structType.ny);
        for (int dy = 0; dy < fny; dy++)
            for (int dx = 0; dx < fnx; dx++)
                World.instance.GetTileAt(x + dx, y + dy)?.SetBlueprintAt(structType.depth, null);
        // Symmetric to the two-tile claim in the ctor.
        if (IsTwoClick)
            World.instance.GetTileAt(x2.Value, y2.Value)?.SetBlueprintAt(structType.depth, null);
        // Symmetric to the ctor's nav refresh: removing a side-ladder blueprint should
        // tear down the chain step-off edge that pointed at this tile.
        if (structType.name == "ladder_side") {
            World.instance.graph?.UpdateNeighbors(x, y);
            World.instance.graph?.RebuildComponents();
        }
    }

    public string GetProgress(){ // for display string
        string progress = "";
        if (state != BlueprintState.Deconstructing) {
            for (int i = 0; i < costs.Length; i++) {
                progress += costs[i].item.name + " " + ItemStack.FormatQ(inv.Quantity(costs[i].item), costs[i].item) + "/" + ItemStack.FormatQ(costs[i]);
            }
        }
        if (state == BlueprintState.Constructing || state == BlueprintState.Deconstructing){
            progress += " (" + constructionProgress.ToString("F0") + "/" + constructionCost.ToString() + ")";
        }
        return progress;
    }
}

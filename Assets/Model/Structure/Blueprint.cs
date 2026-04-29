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

    public Inventory inv;  // holds delivered materials; InvType.Blueprint keeps it out of haul/consolidate searches
    public ItemQuantity[] costs;
    public float constructionCost;
    public float constructionProgress = 0f;
    public enum BlueprintState { Receiving, Constructing, Deconstructing}
    public BlueprintState state = BlueprintState.Receiving;
    public bool cancelled = false;
    public bool disabled = false;
    public int priority = 0;
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
    // Items to give to the completing animal after construction/deconstruction finishes.
    // Set by StructController.Construct (mining output) or Deconstruct (refunded materials).
    public List<ItemQuantity> pendingOutput;

    // Child SR rendering a sliced frame around the blueprint footprint. Always unlit so it
    // stays visible at night and reads as an overlay. Tint/alpha is updated by RefreshColor.
    private SpriteRenderer frameSr;

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

    public Blueprint(StructType structType, int x, int y, bool mirrored = false, bool autoRegister = true, int rotation = 0, int shapeIndex = 0){
        this.structType = structType;
        this.x = x;
        this.y = y;
        this.mirrored = mirrored;
        this.rotation = rotation;
        this.shapeIndex = shapeIndex;
        this.tile = World.instance.GetTileAt(x, y);

        Shape shape = Shape;
        bool shapeAware = structType.HasShapes;
        int claimNx = shapeAware ? shape.nx : structType.nx;
        int claimNy = shapeAware ? shape.ny : 1;
        // Multi-tile blueprint claim: every tile in the shape footprint at this depth
        // points at the same blueprint instance. Legacy single-row claim for non-shape types.
        for (int dy = 0; dy < claimNy; dy++)
            for (int dx = 0; dx < claimNx; dx++)
                World.instance.GetTileAt(x + dx, y + dy).SetBlueprintAt(structType.depth, this);

        if (structType.constructionCost == 0f){
            constructionCost = 2f; // default
        } else {
            constructionCost = structType.constructionCost;
        }

        go = new GameObject();
        // Shape-aware blueprints anchor at (x, y) so per-tile child SRs at local (0, dy)
        // line up with their tile's centre — same convention as Structure.cs uses.
        go.transform.position = shapeAware
            ? new Vector3(x, y + (structType.depth == 3 ? -1f/8f : 0f), 0f)
            : StructureVisuals.PositionFor(structType, x, y);
        go.transform.SetParent(StructController.instance.transform, true);
        go.name = "blueprint_" + structType.name;

        // Anchor sprite — variant `_b` for shape-aware tall, base sprite for 1-tall / legacy.
        Sprite loadedSprite = shapeAware
            ? StructureVisuals.LoadShapeSprite(structType, shape, 0)
            : structType.LoadSprite();
        sprite = loadedSprite ?? Resources.Load<Sprite>("Sprites/Buildings/default");
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = 100;
        LightReceiverUtil.SetSortBucket(sr);
        sr.sprite = sprite;
        sr.flipX = mirrored;
        go.transform.rotation = StructureVisuals.RotationFor(rotation);
        sr.color = new Color(0.8f, 0.9f, 1f, 0.5f); // blueprint half alpha
        if (loadedSprite == null) {
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.size = new Vector2(structType.nx, Mathf.Max(1, structType.ny));
        }

        // Per-tile child SRs for shape-aware vertical extension (`_m` middles, `_t` top).
        // Mirrors the Structure ctor pattern. Same blueprint tint applied so the ghost reads
        // as one consistent translucent column.
        if (shapeAware && shape.nx == 1 && shape.ny > 1) {
            for (int dy = 1; dy < shape.ny; dy++) {
                GameObject extGo = new GameObject($"blueprint_{structType.name}_ext{dy}");
                extGo.transform.SetParent(go.transform, false);
                extGo.transform.localPosition = new Vector3(0f, dy, 0f);
                SpriteRenderer extSr = extGo.AddComponent<SpriteRenderer>();
                extSr.sprite = StructureVisuals.LoadShapeSprite(structType, shape, dy);
                extSr.sortingOrder = sr.sortingOrder;
                extSr.flipX = mirrored;
                extSr.color = sr.color;
                LightReceiverUtil.SetSortBucket(extSr);
            }
        }

        CreateFrameOverlay();

        // Deep-copy costs so LockGroupCostsAfterDelivery only affects this blueprint,
        // not every blueprint sharing the same StructType. Cost scales linearly with
        // shape footprint relative to shapes[0] (the authored baseline) — for the
        // platform's [1×1, 1×2, 1×3] this gives 1×, 2×, 3× the wood per height step.
        int costMul = 1, costDiv = 1;
        if (shapeAware && structType.shapes.Length > 0) {
            costMul = shape.TileCount;
            costDiv = structType.shapes[0].TileCount;
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
        GameObject frameGo = new GameObject("frame");
        frameGo.transform.SetParent(go.transform, false);
        // Centre the frame on the footprint, independent of the main blueprint GO's pivot
        // (which for legacy multi-tile buildings is the visual centre, and for depth-3 floor
        // tiles is offset by -1/8 y, and for shape-aware structures is the anchor tile).
        // The anchor tile is (x, y); the footprint extends by the chosen shape's nx,ny.
        Shape shape = Shape;
        int fnx = structType.HasShapes ? shape.nx : structType.nx;
        int fny = structType.HasShapes ? shape.ny : structType.ny;
        float fx = (fnx - 1) / 2f;
        float fy = Mathf.Max(0, fny - 1) / 2f;
        frameGo.transform.position = new Vector3(x + fx, y + fy, 0);

        int unlitLayer = GetUnlitLayer();
        if (unlitLayer >= 0) frameGo.layer = unlitLayer;
        frameSr = frameGo.AddComponent<SpriteRenderer>();
        Material unlitMat = GetUnlitOverlayMaterial();
        if (unlitMat != null) frameSr.sharedMaterial = unlitMat;
        // Default to the construct frame so the SR always has a valid sliced sprite.
        // RefreshColor swaps to the deconstruct sprite when appropriate.
        frameSr.sprite = GetConstructFrameSprite();
        frameSr.drawMode = SpriteDrawMode.Sliced;
        frameSr.size = new Vector2(fnx, Mathf.Max(1, fny));
        frameSr.sortingOrder = 101; // above the blueprint sprite (100)
    }

    // Multiplicative red tint applied to the underlying structure's sprite while a deconstruct
    // blueprint sits on this tile. Keeps the visual in sync with live sprite changes (plant
    // growth stages, harvest cycles) without duplicating the sprite on the blueprint itself.
    private static readonly Color DeconstructStructureTint = new Color(1f, 0.5f, 0.5f);

    // Returns the structure that a deconstruct blueprint targets, or null if none is present.
    // (The construct paths can't call this meaningfully — there's no target structure yet.)
    private Structure GetDeconstructTarget() {
        for (int i = 0; i < 4; i++)
            if (tile.structs[i] != null) return tile.structs[i];
        return null;
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
        if (frameSr != null) {
            frameSr.sprite = state == BlueprintState.Deconstructing
                ? GetDeconstructFrameSprite()
                : GetConstructFrameSprite();
            float a = (disabled || IsSuspended()) ? 0.5f : 1f;
            frameSr.color = new Color(1f, 1f, 1f, a);
        }

        // Tint the underlying structure red for deconstruct blueprints. Applied every RefreshColor
        // so it's idempotent and works on the load path too. Restored in Destroy() on cancel.
        if (state == BlueprintState.Deconstructing) {
            Structure target = GetDeconstructTarget();
            if (target?.sr != null) target.sr.color = DeconstructStructureTint;
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
        if (structType.isTile || structType.name == "empty" || structType.requiredTileName != null)
            return false;

        if (structType.tileRequirements != null) {
            foreach (TileRequirement req in structType.tileRequirements) {
                int effectiveDx = mirrored ? (structType.nx - 1 - req.dx) : req.dx;
                Tile t = World.instance.GetTileAt(tile.x + effectiveDx, tile.y + req.dy);
                if (t == null) return true;
                if (req.mustBeStandable && !World.instance.graph.nodes[t.x, t.y].standable) return true;
                if (req.mustHaveWater && t.water == 0) return true;
                if (req.mustBeSolidTile && !t.type.solid) return true;
            }
            // fall through to the bottom-row support check — tileRequirements is additive, not a replacement
        }

        // Support is checked along the bottom row of the footprint — only the base of the
        // column needs to rest on something solid; the rest stacks above.
        int bottomNx = structType.HasShapes ? Shape.nx : structType.nx;
        for (int i = 0; i < bottomNx; i++) {
            Node node = World.instance.graph.nodes[tile.x + i, tile.y];
            if (node != null && !node.standable) return true;
        }
        return false;
    }

    public static Blueprint CreateDeconstructBlueprint(Tile tile) {
        Structure structure = tile.structs[0] ?? tile.structs[1] ?? tile.structs[2] ?? tile.structs[3];
        if (structure == null) return null;
        Blueprint bp = new Blueprint(structure.structType, tile.x, tile.y, structure.mirrored, autoRegister: false, rotation: structure.rotation, shapeIndex: structure.shapeIndex);
        bp.state = BlueprintState.Deconstructing;
        // RefreshColor hides the blueprint's duplicate sprite and applies a red multiplicative tint
        // to the underlying structure's SR — so growth stages and other live sprite changes keep
        // rendering through the tint. No per-deconstruct sprite copy needed.
        bp.RefreshColor();
        WorkOrderManager.instance?.RegisterDeconstruct(bp);
        if (tile.building?.storage != null)
            tile.building.storage.locked = true;
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
        // Capture tile products before Construct() changes the tile type
        if (structType.isTile && structType.name == "empty" && tile.type.products != null)
            pendingOutput = new List<ItemQuantity>(tile.type.products);
        StructController.instance.Construct(structType, tile, mirrored, rotation, shapeIndex);
        // Passive research gain from constructing a tech-gated building.
        // No-op for ungated structures (floors, walls, etc.).
        ResearchSystem.instance?.AddConstructionProgress(structType.name);
        ClearBlueprintFromTiles();
        GameObject.Destroy(go);
        if (InfoPanel.instance?.obj == tile) {
            // Auto-select the newly constructed structure if one was placed (non-tile blueprints only).
            Structure newStructure = structType.isTile ? null : tile.structs[structType.depth];
            InfoPanel.instance.RebuildSelection(newStructure);
        }
    }
    public void Deconstruct() {
        StructController.instance.RemoveBlueprint(this);
        WorkOrderManager.instance?.RemoveForBlueprint(this);
        pendingOutput = new List<ItemQuantity>(); // given in asm.handleworking
        foreach (ItemQuantity cost in costs) {
            int amount = Mathf.FloorToInt(cost.quantity / 2f);
            if (amount <= 0) continue;
            // Resolve group costs (e.g. "wood") to the actual leaf item that was delivered (e.g. "pine").
            // cost.item reverts to the group after save/load, so check the inv stack directly.
            Item item = cost.item;
            if (item.children != null) {
                ItemStack delivered = inv.GetItemStack(item);
                if (delivered == null) continue; // nothing was delivered for this cost
                item = delivered.item;
            }
            pendingOutput.Add(new ItemQuantity(item, amount));
        }
        // destroy the building
        for (int i = 0; i < 4; i++) { if (tile.structs[i] != null) { tile.structs[i].Destroy(); break; } }
        // remove blueprint
        ClearBlueprintFromTiles();
        GameObject.Destroy(go);
        if (InfoPanel.instance?.obj == tile) InfoPanel.instance.RebuildSelection();
    }

    // Returns true if this is a deconstruct blueprint on a storage building and the storage still has items.
    // Deconstruction must wait until the storage is emptied by haulers.
    public bool StorageNeedsEmptying() {
        return state == BlueprintState.Deconstructing
            && tile.building?.storage != null
            && !tile.building.storage.IsEmpty();
    }

    // Returns true if completing this blueprint would cause items on the tile(s) above to lose
    // standability and fall. Uses the same logic as Navigation.GetStandability().
    public bool WouldCauseItemsFall() {
        World world = World.instance;
        // Items only sit on the very top of a structure — for shape-aware multi-tile, that's
        // tile.y + (ny-1) + 1; for legacy / non-shape, tile.y + 1 (the row above).
        int fnx = structType.HasShapes ? Shape.nx : structType.nx;
        int fny = structType.HasShapes ? Shape.ny : 1;
        int topY = tile.y + (fny - 1);
        for (int i = 0; i < fnx; i++) {
            int bx = tile.x + i, by = topY;
            Tile above = world.GetTileAt(bx, by + 1);
            if (above == null || above.inv == null || above.inv.IsEmpty()) continue;
            if (!world.graph.nodes[bx, by + 1].standable) continue;

            Tile tileBelow = world.GetTileAt(bx, by);

            bool solidTileAfter = structType.isTile
                ? Db.tileTypeByName[structType.name].solid
                : tileBelow.type.solid;

            bool anySolidTopAfter = false;
            for (int d = 0; d < 4; d++) {
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
        if (state == BlueprintState.Deconstructing && tile.building?.storage != null)
            tile.building.storage.locked = false;
        // Restore the underlying structure's sprite tint if we were colouring it red. Safe
        // to call unconditionally: structures default to white and we're the only writer.
        // Skipped on world clear since Destroy() below tears the structure GO down anyway.
        if (state == BlueprintState.Deconstructing && !WorldController.isClearing) {
            Structure target = GetDeconstructTarget();
            if (target?.sr != null) target.sr.color = Color.white;
        }
        ClearBlueprintFromTiles();
        GameObject.Destroy(go);
        if (InfoPanel.instance?.obj == tile) InfoPanel.instance.RebuildSelection();
    }

    private void ClearBlueprintFromTiles() {
        Shape shape = Shape;
        int fnx = structType.HasShapes ? shape.nx : structType.nx;
        int fny = structType.HasShapes ? shape.ny : 1;
        for (int dy = 0; dy < fny; dy++)
            for (int dx = 0; dx < fnx; dx++)
                World.instance.GetTileAt(x + dx, y + dy)?.SetBlueprintAt(structType.depth, null);
    }

    public string GetProgress(){ // for display string
        string progress = "";
        for (int i = 0; i < costs.Length; i++) {
            progress += costs[i].item.name + " " + ItemStack.FormatQ(inv.Quantity(costs[i].item), costs[i].item.discrete) + "/" + ItemStack.FormatQ(costs[i]);
        }
        if (state == BlueprintState.Constructing){
            progress += " (" + constructionProgress.ToString() + "/" + constructionCost.ToString() + ")";
        }
        return progress;
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Chunked-mesh renderer for tile bodies, overlays, and snow (Phases 2–4 of
// the chunked tile mesh migration — see plans/chunked-tile-mesh-renderer.md).
// Owns one MeshRenderer per (chunk × tile-type) for tile bodies and snow,
// plus one per (chunk × tile-type × overlayState) for overlays. No per-tile
// GameObjects: a 100×80 world now allocates a few hundred mesh renderers
// instead of ~24k SpriteRenderer GameObjects.
//
// Lifecycle: WorldController.Start creates this component immediately after
// World.Awake fires (tile array exists) and calls Initialize, which subscribes
// per-tile callbacks and allocates the chunk grid. Tile.type / overlayMask /
// snow setters mark affected chunks dirty; LateUpdate rebuilds them once per
// frame so bursty events (mining, snow accumulation) coalesce into one mesh
// upload per chunk per frame.
//
// Sorting / atlasing: see SPEC-rendering.md §Tile body rendering and the
// file-level comment in TileSpriteCache.cs for the per-type Texture2DArray
// layout. Each chunk's MeshRenderer binds its type-specific body / overlay /
// snow array and the body type's normal-map array via MaterialPropertyBlock;
// one shared chunkedMaterial drives every chunk.
public class TileMeshController : MonoBehaviour {
    public static TileMeshController instance { get; protected set; }

    public const int ChunkSize = 16;
    // OverlayState enum has 3 values (Live, Dying, Dead). One mesh per state
    // because the atlas-name suffix differs ("grass" / "grass_dying" /
    // "grass_dead"), and each name has its own Texture2DArray.
    const int NumOverlayStates = 3;

    World world;
    Transform chunksRoot;
    Material chunkedMaterial;
    int chunkLayerIndex;

    int chunksX, chunksY;

    // True after BuildWorldSizedResources has run. Used to gate the OnWorldAllocated
    // handler so the first allocation (driven by Initialize) doesn't double-build.
    bool _worldResourcesBuilt;

    // ── Tile-type maps ──────────────────────────────────────────────────
    // Solid types compacted to 0..numTypes-1. Body sortingOrder = -typeIdx,
    // matching the soft-edge contest.
    int numTypes;
    string[] indexToTypeName;
    int[]    indexToTypeId;
    string[] overlayBaseNames;          // [typeIdx] -> tile.type.overlay (null if no overlay)

    // ── Chunk layers ────────────────────────────────────────────────────
    ChunkLayer[,,]   bodyLayers;        // [cx, cy, typeIdx]
    bool[,,]         bodyDirty;
    ChunkLayer[,,,]  overlayLayers;     // [cx, cy, typeIdx, stateIdx]
    bool[,,,]        overlayDirty;
    ChunkLayer[,,]   snowLayers;        // [cx, cy, typeIdx]
    bool[,,]         snowDirty;

    // Reused per-rebuild scratch buffers — avoids per-frame GC.
    readonly List<Vector3> _verts  = new();
    readonly List<Vector2> _uvs    = new();
    readonly List<Vector2> _slices = new();
    readonly List<int>     _tris   = new();

    static readonly int MainTexArrayId = Shader.PropertyToID("_MainTexArr");
    static readonly int NormalArrayId  = Shader.PropertyToID("_NormalArr");
    static readonly int SortBucketId   = Shader.PropertyToID("_SortBucket");

    // Quad spans 1.25 world units — matches the 20×20 sprite at PPU=16 used by
    // the SR-based renderer, including the 2px overhang on each side.
    const float QUAD_HALF = 0.625f;

    // Per-layer sortingOrders. Body uses -typeIdx (computed per-mesh).
    // Overlay (grass) draws in FRONT of mice (50..64) and floor items on dirt
    // (70) so a tuft bevelling up out of the dirt occludes the feet / item base
    // resting on it, rather than the actor floating in front of the grass.
    //
    // But its LIGHTING bucket is pinned separately to GroundPlaneLightingBucket, NOT
    // derived from the 80 draw order. The normals-RT B channel carries the sort
    // bucket (LightCircle.shader), which is the *depth plane* used for front-vs-
    // behind light shaping — and grass physically lives in the ground plane even
    // though we draw it on top. Letting it derive bucket 4 from the 80 order made
    // ground-level lights treat grass as in front of them → back-lit/silhouette
    // shading (lit from the wrong side). Bucket 1 = the Tiles plane, so grass
    // light-shapes identically to the dirt body it grows from (the old bucket-2
    // value was just an artifact of grass's former sortingOrder 11 landing in the
    // 9..17 buildings band). Don't collapse this back into GetBucket(sortingOrder).
    const int OverlaySortingOrder   = 80;
    const int GroundPlaneLightingBucket = 1;  // Tiles plane — body, snow, and the grass overlay all light here even when drawn at a high sortingOrder (see note above)
    // Ground-plane draw order: snow on top, then grass (80), then tile bodies (78..74) — all in
    // FRONT of water (72) and mice (48..64) so solid terrain occludes them and submerged actors
    // read as blued. They're pinned to bucket 1 above so they still light as ground, not as
    // creatures. See SPEC-rendering.md §Water Rendering (the submersion reorder).
    const int SnowSortingOrder      = 81;
    const int BodyBaseSortingOrder  = 78;  // dirt; band is BodyBaseSortingOrder - typeIdx (78..74)

    public void Initialize(World world, Transform chunksRoot, Material chunkedMaterial, string layerName) {
        if (instance != null && instance != this) {
            Debug.LogError("TileMeshController: more than one instance — destroying duplicate.");
            Destroy(this);
            return;
        }
        instance = this;

        this.world           = world;
        this.chunksRoot      = chunksRoot;
        this.chunkedMaterial = chunkedMaterial;

        chunkLayerIndex = LayerMask.NameToLayer(layerName);
        if (chunkLayerIndex < 0) {
            Debug.LogError($"TileMeshController: layer '{layerName}' not found. " +
                            "Add it in ProjectSettings → Tags & Layers, then assign it on LightFeature's tileChunkLayer field.");
            chunkLayerIndex = 0; // fall through to Default so we at least render
        }

        BuildTypeMaps();
        BuildWorldSizedResources();

        // Listen for world re-allocation (different-size save load). The first
        // allocation went through Initialize directly; subsequent events fire from
        // SaveSystem.LoadFromJson and trigger a tear-down + rebuild at the new
        // chunk grid size.
        World.OnWorldAllocated += HandleWorldReallocated;
    }

    void OnDestroy() {
        World.OnWorldAllocated -= HandleWorldReallocated;
    }

    void HandleWorldReallocated() {
        if (!_worldResourcesBuilt) return;
        DisposeWorldSizedResources();
        BuildWorldSizedResources();
        // Per-tile callbacks fire as new content arrives via ApplySaveData →
        // RestoreTile, so we don't need an explicit "rebuild everything dirty"
        // pass here — empty tiles produce empty meshes, and the load fills them
        // in via the normal callback path.
    }

    // Allocates the chunk-grid arrays at the current world size and subscribes
    // per-tile callbacks. Called from Initialize (first-time) and from the
    // re-allocation handler (after disposing the previous resources).
    void BuildWorldSizedResources() {
        chunksX = (world.nx + ChunkSize - 1) / ChunkSize;
        chunksY = (world.ny + ChunkSize - 1) / ChunkSize;

        bodyLayers    = new ChunkLayer[chunksX, chunksY, numTypes];
        bodyDirty     = new bool[chunksX, chunksY, numTypes];
        overlayLayers = new ChunkLayer[chunksX, chunksY, numTypes, NumOverlayStates];
        overlayDirty  = new bool[chunksX, chunksY, numTypes, NumOverlayStates];
        snowLayers    = new ChunkLayer[chunksX, chunksY, numTypes];
        snowDirty     = new bool[chunksX, chunksY, numTypes];

        // Subscribe per-tile callbacks once. Each callback marks the affected
        // chunks dirty; mesh rebuild happens in LateUpdate (coalesced).
        // Old-tile subscriptions die with the GC'd Tile instances on re-allocation;
        // we re-subscribe here against the new tiles.
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile t = world.GetTileAt(x, y);
                t.RegisterCbTileTypeChanged(OnTileTypeChanged);
                t.RegisterCbOverlayChanged(OnTileOverlayChanged);
                t.RegisterCbSnowChanged(OnTileSnowChanged);
                t.RegisterCbBodyChanged(OnTileBodyChanged);
            }
        }

        _worldResourcesBuilt = true;
    }

    // Destroys all chunk meshes + GameObjects so the chunk grid can be
    // re-allocated at a new size. Unity-managed assets need explicit Destroy.
    void DisposeWorldSizedResources() {
        if (bodyLayers != null) {
            for (int cx = 0; cx < chunksX; cx++) {
                for (int cy = 0; cy < chunksY; cy++) {
                    for (int t = 0; t < numTypes; t++) {
                        DestroyChunkLayer(bodyLayers[cx, cy, t]);
                        DestroyChunkLayer(snowLayers[cx, cy, t]);
                        for (int s = 0; s < NumOverlayStates; s++)
                            DestroyChunkLayer(overlayLayers[cx, cy, t, s]);
                    }
                }
            }
        }
        bodyLayers    = null;
        bodyDirty     = null;
        overlayLayers = null;
        overlayDirty  = null;
        snowLayers    = null;
        snowDirty     = null;
        _worldResourcesBuilt = false;
    }

    void DestroyChunkLayer(ChunkLayer layer) {
        if (layer == null) return;
        if (layer.mesh != null) Destroy(layer.mesh);
        if (layer.go != null) Destroy(layer.go);
    }

    void BuildTypeMaps() {
        var solids = new List<TileType>();
        foreach (var tt in Db.tileTypes) {
            if (tt == null || !tt.solid) continue;
            solids.Add(tt);
        }
        solids.Sort((a, b) => a.id.CompareTo(b.id));
        numTypes         = solids.Count;
        indexToTypeName  = new string[numTypes];
        indexToTypeId    = new int[numTypes];
        overlayBaseNames = new string[numTypes];
        for (int i = 0; i < numTypes; i++) {
            indexToTypeName[i]  = solids[i].name;
            indexToTypeId[i]    = solids[i].id;
            overlayBaseNames[i] = solids[i].overlay; // null if the type carries no overlay
        }
    }

    // ── Tile callbacks → mark dirty ─────────────────────────────────────
    // Type flip changes bodyCardinals + nMask + cMask for self and 8 neighbours,
    // so body/overlay/snow geometry for all 9 chunk slots can shift.
    void OnTileTypeChanged(Tile t) {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++) {
                MarkBodyChunkDirty   (t.x + dx, t.y + dy);
                MarkOverlayChunkDirty(t.x + dx, t.y + dy);
                MarkSnowChunkDirty   (t.x + dx, t.y + dy);
            }
    }

    // Overlay mask / state change → body redraws (bodyCardinals + trimMask use
    // overlayBits) and overlay redraws. Only self — overlay bits never reach
    // neighbours' rendering.
    void OnTileOverlayChanged(Tile t) {
        MarkBodyChunkDirty(t.x, t.y);
        MarkOverlayChunkDirty(t.x, t.y);
    }

    // Snow flag change → snow redraws. Only self — neighbours' snow visuals
    // don't depend on this tile's snow flag (they care about their own neighbour
    // solidity, which doesn't move when the snow flag flips). A neighbour's
    // exposed-side snow re-bakes via OnTileTypeChanged's 3×3 mark instead.
    void OnTileSnowChanged(Tile t) {
        MarkSnowChunkDirty(t.x, t.y);
    }

    // Body-state change (rim-suppression mask OR bodyRenderSuppressed) → body AND
    // overlay redraw. bodyRenderSuppressed feeds neighbours' soft-edge contest
    // (they treat a suppressed cell as air for their body silhouette), so dirty
    // the full 3×3 — a neighbour in an adjacent chunk must re-bake too.
    void OnTileBodyChanged(Tile t) {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++) {
                MarkBodyChunkDirty   (t.x + dx, t.y + dy);
                MarkOverlayChunkDirty(t.x + dx, t.y + dy);
            }
    }

    void MarkBodyChunkDirty(int x, int y) {
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) return;
        int cx = x / ChunkSize, cy = y / ChunkSize;
        // Blanket-mark every type at this chunk position — a neighbour-type
        // flip can change the win/lose contest result for *other* types in
        // the same chunk too. Rebuild loops skip empties cheaply.
        for (int ti = 0; ti < numTypes; ti++) bodyDirty[cx, cy, ti] = true;
    }

    void MarkOverlayChunkDirty(int x, int y) {
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) return;
        int cx = x / ChunkSize, cy = y / ChunkSize;
        for (int ti = 0; ti < numTypes; ti++) {
            if (overlayBaseNames[ti] == null) continue;
            // Dirty all 3 states — a single cbOverlayChanged doesn't tell us
            // whether the mask or the state moved. Empty meshes for unaffected
            // states cost essentially nothing (rebuild iterates and emits 0 quads).
            for (int si = 0; si < NumOverlayStates; si++)
                overlayDirty[cx, cy, ti, si] = true;
        }
    }

    void MarkSnowChunkDirty(int x, int y) {
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) return;
        int cx = x / ChunkSize, cy = y / ChunkSize;
        for (int ti = 0; ti < numTypes; ti++) snowDirty[cx, cy, ti] = true;
    }

    // ── Rebuild (once per frame, after all callbacks have settled) ──────
    void LateUpdate() {
        if (world == null) return;
        for (int cx = 0; cx < chunksX; cx++) {
            for (int cy = 0; cy < chunksY; cy++) {
                for (int ti = 0; ti < numTypes; ti++) {
                    if (bodyDirty[cx, cy, ti])    { RebuildBodyChunk(cx, cy, ti);    bodyDirty[cx, cy, ti] = false; }
                    if (snowDirty[cx, cy, ti])    { RebuildSnowChunk(cx, cy, ti);    snowDirty[cx, cy, ti] = false; }
                    if (overlayBaseNames[ti] != null) {
                        for (int si = 0; si < NumOverlayStates; si++)
                            if (overlayDirty[cx, cy, ti, si]) {
                                RebuildOverlayChunk(cx, cy, ti, si);
                                overlayDirty[cx, cy, ti, si] = false;
                            }
                    }
                }
            }
        }
    }

    // ── Body ────────────────────────────────────────────────────────────
    void RebuildBodyChunk(int cx, int cy, int typeIdx) {
        BuildBodyGeometry(cx, cy, typeIdx);
        UploadMesh(ref bodyLayers[cx, cy, typeIdx], () => NewBodyChunkLayer(cx, cy, typeIdx));
    }

    void BuildBodyGeometry(int cx, int cy, int typeIdx) {
        ResetBuffers();
        int x0, y0, x1, y1; ChunkBoundsTiles(cx, cy, out x0, out y0, out x1, out y1);
        string typeName = indexToTypeName[typeIdx];
        int    typeId   = indexToTypeId[typeIdx];

        for (int y = y0; y < y1; y++) {
            for (int x = x0; x < x1; x++) {
                Tile t = world.GetTileAt(x, y);
                if (t == null || !t.type.solid || t.type.id != typeId) continue;
                // Excavation buildings (digging pit) draw their own receding
                // substrate sprite over the cell — skip the full body quad so
                // the dug-out region reads as an open hole. Solidity is
                // unchanged, so neighbours still bake against this as solid.
                // bodyDrawnByStructure (burrow) likewise skips the chunk body, but the cell
                // stays SOLID to neighbours + keeps its overlay — the structure re-draws this
                // body itself at a lower order so it sits behind the facade.
                if (t.bodyRenderSuppressed || t.bodyDrawnByStructure) continue;

                // Two independent masks. The silhouette contest (bodySolid/bodyWin)
                // treats a bodyRenderSuppressed neighbour as open air, so this tile
                // keeps its jagged exposed edge toward an excavation hole. The
                // lighting mask (nMask) treats a `lightAsAir` neighbour as air, so
                // edges facing a sufficiently-dug pit light as exposed cliff faces.
                // Both equal the plain solidity bake away from such neighbours, so
                // normal terrain is unaffected.
                int bodySolid = 0, bodyWin = 0;
                AccumulateTypeBoundary(t, x - 1, y,     1, ref bodySolid, ref bodyWin);
                AccumulateTypeBoundary(t, x + 1, y,     2, ref bodySolid, ref bodyWin);
                AccumulateTypeBoundary(t, x,     y - 1, 4, ref bodySolid, ref bodyWin);
                AccumulateTypeBoundary(t, x,     y + 1, 8, ref bodySolid, ref bodyWin);

                int nMask = 0;
                if (LightSolidAt(x - 1, y    )) nMask |= 1;
                if (LightSolidAt(x + 1, y    )) nMask |= 2;
                if (LightSolidAt(x,     y - 1)) nMask |= 4;
                if (LightSolidAt(x,     y + 1)) nMask |= 8;
                if (LightSolidAt(x - 1, y - 1)) nMask |= 16;
                if (LightSolidAt(x + 1, y - 1)) nMask |= 32;
                if (LightSolidAt(x - 1, y + 1)) nMask |= 64;
                if (LightSolidAt(x + 1, y + 1)) nMask |= 128;

                bool roadSuppressed = t.structs[3] != null;
                int  overlayBits    = roadSuppressed ? 0 : (t.overlayMask & 0xF);
                // bodyEdgeSuppressMask: structures (today: doored preservesTile buildings)
                // can mark a side as "draw no rim" — OR it in so the bake picks the
                // buried-side slice for that direction, hiding the 2px air bevel.
                int  bodyCardinals  = (bodySolid & ~bodyWin) | overlayBits | (t.bodyEdgeSuppressMask & 0xF);
                int  trimMask       = (overlayBits & ~bodySolid) & 0xF;

                int bodySlice = TileSpriteCache.GetBodySlice(typeName, bodyCardinals, trimMask, x, y);
                int nSlice    = TileSpriteCache.GetNormalMapSlice(typeName, nMask, x, y);
                EmitQuad(x - x0, y - y0, bodySlice, nSlice);
            }
        }
    }

    ChunkLayer NewBodyChunkLayer(int cx, int cy, int typeIdx) {
        // BodyBaseSortingOrder - typeIdx matches the soft-edge contest's per-type rank:
        // lowest-id solid type sorts highest, so its overhang covers higher-id types' Main
        // extension at boundaries. The base offset puts the whole band (78..74) in front of
        // water/mice; lighting is pinned to the ground plane so the draw-order bump doesn't
        // relight the dirt as a creature.
        int sortingOrder = BodyBaseSortingOrder - typeIdx;
        return NewChunkLayer(cx, cy,
            $"BodyChunk_{indexToTypeName[typeIdx]}",
            sortingOrder,
            TileSpriteCache.GetBodyArray(indexToTypeName[typeIdx]),
            TileSpriteCache.GetNormalMapArray(indexToTypeName[typeIdx]),
            GroundPlaneLightingBucket);
    }

    // ── Overlay ────────────────────────────────────────────────────────
    void RebuildOverlayChunk(int cx, int cy, int typeIdx, int stateIdx) {
        BuildOverlayGeometry(cx, cy, typeIdx, stateIdx);
        UploadMesh(ref overlayLayers[cx, cy, typeIdx, stateIdx], () => NewOverlayChunkLayer(cx, cy, typeIdx, stateIdx));
    }

    void BuildOverlayGeometry(int cx, int cy, int typeIdx, int stateIdx) {
        ResetBuffers();
        string baseOverlay = overlayBaseNames[typeIdx];
        if (baseOverlay == null) return;
        string overlayName = OverlayAtlasName(baseOverlay, stateIdx);

        int x0, y0, x1, y1; ChunkBoundsTiles(cx, cy, out x0, out y0, out x1, out y1);
        int typeId = indexToTypeId[typeIdx];
        string typeName = indexToTypeName[typeIdx];

        for (int y = y0; y < y1; y++) {
            for (int x = x0; x < x1; x++) {
                Tile t = world.GetTileAt(x, y);
                if (t == null || !t.type.solid || t.type.id != typeId) continue;
                if (t.bodyRenderSuppressed) continue; // dug-up cell: no grass over the hole
                if (t.overlayMask == 0) continue;
                if ((int)t.overlayState != stateIdx) continue;
                // Roads visually replace the ground surface — suppress overlay
                // wholesale on roaded tiles.
                if (t.structs[3] != null) continue;

                // cMask = bit set when neighbour is solid (overlay-buried side).
                int cMask = 0;
                if (IsSolidAt(x - 1, y))     cMask |= 1;
                if (IsSolidAt(x + 1, y))     cMask |= 2;
                if (IsSolidAt(x,     y - 1)) cMask |= 4;
                if (IsSolidAt(x,     y + 1)) cMask |= 8;

                int effective = (t.overlayMask & ~cMask & ~t.bodyEdgeSuppressMask) & 0xF;
                if (effective == 0) continue; // every overlay side is buried

                // Normal map shares the body type's normal — same nMask the body uses,
                // so the directional edge bevel is consistent across body+overlay.
                int nMask = cMask;
                if (IsSolidAt(x - 1, y - 1)) nMask |= 16;
                if (IsSolidAt(x + 1, y - 1)) nMask |= 32;
                if (IsSolidAt(x - 1, y + 1)) nMask |= 64;
                if (IsSolidAt(x + 1, y + 1)) nMask |= 128;

                // Inverted-cardinal mask: GetOverlay expects "which sides are NOT
                // decorated" (the baker reads bit-set ⇒ Main interior, and overlay
                // bakes force Main transparent, so bit-set ⇒ transparent here).
                int invertedCardinals = (~effective) & 0xF;
                int overlaySlice = TileSpriteCache.GetOverlaySlice(overlayName, invertedCardinals, x, y);
                int nSlice       = TileSpriteCache.GetNormalMapSlice(typeName, nMask, x, y);
                EmitQuad(x - x0, y - y0, overlaySlice, nSlice);
            }
        }
    }

    ChunkLayer NewOverlayChunkLayer(int cx, int cy, int typeIdx, int stateIdx) {
        string baseOverlay = overlayBaseNames[typeIdx];
        string overlayName = OverlayAtlasName(baseOverlay, stateIdx);
        return NewChunkLayer(cx, cy,
            $"OverlayChunk_{indexToTypeName[typeIdx]}_{((OverlayState)stateIdx)}",
            OverlaySortingOrder,
            TileSpriteCache.GetOverlayArray(overlayName),
            TileSpriteCache.GetNormalMapArray(indexToTypeName[typeIdx]),
            GroundPlaneLightingBucket);  // light as ground plane despite the 80 draw order
    }

    static string OverlayAtlasName(string baseName, int stateIdx) {
        switch ((OverlayState)stateIdx) {
            case OverlayState.Dying: return baseName + "_dying";
            case OverlayState.Dead:  return baseName + "_dead";
            default:                 return baseName;
        }
    }

    // ── Snow ───────────────────────────────────────────────────────────
    void RebuildSnowChunk(int cx, int cy, int typeIdx) {
        BuildSnowGeometry(cx, cy, typeIdx);
        UploadMesh(ref snowLayers[cx, cy, typeIdx], () => NewSnowChunkLayer(cx, cy, typeIdx));
    }

    void BuildSnowGeometry(int cx, int cy, int typeIdx) {
        ResetBuffers();
        int x0, y0, x1, y1; ChunkBoundsTiles(cx, cy, out x0, out y0, out x1, out y1);
        int typeId = indexToTypeId[typeIdx];
        string typeName = indexToTypeName[typeIdx];

        for (int y = y0; y < y1; y++) {
            for (int x = x0; x < x1; x++) {
                Tile t = world.GetTileAt(x, y);
                if (t == null || !t.type.solid || t.type.id != typeId) continue;
                if (!t.snow) continue;

                // Strict bounds check (IsSolidAt would falsely treat off-map as
                // solid). Top-row tiles still render snow even though there's
                // technically no tile above — open sky is the intent.
                int yAbove = y + 1;
                bool buriedAbove = yAbove < world.ny && world.GetTileAt(x, yAbove).type.solid;
                if (buriedAbove) continue;

                int nMask = 0;
                if (IsSolidAt(x - 1, y))     nMask |= 1;
                if (IsSolidAt(x + 1, y))     nMask |= 2;
                if (IsSolidAt(x,     y - 1)) nMask |= 4;
                if (IsSolidAt(x,     y + 1)) nMask |= 8;
                if (IsSolidAt(x - 1, y - 1)) nMask |= 16;
                if (IsSolidAt(x + 1, y - 1)) nMask |= 32;
                if (IsSolidAt(x - 1, y + 1)) nMask |= 64;
                if (IsSolidAt(x + 1, y + 1)) nMask |= 128;

                // Snow caps the top and wraps onto any exposed vertical face, so a
                // cliff-edge block shows snow clinging to its open side rather than
                // just a flat top line. Bottom is never decorated — snow doesn't
                // hang from an underside (and the atlas has no bottom-edge art).
                // Decorated bits (LRDU): U always (a snow tile has open sky above);
                // L/R only where that neighbour is open air. GetOverlaySlice wants
                // the inverted mask (bit SET = NOT decorated), so invert at the end.
                int decorated = 0b1000;                    // U (top)
                if (!IsSolidAt(x - 1, y)) decorated |= 1;   // L exposed
                if (!IsSolidAt(x + 1, y)) decorated |= 2;   // R exposed
                int snowCardinals = (~decorated) & 0xF;

                int snowSlice = TileSpriteCache.GetOverlaySlice("snow", snowCardinals, x, y);
                int nSlice    = TileSpriteCache.GetNormalMapSlice(typeName, nMask, x, y);
                EmitQuad(x - x0, y - y0, snowSlice, nSlice);
            }
        }
    }

    ChunkLayer NewSnowChunkLayer(int cx, int cy, int typeIdx) {
        return NewChunkLayer(cx, cy,
            $"SnowChunk_{indexToTypeName[typeIdx]}",
            SnowSortingOrder,
            TileSpriteCache.GetOverlayArray("snow"),
            TileSpriteCache.GetNormalMapArray(indexToTypeName[typeIdx]),
            GroundPlaneLightingBucket);  // draws at 81 but lights as ground, like the overlay
    }

    // ── Generic mesh / chunk plumbing ───────────────────────────────────
    void ResetBuffers() {
        _verts.Clear();
        _uvs.Clear();
        _slices.Clear();
        _tris.Clear();
    }

    void EmitQuad(float lx, float ly, int spriteSlice, int normalSlice) {
        int vi = _verts.Count;
        _verts.Add(new Vector3(lx - QUAD_HALF, ly - QUAD_HALF, 0));
        _verts.Add(new Vector3(lx + QUAD_HALF, ly - QUAD_HALF, 0));
        _verts.Add(new Vector3(lx + QUAD_HALF, ly + QUAD_HALF, 0));
        _verts.Add(new Vector3(lx - QUAD_HALF, ly + QUAD_HALF, 0));
        _uvs.Add(new Vector2(0, 0));
        _uvs.Add(new Vector2(1, 0));
        _uvs.Add(new Vector2(1, 1));
        _uvs.Add(new Vector2(0, 1));
        var slice = new Vector2(spriteSlice, normalSlice);
        _slices.Add(slice); _slices.Add(slice); _slices.Add(slice); _slices.Add(slice);
        // CCW winding — keeps NormalsCapture's worldT/worldB derivation consistent.
        _tris.Add(vi); _tris.Add(vi + 2); _tris.Add(vi + 1);
        _tris.Add(vi); _tris.Add(vi + 3); _tris.Add(vi + 2);
    }

    void UploadMesh(ref ChunkLayer layer, System.Func<ChunkLayer> factory) {
        if (_verts.Count == 0) {
            // No tiles in this layer for this chunk — leave the GO inactive
            // (or skip allocation entirely if no layer exists yet).
            if (layer != null && layer.go.activeSelf) layer.go.SetActive(false);
            return;
        }
        if (layer == null) layer = factory();
        if (!layer.go.activeSelf) layer.go.SetActive(true);

        var mesh = layer.mesh;
        mesh.Clear();
        mesh.SetVertices(_verts);
        mesh.SetUVs(0, _uvs);
        mesh.SetUVs(1, _slices);
        mesh.SetTriangles(_tris, 0);
        mesh.bounds = ChunkBounds();
    }

    // lightingBucket < 0 → derive the bucket from sortingOrder (body/snow, where
    // draw order and light depth-plane agree). Pass a bucket explicitly to
    // decouple them — the overlay draws far in front (80) but must light as the
    // ground plane (see GroundPlaneLightingBucket note). The bucket is written into
    // the normals-RT B channel and drives front-vs-behind shaping in LightCircle.
    ChunkLayer NewChunkLayer(int cx, int cy, string namePrefix, int sortingOrder,
                              Texture2DArray mainArray, Texture2DArray normalArray,
                              int lightingBucket = -1) {
        GameObject go = new GameObject($"{namePrefix}_{cx}_{cy}");
        go.transform.SetParent(chunksRoot, false);
        go.transform.localPosition = new Vector3(cx * ChunkSize, cy * ChunkSize, 0);
        go.layer = chunkLayerIndex;

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = chunkedMaterial;
        mr.sortingOrder = sortingOrder;

        var mesh = new Mesh { name = $"{namePrefix}_{cx}_{cy}" };
        mesh.indexFormat = IndexFormat.UInt32;
        mf.sharedMesh = mesh;

        // Per-renderer MPB: tile-type's body/overlay/snow array + the body
        // type's normal-map array + sort bucket (read per-pixel by
        // ChunkedNormalsCapture for sort-aware lighting). The MPB on chunks
        // is unavoidable (per-chunk Texture2DArray bindings) — chunked
        // tiles are deliberately outside Phase 4's renderingLayerMask
        // bucket-loop scheme. They still must write their sort bucket on
        // the SAME scale the rest of the system uses (SortBucketUtil),
        // otherwise lights compare against tiles on different scales and
        // sort-aware lighting flips sign — see LightSource.sortBucket.
        var mpb = new MaterialPropertyBlock();
        mpb.SetTexture(MainTexArrayId, mainArray);
        mpb.SetTexture(NormalArrayId,  normalArray);
        int bucket = lightingBucket >= 0 ? lightingBucket : SortBucketUtil.GetBucket(sortingOrder);
        mpb.SetFloat(SortBucketId, SortBucketUtil.BucketToNormalized(bucket));
        mr.SetPropertyBlock(mpb);

        return new ChunkLayer { go = go, mf = mf, mr = mr, mesh = mesh };
    }

    void ChunkBoundsTiles(int cx, int cy, out int x0, out int y0, out int x1, out int y1) {
        x0 = cx * ChunkSize;
        y0 = cy * ChunkSize;
        x1 = Mathf.Min(x0 + ChunkSize, world.nx);
        y1 = Mathf.Min(y0 + ChunkSize, world.ny);
    }

    // ── Helpers ────────────────────────────────────────────────────────
    // Soft-edge contest for the body silhouette. A bodyRenderSuppressed neighbour
    // (excavation hole) reads as open air, so this tile draws its exposed/jagged
    // edge toward it rather than a buried seam.
    void AccumulateTypeBoundary(Tile tile, int nx, int ny, int bit, ref int bodySolid, ref int bodyWin) {
        if (nx < 0 || nx >= world.nx || ny < 0 || ny >= world.ny) {
            bodySolid |= bit;   // off-map: solid (no jagged edge at the world border)
            return;
        }
        Tile n = world.GetTileAt(nx, ny);
        if (n == null || !n.type.solid || n.bodyRenderSuppressed) return;
        bodySolid |= bit;
        if (n.type == tile.type) return;
        if (tile.type.id < n.type.id) bodyWin |= bit;
    }

    bool IsSolidAt(int x, int y) {
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) return true;
        Tile t = world.GetTileAt(x, y);
        return t != null && t.type.solid;
    }

    // Solidity as seen by the normal-map (lighting) bake: a `lightAsAir` cell
    // (a sufficiently-excavated pit) reads as open air so neighbours light their
    // facing edges as exposed cliffs. Off-map reads solid (no border glow).
    bool LightSolidAt(int x, int y) {
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) return true;
        Tile t = world.GetTileAt(x, y);
        return t != null && t.type.solid && !t.lightAsAir;
    }

    static Bounds ChunkBounds() {
        // Chunk-local: spans (0, 0) to (ChunkSize, ChunkSize), padded by the
        // 0.625-unit overhang each side. Set explicitly so culling is correct
        // without per-frame RecalculateBounds.
        const float PAD = QUAD_HALF;
        Vector3 size   = new Vector3(ChunkSize + 2 * PAD, ChunkSize + 2 * PAD, 1);
        Vector3 center = new Vector3(ChunkSize * 0.5f, ChunkSize * 0.5f, 0);
        return new Bounds(center, size);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; }

    class ChunkLayer {
        public GameObject   go;
        public MeshFilter   mf;
        public MeshRenderer mr;
        public Mesh         mesh;
    }
}

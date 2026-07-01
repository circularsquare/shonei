using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Chunked-mesh renderer for BACKGROUND walls — the parallel of TileMeshController
// for the back wall behind the foreground tiles (sortingOrder -10 / -11). Replaces
// the old full-screen BackgroundTile mask sprite, whose shader step()-picked between
// flat wall textures and so produced a hard straight-line cutoff between Stone and
// Dirt walls (and against sky). Here each background type ships a 32×32 9-piece atlas
// baked into a Texture2DArray (same pipeline as foreground tiles via TileSpriteCache),
// and a per-tile cMask + lower-id-wins contest gives autotiled soft edges for free —
// against sky AND between wall types — exactly like the foreground body bake.
//
// Design notes:
//  - Body-only. No overlay / snow / trim / normal-map layers (background is flat-lit
//    in v1: the capture override writes a flat normal + alpha 0.5).
//  - Visible pass reuses the foreground chunked material (Custom/ChunkedTileSprite —
//    it only samples _MainTexArr, which we bind per-chunk to the background array).
//    The normals capture uses a dedicated flat override (Hidden/ChunkedBackgroundNormalsCapture),
//    which LightFeature draws for the Background layer (reused — the old mask sprite is retired).
//  - backgroundTile rarely changes after worldgen (only a quarry/pit exhausting a wall),
//    so cbBackgroundChanged fires mostly during gen / load — the dirty+rebuild machinery
//    is cheap. We mirror it (rather than build-once) so load and different-size-save
//    re-allocation are handled identically to TileMeshController.
//  - Dormant until its atlases exist: if neither a baked asset nor a source PNG is
//    present for a background type, that type is skipped; if none are present the whole
//    controller stays idle (so committing this ahead of the art never renders magenta).
//
// A shared chunked-mesh base could be extracted from this + TileMeshController if a
// third chunked layer appears — today the two are small enough to keep separate.
public class BackgroundTileMeshController : MonoBehaviour {
    public static BackgroundTileMeshController instance { get; private set; }
    // True once this renderer has live background atlases and built its chunk grid.
    // The legacy BackgroundTile mask sprite reads this to hide itself, so the two
    // never double-draw at sortingOrder -10 during the hand-off.
    public bool Active { get; private set; }

    // Matches TileMeshController.ChunkSize so the two chunk grids align.
    const int ChunkSize = 16;
    // Background sits at -10 (matches the retired BackgroundTile sprite); each
    // additional type ranks one lower so the contest winner draws on top.
    const int BaseSortingOrder = -10;
    // Quad spans 1.25 world units — same 20×20-at-PPU16 footprint + 2px overhang
    // as the foreground tile quads, so edge art straddles tile boundaries identically.
    const float QUAD_HALF = 0.625f;

    World world;
    Transform chunksRoot;
    Material chunkedMaterial;     // reused Custom/ChunkedTileSprite material (visible pass)
    int chunkLayerIndex;

    int chunksX, chunksY;
    bool _worldResourcesBuilt;

    static readonly int MainTexArrayId = Shader.PropertyToID("_MainTexArr");
    static readonly int SortBucketId   = Shader.PropertyToID("_SortBucket");

    // ── Background-atlas maps ────────────────────────────────────────────
    // One render group per distinct background-wall atlas declared by the tile types
    // (TileType.backgroundAtlas — limestone→"limestoneback", dirt→"dirtback", etc).
    // The contest id orders the soft edge: the lower id wins (draws its air-edge art
    // overhanging the neighbour) and gets the higher sortingOrder. id = the smallest
    // tile-type id using that atlas, so the ordering is deterministic (purely aesthetic).
    // Groups are keyed by atlas string, so two materials sharing an atlas render as one
    // seamless wall.

    // Available (art present) atlases, compacted to 0..numTypes-1 ascending by id.
    int      numTypes;
    string[] indexToAtlas;
    int[]    indexToId;

    // ── Chunk layers ─────────────────────────────────────────────────────
    ChunkLayer[,,] bodyLayers;   // [cx, cy, typeIdx]
    bool[,,]       bodyDirty;

    // Reused per-rebuild scratch buffers — avoids per-frame GC.
    readonly List<Vector3> _verts  = new();
    readonly List<Vector2> _uvs    = new();
    readonly List<Vector2> _slices = new();
    readonly List<int>     _tris   = new();

    public void Initialize(World world, Transform chunksRoot, Material chunkedMaterial, string layerName) {
        if (instance != null && instance != this) {
            Debug.LogError("BackgroundTileMeshController: more than one instance — destroying duplicate.");
            Destroy(this);
            return;
        }
        instance = this;

        this.world           = world;
        this.chunksRoot      = chunksRoot;
        this.chunkedMaterial = chunkedMaterial;

        chunkLayerIndex = LayerMask.NameToLayer(layerName);

        BuildTypeMaps();
        if (numTypes == 0) {
            // No background atlases drawn/baked yet — stay dormant rather than render
            // magenta fallbacks. Activates automatically once the art exists next play.
            Debug.Log("BackgroundTileMeshController: no background atlases found (e.g. " +
                      "Resources/Sprites/Tiles/Sheets/limestoneback.png) — chunked background renderer idle.");
            return;
        }
        if (chunkLayerIndex < 0) {
            Debug.LogError($"BackgroundTileMeshController: layer '{layerName}' not found. Add it in " +
                            "ProjectSettings → Tags & Layers, assign it on LightFeature.backgroundTileChunkLayer, " +
                            "and include it in the camera culling mask.");
            chunkLayerIndex = 0; // fall through to Default so we at least render
        }

        BuildWorldSizedResources();
        World.OnWorldAllocated += HandleWorldReallocated;
        Active = true;
    }

    void OnDestroy() {
        World.OnWorldAllocated -= HandleWorldReallocated;
    }

    void HandleWorldReallocated() {
        if (!_worldResourcesBuilt) return;
        DisposeWorldSizedResources();
        BuildWorldSizedResources();
        // Per-tile callbacks fire as content arrives via ApplySaveData → RestoreTile,
        // so the new chunks fill in through the normal dirty path — no explicit rebuild.
    }

    // Enumerate the distinct background-wall atlases declared by tile types (stone + earth
    // materials). Each becomes a render group; only atlases whose art is present are kept,
    // so committing ahead of missing art never renders magenta.
    void BuildTypeMaps() {
        var idByAtlas = new Dictionary<string, int>();
        foreach (var tt in Db.tileTypeByName.Values) {
            if (string.IsNullOrEmpty(tt.backgroundAtlas)) continue;
            if (!ArtAvailable(tt.backgroundAtlas)) continue;
            if (!idByAtlas.TryGetValue(tt.backgroundAtlas, out int cur) || tt.id < cur)
                idByAtlas[tt.backgroundAtlas] = tt.id;
        }
        var avail = new List<KeyValuePair<string, int>>(idByAtlas);
        avail.Sort((a, b) => a.Value.CompareTo(b.Value));

        numTypes     = avail.Count;
        indexToAtlas = new string[numTypes];
        indexToId    = new int[numTypes];
        for (int i = 0; i < numTypes; i++) {
            indexToAtlas[i] = avail[i].Key;
            indexToId[i]    = avail[i].Value;
        }
    }

    // True if a baked array OR a source atlas/flat sprite exists for this stem.
    static bool ArtAvailable(string atlas) {
        return Resources.Load<Texture2DArray>($"BakedTileAtlases/{atlas}_body") != null
            || Resources.Load<Texture2D>($"Sprites/Tiles/Sheets/{atlas}") != null
            || Resources.Load<Texture2D>($"Sprites/Tiles/{atlas}") != null;
    }

    void BuildWorldSizedResources() {
        chunksX = (world.nx + ChunkSize - 1) / ChunkSize;
        chunksY = (world.ny + ChunkSize - 1) / ChunkSize;

        bodyLayers = new ChunkLayer[chunksX, chunksY, numTypes];
        bodyDirty  = new bool[chunksX, chunksY, numTypes];

        for (int x = 0; x < world.nx; x++)
            for (int y = 0; y < world.ny; y++)
                world.GetTileAt(x, y).RegisterCbBackgroundChanged(OnBackgroundChanged);

        _worldResourcesBuilt = true;
    }

    void DisposeWorldSizedResources() {
        if (bodyLayers != null) {
            for (int cx = 0; cx < chunksX; cx++)
                for (int cy = 0; cy < chunksY; cy++)
                    for (int t = 0; t < numTypes; t++)
                        DestroyChunkLayer(bodyLayers[cx, cy, t]);
        }
        bodyLayers = null;
        bodyDirty  = null;
        _worldResourcesBuilt = false;
    }

    void DestroyChunkLayer(ChunkLayer layer) {
        if (layer == null) return;
        if (layer.mesh != null) Destroy(layer.mesh);
        if (layer.go != null) Destroy(layer.go);
    }

    // ── Tile callback → mark dirty ───────────────────────────────────────
    // A background-type change shifts this tile's cMask AND its neighbours' contest
    // results (a different-type neighbour flips who wins the shared edge), so dirty
    // the full 3×3 — a neighbour in an adjacent chunk must re-bake too. Blanket-mark
    // every type at each position since the contest can move quads between types.
    void OnBackgroundChanged(Tile t) {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                MarkChunkDirty(t.x + dx, t.y + dy);
    }

    void MarkChunkDirty(int x, int y) {
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) return;
        int cx = x / ChunkSize, cy = y / ChunkSize;
        for (int ti = 0; ti < numTypes; ti++) bodyDirty[cx, cy, ti] = true;
    }

    // ── Rebuild (once per frame, after callbacks settle) ─────────────────
    void LateUpdate() {
        if (world == null || !_worldResourcesBuilt) return;
        for (int cx = 0; cx < chunksX; cx++)
            for (int cy = 0; cy < chunksY; cy++)
                for (int ti = 0; ti < numTypes; ti++)
                    if (bodyDirty[cx, cy, ti]) {
                        RebuildChunk(cx, cy, ti);
                        bodyDirty[cx, cy, ti] = false;
                    }
    }

    void RebuildChunk(int cx, int cy, int typeIdx) {
        BuildGeometry(cx, cy, typeIdx);
        UploadMesh(ref bodyLayers[cx, cy, typeIdx], () => NewChunkLayer(cx, cy, typeIdx));
    }

    void BuildGeometry(int cx, int cy, int typeIdx) {
        ResetBuffers();
        int x0, y0, x1, y1; ChunkBoundsTiles(cx, cy, out x0, out y0, out x1, out y1);
        string atlas = indexToAtlas[typeIdx];

        for (int y = y0; y < y1; y++) {
            for (int x = x0; x < x1; x++) {
                Tile t = world.GetTileAt(x, y);
                if (t == null || t.backgroundTile == null || t.backgroundTile.backgroundAtlas != atlas) continue;

                int bgSolid = 0, bgWin = 0;
                AccumulateBoundary(t, x - 1, y,     1, ref bgSolid, ref bgWin);
                AccumulateBoundary(t, x + 1, y,     2, ref bgSolid, ref bgWin);
                AccumulateBoundary(t, x,     y - 1, 4, ref bgSolid, ref bgWin);
                AccumulateBoundary(t, x,     y + 1, 8, ref bgSolid, ref bgWin);

                int cardinals = bgSolid & ~bgWin;
                int slice     = TileSpriteCache.GetBodySlice(atlas, cardinals, 0, x, y);
                EmitQuad(x - x0, y - y0, slice);
            }
        }
    }

    // Soft-edge contest for the background silhouette, analogous to
    // TileMeshController.AccumulateTypeBoundary with "solid" → "has a wall":
    //   off-map        → buried (wall continues past the world edge; no air rim)
    //   no wall (sky)  → exposed (bit unset → draws air-edge art against sky)
    //   same type      → buried (seamless Main interior)
    //   different type → buried, but if I'm the lower id I WIN this side: I draw the
    //                    air-edge piece overhanging into the loser, sortingOrder on top.
    void AccumulateBoundary(Tile tile, int nx, int ny, int bit, ref int bgSolid, ref int bgWin) {
        if (nx < 0 || nx >= world.nx || ny < 0 || ny >= world.ny) {
            bgSolid |= bit;
            return;
        }
        Tile n = world.GetTileAt(nx, ny);
        if (n == null || n.backgroundTile == null) return;
        bgSolid |= bit;
        string myAtlas = tile.backgroundTile.backgroundAtlas;
        string nAtlas  = n.backgroundTile.backgroundAtlas;
        if (nAtlas == myAtlas) return;
        if (AtlasId(myAtlas) < AtlasId(nAtlas)) bgWin |= bit;
    }

    // Contest id for a background atlas (lower wins the shared edge). Returns int.MaxValue
    // for any atlas not in the available set so it never wins against a rendered neighbour.
    int AtlasId(string atlas) {
        for (int i = 0; i < numTypes; i++)
            if (indexToAtlas[i] == atlas) return indexToId[i];
        return int.MaxValue;
    }

    // ── Mesh / chunk plumbing ────────────────────────────────────────────
    void ResetBuffers() {
        _verts.Clear();
        _uvs.Clear();
        _slices.Clear();
        _tris.Clear();
    }

    // Background quads carry only a body slice; .y of the slice attribute is unused
    // (the flat capture override reads only .x, the visible shader only .x).
    void EmitQuad(float lx, float ly, int spriteSlice) {
        int vi = _verts.Count;
        _verts.Add(new Vector3(lx - QUAD_HALF, ly - QUAD_HALF, 0));
        _verts.Add(new Vector3(lx + QUAD_HALF, ly - QUAD_HALF, 0));
        _verts.Add(new Vector3(lx + QUAD_HALF, ly + QUAD_HALF, 0));
        _verts.Add(new Vector3(lx - QUAD_HALF, ly + QUAD_HALF, 0));
        _uvs.Add(new Vector2(0, 0));
        _uvs.Add(new Vector2(1, 0));
        _uvs.Add(new Vector2(1, 1));
        _uvs.Add(new Vector2(0, 1));
        var slice = new Vector2(spriteSlice, 0);
        _slices.Add(slice); _slices.Add(slice); _slices.Add(slice); _slices.Add(slice);
        _tris.Add(vi); _tris.Add(vi + 2); _tris.Add(vi + 1);
        _tris.Add(vi); _tris.Add(vi + 3); _tris.Add(vi + 2);
    }

    void UploadMesh(ref ChunkLayer layer, System.Func<ChunkLayer> factory) {
        if (_verts.Count == 0) {
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

    ChunkLayer NewChunkLayer(int cx, int cy, int typeIdx) {
        // Lower-id type ranks first (typeIdx 0 after the id-ascending sort) and sorts
        // highest, so its overhang covers the higher-id type's Main at a wall seam.
        int sortingOrder = BaseSortingOrder - typeIdx;

        GameObject go = new GameObject($"BgChunk_{indexToAtlas[typeIdx]}_{cx}_{cy}");
        go.transform.SetParent(chunksRoot, false);
        go.transform.localPosition = new Vector3(cx * ChunkSize, cy * ChunkSize, 0);
        go.layer = chunkLayerIndex;

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = chunkedMaterial;
        mr.sortingOrder   = sortingOrder;

        var mesh = new Mesh { name = $"BgChunk_{indexToAtlas[typeIdx]}_{cx}_{cy}" };
        mesh.indexFormat = IndexFormat.UInt32;
        mf.sharedMesh = mesh;

        // Per-chunk MPB: this type's background body array + sort bucket (read per-pixel
        // by ChunkedBackgroundNormalsCapture for sort-aware lighting, on the same
        // SortBucketUtil scale as every other receiver). No normal array — background
        // is flat-lit in v1.
        var mpb = new MaterialPropertyBlock();
        mpb.SetTexture(MainTexArrayId, TileSpriteCache.GetBodyArray(indexToAtlas[typeIdx]));
        mpb.SetFloat(SortBucketId, SortBucketUtil.BucketToNormalized(SortBucketUtil.GetBucket(sortingOrder)));
        mr.SetPropertyBlock(mpb);

        return new ChunkLayer { go = go, mf = mf, mr = mr, mesh = mesh };
    }

    void ChunkBoundsTiles(int cx, int cy, out int x0, out int y0, out int x1, out int y1) {
        x0 = cx * ChunkSize;
        y0 = cy * ChunkSize;
        x1 = Mathf.Min(x0 + ChunkSize, world.nx);
        y1 = Mathf.Min(y0 + ChunkSize, world.ny);
    }

    static Bounds ChunkBounds() {
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

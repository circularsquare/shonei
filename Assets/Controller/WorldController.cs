using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using System;

// this class handles tile sprites, and also places initial objects into world.
public class WorldController : MonoBehaviour {
    public static WorldController instance {get; protected set;}

    // Test hook: when true, Start() skips auto-loading the most recent save and
    // generates a fresh world instead. Snapshot tests set this so they get a
    // deterministic, save-independent starting state. Default false (production).
    public static bool skipAutoLoad = false;
    public World world {get; protected set;}
    public Transform tilesTransform;
    Dictionary<Tile, GameObject> tileGameObjectMap;
    // Per-tile overlay SpriteRenderer (grass on dirt; future moss on stone, etc.).
    // One per tile; sprite is null when the tile has no overlayMask, no overlay
    // sheet, or is covered by a road. See OnTileOverlayChanged.
    Dictionary<Tile, SpriteRenderer> tileOverlaySrMap;
    // Per-tile snow SpriteRenderer. Orthogonal to the grass overlay — snow lands
    // on any solid tile and is driven by SnowAccumulationSystem. See OnTileSnowChanged.
    Dictionary<Tile, SpriteRenderer> tileSnowSrMap;
    Coroutine defaultSetupCoroutine;
    Material tileMaterial; // Custom/TileSprite shader for tiles
    // Inspector-assigned so the shader is part of the scene's serialized graph
    // and force-included in builds. Shader.Find() works in the editor but the
    // build pipeline strips shaders that aren't reachable from a Material asset.
    [SerializeField] Shader tileShader;

    // FRAME 0: runs up to the first yield, pausing to let all other Start()s finish.
    // FRAME 1: resumes and calls GenerateDefault() (or waits for save/reset to do so).
    IEnumerator Start() {
        if (instance != null) {
            Debug.LogError("there should only be one world controller");}
        instance = this;

        Application.runInBackground = true;

        // Replicate the 2D Renderer's Y-axis sprite sorting under URP Universal
        // Renderer. UniversalRendererData has no transparency-sort field, and
        // URP hides the project-level Graphics setting from the Inspector when
        // an SRP is active. Without this, two sprites sharing a sortingOrder
        // (e.g. two animals at the same world position) could swap draw order
        // between frames. Almost every sprite in this project sets an explicit
        // sortingOrder per SPEC-rendering.md, so this is mostly belt-and-braces.
        GraphicsSettings.transparencySortMode = TransparencySortMode.CustomAxis;
        GraphicsSettings.transparencySortAxis = new Vector3(0f, 1f, 0f);

        world = this.gameObject.AddComponent<World>(); // add world

        tileGameObjectMap = new Dictionary<Tile, GameObject>();
        tileOverlaySrMap = new Dictionary<Tile, SpriteRenderer>();
        tileSnowSrMap = new Dictionary<Tile, SpriteRenderer>();
        tilesTransform = transform.Find("Tiles");

        // Create material with Custom/TileSprite shader.
        if (tileShader != null) tileMaterial = new Material(tileShader);
        else Debug.LogError("WorldController: tileShader unassigned in Inspector — assign Custom/TileSprite (Assets/Lighting/TileSprite.shader)");

        // Register callbacks on every tile. GameObjects are created lazily —
        // body GOs only for solid tiles (see EnsureBodyGO via OnTileTypeChanged),
        // overlay/snow SRs only when grass or snow actually wants to render
        // (see Ensure*SR called from OnTileOverlayChanged/OnTileSnowChanged).
        // For a 100×80 world this drops load-time GO allocations from ~24k to
        // whatever the initial solid-tile count is (typically <half), plus a
        // handful as grass/snow appear later.
        for (int x = 0; x < world.nx; x++){
            for (int y = world.ny - 1; y >= 0; y--){
                Tile tile = world.GetTileAt(x, y);
                tile.RegisterCbTileTypeChanged(OnTileTypeChanged);
                tile.RegisterCbOverlayChanged(OnTileOverlayChanged);
                tile.RegisterCbSnowChanged(OnTileSnowChanged);
            }
        }

        yield return null; // wait one frame so all other Start()s finish before we generate the world
        string mostRecent = skipAutoLoad ? null : SaveSystem.instance.GetMostRecentSlot();
        if (mostRecent != null) {
            SaveSystem.instance.Load(mostRecent);
        } else {
            GenerateDefault();
            StartCoroutine(SaveSystem.instance.PostLoadInit());
        }
        World.OnItemFall += SpawnItemFallAnimation;
    }

    void OnDestroy() {
        World.OnItemFall -= SpawnItemFallAnimation;
    }

    void SpawnItemFallAnimation(int srcX, int srcY, int dstX, int dstY, Item item) {
        StartCoroutine(ItemFallAnimCoroutine(srcX, srcY, dstX, dstY, item));
    }

    IEnumerator ItemFallAnimCoroutine(int srcX, int srcY, int dstX, int dstY, Item item) {
        string iName = item.name.Trim().Replace(" ", "");
        Sprite sprite = Resources.Load<Sprite>($"Sprites/Items/split/{iName}/floor");
        sprite ??= Resources.Load<Sprite>($"Sprites/Items/split/{iName}/icon");
        sprite ??= Resources.Load<Sprite>("Sprites/Items/split/default/icon");

        GameObject go = new GameObject("FallAnim_" + iName);
        go.transform.SetParent(transform, true);
        SpriteRenderer sr = SpriteMaterialUtil.AddSpriteRenderer(go);
        sr.sprite = sprite;
        sr.sortingOrder = 65; // below floor items (sortingOrder 70)
        LightReceiverUtil.SetSortBucket(sr);

        Vector3 start = new Vector3(srcX, srcY, 0);
        Vector3 end   = new Vector3(dstX, dstY, 0);
        go.transform.position = start;

        float dist = srcY - dstY;
        float duration = World.fallSecondsPerTile * dist;
        float elapsed = 0f;
        while (elapsed < duration) {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            go.transform.position = Vector3.Lerp(start, end, t * t); // t² = gravity ease-in
            yield return null;
        }

        Destroy(go);
    }

    // -----------------------------------------------------------------------
    // WORLD LIFECYCLE
    // -----------------------------------------------------------------------

    public static bool isClearing = false;

    public void ClearWorld() {
        if (defaultSetupCoroutine != null) { StopCoroutine(defaultSetupCoroutine); defaultSetupCoroutine = null; }
        WorkOrderManager.instance?.ClearAllOrders();
        EventFeed.instance?.Clear();
        isClearing = true;
        // Null all tile.inv refs before destroying structures — prevents Structure.Destroy()
        // → FallIfUnstandable() from spawning stale fall animations during the clear.
        for (int x = 0; x < world.nx; x++)
            for (int y = 0; y < world.ny; y++)
                world.GetTileAt(x, y).inv = null;

        // 1. Destroy all structures (copy list since Destroy modifies it)
        foreach (Structure s in StructController.instance.GetStructures()) s.Destroy();

        // 2. Destroy all blueprints
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                for (int i = 0; i < Tile.NumDepths; i++) tile.blueprints[i]?.Destroy();
            }
        }

        // 3. Destroy animals (Animal.Destroy handles their inventories)
        for (int i = 0; i < AnimalController.instance.na; i++) {
            AnimalController.instance.animals[i]?.Destroy();
            AnimalController.instance.animals[i] = null;
        }
        AnimalController.instance.na = 0;
        AnimalController.instance.ResetJobCounts();
        AnimalController.instance.ClearTileOccupancy();
        AnimalController.instance.ResetTickAccumulator();

        // 4. Destroy remaining inventories (tile invs; animal invs already gone)
        foreach (Inventory inv in new List<Inventory>(InventoryController.instance.inventories)) {
            inv.Destroy();
        }
        InventoryController.instance.selectedInventory = null;

        // 5. Zero GlobalInventory
        foreach (int key in new List<int>(GlobalInventory.instance.itemAmounts.Keys)) {
            GlobalInventory.instance.itemAmounts[key] = 0;
        }

        // 6. Reset all tiles: reset tile types (fires sprite callbacks), background
        //    walls, water, and moisture. Background walls in particular must be
        //    cleared — saves only persist tiles that have content, so stale walls
        //    from a previous world would otherwise survive into the next load and
        //    appear as ghost walls in sky columns.
        WaterController.instance?.ClearWater();
        MoistureSystem.instance?.Clear();
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                tile.type = Db.tileTypeByName["empty"];
                tile.backgroundType = BackgroundType.None;
                tile.overlayMask = 0; // belt-and-braces; the type setter also clears it on dirt→empty
            }
        }

        world.timer = 0;
        InfoPanel.instance.ShowInfo(null);
        isClearing = false;
    }

    // Called synchronously in frame 1 (from Start, Reset, or Load path).
    // graph.Initialize() here is what makes node.standable valid — must happen before DefaultJobSetup.
    public void GenerateDefault() {
        // The world seed seeds Rng (all gameplay randomness) AND WorldGen. Generated once
        // here for new worlds; on the load path SaveSystem.ApplySaveData calls Rng.Init from
        // the persisted seed instead, so this only runs on Initial / Reset.
        // Range stays modest because WorldGen does `seed + offset` arithmetic — full int range
        // could overflow, and the resulting world variety from 100k seeds is plenty.
        int seed = UnityEngine.Random.Range(1, 100000);
        Rng.Init(seed);
        int[] surfaceY = WorldGen.Generate(world, seed);
        int sy = surfaceY[WorldGen.SpawnMinX]; // surface height at spawn zone (flat)

        // Market is placed at the left world edge and is intentionally off-screen.
        // Merchants walk here and "disappear" for a travel period before goods arrive.
        Building market = new(Db.structTypeByName["market"], 0, surfaceY[0]);
        StructController.instance.Place(market);

        // Starter plants near spawn — always mature so a fresh colony has usable plants.
        void PlantAt(string plantName, int x) {
            Plant p = new Plant(Db.plantTypeByName[plantName], x, surfaceY[x]);
            p.Mature();
            StructController.instance.Place(p);
        }
        PlantAt("pinetree", 29);
        PlantAt("appletree", 25);
        PlantAt("pinetree", 28);
        PlantAt("wheat", 35);
        PlantAt("wheat", 36);

        WorldGen.ScatterPlants(world, surfaceY, seed);

        // Re-fire tile type callbacks now that all terrain + caves are placed.
        // During generation, cave carving can expose dirt tiles to air above them
        // without re-triggering the neighbor's sprite (grass vs dirt). This pass
        // ensures every tile's sprite reflects its final surroundings.
        RefreshAllTileSprites();

        // Background walls follow the natural surface contour — tiles below
        // surfaceY[x] get a wall (Dirt for the top DirtDepth rows, Stone deeper),
        // with shallow caves left open as skylights. See WorldGen.SetBackgrounds.
        WorldGen.SetBackgrounds(world, surfaceY);

        SkyExposure.InitializeWorld(world);
        BackgroundTile.InitializeWorld(world);

        world.timer = World.ticksInDay * 0.3f;
        world.graph.Initialize();

        // Register WOM orders for all placed structures (harvest, market, etc.) now that the
        // graph is built. Mirrors the Reconcile() call at the end of ApplySaveData().
        WorkOrderManager.instance?.Reconcile(silent: true);

        for (int i = 0; i < 4; i++) AnimalController.instance.AddAnimal(29 + i, sy);
        Camera.main.transform.position = new Vector3(30f, sy + 4f, Camera.main.transform.position.z);
        defaultSetupCoroutine = StartCoroutine(DefaultJobSetup(sy));
    }

    // FRAME 2 — one frame after GenerateDefault(). By this point:
    //   Animal.Start() has run (safe to assign jobs)
    //   graph.Initialize() has run (safe to call ProduceAtTile — standability is valid)
    IEnumerator DefaultJobSetup(int surfaceY) {
        yield return null; // wait one frame for Animal.Start() to run
        AnimalController.instance.AddJob("logger", 1);
        AnimalController.instance.AddJob("hauler", 1);
        AnimalController.instance.AddJob("farmer", 1);
        world.ProduceAtTile("silver", 50, world.GetTileAt(31, surfaceY));
        world.ProduceAtTile("wheat", 2000, world.GetTileAt(31, surfaceY));

        // Mice start with social half-satisfied so a fresh colony isn't immediately wanting company.
        foreach (Animal a in AnimalController.instance.animals) {
            if (a != null && a.happiness != null)
                a.happiness.satisfactions["social"] = 2.5f;
        }
    }

    // Re-fires OnTileTypeChanged for every tile so sprites reflect final terrain.
    // Called once after world generation to fix grass/dirt on cave boundaries.
    // Also refreshes overlays — defensive: PopulateOverlays already triggers the
    // overlayMask setter callback, but this guarantees every tile is re-evaluated
    // (e.g. tiles whose mask happens to be 0 still need their overlay sprite
    // explicitly set to null, which the setter skips since the value didn't change).
    void RefreshAllTileSprites() {
        for (int x = 0; x < world.nx; x++)
            for (int y = 0; y < world.ny; y++) {
                Tile t = world.GetTileAt(x, y);
                OnTileTypeChanged(t);
                OnTileOverlayChanged(t);
                OnTileSnowChanged(t);
            }
    }

    // ── Lazy tile GO creation ───────────────────────────────────────────────
    // Tiles spawn no GameObjects at load — we only create them when something
    // actually wants to render. Body GO appears the first time a tile becomes
    // solid. Overlay and Snow SRs appear the first time grass/snow wants to
    // draw on that tile. Once created, GOs stay around (terrain rarely flips
    // back to air; snow/grass rarely vanishes "forever"). At rest the sprite
    // is nulled, which is effectively free.

    GameObject EnsureBodyGO(Tile tile) {
        if (tileGameObjectMap.TryGetValue(tile, out var existing)) return existing;

        GameObject go = new GameObject("Tile_" + tile.x + "_" + tile.y);
        go.transform.position = new Vector3(tile.x, tile.y, 0);
        go.transform.SetParent(tilesTransform);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = 0;
        LightReceiverUtil.SetSortBucket(sr);
        if (tileMaterial != null) sr.material = tileMaterial;
        tileGameObjectMap.Add(tile, go);
        return go;
    }

    // Overlay child at sortingOrder=11, just above buildings (10), so grass
    // tufts that bevel up out of a grassy dirt tile read in front of building
    // bottoms placed on or beside the tile.
    SpriteRenderer EnsureOverlaySR(Tile tile) {
        if (tileOverlaySrMap.TryGetValue(tile, out var existing)) return existing;
        GameObject parent = EnsureBodyGO(tile);
        GameObject go = new GameObject("Overlay");
        go.transform.SetParent(parent.transform, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = 11;
        LightReceiverUtil.SetSortBucket(sr);
        if (tileMaterial != null) sr.material = tileMaterial;
        tileOverlaySrMap.Add(tile, sr);
        return sr;
    }

    // Snow child at sortingOrder=2 — above the tile body so accumulating snow
    // visually covers it. Snow and grass are mutually exclusive at runtime
    // (snow accumulation snapshots and clears the overlay mask), so the
    // relative ordering between snow (2) and the bumped grass overlay (11)
    // is moot.
    SpriteRenderer EnsureSnowSR(Tile tile) {
        if (tileSnowSrMap.TryGetValue(tile, out var existing)) return existing;
        GameObject parent = EnsureBodyGO(tile);
        GameObject go = new GameObject("Snow");
        go.transform.SetParent(parent.transform, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = 2;
        LightReceiverUtil.SetSortBucket(sr);
        if (tileMaterial != null) sr.material = tileMaterial;
        tileSnowSrMap.Add(tile, sr);
        return sr;
    }

    // Updates the gameobject sprite and shadow caster when the tile data is changed
    void OnTileTypeChanged(Tile tile) {
        // Solid → ensure body GO exists; non-solid → clear sprite if one was made
        // earlier (e.g. tile was solid then mined out). We never create a body
        // GO for a tile that has only ever been non-solid.
        if (tile.type.solid) {
            EnsureBodyGO(tile);
        } else if (tileGameObjectMap.TryGetValue(tile, out var existing)) {
            existing.GetComponent<SpriteRenderer>().sprite = null;
            existing.transform.localScale = Vector3.one;
        }
        // Sprite for solid tiles is set in ApplyTileNormalMap (depends on adjacency).
        // Scale stays at 1 — baked 20×20 sprites are natively 1.25 units at PPU=16.

        // Refresh body+overlay for this tile and all 8 neighbours.
        // OnTileOverlayChanged calls ApplyTileNormalMap internally — the body's
        // bodyCardinals depends on overlayMask, and body+overlay share the
        // normal map (overlay points its _NormalMap at the dirt body's normal),
        // so they refresh as a unit. Cardinal neighbours' cMasks change when
        // this tile's solidity flips; diagonal neighbours' nMasks change too
        // (corner bevels), so all 8 need a refresh.
        OnTileOverlayChanged(tile);
        OnTileOverlayChanged(world.GetTileAt(tile.x - 1, tile.y));
        OnTileOverlayChanged(world.GetTileAt(tile.x + 1, tile.y));
        OnTileOverlayChanged(world.GetTileAt(tile.x, tile.y - 1));
        OnTileOverlayChanged(world.GetTileAt(tile.x, tile.y + 1));
        OnTileOverlayChanged(world.GetTileAt(tile.x - 1, tile.y - 1));
        OnTileOverlayChanged(world.GetTileAt(tile.x + 1, tile.y - 1));
        OnTileOverlayChanged(world.GetTileAt(tile.x - 1, tile.y + 1));
        OnTileOverlayChanged(world.GetTileAt(tile.x + 1, tile.y + 1));

        // Snow on tile T-1 depends on whether T is solid (we hide snow if the
        // tile above is solid). So a solidity flip on T must refresh T-1's snow.
        // Other neighbours' snow visuals don't depend on T, so we don't refresh
        // them — keeps the touched-tile set minimal.
        OnTileSnowChanged(world.GetTileAt(tile.x, tile.y - 1));

        world.graph.UpdateNeighbors(tile.x, tile.y); // i think this is redundant. should laready be called
        // in structcontroller whenever a tile type changes?

        // Floor-item sort follows the surface below: a dirt → air change (or
        // reverse) flips the pile sitting on top of this tile between the
        // dirt-fallback (70) and whatever else is below it.
        Inventory.RefreshFloorAt(tile.x, tile.y + 1);
    }

    // Updates the overlay sprite (and its normal map) for a tile. Overlay is
    // displayed only on sides where (a) the overlayMask bit is set AND (b) the
    // neighbour is not solid (otherwise the side is buried). The whole overlay
    // is suppressed when a road occupies depth 3 — roads visually replace the
    // ground surface, so grass tufts shouldn't poke through.
    //
    // Reuses TileSpriteCache via an inverted-mask trick: feed `~effective & 0xF`
    // as cMask, so the baker's "Main interior on bit-set sides" rule with a
    // transparent-Main grass atlas produces edge art exactly on the desired sides.
    //
    // Always refreshes the body too (via ApplyTileNormalMap), because the body
    // hides its own edge piece on grass sides — see ApplyTileNormalMap's
    // bodyCardinals comment. So overlayMask changes / road place-or-destroy
    // both have to redraw the body, not just the overlay.
    void OnTileOverlayChanged(Tile tile) {
        if (tile == null) return;

        // Refresh body first — bodyCardinals depends on tile.overlayMask and
        // structs[3] (road suppression), so a change here flips which pieces of
        // the dirt sprite are visible. Returns early if no body GO exists yet
        // (non-solid tile that's never been solid).
        ApplyTileNormalMap(tile);

        string overlayName = tile.type.overlay;
        // Depth 3 is the road slot (see Tile.cs: 0=building 1=platform 2=foreground
        // 3=road 4=shaft). Roads visually replace the ground surface, so grass
        // tufts shouldn't poke through them.
        bool roadCovered = tile.structs[3] != null;

        // cMask = bit set when neighbour is solid (matches ApplyTileNormalMap).
        // Computed up-front because we need `effective` to decide whether to
        // create an overlay GO at all (avoids spawning one only to assign null).
        int cMask = 0;
        if (IsSolidAt(tile.x - 1, tile.y))     cMask |= 1;
        if (IsSolidAt(tile.x + 1, tile.y))     cMask |= 2;
        if (IsSolidAt(tile.x,     tile.y - 1)) cMask |= 4;
        if (IsSolidAt(tile.x,     tile.y + 1)) cMask |= 8;

        int effective = (overlayName != null && !roadCovered)
                        ? (tile.overlayMask & ~cMask & 0xF) : 0;

        // No sprite to draw: skip GO creation. If one was made earlier (grass
        // since buried/roaded/wiped), null its sprite.
        if (effective == 0) {
            if (tileOverlaySrMap.TryGetValue(tile, out var existing)) existing.sprite = null;
            return;
        }

        SpriteRenderer sr = EnsureOverlaySR(tile);

        // Health-state atlas swap: "<base>_dying" / "<base>_dead" for non-Live tiles.
        // Live keeps the bare overlay name. Atlas geometry (edges, corners) is identical
        // across the three variants — only the colours differ — so per-side bit semantics
        // and the inverted-cardinal trick still apply unchanged.
        if (tile.overlayState == OverlayState.Dying) overlayName += "_dying";
        else if (tile.overlayState == OverlayState.Dead) overlayName += "_dead";

        int nMask = cMask;
        if (IsSolidAt(tile.x - 1, tile.y - 1)) nMask |= 16;   // BL
        if (IsSolidAt(tile.x + 1, tile.y - 1)) nMask |= 32;   // BR
        if (IsSolidAt(tile.x - 1, tile.y + 1)) nMask |= 64;   // TL
        if (IsSolidAt(tile.x + 1, tile.y + 1)) nMask |= 128;  // TR

        int invertedCardinals = (~effective) & 0xF;

        // Sprite: grass atlas, keyed by inverted cardinals so edge art appears
        // on the decorated sides. NormalMap: the BODY's REAL nMask normal map
        // (dirt's bevel data, NOT grass.png's). NormalsCapture has Blend Off,
        // so whichever SR draws last at a pixel wins; pointing both body and
        // overlay at the same dirt-derived normal texture means whoever wins,
        // we get the same directional edge bevel — and not the flat-blade-
        // interior bevel that would otherwise come from sampling grass.png's
        // thin grass-blade silhouette through BakeNormalMap's Sobel kernel.
        sr.sprite = TileSpriteCache.GetOverlay(overlayName, invertedCardinals, tile.x, tile.y);
        SetNormalMap(sr, TileSpriteCache.GetNormalMap(tile.type.name, nMask, tile.x, tile.y));
    }

    // Snow uses the cardinal-mask atlas pipeline (TileSpriteCache.GetOverlay)
    // so edges and corners connect properly across neighbouring snowy tiles.
    // Distinct from grass in one important way: we **don't** augment the body's
    // bodyCardinals with snow bits, so the body keeps drawing its real edge
    // pieces. The snow sprite stacks on top of the body's unmodified edges —
    // not "replace, don't stack" like grass. The artist authors snow.png with
    // transparency / vertical positioning so the body's bevel still reads
    // through the snow layer.
    //
    // Hidden when (a) tile.snow is false, or (b) the tile directly above is
    // solid — a buried tile can carry snow data (snow accumulated then a wall
    // was built above) but shouldn't render it.
    void OnTileSnowChanged(Tile tile) {
        if (tile == null) return;

        // Strict bounds check: IsSolidAt treats off-map as solid for visual
        // adjacency, but top-row snow should still render (open sky above).
        int yAbove = tile.y + 1;
        bool buriedAbove = yAbove < world.ny && world.GetTileAt(tile.x, yAbove).type.solid;

        // Nothing to draw: skip GO creation. If one was made earlier (snow that
        // has since melted or been buried), null its sprite.
        if (!tile.snow || buriedAbove) {
            if (tileSnowSrMap.TryGetValue(tile, out var existing)) existing.sprite = null;
            return;
        }

        SpriteRenderer sr = EnsureSnowSR(tile);

        // U-bit only. Inverted-cardinal mask convention (passed to GetOverlay)
        // is "which sides are NOT decorated", so 0b0111 means only U is.
        const int InvertedCardinalsTopOnly = 0b0111;

        // nMask matches the body's so the normal-map sample reflects the same
        // bevels as the body — same trick used by the grass overlay.
        int nMask = 0;
        if (IsSolidAt(tile.x - 1, tile.y))     nMask |= 1;
        if (IsSolidAt(tile.x + 1, tile.y))     nMask |= 2;
        if (IsSolidAt(tile.x,     tile.y - 1)) nMask |= 4;
        if (IsSolidAt(tile.x,     tile.y + 1)) nMask |= 8;
        if (IsSolidAt(tile.x - 1, tile.y - 1)) nMask |= 16;
        if (IsSolidAt(tile.x + 1, tile.y - 1)) nMask |= 32;
        if (IsSolidAt(tile.x - 1, tile.y + 1)) nMask |= 64;
        if (IsSolidAt(tile.x + 1, tile.y + 1)) nMask |= 128;

        sr.sprite = TileSpriteCache.GetOverlay("snow", InvertedCardinalsTopOnly, tile.x, tile.y);
        SetNormalMap(sr, TileSpriteCache.GetNormalMap(tile.type.name, nMask, tile.x, tile.y));
    }

    static readonly int NormalMapID = Shader.PropertyToID("_NormalMap");
    // Lazy-initialized: Unity forbids constructing MaterialPropertyBlock in a static
    // field initializer that runs before the first Awake/Start on a MonoBehaviour type.
    static MaterialPropertyBlock _normalMpb;
    static MaterialPropertyBlock normalMpb => _normalMpb ??= new MaterialPropertyBlock();

    void ApplyTileNormalMap(Tile tile) {
        if (tile == null || !tileGameObjectMap.ContainsKey(tile)) return;
        var sr = tileGameObjectMap[tile].GetComponent<SpriteRenderer>();
        if (!tile.type.solid) { SetNormalMap(sr, TileSpriteCache.FlatNormalMap); return; }

        // Per-cardinal classification. Same-type and off-map neighbours bury
        // this tile's edge — Main extends to the boundary, no border art.
        // Different-type neighbours kick off a "soft-edge" contest, won by the
        // lower tile-id: the winner bakes its own border piece on that side,
        // letting its teeth straddle the boundary 4-pixel zone (own interior
        // cols 16-17 + own overhang cols 18-19, which physically sit inside
        // the loser's interior 2px). The loser keeps its 16×16 Main fully
        // intact. To resolve the loser-Main vs winner-overhang overlap, tile
        // bodies get a per-type sortingOrder offset so the winner draws on top
        // (see TileBodySortOrder). Lighting is intentionally unaffected — the
        // normal map runs on realSolid so type seams act exactly like buried
        // interior for bevel and edge-depth alpha (no ambient leak through
        // adjacent solid material).
        int realSolid = 0, win = 0;
        AccumulateTypeBoundary(tile, tile.x - 1, tile.y,     1, ref realSolid, ref win);
        AccumulateTypeBoundary(tile, tile.x + 1, tile.y,     2, ref realSolid, ref win);
        AccumulateTypeBoundary(tile, tile.x,     tile.y - 1, 4, ref realSolid, ref win);
        AccumulateTypeBoundary(tile, tile.x,     tile.y + 1, 8, ref realSolid, ref win);

        // 8-bit mask for the normal map: cardinals + diagonals from raw
        // solidity. Win-sides are NOT unset here on purpose — the bake treats
        // them as buried, so the winner's teeth in cols 16-19 catch the same
        // flat-lit, deep-alpha treatment as continuous material. Light never
        // bleeds through a seam where there are solid tiles on both sides.
        int nMask = realSolid;
        if (IsSolidAt(tile.x - 1, tile.y - 1)) nMask |= 16;   // BL
        if (IsSolidAt(tile.x + 1, tile.y - 1)) nMask |= 32;   // BR
        if (IsSolidAt(tile.x - 1, tile.y + 1)) nMask |= 64;   // TL
        if (IsSolidAt(tile.x + 1, tile.y + 1)) nMask |= 128;  // TR

        // Body cardinal-piece mask: bit set means "use Main interior, suppress
        // border art." Win-sides are unset so the body bakes a border piece
        // on those sides. Overlay-bearing sides are set so grass replaces the
        // body's edge art (road suppression zeroes overlayBits so the body
        // shows its real edges under a road).
        bool roadSuppressed = tile.structs[3] != null;
        int overlayBits = roadSuppressed ? 0 : (tile.overlayMask & 0xF);
        int bodyCardinals = (realSolid & ~win) | overlayBits;

        // Trim inner 2px only where the grass overlay replaces an exposed
        // edge (overlay bit set AND neighbour non-solid). No loser-side trim:
        // the loser's Main extends to the boundary as before, and sortingOrder
        // alone decides who shows in the 2px overlap with the winner's overhang.
        int trimMask = (overlayBits & ~realSolid) & 0xF;

        sr.sprite = TileSpriteCache.Get(tile.type.name, bodyCardinals, trimMask, tile.x, tile.y);
        SetNormalMap(sr, TileSpriteCache.GetNormalMap(tile.type.name, nMask, tile.x, tile.y));

        // Per-type body sort order. Same value for every tile of a given type,
        // so same-type neighbours stay co-planar; only different-type pairs
        // get an ordering tiebreak. SortBucket MPB write is belt-and-braces —
        // negative sortingOrders clamp to 0 in the shader bucket, same as the
        // initial spawn value, so the visible result doesn't change. We still
        // call it to keep the SR's stored MPB consistent with sortingOrder.
        int newSort = TileBodySortOrder(tile.type);
        if (sr.sortingOrder != newSort) {
            sr.sortingOrder = newSort;
            LightReceiverUtil.SetSortBucket(sr);
        }
    }

    // Classifies one cardinal neighbour for the soft-edge contest. Off-map is
    // treated as continuous same-type material so the world boundary doesn't
    // grow outward-facing teeth (matches IsSolidAt's "edges read as more of
    // the same material" convention). Same-type solid neighbours land in
    // realSolid only — buried, no contest. "Win" = lower id than neighbour;
    // we draw our border art into their overhang. The losing tile takes no
    // action on its side — its untrimmed Main stays, the per-type sortingOrder
    // gap lets the winner's overhang teeth render on top of it.
    void AccumulateTypeBoundary(Tile tile, int nx, int ny, int bit,
                                ref int realSolid, ref int win) {
        if (nx < 0 || nx >= world.nx || ny < 0 || ny >= world.ny) {
            realSolid |= bit;
            return;
        }
        Tile n = world.GetTileAt(nx, ny);
        if (n == null || !n.type.solid) return;
        realSolid |= bit;
        if (n.type == tile.type) return;
        if (tile.type.id < n.type.id) win |= bit;
        // higher-id (lose) and equal-id (shouldn't happen) cases: do nothing —
        // bit stays in realSolid only, buried-side behaviour.
    }

    // Per-type sort-order rank: solid tile types sorted by id ascending,
    // assigned ranks 0..k-1, sortingOrder = -rank. Lower-id tiles get the
    // higher sortingOrder, so at a different-type boundary the lower-id
    // tile's body draws on top of the higher-id tile's body in the 2px
    // overhang/Main overlap region. Cached on first call; reset by
    // ResetTileSortRanks (RuntimeInitializeOnLoad) so scene reloads see a
    // fresh build of the rank table.
    //
    // Range: with k current solid types (4: dirt, limestone, granite, slate)
    // ranks span 0..3 → sortingOrder 0..-3. Fits cleanly inside [-4, 1] —
    // above water (-5) so water still renders behind tile bodies, below snow
    // (2) so snow still covers them.
    static int[] _solidTypeRanks;
    static int TileBodySortOrder(TileType tt) {
        if (_solidTypeRanks == null || tt.id >= _solidTypeRanks.Length) BuildSolidTypeRanks();
        return tt.id < _solidTypeRanks.Length ? _solidTypeRanks[tt.id] : 0;
    }

    static void BuildSolidTypeRanks() {
        var solids = new List<TileType>();
        int maxId = 0;
        foreach (var t in Db.tileTypes) {
            if (t == null || !t.solid) continue;
            solids.Add(t);
            if (t.id > maxId) maxId = t.id;
        }
        solids.Sort((a, b) => a.id.CompareTo(b.id));
        _solidTypeRanks = new int[maxId + 1];
        for (int i = 0; i < solids.Count; i++) _solidTypeRanks[solids[i].id] = -i;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetTileSortRanks() { _solidTypeRanks = null; }

    static void SetNormalMap(SpriteRenderer sr, Texture2D tex) {
        if (tex == null) return; // tile type has no normal map; leave the existing one untouched
        sr.GetPropertyBlock(normalMpb);
        normalMpb.SetTexture(NormalMapID, tex);
        sr.SetPropertyBlock(normalMpb);
    }

    // Visual adjacency helper. Off-map neighbours read as solid so the world's
    // boundary tiles don't render outward-facing bevels, edge-depth darkening,
    // or grass tufts pointing into the void — the edge reads as "more of the
    // same material continues beyond the map". Callers that need actual
    // physical solidity (e.g. sky-blocking checks) must bounds-check explicitly
    // rather than using this helper.
    bool IsSolidAt(int x, int y) {
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) return true;
        Tile t = world.GetTileAt(x, y);
        return t != null && t.type.solid;
    }
}

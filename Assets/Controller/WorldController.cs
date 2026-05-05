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

        // Create tile GameObjects and register callbacks (persists across resets)
        for (int x = 0; x < world.nx; x++){
            for (int y = world.ny - 1; y >= 0; y--){
                Tile tile = world.GetTileAt(x, y);

                GameObject tile_go = new GameObject();
                tile_go.name = "Tile_" + x + "_" + y;
                tile_go.transform.position = new Vector3(tile.x, tile.y, 0);
                tile_go.transform.SetParent(tilesTransform);
                tileGameObjectMap.Add(tile, tile_go);
                tile.go = tile_go;

                SpriteRenderer tile_sr = tile_go.AddComponent<SpriteRenderer>();
                tile_sr.sortingOrder = 0;
                LightReceiverUtil.SetSortBucket(tile_sr);
                if (tileMaterial != null) tile_sr.material = tileMaterial;
                tile.RegisterCbTileTypeChanged(OnTileTypeChanged);

                // Overlay child for per-side decoration (grass on dirt). Lives at
                // sortingOrder=1 alongside roads — they're mutually exclusive on a
                // tile (overlay is suppressed when a road is present), so no fight
                // for draw order. Sprite is set on demand in OnTileOverlayChanged.
                GameObject overlay_go = new GameObject("Overlay");
                overlay_go.transform.SetParent(tile_go.transform, false);
                SpriteRenderer overlay_sr = overlay_go.AddComponent<SpriteRenderer>();
                overlay_sr.sortingOrder = 1;
                LightReceiverUtil.SetSortBucket(overlay_sr);
                if (tileMaterial != null) overlay_sr.material = tileMaterial;
                tileOverlaySrMap.Add(tile, overlay_sr);
                tile.RegisterCbOverlayChanged(OnTileOverlayChanged);

                // Snow child renders above the grass overlay (sortingOrder=2) so
                // accumulating snow visually covers the tile body. The grass
                // overlay is also cleared on accumulation, so they shouldn't
                // both be visible simultaneously — but the layering reflects
                // the conceptual stack regardless.
                GameObject snow_go = new GameObject("Snow");
                snow_go.transform.SetParent(tile_go.transform, false);
                SpriteRenderer snow_sr = snow_go.AddComponent<SpriteRenderer>();
                snow_sr.sortingOrder = 2;
                LightReceiverUtil.SetSortBucket(snow_sr);
                if (tileMaterial != null) snow_sr.material = tileMaterial;
                tileSnowSrMap.Add(tile, snow_sr);
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
        PlantAt("tree", 29);
        PlantAt("appletree", 25);
        PlantAt("tree", 28);
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

    // Updates the gameobject sprite and shadow caster when the tile data is changed
    void OnTileTypeChanged(Tile tile) {
        if (!tileGameObjectMap.ContainsKey(tile)){
            Debug.LogError("tile data is not in tile game object map!");
        }
        GameObject tile_go = tileGameObjectMap[tile];
        SpriteRenderer sr = tile_go.GetComponent<SpriteRenderer>();

        if (!tile.type.solid) {
            sr.sprite = null;
            tile_go.transform.localScale = Vector3.one;
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
        // the dirt sprite are visible.
        ApplyTileNormalMap(tile);

        if (!tileOverlaySrMap.TryGetValue(tile, out var sr)) return;

        string overlayName = tile.type.overlay;
        // Depth 3 is the road slot (see Tile.cs: 0=building 1=platform 2=foreground
        // 3=road 4=shaft). Roads visually replace the ground surface, so grass
        // tufts shouldn't poke through them.
        bool roadCovered = tile.structs[3] != null;
        if (overlayName == null || tile.overlayMask == 0 || roadCovered) {
            sr.sprite = null;
            return;
        }

        // Health-state atlas swap: "<base>_dying" / "<base>_dead" for non-Live tiles.
        // Live keeps the bare overlay name. Atlas geometry (edges, corners) is identical
        // across the three variants — only the colours differ — so per-side bit semantics
        // and the inverted-cardinal trick still apply unchanged.
        if (tile.overlayState == OverlayState.Dying) overlayName += "_dying";
        else if (tile.overlayState == OverlayState.Dead) overlayName += "_dead";

        // cMask = bit set when neighbour is solid (matches ApplyTileNormalMap).
        int cMask = 0;
        if (IsSolidAt(tile.x - 1, tile.y))     cMask |= 1;
        if (IsSolidAt(tile.x + 1, tile.y))     cMask |= 2;
        if (IsSolidAt(tile.x,     tile.y - 1)) cMask |= 4;
        if (IsSolidAt(tile.x,     tile.y + 1)) cMask |= 8;

        int effective = tile.overlayMask & ~cMask & 0xF;
        if (effective == 0) {
            sr.sprite = null;
            return;
        }

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
        if (!tileSnowSrMap.TryGetValue(tile, out var sr)) return;

        if (!tile.snow || IsSolidAt(tile.x, tile.y + 1)) {
            sr.sprite = null;
            return;
        }

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

        // Sprite uses the 4-bit cardinal mask (16 variants). Normal map uses
        // the 8-bit mask with diagonals (256 variants) — the extra bits drive
        // inside-corner edge-depth alpha (e.g. hasL && hasD && !hasBL lets
        // light penetrate into the BL interior corner via the diagonal gap).
        int cMask = 0;
        if (IsSolidAt(tile.x - 1, tile.y))     cMask |= 1;
        if (IsSolidAt(tile.x + 1, tile.y))     cMask |= 2;
        if (IsSolidAt(tile.x,     tile.y - 1)) cMask |= 4;
        if (IsSolidAt(tile.x,     tile.y + 1)) cMask |= 8;

        int nMask = cMask;
        if (IsSolidAt(tile.x - 1, tile.y - 1)) nMask |= 16;   // BL
        if (IsSolidAt(tile.x + 1, tile.y - 1)) nMask |= 32;   // BR
        if (IsSolidAt(tile.x - 1, tile.y + 1)) nMask |= 64;   // TL
        if (IsSolidAt(tile.x + 1, tile.y + 1)) nMask |= 128;  // TR

        // The overlay (e.g. grass) replaces — not stacks on top of — the body's
        // edge piece on sides where it carries decoration. So pretend the body's
        // neighbour on those sides is "solid" for sprite-piece selection: the
        // body uses its Main interior piece there, and the overlay's edge art
        // covers it. The normal map stays on the REAL nMask so the body's Main
        // pixels still capture true edge bevels — and OnTileOverlayChanged
        // points the overlay's _NormalMap at this same texture so grass blades
        // inherit the dirt body's directional bevel instead of the flat
        // bevel-of-thin-blades that grass.png pixels would produce on their own.
        // Suppressed when a road occupies the tile (overlay isn't drawn → body
        // shows its real edges).
        bool roadSuppressed = tile.structs[3] != null;
        int overlayBits = roadSuppressed ? 0 : (tile.overlayMask & 0xF);
        int bodyCardinals = cMask | overlayBits;
        // Trim sides where the overlay replaces an exposed dirt edge (overlay
        // bit set AND real neighbour empty). Clears the inner 2 pixels of Main
        // on those sides so the body's Main extension doesn't poke through
        // transparent gaps in the overlay's edge art. Sides that are buried by
        // a real solid neighbour stay un-trimmed — that Main extension blends
        // seamlessly into the neighbour and the overlay isn't covering anything.
        int trimMask = overlayBits & ~cMask & 0xF;

        sr.sprite = TileSpriteCache.Get(tile.type.name, bodyCardinals, trimMask, tile.x, tile.y);
        SetNormalMap(sr, TileSpriteCache.GetNormalMap(tile.type.name, nMask, tile.x, tile.y));
    }

    static void SetNormalMap(SpriteRenderer sr, Texture2D tex) {
        if (tex == null) return; // tile type has no normal map; leave the existing one untouched
        sr.GetPropertyBlock(normalMpb);
        normalMpb.SetTexture(NormalMapID, tex);
        sr.SetPropertyBlock(normalMpb);
    }

    bool IsSolidAt(int x, int y) {
        Tile t = world.GetTileAt(x, y);
        return t != null && t.type.solid;
    }
}

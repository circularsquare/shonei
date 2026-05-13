using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using System;

// Scene-level orchestrator: creates World, owns the lifetime of the chunked
// tile renderer (TileMeshController), drives worldgen / save-load, and runs
// the falling-item animation. Tile visuals themselves are owned by
// TileMeshController; this class only handles non-rendering side effects of
// tile-data changes (pathfinding graph, floor-item sort).
public class WorldController : MonoBehaviour {
    public static WorldController instance {get; protected set;}

    // Test hook: when true, Start() skips auto-loading the most recent save and
    // generates a fresh world instead. Snapshot tests set this so they get a
    // deterministic, save-independent starting state. Default false (production).
    public static bool skipAutoLoad = false;
    public World world {get; protected set;}
    public Transform tilesTransform;
    Coroutine defaultSetupCoroutine;
    Material chunkedTileMaterial; // Custom/ChunkedTileSprite shader for chunked body/overlay/snow meshes
    // Inspector-assigned so the shader is part of the scene's serialized graph
    // and force-included in builds. Shader.Find() works in the editor but the
    // build pipeline strips shaders that aren't reachable from a Material asset.
    [SerializeField] Shader chunkedTileShader;
    // Unity layer name for chunked tile meshes. Must match a layer in
    // ProjectSettings/TagManager.asset, and must be assigned on
    // LightFeature.tileChunkLayer + included in the camera's culling mask.
    public const string ChunkLayerName = "TileChunk";

    // FRAME 0: runs up to the first yield, pausing to let all other Start()s finish.
    // FRAME 1: resumes and calls GenerateDefault() (or waits for save/reset to do so).
    IEnumerator Start() {
        if (instance != null) {
            Debug.LogError("there should only be one world controller");}
        instance = this;

        Application.runInBackground = true;

        // Loading-time instrumentation. Phase labels show up in the on-screen
        // overlay and as `[Load] …` console lines; deltas between phases tell
        // you which step dominates load. Hide on completion. See LoadingScreen.cs.
        LoadingScreen.Begin("Allocating world (tile grid)");

        // Replicate the 2D Renderer's Y-axis sprite sorting under URP Universal
        // Renderer. UniversalRendererData has no transparency-sort field, and
        // URP hides the project-level Graphics setting from the Inspector when
        // an SRP is active. Without this, two sprites sharing a sortingOrder
        // (e.g. two animals at the same world position) could swap draw order
        // between frames. Almost every sprite in this project sets an explicit
        // sortingOrder per SPEC-rendering.md, so this is mostly belt-and-braces.
        GraphicsSettings.transparencySortMode = TransparencySortMode.CustomAxis;
        GraphicsSettings.transparencySortAxis = new Vector3(0f, 1f, 0f);

        world = this.gameObject.AddComponent<World>(); // add world (allocates Tile grid + graph nodes)

        tilesTransform = transform.Find("Tiles");

        if (chunkedTileShader != null) chunkedTileMaterial = new Material(chunkedTileShader);
        else Debug.LogError("WorldController: chunkedTileShader unassigned in Inspector — assign Custom/ChunkedTileSprite (Assets/Lighting/ChunkedTileSprite.shader)");

        LoadingScreen.SetPhase("Subscribing tile callbacks");

        // TileMeshController owns all visible tile geometry: body, overlay, snow.
        // It subscribes its own per-tile callbacks for cbTileType / cbOverlay /
        // cbSnow and rebuilds chunked meshes on dirty in LateUpdate. WorldController
        // still subscribes cbTileTypeChanged for non-rendering side effects
        // (pathfinding graph, floor-item sort).
        var tmc = gameObject.AddComponent<TileMeshController>();
        tmc.Initialize(world, tilesTransform, chunkedTileMaterial, ChunkLayerName);

        // Decorative flowers — scattered across grass-topped tiles. No save
        // state of its own (deterministic from world seed). Subscribes per-tile
        // overlay / snow callbacks on first OnWorldReady so the spawn set stays
        // current as grass grows, dies, snows over, etc. See FlowerController.cs.
        gameObject.AddComponent<FlowerController>();

        for (int x = 0; x < world.nx; x++){
            for (int y = world.ny - 1; y >= 0; y--){
                Tile tile = world.GetTileAt(x, y);
                tile.RegisterCbTileTypeChanged(OnTileTypeChanged);
            }
        }

        LoadingScreen.SetPhase("Waiting for other Start()s");
        yield return null; // wait one frame so all other Start()s finish before we generate the world

        string mostRecent = skipAutoLoad ? null : SaveSystem.instance.GetMostRecentSlot();
        if (mostRecent != null) {
            LoadingScreen.SetPhase($"Loading save: {mostRecent}");
            SaveSystem.instance.Load(mostRecent);
        } else {
            LoadingScreen.SetPhase("Generating world");
            GenerateDefault();
            StartCoroutine(SaveSystem.instance.PostLoadInit());
        }
        World.OnItemFall += SpawnItemFallAnimation;

        // Decorative flowers do a full-world scan + subscribe their per-tile
        // callbacks here, after both worldgen and save-load paths have settled.
        // Rng.worldSeed is authoritative on both paths (set by GenerateDefault
        // or by SaveSystem.ApplySaveData), so the layout is reproducible.
        LoadingScreen.SetPhase("Scattering flowers");
        FlowerController.instance?.OnWorldReady(world, Rng.worldSeed);

        // One more yield so the next frame's LateUpdate fires — that's where
        // TileMeshController rebuilds every dirty chunk for the first time
        // (and the per-type Texture2DArray bundles in TileSpriteCache get
        // their normal-slice uploads). End() captures that phase's cost.
        LoadingScreen.SetPhase("Building chunk meshes (first rebuild)");
        yield return null;
        LoadingScreen.End();
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
        // Stash on World so decoration systems (FlowerController, OverlayGrowthSystem)
        // can gate placement by depth. Authoritative for the new-world path —
        // save loads recompute from the loaded tile grid instead (see ApplySaveData).
        world.surfaceY = surfaceY;
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

        // No "refresh all tiles" pass needed: TileMeshController subscribes to
        // cbTileTypeChanged and blanket-dirties body + overlay + snow chunks
        // for the touched tile + 8 neighbours on every type set during worldgen,
        // so cave-adjacent tiles automatically pick up their final cMask/nMask
        // when LateUpdate rebuilds. (The legacy SR-based renderer needed an
        // explicit RefreshAllTileSprites pass here because cbOverlayChanged is
        // skipped when the new mask equals the old, leaving 0-masked tiles
        // un-evaluated.)

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

    // Tile-data side effects of a type change — the visible geometry refresh
    // for body / overlay / snow lives in TileMeshController (which subscribes
    // its own cbTileTypeChanged). Here we only handle non-rendering effects:
    // pathfinding-graph repathing and the floor-item sort follow-up on the
    // tile above (whose pile-vs-surface relationship may have changed).
    void OnTileTypeChanged(Tile tile) {
        world.graph.UpdateNeighbors(tile.x, tile.y);
        Inventory.RefreshFloorAt(tile.x, tile.y + 1);
    }
}

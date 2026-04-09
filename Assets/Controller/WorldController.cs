using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

// this class handles tile sprites, and also places initial objects into world.
public class WorldController : MonoBehaviour {
    public static WorldController instance {get; protected set;}
    public World world {get; protected set;}
    public Transform tilesTransform;
    Dictionary<Tile, GameObject> tileGameObjectMap;
    Coroutine defaultSetupCoroutine;
    Material tileMaterial; // Custom/TileSprite shader for jagged tile edges

    // FRAME 0: runs up to the first yield, pausing to let all other Start()s finish.
    // FRAME 1: resumes and calls GenerateDefault() (or waits for save/reset to do so).
    IEnumerator Start() {
        if (instance != null) {
            Debug.LogError("there should only be one world controller");}
        instance = this;

        Application.runInBackground = true;
        world = this.gameObject.AddComponent<World>(); // add world

        tileGameObjectMap = new Dictionary<Tile, GameObject>();
        tilesTransform = transform.Find("Tiles");

        // Create material with Custom/TileSprite shader for jagged tile edges.
        var tileShader = Shader.Find("Custom/TileSprite");
        if (tileShader != null) tileMaterial = new Material(tileShader);
        else Debug.LogError("WorldController: Custom/TileSprite shader not found");

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
                if (tileMaterial != null) tile_sr.material = tileMaterial;
                tile.RegisterCbTileTypeChanged(OnTileTypeChanged);
            }
        }

        yield return null; // wait one frame so all other Start()s finish before we generate the world
        string mostRecent = SaveSystem.instance.GetMostRecentSlot();
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
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = 65; // below floor items (sortingOrder 70)

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
                for (int i = 0; i < 4; i++) tile.blueprints[i]?.Destroy();
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

        // 6. Reset all tiles: reset tile types (fires sprite callbacks) and water
        WaterController.instance?.ClearWater();
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                tile.type = Db.tileTypeByName["empty"];
            }
        }

        world.timer = 0;
        InfoPanel.instance.ShowInfo(null);
        isClearing = false;
    }

    // Called synchronously in frame 1 (from Start, Reset, or Load path).
    // graph.Initialize() here is what makes node.standable valid — must happen before DefaultJobSetup.
    public void GenerateDefault() {
        int seed = UnityEngine.Random.Range(0, 100000);
        int[] surfaceY = WorldGen.Generate(world, seed);
        int sy = surfaceY[WorldGen.SpawnMinX]; // surface height at spawn zone (flat)

        // Market is placed at the left world edge and is intentionally off-screen.
        // Merchants walk here and "disappear" for a travel period before goods arrive.
        Building market = new(Db.structTypeByName["market"], 0, surfaceY[0]);
        StructController.instance.Place(market);

        // Starter plants near spawn
        void PlantAt(string plantName, int x, bool mature = false) {
            Plant p = new Plant(Db.plantTypeByName[plantName], x, surfaceY[x]);
            if (mature) p.Mature();
            StructController.instance.Place(p);
        }
        PlantAt("tree", 32, mature: true);
        PlantAt("appletree", 25);
        PlantAt("tree", 28);
        PlantAt("wheat", 35);
        PlantAt("wheat", 36);
        PlantAt("soybean", 24);

        WorldGen.ScatterPlants(world, surfaceY, seed);

        // Re-fire tile type callbacks now that all terrain + caves are placed.
        // During generation, cave carving can expose dirt tiles to air above them
        // without re-triggering the neighbor's sprite (grass vs dirt). This pass
        // ensures every tile's sprite reflects its final surroundings.
        RefreshAllTileSprites();

        // Set background walls for underground tiles. Tiles at y <= 43 get a
        // background (underground backdrop); above that is open sky.
        for (int x = 0; x < world.nx; x++)
            for (int y = 0; y <= 43 && y < world.ny; y++)
                world.GetTileAt(x, y).hasBackground = true;

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
    }

    // Re-fires OnTileTypeChanged for every tile so sprites reflect final terrain.
    // Called once after world generation to fix grass/dirt on cave boundaries.
    void RefreshAllTileSprites() {
        for (int x = 0; x < world.nx; x++)
            for (int y = 0; y < world.ny; y++)
                OnTileTypeChanged(world.GetTileAt(x, y));
    }

    // Updates the gameobject sprite and shadow caster when the tile data is changed
    void OnTileTypeChanged(Tile tile) {
        if (!tileGameObjectMap.ContainsKey(tile)){
            Debug.LogError("tile data is not in tile game object map!");
        }
        GameObject tile_go = tileGameObjectMap[tile];
        Sprite sprite;
        if (tile.type.name == "dirt") {
            if (tile.y < world.ny - 1 && world.GetTileAt(tile.x, tile.y + 1).type != Db.tileTypes[0])
                sprite = LoadTileSprite("dirt");
            else
                sprite = LoadTileSprite("grass");
        } else {
            sprite = LoadTileSprite(tile.type.name);
        }
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Tiles/default");
        }
        tile_go.GetComponent<SpriteRenderer>().sprite = sprite;

        // Update normal map for this tile and all 8 neighbours
        // (a neighbour's exposed edges and corner depths change when this tile changes).
        ApplyTileNormalMap(tile);
        ApplyTileNormalMap(world.GetTileAt(tile.x - 1, tile.y));
        ApplyTileNormalMap(world.GetTileAt(tile.x + 1, tile.y));
        ApplyTileNormalMap(world.GetTileAt(tile.x, tile.y - 1));
        ApplyTileNormalMap(world.GetTileAt(tile.x, tile.y + 1));
        ApplyTileNormalMap(world.GetTileAt(tile.x - 1, tile.y - 1));
        ApplyTileNormalMap(world.GetTileAt(tile.x + 1, tile.y - 1));
        ApplyTileNormalMap(world.GetTileAt(tile.x - 1, tile.y + 1));
        ApplyTileNormalMap(world.GetTileAt(tile.x + 1, tile.y + 1));

        world.graph.UpdateNeighbors(tile.x, tile.y); // i think this is redundant. should laready be called
        // in structcontroller whenever a tile type changes?
    }

    void ApplyTileNormalMap(Tile tile) {
        if (tile == null || !tileGameObjectMap.ContainsKey(tile)) return;
        var sr = tileGameObjectMap[tile].GetComponent<SpriteRenderer>();
        if (!tile.type.solid) { TileNormalMaps.Clear(sr); return; }

        // Build 8-bit mask: bits 0-3 = cardinal (L/R/D/U), bits 4-7 = diagonal (BL/BR/TL/TR)
        int mask = 0;
        if (IsSolidAt(tile.x - 1, tile.y))     mask |= 1;
        if (IsSolidAt(tile.x + 1, tile.y))     mask |= 2;
        if (IsSolidAt(tile.x,     tile.y - 1)) mask |= 4;
        if (IsSolidAt(tile.x,     tile.y + 1)) mask |= 8;
        if (IsSolidAt(tile.x - 1, tile.y - 1)) mask |= 16;
        if (IsSolidAt(tile.x + 1, tile.y - 1)) mask |= 32;
        if (IsSolidAt(tile.x - 1, tile.y + 1)) mask |= 64;
        if (IsSolidAt(tile.x + 1, tile.y + 1)) mask |= 128;
        TileNormalMaps.Apply(sr, mask);
    }

    bool IsSolidAt(int x, int y) {
        Tile t = world.GetTileAt(x, y);
        return t != null && t.type.solid;
    }
    Sprite LoadTileSprite(string name) {
        Sprite variant = Resources.Load<Sprite>("Sprites/Tiles/" + name + "2");
        Sprite original = Resources.Load<Sprite>("Sprites/Tiles/" + name);
        
        if (variant != null && UnityEngine.Random.value > 0.5f)
            return variant;
        return original ?? Resources.Load<Sprite>("Sprites/Tiles/default");
    }

}

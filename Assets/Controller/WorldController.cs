using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using System;

// this class handles tile sprites, and also places initial objects into world.
public class WorldController : MonoBehaviour {
    public static WorldController instance {get; protected set;}
    public World world {get; protected set;}
    public Transform tilesTransform;
    Dictionary<Tile, GameObject> tileGameObjectMap;
    Coroutine defaultSetupCoroutine;

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
                tile.RegisterCbTileTypeChanged(OnTileTypeChanged);
            }
        }

        yield return null; // wait one frame so all other Start()s finish before we generate the world
        GenerateDefault();
        StartCoroutine(SaveSystem.instance.PostLoadInit());
        World.OnItemFall += SpawnItemFallAnimation;
    }

    void OnDestroy() {
        World.OnItemFall -= SpawnItemFallAnimation;
    }

    void SpawnItemFallAnimation(int srcX, int srcY, int dstX, int dstY, Item item) {
        StartCoroutine(ItemFallAnimCoroutine(srcX, srcY, dstX, dstY, item));
    }

    IEnumerator ItemFallAnimCoroutine(int srcX, int srcY, int dstX, int dstY, Item item) {
        string iName = item.name.Replace(" ", "");
        Sprite sprite = Resources.Load<Sprite>($"Sprites/Items/{iName}/floor");
        sprite ??= Resources.Load<Sprite>($"Sprites/Items/{iName}/icon");
        sprite ??= Resources.Load<Sprite>("Sprites/Items/default/icon");

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
        for (int x = 0; x < world.nx; x++) {
            for (int y = world.ny - 1; y > 0; y--) {
                if (y < 10) world.GetTileAt(x, y).type = Db.tileTypeByName["dirt"];
                if (y < 8)  world.GetTileAt(x, y).type = Db.tileTypeByName["stone"];
            }
        }

        Building market = new(Db.structTypeByName["market"], 10, 10);
        StructController.instance.Place(market);

        Plant plant1 = new Plant(Db.plantTypeByName["tree"], 22, 10);
        plant1.Mature();
        StructController.instance.Place(plant1);
        Plant plant2 = new Plant(Db.plantTypeByName["appletree"], 15, 10);
        StructController.instance.Place(plant2);
        Plant plant3 = new Plant(Db.plantTypeByName["tree"], 18, 10);
        StructController.instance.Place(plant3);
        Plant plant4 = new Plant(Db.plantTypeByName["wheat"], 25, 10);
        StructController.instance.Place(plant4);
        Plant plant5 = new Plant(Db.plantTypeByName["wheat"], 26, 10);
        StructController.instance.Place(plant5);

        // Seed water sources at the surface (y=10) in the specified x ranges.
        // Clear the tile first so water isn't blocked by solid terrain.
        for (int x = 0; x < world.nx; x++) {
            if ((x >= 0 && x <= 3) || (x >= 30 && x <= 40)) {
                Tile t = world.GetTileAt(x, 9);
                t.type = Db.tileTypeByName["empty"];
                t.water = WaterController.WaterMax;
            }
        }

        world.timer = World.ticksInDay * 0.3f;
        world.graph.Initialize();

        for (int i = 0; i < 4; i++) AnimalController.instance.AddAnimal(20, 10);
        defaultSetupCoroutine = StartCoroutine(DefaultJobSetup());
    }

    // FRAME 2 — one frame after GenerateDefault(). By this point:
    //   Animal.Start() has run (safe to assign jobs)
    //   graph.Initialize() has run (safe to call ProduceAtTile — standability is valid)
    IEnumerator DefaultJobSetup() {
        yield return null; // wait one frame for Animal.Start() to run
        AnimalController.instance.AddJob("logger", 1);
        AnimalController.instance.AddJob("hauler", 1);
        AnimalController.instance.AddJob("farmer", 1);
        world.ProduceAtTile("silver", 50, world.GetTileAt(21, 10));
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

        // Solid tiles cast shadows; non-solid tiles don't.
        ShadowCaster2D sc = tile_go.GetComponent<ShadowCaster2D>();
        if (tile.type.solid) {
            if (sc == null) {
                sc = tile_go.AddComponent<ShadowCaster2D>();
                sc.selfShadows = false;
            }
        } else {
            if (sc != null) Destroy(sc);
        }

        // Update normal map for this tile and its 4 orthogonal neighbours
        // (a neighbour's exposed/covered edges change whenever this tile changes).
        ApplyTileNormalMap(tile);
        ApplyTileNormalMap(world.GetTileAt(tile.x - 1, tile.y));
        ApplyTileNormalMap(world.GetTileAt(tile.x + 1, tile.y));
        ApplyTileNormalMap(world.GetTileAt(tile.x, tile.y - 1));
        ApplyTileNormalMap(world.GetTileAt(tile.x, tile.y + 1));

        world.graph.UpdateNeighbors(tile.x, tile.y); // i think this is redundant. should laready be called
        // in structcontroller whenever a tile type changes?
    }

    void ApplyTileNormalMap(Tile tile) {
        if (tile == null || !tileGameObjectMap.ContainsKey(tile)) return;
        var sr = tileGameObjectMap[tile].GetComponent<SpriteRenderer>();
        if (!tile.type.solid) { TileNormalMaps.Clear(sr); return; }

        // Build 4-bit mask: bit 0=left, 1=right, 2=down, 3=up
        int mask = 0;
        if (IsSolidAt(tile.x - 1, tile.y)) mask |= 1;
        if (IsSolidAt(tile.x + 1, tile.y)) mask |= 2;
        if (IsSolidAt(tile.x,     tile.y - 1)) mask |= 4;
        if (IsSolidAt(tile.x,     tile.y + 1)) mask |= 8;
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

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
    }

    // -----------------------------------------------------------------------
    // WORLD LIFECYCLE
    // -----------------------------------------------------------------------

    public void ClearWorld() {
        // 1. Destroy all structures (copy list since Destroy modifies it)
        foreach (Structure s in StructController.instance.GetStructures()) s.Destroy();

        // 2. Destroy all blueprints
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                tile.bBlueprint?.Destroy();
                tile.mBlueprint?.Destroy();
                tile.fBlueprint?.Destroy();
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

        // 6. Reset all tiles: null stale inv refs, reset tile types (fires sprite callbacks)
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                tile.inv = null;
                tile.type = Db.tileTypeByName["empty"];
            }
        }

        world.timer = 0;
        InfoPanel.instance.ShowInfo(null);
    }

    // Called synchronously in frame 1 (from Start, Reset, or Load path).
    // graph.Initialize() here is what makes node.standable valid — must happen before DefaultJobSetup.
    public void GenerateDefault() {
        for (int x = 0; x < world.nx; x++) {
            for (int y = world.ny - 1; y > 0; y--) {
                if (y < 10) world.GetTileAt(x, y).type = Db.tileTypeByName["soil"];
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

        world.timer = Db.ticksInDay * 0.3f;
        world.graph.Initialize();

        for (int i = 0; i < 4; i++) AnimalController.instance.AddAnimal(20, 10);
        StartCoroutine(DefaultJobSetup());
    }

    // FRAME 2 — one frame after GenerateDefault(). By this point:
    //   Animal.Start() has run (safe to assign jobs)
    //   graph.Initialize() has run (safe to call ProduceAtTile — standability is valid)
    IEnumerator DefaultJobSetup() {
        yield return null; // wait one frame for Animal.Start() to run
        AnimalController.instance.AddJob("logger", 1);
        AnimalController.instance.AddJob("hauler", 1);
        AnimalController.instance.AddJob("farmer", 1);
        ProduceAtTile("silver", 50, world.GetTileAt(21, 10));
    }

    // Produces items on a tile's floor inventory.
    // If the tile is full, searches nearby standable tiles (expanding rings, radius 5).
    public void ProduceAtTile(Item item, int quantity, Tile tile) {
        int remaining = PutOnFloor(tile, item, quantity);
        if (remaining == 0) return;

        Debug.LogError($"ProduceAtTile: no space for {remaining} {item.name} at ({tile.x},{tile.y}), searching nearby.");

        for (int r = 1; r <= 5 && remaining > 0; r++) {
            for (int dx = -r; dx <= r && remaining > 0; dx++) {
                for (int dy = -r; dy <= r && remaining > 0; dy++) {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue; // shell only
                    int nx = tile.x + dx, ny = tile.y + dy;
                    if (nx < 0 || ny < 0 || nx >= world.nx || ny >= world.ny) continue;
                    Node n = world.graph.nodes[nx, ny];
                    if (n == null || !n.standable || !n.tile.HasSpaceForItem(item)) continue;
                    remaining = PutOnFloor(n.tile, item, remaining);
                }
            }
        }

        if (remaining > 0)
            Debug.LogError($"ProduceAtTile: couldn't place {remaining} {item.name} anywhere near ({tile.x},{tile.y})");
    }
    public void ProduceAtTile(string itemName, int quantity, Tile tile) =>
        ProduceAtTile(Db.itemByName[itemName], quantity, tile);

    int PutOnFloor(Tile tile, Item item, int quantity) {
        if (tile.inv == null) tile.inv = new Inventory(x: tile.x, y: tile.y);
        return tile.inv.Produce(item, quantity);
    }

    public void ApplySaveData(WorldSaveData save) {
        world.timer = save.timer;

        foreach (TileSaveData tsd in save.tiles) {
            Tile tile = world.GetTileAt(tsd.x, tsd.y);
            if (tile == null) continue;

            // Tile type
            if (!string.IsNullOrEmpty(tsd.tileType) && Db.tileTypeByName.ContainsKey(tsd.tileType)) {
                tile.type = Db.tileTypeByName[tsd.tileType];
            }

            // Blueprints first so deconstruct blueprints can coexist with structures below
            if (tsd.bBlueprint != null) RestoreBlueprint(tsd.bBlueprint, tile);
            if (tsd.mBlueprint != null) RestoreBlueprint(tsd.mBlueprint, tile);
            if (tsd.fBlueprint != null) RestoreBlueprint(tsd.fBlueprint, tile);

            // Structures
            if (tsd.building != null) RestoreStructure(tsd.building, tile);
            if (tsd.mStruct  != null) RestoreStructure(tsd.mStruct,  tile);
            if (tsd.fStruct  != null) RestoreStructure(tsd.fStruct,  tile);

            // Inventory (floor items or building storage)
            if (tsd.inv != null) RestoreInventory(tsd.inv, tile);
        }

        world.graph.Initialize();

        if (save.animals != null) {
            foreach (AnimalSaveData asd in save.animals) {
                AnimalController.instance.LoadAnimal(asd);
            }
        }
    }

    void RestoreStructure(StructureSaveData ssd, Tile tile) {
        if (!Db.structTypeByName.ContainsKey(ssd.typeName)) {
            Debug.LogError("Unknown struct type on load: " + ssd.typeName); return;
        }
        StructType st = Db.structTypeByName[ssd.typeName];
        Structure structure = null;

        if (st.isPlant) {
            Plant plant = new Plant(st as PlantType, tile.x, tile.y);
            plant.age = ssd.plantAge;
            plant.growthStage = ssd.plantGrowthStage;
            plant.harvestable = ssd.plantHarvestable;
            plant.UpdateSprite();
            structure = plant;
        } else if (st.depth == "b") {
            structure = new Building(st, tile.x, tile.y) { uses = ssd.uses };
        } else if (st.name == "platform") {
            structure = new Platform(st, tile.x, tile.y);
        } else if (st.name == "stairs") {
            structure = new Stairs(st, tile.x, tile.y);
        } else if (st.name == "ladder") {
            structure = new Ladder(st, tile.x, tile.y);
        } else {
            Debug.LogError("Unhandled struct type on load: " + ssd.typeName); return;
        }

        if (structure != null) {
            StructController.instance.Place(structure);
            world.graph.UpdateNeighbors(tile.x, tile.y);
            world.graph.UpdateNeighbors(tile.x, tile.y + 1);
        }
    }

    void RestoreBlueprint(BlueprintSaveData bsd, Tile tile) {
        if (!Db.structTypeByName.ContainsKey(bsd.typeName)) {
            Debug.LogError("Unknown blueprint struct type on load: " + bsd.typeName); return;
        }
        StructType st = Db.structTypeByName[bsd.typeName];
        Blueprint bp = new Blueprint(st, tile.x, tile.y);
        bp.state = (Blueprint.BlueprintState)bsd.state;
        bp.constructionProgress = bsd.constructionProgress;
        bp.priority = bsd.priority;
        if (bsd.inv != null) {
            for (int i = 0; i < bsd.inv.stacks.Length && i < bp.inv.itemStacks.Length; i++) {
                var ssd = bsd.inv.stacks[i];
                if (!string.IsNullOrEmpty(ssd.itemName) && Db.itemByName.ContainsKey(ssd.itemName) && ssd.quantity > 0) {
                    bp.inv.itemStacks[i].item        = Db.itemByName[ssd.itemName];
                    bp.inv.itemStacks[i].quantity     = ssd.quantity;
                    bp.inv.itemStacks[i].res.capacity = ssd.quantity;
                    bp.inv.itemStacks[i].res.reserved = 0;
                    GlobalInventory.instance.AddItem(Db.itemByName[ssd.itemName], ssd.quantity);
                }
            }
        }
        if (bp.state == Blueprint.BlueprintState.Deconstructing) {
            bp.go.GetComponent<SpriteRenderer>().color = new Color(1f, 0.3f, 0.3f, 0.5f);
        }
    }

    void RestoreInventory(InventorySaveData isd, Tile tile) {
        // Use existing inv (e.g. created by Building constructor for drawers) or create a floor inv
        Inventory inv = tile.inv ?? tile.EnsureFloorInventory();

        for (int i = 0; i < isd.stacks.Length && i < inv.itemStacks.Length; i++) {
            ItemStackSaveData ssd = isd.stacks[i];
            if (!string.IsNullOrEmpty(ssd.itemName) && Db.itemByName.ContainsKey(ssd.itemName) && ssd.quantity > 0) {
                Item item = Db.itemByName[ssd.itemName];
                inv.itemStacks[i].item = item;
                inv.itemStacks[i].quantity = ssd.quantity;
                inv.itemStacks[i].decayCounter = ssd.decayCounter;
                inv.itemStacks[i].res.capacity = ssd.quantity;
                inv.itemStacks[i].res.reserved = 0;
                GlobalInventory.instance.AddItem(item, ssd.quantity);
            }
        }
        foreach (Item item in Db.itemsFlat) { inv.AllowItem(item); }
        if (isd.disallowedItemIds != null) {
            foreach (int id in isd.disallowedItemIds) {
                if (id < Db.items.Length && Db.items[id] != null) {
                    inv.DisallowItem(Db.items[id]);
                }
            }
        }
        inv.UpdateSprite();
    }

    // Updates the gameobject sprite and shadow caster when the tile data is changed
    void OnTileTypeChanged(Tile tile) {
        if (!tileGameObjectMap.ContainsKey(tile)){
            Debug.LogError("tile data is not in tile game object map!");
        }
        GameObject tile_go = tileGameObjectMap[tile];
        Sprite sprite;
        if (tile.type.name == "soil") {
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

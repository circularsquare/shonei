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

    public void GenerateDefault() {
        for (int x = 0; x < world.nx; x++) {
            for (int y = world.ny - 1; y > 0; y--) {
                if (y < 10) world.GetTileAt(x, y).type = Db.tileTypeByName["soil"];
                if (y < 8)  world.GetTileAt(x, y).type = Db.tileTypeByName["stone"];
            }
        }

        Plant plant1 = new Plant(Db.plantTypeByName["tree"], 22, 10);
        plant1.Mature();
        StructController.instance.Place(plant1);
        Plant plant2 = new Plant(Db.plantTypeByName["tree"], 15, 10);
        StructController.instance.Place(plant2);
        Plant plant3 = new Plant(Db.plantTypeByName["tree"], 18, 10);
        StructController.instance.Place(plant3);
        Plant plant4 = new Plant(Db.plantTypeByName["wheat"], 25, 10);
        StructController.instance.Place(plant4);
        Plant plant5 = new Plant(Db.plantTypeByName["wheat"], 26, 10);
        StructController.instance.Place(plant5);

        world.graph.Initialize();

        for (int i = 0; i < 4; i++) AnimalController.instance.AddAnimal(20, 10);
        StartCoroutine(DefaultJobSetup());
    }

    IEnumerator DefaultJobSetup() {
        yield return null; // wait one frame for Animal.Start() to run
        AnimalController.instance.AddJob("logger", 1);
        AnimalController.instance.AddJob("hauler", 1);
        AnimalController.instance.AddJob("farmer", 1);
        if (AnimalController.instance.animals[0] != null) {
            AnimalController.instance.animals[0].Produce("wheat", 2);
        }
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
            structure = new Building(st, tile.x, tile.y);
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
        if (bsd.deliveredResources != null) {
            for (int i = 0; i < bsd.deliveredResources.Length && i < bp.deliveredResources.Length; i++) {
                bp.deliveredResources[i].quantity = bsd.deliveredResources[i].quantity;
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
                GlobalInventory.instance.AddItem(item, ssd.quantity);
            }
        }
        if (isd.disallowedItemIds != null) {
            foreach (int id in isd.disallowedItemIds) {
                if (id < Db.items.Length && Db.items[id] != null) {
                    inv.DisallowItem(Db.items[id]);
                }
            }
        }
        inv.UpdateSprite();
    }

    // updaes the gameobjects sprite when the tile data is changed
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

        world.graph.UpdateNeighbors(tile.x, tile.y); // i think this is redundant. should laready be called
        // in structcontroller whenever a tile type changes?
    }
    Sprite LoadTileSprite(string name) {
        Sprite variant = Resources.Load<Sprite>("Sprites/Tiles/" + name + "2");
        Sprite original = Resources.Load<Sprite>("Sprites/Tiles/" + name);
        
        if (variant != null && UnityEngine.Random.value > 0.5f)
            return variant;
        return original ?? Resources.Load<Sprite>("Sprites/Tiles/default");
    }

}

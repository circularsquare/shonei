using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

// The world grid plus the per-frame tick that drives everything on it. Owns the Tile
// array and the pathfinding Graph; hosts periodic dispatch to WeatherSystem,
// MaintenanceSystem, ResearchSystem, inventory decay, graph reconcile, etc. Falling
// items queued via FallItems land on the nearest standable tile below.
public class World : MonoBehaviour {
    Tile[,] tiles;
    public Graph graph;
    public int nx;
    public int ny;
    public WorldController worldController;
    public InventoryController invController;
    public AnimalController animalController;
    public PlantController plantController;
    public WaterController waterController;

    public static World instance { get; protected set; }
    public float timer = 0f;

    // Fired just before items are moved; controllers subscribe to play fall animations.
    // Args: srcX, srcY, dstX, dstY, representative item
    public static event Action<int, int, int, int, Item> OnItemFall;

    // Physics constant shared by item fall animation and mouse falling.
    // Gravity is derived so a 1-tile fall takes fallSecondsPerTile seconds.
    public const float fallSecondsPerTile = 0.4f;
    public const float fallGravity = 2f / (fallSecondsPerTile * fallSecondsPerTile); // 12.5 tiles/s²

    public static int ticksInDay = 240;
    public static int daysInYear = 20; // year is 6000s = 100 min

    // FRAME 0 — runs before any Start(). Allocates tiles and graph.nodes.
    // node.standable stays false until graph.Initialize() runs in GenerateDefault() (frame 1).
    public void Awake(){
        if (instance != null){
            Debug.LogError("there should only be one world?");}
        instance = this;
        Application.targetFrameRate = 100;

        graph = new Graph(this);

        nx = 100;
        ny = 80;
        tiles = new Tile[nx, ny];
        graph.nodes = new Node[nx, ny];
        for (int x = 0; x < nx; x++){
            for (int y = 0; y < ny; y++){
                tiles[x, y] = new Tile(this, x, y);
                graph.nodes[x, y] = tiles[x, y].node;
            }
        }
        invController = InventoryController.instance;
        worldController = WorldController.instance;
        animalController = AnimalController.instance;
        plantController = PlantController.instance;
        waterController = WaterController.instance;
        WeatherSystem.Create();
        MaintenanceSystem.Create();
    }

    public void Update(){
        if (Math.Floor(timer + Time.deltaTime) - Math.Floor(timer) > 0){ // every 1 sec
            // AnimalController self-drives staggered ticks from its own Update()
            plantController.TickUpdate();
            if (ResearchSystem.instance != null) ResearchSystem.instance.TickUpdate();
            MaintenanceSystem.instance?.Tick();
        }
        float reconcilePeriod = 10f;
        if (Math.Floor((timer + Time.deltaTime) / reconcilePeriod) - Math.Floor(timer / reconcilePeriod) > 0)
            WorkOrderManager.instance?.Reconcile();        
        float period = 0.2f;
        if (Math.Floor((timer + Time.deltaTime) / period) - Math.Floor(timer / period) > 0){  // every 0.2 sec
            invController.TickUpdate(); // update itemdisplay, add controller instances
            waterController?.TickUpdate();
            StructController.instance?.TickUpdate();
            InfoPanel.instance.UpdateInfo();
        }
        float hourPeriod = ticksInDay / 24f; // 10 seconds = 1 in-game hour
        if (Math.Floor((timer + Time.deltaTime) / hourPeriod) - Math.Floor(timer / hourPeriod) > 0)
            WeatherSystem.instance?.OnHourElapsed();
        WeatherSystem.instance?.Tick(Time.deltaTime);
#if UNITY_EDITOR
        // Editor-only leak canary: every 30s, flag any ItemStack whose resAmount/resSpace
        // exceeds what live tasks claim. Silent on clean runs. See ItemResChecker.cs.
        const float invariantPeriod = 30f;
        if (Math.Floor((timer + Time.deltaTime) / invariantPeriod) - Math.Floor(timer / invariantPeriod) > 0)
            ItemResChecker.Check();
#endif
        timer += Time.deltaTime;
    }
        

    // ── Tile stuff ───────────────────────────────────────────────────────────
    public Tile GetTileAt(int x, int y){
        if (x >= nx || x < 0 || y >= ny || y < 0){
            // Debug.Log("tile " + x + "," + y +  " out of range");
            return null;
        }
        return tiles[x,y];
    }
    public Tile GetTileAt(float x, float y){
        int xi = Mathf.FloorToInt(x + 0.5f);
        int yi = Mathf.FloorToInt(y + 0.5f);
        return GetTileAt(xi, yi);
    }

    // True if nothing blocks a line of rain (or sun) from reaching (x, y) from the sky.
    // Blockers: solid ground tiles, or any structure layer with solidTop=true
    // (buildings, platforms, foreground, road) on a tile above this one.
    // Blueprints are ignored — unbuilt doesn't block.
    // Shared primitive for rain-catching tanks and future plant sun/rain systems.
    public bool IsExposedAbove(int x, int y){
        if (x < 0 || x >= nx) return false;
        for (int py = y + 1; py < ny; py++){
            Tile t = tiles[x, py];
            if (t.type.solid) return false;
            for (int d = 0; d < t.structs.Length; d++){
                Structure s = t.structs[d];
                if (s != null && s.structType.solidTop) return false;
            }
        }
        return true;
    }

    // Produces items on a tile's floor inventory.
    // If the tile is full, searches nearby standable tiles (expanding rings, radius 5).
    public void ProduceAtTile(Item item, int quantity, Tile tile) {
        int remaining = PutOnFloor(tile, item, quantity);
        if (remaining == 0) return;

        // Debug.Log($"ProduceAtTile: no space for {remaining} {item.name} at ({tile.x},{tile.y}), searching nearby.");

        for (int r = 1; r <= 5 && remaining > 0; r++) {
            for (int dx = -r; dx <= r && remaining > 0; dx++) {
                for (int dy = -r; dy <= r && remaining > 0; dy++) {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue; // shell only
                    int px = tile.x + dx, py = tile.y + dy;
                    if (px < 0 || py < 0 || px >= nx || py >= ny) continue;
                    Node n = graph.nodes[px, py];
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
        if (tile.inv == null) tile.inv = new Inventory(n: 1, x: tile.x, y: tile.y);
        // Don't mix different items on a floor tile (FallItems bypasses this)
        if (!tile.inv.IsEmpty() && tile.inv.Quantity(item) == 0) return quantity;
        return tile.inv.Produce(item, quantity);
    }

    // Drops all items from tile.inv straight down to the first standable tile below.
    // Uses MoveItemTo to avoid double-counting GlobalInventory.
    // Falls items on tile if it is no longer standable. Call after graph updates.
    public void FallIfUnstandable(int x, int y) {
        Tile t = GetTileAt(x, y);
        if (t == null || t.inv == null || t.inv.IsEmpty()) return;
        if (!graph.nodes[x, y].standable) FallItems(t);
    }

    public void FallItems(Tile tile) {
        if (tile.inv == null || tile.inv.IsEmpty()) return;
        // Log reserved stacks up front — if a reservation holder later hits a stale reference,
        // this entry tells us FallItems (not FetchObjective cleanup) was the cause.
        foreach (ItemStack s in tile.inv.itemStacks) {
            if (s != null && s.resAmount > 0)
                Debug.Log($"FallItems: tile ({tile.x},{tile.y}) has reserved stack {s.item?.name} qty={s.quantity} resAmount={s.resAmount} resTask={s.resTask} animal={s.resTask?.animal?.aName}");
        }
        Tile landing = null;
        for (int ty = tile.y - 1; ty >= 0; ty--) {
            Node n = graph.nodes[tile.x, ty];
            if (n != null && n.standable) { landing = n.tile; break; }
        }
        if (landing == null) {
            foreach (ItemStack stack in tile.inv.itemStacks) {
                if (stack == null || stack.item == null || stack.quantity <= 0) continue;
                Debug.LogError($"FallItems: {stack.quantity} {stack.item.name} at ({tile.x},{tile.y}) lost — no standable tile below");
                GlobalInventory.instance.AddItem(stack.item, -stack.quantity);
            }
            tile.inv.Destroy(); tile.inv = null; return;
        }
        landing.inv ??= new Inventory(n: 1, x: landing.x, y: landing.y);
        foreach (ItemStack stack in tile.inv.itemStacks) {
            if (stack == null || stack.item == null || stack.quantity <= 0) continue;
            OnItemFall?.Invoke(tile.x, tile.y, landing.x, landing.y, stack.item);
            tile.inv.MoveItemTo(landing.inv, stack.item, stack.quantity);
        }
        // Anything left in tile.inv couldn't land — remove from ginv before destroying.
        foreach (ItemStack stack in tile.inv.itemStacks) {
            if (stack == null || stack.item == null || stack.quantity <= 0) continue;
            Debug.LogError($"FallItems: {stack.quantity} {stack.item.name} lost at ({tile.x},{tile.y}) — landing ({landing.x},{landing.y}) full");
            GlobalInventory.instance.AddItem(stack.item, -stack.quantity);
        }
        tile.inv.Destroy();
        tile.inv = null;
    }

    // ── Callbacks ────────────────────────────────────────────────────────────


}


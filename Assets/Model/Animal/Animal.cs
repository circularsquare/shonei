using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;


public class Animal : MonoBehaviour{
    public string aName;
    public int id;
    public float x;
    public float y;
    public float z => -0.0001f * id; // tiny per-animal Z offset to prevent sprite flicker
    public float maxSpeed = 2f;
    public bool facingRight = true;
    public bool pendingRefresh = false; // deferred Refresh() when SetJob fires mid-waypoint

    public Tile target;         // where you are currently going
    public Tile homeTile;
    private Tile _currentTile;  // cached for O(1) tile-occupancy tracking

    public Recipe recipe;
    public int numRounds = 0;
    public float workProgress = 0f;

    public Job job;
    public Inventory inv;
    public Inventory foodSlotInv; // equip slot: food only, 1 stack, 5 liang capacity
    public Inventory toolSlotInv; // equip slot: tool, 1 stack
    public Inventory clothingSlotInv; // equip slot: clothing (top), 1 stack
    public GlobalInventory ginv;

    public float energy;     // every time you get 1 energy you can do 1 work
    public float efficiency; // energy gain rate

    public SkillSet skills = new SkillSet();

    public Eating eating;
    public Eeping eeping;
    public Happiness happiness;
    public Nav nav;
    public AnimationController animationController;

    public Task task;

    public enum AnimalState{
        Idle,
        Working,        // For any work at a location (crafting, building, etc)
        Eeping,
        Moving,         // any type of moving, under the task system
        Falling,        // involuntary fall, bypasses task and nav systems
        Leisuring,      // leisure activity (chatting, tea house, etc.)
        Traveling,      // hidden while journeying to/from the off-screen market
    }
    private AnimalStateManager stateManager;
    private AnimalState _state;
    public AnimalState state{
        get { return _state; }
        set { if (_state != value) {
                _state = value;
                animationController?.UpdateState();
            }
        }
    }

    public System.Random random;
    public float tickOffset;    // [0,1) — stagger phase for per-frame tick dispatch
    public int tickCounter = 0;

    // World.timer value after which DropTask attempts are allowed again. Set by
    // DropObjective when no drop target is reachable, to avoid log-spamming every
    // tick while the animal is boxed in. Transient — not saved.
    [System.NonSerialized] public float dropCooldownUntil = 0f;

    // Set before Start() runs when loading a save; Start() checks this and applies it.
    // [NonSerialized] prevents Unity from serializing a default AnimalSaveData instance in place of null.
    [System.NonSerialized] public AnimalSaveData pendingSaveData = null;

    public GameObject go;
    public SpriteRenderer sr;
    public Sprite sprite;
    public World world;

    Action<Animal, Job> cbAnimalChanged;



    // FRAME 2 (for default world) — runs the frame after the animal's GameObject is spawned.
    // If pendingSaveData is set (load path), applies saved state; otherwise initializes fresh.
    public void Start(){
        world = World.instance;
        this.stateManager = new AnimalStateManager(this);
        this.go = this.gameObject;
        this.sr = go.transform.Find("Body").GetComponent<SpriteRenderer>();
        animationController = go.GetComponent<AnimationController>();
        this.inv = new Inventory(5, 1000, Inventory.InvType.Animal);
        this.foodSlotInv = new Inventory(1, 300, Inventory.InvType.Equip);
        this.toolSlotInv = new Inventory(1, 200, Inventory.InvType.Equip);
        this.clothingSlotInv = new Inventory(1, 200, Inventory.InvType.Equip);
        this.nav = new Nav(this);
        ginv = GlobalInventory.instance;
        random = new System.Random();

        if (pendingSaveData != null) {
            this.aName = string.IsNullOrEmpty(pendingSaveData.aName) ? Db.DrawName(AnimalController.instance.UsedNames()) : pendingSaveData.aName;
            this.go.name = "animal_" + aName;
            this.energy = pendingSaveData.energy;
            this.eating = new Eating();
            this.eating.food = pendingSaveData.food;
            this.eeping = new Eeping();
            this.eeping.eep = pendingSaveData.eep;
            this.happiness = new Happiness();
            if (pendingSaveData.satisfactions != null)
                foreach (var kv in pendingSaveData.satisfactions)
                    if (this.happiness.satisfactions.ContainsKey(kv.Key))
                        this.happiness.satisfactions[kv.Key] = kv.Value;
            this.happiness.warmth = pendingSaveData.warmth;
            this.job = Db.GetJobByName(pendingSaveData.jobName) ?? Db.jobs[0];
            this.state = AnimalState.Idle;
            this.efficiency = eating.Efficiency() * eeping.Efficiency() * happiness.TemperatureEfficiency();
            // Restore animal inventory items
            foreach (ItemStackSaveData ssd in pendingSaveData.inv.stacks) {
                if (!string.IsNullOrEmpty(ssd.itemName) && Db.itemByName.ContainsKey(ssd.itemName) && ssd.quantity > 0) {
                    inv.Produce(Db.itemByName[ssd.itemName], ssd.quantity);
                }
            }
            SaveSystem.LoadInventory(foodSlotInv, pendingSaveData.foodSlotInv);
            SaveSystem.LoadInventory(toolSlotInv, pendingSaveData.toolSlotInv);
            SaveSystem.LoadInventory(clothingSlotInv, pendingSaveData.clothingSlotInv);
            skills.Deserialize(pendingSaveData.skillXp, pendingSaveData.skillLevel);
            // Resume mid-journey travel if the animal was saved while traveling.
            // New saves carry a travelTaskType descriptor so we can rebuild the full
            // HaulTo/FromMarketTask tail — delivering or receiving at the market and
            // walking home with the goods. The resumed TravelingObjective runs the
            // full canonical leg duration (MarketTransitTicks); we then restore
            // workProgress from the save so the mouse picks up at the same fraction
            // of the strip it was on at save time (workProgress is the single source
            // of travel progress — travelProgress on save data is just its persisted
            // form). Legacy saves, or cases where the market / storage building was
            // demolished between save and load, fall through to ResumeTravelTask,
            // which just finishes the remaining ticks and goes idle.
            if (pendingSaveData.isTraveling && pendingSaveData.travelDuration > 0) {
                Task resumed = null;
                switch (pendingSaveData.travelTaskType) {
                    case "HaulToMarket":
                        if (!string.IsNullOrEmpty(pendingSaveData.travelItemName)
                            && Db.itemByName.TryGetValue(pendingSaveData.travelItemName, out Item htItem))
                            resumed = new HaulToMarketTask(this,
                                new ItemQuantity(htItem, pendingSaveData.travelItemQty),
                                pendingSaveData.travelReturnLeg);
                        break;
                    case "HaulFromMarket":
                        if (!string.IsNullOrEmpty(pendingSaveData.travelItemName)
                            && Db.itemByName.TryGetValue(pendingSaveData.travelItemName, out Item hfItem)
                            && pendingSaveData.travelStorageX.HasValue
                            && pendingSaveData.travelStorageY.HasValue) {
                            Tile storageTile = World.instance.GetTileAt(
                                pendingSaveData.travelStorageX.Value,
                                pendingSaveData.travelStorageY.Value);
                            if (storageTile != null)
                                resumed = new HaulFromMarketTask(this,
                                    new ItemQuantity(hfItem, pendingSaveData.travelItemQty),
                                    storageTile, pendingSaveData.travelReturnLeg);
                        }
                        break;
                }
                // Legacy fallback: ResumeTravelTask only knows how to finish the
                // remaining ticks, so it takes the tail-only duration.
                if (resumed == null) {
                    int remaining = Mathf.Max(1, pendingSaveData.travelDuration - (int)pendingSaveData.travelProgress);
                    resumed = new ResumeTravelTask(this, remaining);
                }
                this.task = resumed;
                if (this.task.Start()) {
                    // TravelingObjective.Start() zeroed workProgress; restore it so the
                    // journey-display icon picks up where the save left off.
                    this.workProgress = pendingSaveData.travelProgress;
                } else {
                    this.task = null;
                }
            }
            pendingSaveData = null;
        } else {
            this.aName = Db.DrawName(AnimalController.instance.UsedNames());
            this.go.name = "animal_" + aName;
            this.state = AnimalState.Idle;
            this.job = Db.jobs[0];
            this.efficiency = 1f;
            this.energy = 0f;
            this.eating = new Eating();
            this.eating.food = this.eating.maxFood;
            this.eeping = new Eeping();
            this.eeping.eep = this.eeping.maxEep;
            this.happiness = new Happiness();
            FindHome();
        }
        // Stagger SlowUpdate across animals so they don't all fire on the same tick
        tickCounter = id % 10;
        // Register initial tile occupancy
        _currentTile = TileHere();
        AnimalController.instance.RegisterAnimalOnTile(_currentTile);
        // Add to the tickable animals array now that we're fully initialized.
        // This is deferred from AddAnimal() so TickUpdate/UpdateColonyStats never
        // iterate over an animal whose Start() hasn't run yet.
        AnimalController.instance.RegisterReady(this);
    }

    public void TickUpdate() { // called from animalcontroller each second.
        tickCounter++;
        if (tickCounter % 10 == 0) {
            SlowUpdate();
        }
        HandleNeeds();
        UpdateEfficiency();
        energy += efficiency;
        if (energy > 1f) { // if you have enough energy, spend it. also then you can work if you're Working.
            energy -= 1f;
            stateManager.UpdateState();
        }
    }

    private void HandleNeeds() {
        eating.Update();
        eeping.Update();
        if (eating.Hungry()) {
            Item slotFood = foodSlotInv.itemStacks[0].item;
            if (slotFood != null && foodSlotInv.Quantity(slotFood) > 0) {
                int qty = foodSlotInv.Quantity(slotFood);
                if (qty >= 100) {
                    // Full meal
                    foodSlotInv.Produce(slotFood, -100);
                    eating.Eat(slotFood.foodValue);
                    happiness.NoteAte(slotFood, 1f);
                } else {
                    // Partial meal — consume remainder, scale nutrition, partial happiness credit
                    foodSlotInv.Produce(slotFood, -qty);
                    eating.Eat(slotFood.foodValue * qty / 100f);
                    happiness.NoteAte(slotFood, qty / 100f);
                }
            }
        }

    }
    private void UpdateEfficiency() {
        efficiency = eating.Efficiency() * eeping.Efficiency() * happiness.TemperatureEfficiency();
        maxSpeed = 1.5f * efficiency;
    }

    // True between 9 pm (phase 0.875) and 6 am (phase 0.25).
    private bool IsNighttime() {
        float phase = (World.instance.timer % World.ticksInDay) / (float)World.ticksInDay;
        return phase >= 21f / 24f || phase < 6f / 24f;
    }

    // True between 5 pm and 9 pm — mice prefer leisure over work during this window.
    private bool IsLeisureTime() {
        float phase = (World.instance.timer % World.ticksInDay) / (float)World.ticksInDay;
        return phase >= 17f / 24f && phase < 21f / 24f;
    }

    private static bool IsHourInRange(float startHour, float endHour) => SunController.IsHourInRange(startHour, endHour);

    public void SlowUpdate() { // called every 10 or so seconds
        FindHome();
        eating.SlowUpdate();
        happiness.UpdateComfortRange(this);
        happiness.SlowUpdate(this);
        ScanForNearbyDecorations();
    }

    // Scans the Chebyshev neighbourhood for active decoration buildings and notifies Happiness.
    // Each decoration type has its own timer — all in-range types are refreshed per call.
    // A decoration with a reservoir only counts when it has fuel (e.g. fountain needs water).
    // Computed from DB at startup — always equals the largest decorRadius across all structTypes.
    private static int MaxDecoScanRadius => Db.maxDecoScanRadius;

    private void ScanForNearbyDecorations() {
        int ax = Mathf.RoundToInt(x);
        int ay = Mathf.RoundToInt(y);
        for (int dx = -MaxDecoScanRadius; dx <= MaxDecoScanRadius; dx++) {
            for (int dy = -MaxDecoScanRadius; dy <= MaxDecoScanRadius; dy++) {
                Tile t = world.GetTileAt(ax + dx, ay + dy);
                if (t == null) continue;
                foreach (Structure s in t.structs) {
                    if (s == null) continue;
                    if (!(s is Building b) || !b.structType.isDecoration) continue;
                    int dist = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
                    if (dist > b.structType.decorRadius) continue;
                    if (b.IsBroken) continue;
                    if (b.reservoir != null && !b.reservoir.HasFuel()) continue;
                    if (string.IsNullOrEmpty(b.structType.decorationNeed)) {
                        Debug.LogError($"Decoration building '{b.structType.name}' has no decorationNeed set");
                        continue;
                    }
                    happiness.NoteSawDecoration(b.structType.decorationNeed);
                }
            }
        }
    }

    public void Update() { // called all the time, for movement and detecting arrival
        stateManager.UpdateMovement(Time.deltaTime);
    }


    public void ChooseTask() {
        if (task != null){ return; }

        // 1. Survival needs (always first)
        // drop all main inventory when idle (food/tools are in equip slots)
        // Skip if a recent drop attempt failed — the cooldown lets ChooseTask fall through
        // to other branches (food, eep, work) instead of looping on an unreachable drop.
        if (!inv.IsEmpty() && World.instance.timer >= dropCooldownUntil) {
            task = new DropTask(this);
            if (task.Start()) return; }
        if (eating.Hungry()) { if (FindFood()) return; }
        if (eeping.ShouldSleep(IsNighttime())) {
            task = new EepTask(this);
            if (task.Start()) return; }
        if (FindEquipment()) return;
        if (FindClothing()) return;

        // 1b. Time-of-day behavior roll.
        //     Leisure (5–9 pm): 40% leisure, 40% idle, 20% work.
        //     Work (rest of day): 5% leisure, 15% idle, 80% work.
        //     Leisure pick is need-based: target the lowest happiness satisfaction.
        float leisureChance, idleChance;
        if (IsLeisureTime()) {
            leisureChance = 0.40f; idleChance = 0.40f;
        } else {
            leisureChance = 0.05f; idleChance = 0.15f;
        }

        float roll = (float)random.NextDouble();
        if (roll < leisureChance) {
            if (TryPickLeisure()) return;
            task = null; return; // no leisure available — idle
        }
        if (roll < leisureChance + idleChance) {
            task = null; return; // idle
        }

        // 2. Work orders: p1 → p2 → p3 (haul, then craft via recipe-first) → p4
        //    Craft uses ChooseCraftTask() instead of ChooseOrder so recipe score drives building selection.
        var wom = WorkOrderManager.instance;
        if (wom != null) {
            wom.PruneStale();
            task = wom.ChooseOrder(this, 1); if (task != null) return;
            task = wom.ChooseOrder(this, 2); if (task != null) return;
            task = wom.ChooseOrder(this, 3, exclude: WorkOrderManager.OrderType.Craft); if (task != null) return;
            task = ChooseCraftTask(); if (task != null) return;
            task = wom.ChooseOrder(this, 4); if (task != null) return;
        }
        task = null;
    }

    // Scores all of this animal's recipes globally, then finds the nearest building for the
    // top-scoring recipe. Falls through to lower-scoring recipes when no building is available.
    // This is recipe-first selection: economic score drives which building type to visit,
    // rather than proximity driving which recipes are considered.
    private Task ChooseCraftTask() {
        var wom = WorkOrderManager.instance;
        if (wom == null) return null;
        var targets = InventoryController.instance?.targets;

        var scored = new List<(Recipe recipe, float score)>();
        foreach (var r in job.recipes) {
            if (r == null) continue;
            if (RecipePanel.instance != null && !RecipePanel.instance.IsAllowed(r.id)) continue;
            if (!ginv.SufficientResources(r.inputs)) continue;
            if (r.AllOutputsSatisfied(targets)) continue;
            scored.Add((r, r.Score(targets)));
        }
        scored.Sort((a, b) => b.score.CompareTo(a.score)); // highest score first

        foreach (var (recipe, _) in scored) {
            var found = wom.FindCraftOrder(recipe.tile, this);
            if (found == null) continue;
            var (order, building) = found.Value;
            var craftTask = new CraftTask(this, building, recipe);
            if (craftTask.Start()) {
                order.res.Reserve();
                craftTask.workOrder = order;
                return craftTask;
            }
        }
        return null;
    }

    // Picks up one tool into toolSlotInv if the slot is empty.
    private bool FindEquipment() {
        if (toolSlotInv.itemStacks[0].item != null) return false; // already holding a tool
        foreach (Item equipment in Db.equipmentItems) {
            task = new ObtainTask(this, equipment, 100, toolSlotInv);
            if (task.Start()) return true;
        }
        return false;
    }

    // Picks up one clothing item into clothingSlotInv if the slot is empty.
    private bool FindClothing() {
        if (clothingSlotInv.itemStacks[0].item != null) return false; // already wearing clothing
        foreach (Item clothing in Db.clothingItems) {
            task = new ObtainTask(this, clothing, 100, clothingSlotInv);
            if (task.Start()) return true;
        }
        return false;
    }

    // Prioritizes foods from unhappy categories; falls back to any available food.
    // Food goes directly into foodSlotInv (equip slot), not the main inventory.
    private bool FindFood() {
        Item slotItem = foodSlotInv.itemStacks[0].item; // null if empty
        int room = slotItem != null
            ? foodSlotInv.GetStorageForItem(slotItem)
            : foodSlotInv.stackSize;
        if (room <= 0) return false; // slot already full

        int amountToPickUp = Math.Max(room, 100);

        foreach (Item food in Db.edibleItems) {
            if (!happiness.WouldHelp(food)) continue;
            if (slotItem != null && slotItem != food) continue; // slot has different food
            task = new ObtainTask(this, food, amountToPickUp, foodSlotInv);
            if (task.Start()) return true;
        }
        foreach (Item food in Db.edibleItems) {
            if (slotItem != null && slotItem != food) continue;
            task = new ObtainTask(this, food, amountToPickUp, foodSlotInv);
            if (task.Start()) return true;
        }
        return false;
    }


    // Picks the best available leisure activity by targeting the lowest happiness satisfaction.
    // Gathers all options (chat + each leisure building need), sorts by satisfaction, tries in order.
    private bool TryPickLeisure() {
        var candidates = new List<(float sat, System.Func<bool> tryStart)>();

        // Chat option (social need) — only seek chat when social is low
        float socialSat = happiness.GetSatisfaction("social");
        if (socialSat < 2.0f && AnimalController.instance.FindIdleAnimalNear(this, 6) != null)
            candidates.Add((socialSat, FindChatPartner));

        // Building options: find nearest available building per leisure need
        var sc = StructController.instance;
        if (sc != null) {
            // Group by leisureNeed, pick nearest available building per need
            var bestPerNeed = new Dictionary<string, (Building b, float dist)>();
            foreach (Building b in sc.GetLeisureBuildings()) {
                if (b.disabled) continue;
                if (b.IsBroken) continue;
                if (b.reservoir != null && !b.reservoir.HasFuel()) continue;
                if (!IsHourInRange(b.structType.activeStartHour, b.structType.activeEndHour)) continue;
                if (b.seatRes != null) { if (!b.AnySeatAvailable()) continue; }
                else if (b.res != null && !b.res.Available()) continue;
                string need = b.structType.leisureNeed;
                if (string.IsNullOrEmpty(need)) {
                    Debug.LogError($"Leisure building '{b.structType.name}' has no leisureNeed set");
                    continue;
                }
                float dist = Mathf.Abs(b.x - x) + Mathf.Abs(b.y - y);
                if (!bestPerNeed.ContainsKey(need) || dist < bestPerNeed[need].dist)
                    bestPerNeed[need] = (b, dist);
            }
            foreach (var kvp in bestPerNeed) {
                string need = kvp.Key;
                Building building = kvp.Value.b;
                float sat = happiness.GetLeisureSatisfaction(need);
                candidates.Add((sat, () => TryStartLeisureAt(building)));
            }
        }

        if (candidates.Count == 0) return false;

        // Sort by satisfaction ascending — try the least-satisfied need first
        candidates.Sort((a, b) => a.sat.CompareTo(b.sat));
        foreach (var c in candidates) {
            if (c.tryStart()) return true;
        }
        return false;
    }

    private bool FindChatPartner() {
        Animal partner = AnimalController.instance.FindIdleAnimalNear(this, 6);
        if (partner == null) return false;
        task = new ChatTask(this, partner);
        if (task.Start()) return true;
        task = null;
        return false;
    }

    private bool TryStartLeisureAt(Building building) {
        task = new LeisureTask(this, building);
        if (task.Start()) return true;
        task = null;
        return false;
    }

    // this uses the task/objective system!
    public void OnArrival(){
        task?.OnArrival();
    }

    public void Refresh(){ // end task, go idle, AND drop items
        // Debug.Log(aName + " refreshed! interrupting current task");
        pendingRefresh = false; // clear in case this was deferred
        task?.Fail();
        task = new DropTask(this);
        if (!task.Start()){
            task = null;
            state = AnimalState.Idle;
        }
    }

    // ── Item movement (maybe these should be moved into Inventory class??) ───
    // Picks up item at current location, returns amount *not* taken.
    // Pass targetInv to deposit into an equip slot instead of main inventory.
    public int TakeItem(Item item, int quantity, Inventory targetInv = null){
        Tile tileHere = TileHere();
        if (tileHere != null && tileHere.inv != null) {
            Inventory dest = targetInv ?? inv;
            int leftover = tileHere.inv.MoveItemTo(dest, item, quantity);
            if (tileHere.inv.IsEmpty() && tileHere.inv.invType == Inventory.InvType.Floor){
                tileHere.inv.Destroy();
                tileHere.inv = null; // delete an empty floor inv.
            }
            return leftover;
        }
        return quantity;
    }
    public int TakeItem(ItemQuantity iq, Inventory targetInv = null){ return(TakeItem(iq.item, iq.quantity, targetInv)); }
    // Moves item from equip slot back to main inventory. Leftover stays in slot if inv is full.
    public void Unequip(Inventory slotInv) {
        Item item = slotInv.itemStacks[0].item;
        if (item == null) return;
        slotInv.MoveItemTo(inv, item, slotInv.Quantity(item));
    }
    // moves item to tile here. returns amount *not* dropped
    public int DropItem(Item item, int quantity = -1){ 
        if (quantity == -1) { quantity = inv.Quantity(item); }
        return (inv.MoveItemTo(TileHere().EnsureFloorInventory(), item, quantity));
        // maybe need failsafe?
    }
    public int DropItem(ItemQuantity iq) { return(DropItem(iq.item, iq.quantity)); }

    // produces item in ani inv, dumps at nearby tile if inv full
    public void Produce(Item item, int quantity = 1){
        if (quantity < 0){
            Debug.LogError("called produce with negative quantity, use consume instead");
            Consume(item, -quantity); return;
        }
        int leftover = inv.Produce(item, quantity); 
        if (leftover > 0){
            Debug.LogError(aName + " produced without space in inventory");
            Path dropPath = nav.FindPathToDrop(item, leftover);
            if (dropPath == null){
                Debug.Log("no place to drop " + item.name + "!! excess item disappearing.");
                return;
            }
            Tile dTile = dropPath.tile;
            if (dTile.inv == null){
                dTile.inv = new Inventory(x: dTile.x, y: dTile.y);
            }
            dTile.inv.Produce(item, leftover);
        }
    }
    public void Produce(string itemName, int quantity = 1) { Produce(Db.itemByName[itemName], quantity); }
    public void Produce(ItemQuantity iq) { Produce(iq.item, iq.quantity); }
    public void Produce(ItemQuantity[] iqs) { Array.ForEach(iqs, iq => Produce(iq)); }
    public void Produce(Recipe recipe) { // checks inv for recipe inputs
        if (recipe != null && inv.ContainsItems(recipe.inputs)){
            foreach (ItemQuantity iq in recipe.inputs) { inv.Produce(iq.item, -iq.quantity); }
            Produce(recipe.outputs);
        }
        else { Debug.Log($"{aName} ({job.name}) called produce without ingredients for recipe at ({(int)x},{(int)y})"); }
    }
    public bool CanProduce(Recipe recipe) {
        Building b = TileHere()?.building;
        return b != null && inv.ContainsItems(recipe.inputs) && recipe.tile == b.structType.name;
    }
    public void Consume(Item item, int quantity = 1){
        // Group items (e.g. "planks") can't exist in inventories — resolve to the leaf actually held.
        if (item.children != null) {
            ItemStack stack = inv.GetItemStack(item);
            if (stack == null) {
                Debug.LogError($"tried consuming {item.name} but no matching leaf found in inventory!");
                return;
            }
            item = stack.item;
        }
        if (inv.Produce(item, -quantity) < 0){
            Debug.LogError("tried consuming more than you have!");
        }
    }


    // ── Thinking ─────────────────────────────────────────────────────────────
    public Recipe PickRecipeRandom(){ // randomized selection
        if (job.recipes.Length == 0) { return null; }
        List<Recipe> eligibleRecipes = new List<Recipe>();
        foreach (Recipe recipe in job.recipes){
            if (recipe == null) continue;
            if (RecipePanel.instance != null && !RecipePanel.instance.IsAllowed(recipe.id)) continue;
            if (ResearchSystem.instance != null && !ResearchSystem.instance.IsRecipeUnlocked(recipe.id)) continue;
            if (ginv.SufficientResources(recipe.inputs)){
                if (!Db.structTypeByName.ContainsKey(recipe.tile) ||
                    !nav.CanReachBuilding(Db.structTypeByName[recipe.tile])) continue;
                eligibleRecipes.Add(recipe);
            }
        }
        if (eligibleRecipes.Count == 0) { return null; }
        int index = UnityEngine.Random.Range(0, eligibleRecipes.Count);
        return eligibleRecipes[index];
    }
    public Recipe PickRecipe(){ // score based selection
        if (job.recipes.Length == 0) { return null; }
        var targets = InventoryController.instance.targets;
        float maxScore = 0;
        Recipe bestRecipe = null;
        foreach (Recipe recipe in job.recipes){
            if (recipe == null) continue;
            if (RecipePanel.instance != null && !RecipePanel.instance.IsAllowed(recipe.id)) continue;
            if (ResearchSystem.instance != null && !ResearchSystem.instance.IsRecipeUnlocked(recipe.id)) continue;
            if (ginv.SufficientResources(recipe.inputs)){
                if (!Db.structTypeByName.ContainsKey(recipe.tile) ||
                    !nav.CanReachBuilding(Db.structTypeByName[recipe.tile])) continue;
                float score = recipe.Score(targets);
                if (score > maxScore){
                    maxScore = score;
                    bestRecipe = recipe;
                }
            }
        }
        return bestRecipe;
    }

    // Like PickRecipe but scoped to a specific building instance assigned by the WorkOrderManager.
    // Only considers recipes for that building type; skips the CanReachBuilding check since
    // the building reference is already known to the caller.
    public Recipe PickRecipeForBuilding(Building building){
        var targets = InventoryController.instance.targets;
        float maxScore = 0;
        Recipe bestRecipe = null;
        foreach (Recipe recipe in job.recipes){
            if (recipe == null) continue;
            if (recipe.tile != building.structType.name) continue;
            if (RecipePanel.instance != null && !RecipePanel.instance.IsAllowed(recipe.id)) continue;
            if (ResearchSystem.instance != null && !ResearchSystem.instance.IsRecipeUnlocked(recipe.id)) continue;
            if (!ginv.SufficientResources(recipe.inputs)) continue;
            if (recipe.AllOutputsSatisfied(targets)) continue;
            float score = recipe.Score(targets);
            if (score > maxScore){
                maxScore = score;
                bestRecipe = recipe;
            }
        }
        return bestRecipe;
    }

    const float MaxCraftSeconds = 20f;

    public int CalculateWorkPossible(Recipe recipe){
        // looks at inputs in gInv, and first available storage you can find in animal range, at least 1.
        // the storage thing makes it a bit conservative.

        // Cap rounds by estimated time rather than a fixed count, so slow recipes
        // don't lock the mouse for ages. We estimate seconds-per-round from the
        // recipe workload and the animal's current work efficiency (ticks are 1 s).
        Skill? skill = null;
        if (recipe.skill != null && System.Enum.TryParse<Skill>(recipe.skill, ignoreCase: true, out Skill s))
            skill = s;
        float workEff = ModifierSystem.GetWorkMultiplier(this, skill);
        int numRounds = workEff > 0f
            ? Math.Max((int)(MaxCraftSeconds * workEff / recipe.workload), 1)
            : 1;
        int n;
        foreach (ItemQuantity input in recipe.inputs){
            n = InventoryController.instance.TotalAvailableQuantity(input.item) / input.quantity;
            if (n < numRounds) { numRounds = n; }
        }
        foreach (ItemQuantity output in recipe.outputs){
            var (storePath, storeInv) = nav.FindPathToStorage(output.item);
            if (storePath == null) { n = 0; }
            else{
                n = storeInv.GetStorageForItem(output.item) / output.quantity;
            }
            if (n < numRounds) { numRounds = Math.Max(n, 1); }
        }
        return numRounds;
    }


    // ── Utils ────────────────────────────────────────────────────────────────
    public void SetJob(Job newJob){
        Job oldJob = this.job;
        this.job = newJob;
        if (cbAnimalChanged != null){
            cbAnimalChanged(this, oldJob);
        }
        // Defer Refresh until solid ground if we're mid-waypoint traversal.
        // preventFall==true means the animal is between waypoint nodes (cliff/stair);
        // calling Refresh now would leave them frozen on an unstandable tile.
        if (nav != null && nav.preventFall){
            pendingRefresh = true;
        } else {
            Refresh();
        }
    }
    public void SetJob(string jobStr) { SetJob(Db.GetJobByName(jobStr)); }


    public void FindHome(){
        if (nav == null) return;
        // if you have no home tile, or your hometile does not have a house,
            // if u can find a house, set ur hometile to that
        if (homeTile == null || !(homeTile?.building?.structType.name == "house") || homeTile.building.IsBroken){
            Path housePath = nav.FindPathToBuilding(Db.structTypeByName["house"]);
            if (housePath != null && housePath.tile.building?.structType.name == "house") {
                // should maybe also unreserve previous house, if you can?
                if (homeTile?.building?.structType.name == "house") homeTile.building.res.Unreserve();
                homeTile = housePath.tile;
                homeTile.building.res.Reserve(aName);
            }
        } else if (!homeTile.building.res.Available()) {
            // current house is full — look for another house with >= 2 free slots
            Tile currentHome = homeTile;
            Path betterPath = nav.FindPathTo(t =>
                t != currentHome &&
                t.building?.structType.name == "house" &&
                !t.building.IsBroken &&
                t.building.res.capacity - t.building.res.reserved >= 2);
            if (betterPath != null) {
                homeTile.building.res.Unreserve();
                homeTile = betterPath.tile;
                homeTile.building.res.Reserve(aName);
            }
        }
    }

    public Tile TileHere() { return world.GetTileAt(x, y); }

    // Call after any position change to keep tile-occupancy tracking up to date.
    public void UpdateCurrentTile() {
        Tile newTile = TileHere();
        if (newTile != _currentTile) {
            AnimalController.instance.UnregisterAnimalFromTile(_currentTile);
            AnimalController.instance.RegisterAnimalOnTile(newTile);
            _currentTile = newTile;
        }
    }

    public bool AtHome() {
        return homeTile != null && homeTile == TileHere() && homeTile.building?.structType.name == "house";
    }

    public bool HasHouse => homeTile?.building?.structType.name == "house";

    public bool IsMoving(){
        return state == AnimalState.Moving || state == AnimalState.Falling;
    }

    public float SquareDistance(float x1, float x2, float y1, float y2) { return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2); }
    public void Destroy() {
        // Fail before tearing down inv — otherwise in-flight reservations strand on about-to-die stacks.
        task?.Fail();
        AnimalController.instance.UnregisterAnimalFromTile(_currentTile);
        _currentTile = null;
        if (inv != null) { inv.Destroy(); inv = null; }
        GameObject.Destroy(gameObject);
    }

    public void RegisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged += callback; }
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged -= callback; }
}



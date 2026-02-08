using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// an animal is a 

public class Animal : MonoBehaviour
{
    public string aName;
    public int id;
    public float x;
    public float y;
    public float maxSpeed = 2f;
    public bool isMovingRight = true;

    public Tile target;         // where you are currently going
    public enum DeliveryTarget { None, Storage, Blueprint, Drop, Self }
    public DeliveryTarget deliveryTarget = DeliveryTarget.None;
    public Tile workTile;       // where production happens
    public Item desiredItem;    // item you're fetching
    public int desiredItemQuantity;
    public Tile storageTile;    // tile to store fetched items in (for haulers)
    public Tile homeTile;

    public Recipe recipe;
    public int numRounds = 0;

    public Job job;
    public Inventory inv;
    public GlobalInventory ginv;

    public float energy;     // every time you get 1 energy you can do 1 work
    public float efficiency; // energy gain rate

    // list of skills goes here?
    // maybe one for each job?

    public Eating eating;
    public Eeping eeping;
    public Nav nav;
    public AnimationController animationController;

    public Task task;

    public enum AnimalState{
        Idle,
        Walking,        // Generic movement state
        Working,        // For any work at a location (crafting, building, etc)
        Fetching,       // Getting items
        Delivering,     // Delivering items
        Eeping,
        Moving,         // any type of moving, under the task system
    }
    public enum Objective { // need to actually use this to make remembering what you wanted to do more foolproof
        None,
        Construct,
    }
    private AnimalStateManager stateManager;
    private AnimalState _state;
    public AnimalState state{
        get { return _state; }
        set {
            if (_state != value) {
                _state = value;
                stateManager.OnStateEnter(value);
                if (animationController != null)
                {
                    animationController.UpdateState();
                }
            }
        }
    }
    
    public Objective objective;

    public System.Random random;
    public int tickCounter = 0;

    public GameObject go;
    public SpriteRenderer sr;
    public Sprite sprite;
    public Bounds bounds; // a box to click on to select the animal
    public World world;



    Action<Animal, Job> cbAnimalChanged;

    // -----------------------
    //  
    // -----------------------
    public void Start(){
        world = World.instance;
        this.aName = "mouse" + id.ToString();
        this.stateManager = new AnimalStateManager(this);
        this.state = AnimalState.Idle;
        this.objective = Objective.None;
        this.job = Db.jobs[0];
        this.go = this.gameObject;
        this.go.name = "animal_" + aName;
        this.sr = go.GetComponent<SpriteRenderer>();
        animationController = go.GetComponent<AnimationController>();
        sr.sortingOrder = 50;
        this.inv = new Inventory(5, 10, Inventory.InvType.Animal);
        this.efficiency = 1f;
        this.energy = 0f;

        this.eating = new Eating();
        this.eeping = new Eeping();
        this.nav = new Nav(this);

        ginv = GlobalInventory.instance;
        random = new System.Random();

        FindHome();
    }


    public void TickUpdate() { // called from animalcontroller each second.
        if (this.eating == null) { return; } // animal not fully initted yet
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
            if (inv.ContainsItem(Db.itemByName["wheat"])) {
                Consume(Db.itemByName["wheat"], 1);
                eating.Eat(20f);
            }
            else {
                task = new ObtainTask(this, Db.itemByName["wheat"], 5);
                if (!task.Start()){
                    Debug.Log("can't find food!");
                }
            }
        } else if (eeping.Eepy() && state != AnimalState.Eeping) {
            task = new EepTask(this);
            task.Start();
        }
    }
    private void UpdateEfficiency() {
        efficiency = eating.Efficiency() * eeping.Efficiency();
        maxSpeed = 2f * efficiency;
    }

    public void SlowUpdate() { // called every 10 or so seconds
        FindHome();
    }

    public void Update() { // called all the time, for movement and detecting arrival
        stateManager.UpdateMovement(Time.deltaTime);
    }



    public void FindWork() {
        if (task != null){ return; } //TODO: change when findwork is called to just whenever task is null
        if (job.name == "none") { // free past worktile
            StartDropping();
            Refresh();
            return;
        }
        // 1. Check for blueprints needing construction
        Tile constructionTile = nav.FindConstructingBlueprint(job);
        if (constructionTile != null){
            Path constructionPath = nav.FindPathConstructingBlueprint(job);
            if (constructionPath != null){
                SetWorkTile(constructionTile);
                objective = Objective.Construct;
                GoTo(constructionTile);
                return;
            }
            else { // find adjacent tile to blueprint
                Tile[] adjacents = constructionTile.GetAdjacents();
                Path shortestConstructionPath = null;
                float shortestPathCost = 1000000f;
                foreach (Tile adjacent in adjacents)
                { // try navigating to any adjacent tile of the blueprint tile. 
                    if (adjacent != null && adjacent.node.standable){
                        Path candidatePath = nav.FindPathTo(adjacent);
                        if ((candidatePath != null) && (candidatePath.cost < shortestPathCost)){
                            shortestConstructionPath = candidatePath;
                            shortestPathCost = candidatePath.cost;
                        }
                    }
                }
                if (shortestConstructionPath != null){
                    constructionPath = shortestConstructionPath;
                    SetWorkTile(constructionTile); // note this is different from the tile you are going to!
                    objective = Objective.Construct;
                    GoTo(constructionPath);
                    Debug.Log("travelling to adjacent tile of blueprint!");
                }
            }
        }
        // 1.5: Check for blueprints that need resources
        Path blueprintPath = nav.FindReceivingBlueprint(job);
        if (blueprintPath != null){
            deliveryTarget = DeliveryTarget.Blueprint;
            StartFetching();
            return;
        }
        // 2. Check for harvestable resources matching job
        Path harvestPath = nav.FindHarvestable(job);
        if (harvestPath != null) { 
            task = new HarvestTask(this, harvestPath.tile);
            if (task.Start()) return;
        }
        // 3. if hauler, hauls
        if (job.name == "hauler") {
            // StartDropping();
            // deliveryTarget = DeliveryTarget.Storage;
            // StartFetching();
            task = new HaulTask(this);
            if (task.Start()) return;
        }
        // 4. start crafting
        // TryStartCrafting();
        task = new CraftTask(this);
        if (task.Start()) return;

        task = null; // none of the above tasks started successfully...
        return;
    }

    // returns whether you have gathered all ingredients
    // private bool TryStartCrafting(){
    //     recipe = PickRecipe();
    //     if (recipe == null) { return false; }
    //     Path p = null;
    //     // first find work tile.
    //     if (Db.structTypeByName.ContainsKey(recipe.tile)){
    //         p = nav.FindBuilding(Db.structTypeByName[recipe.tile]);
    //     }
    //     if (p == null) { return false; }

    //     numRounds = CalculateWorkPossible(recipe);      // calc numRounds
    //     if (inv.ContainsItems(recipe.inputs, numRounds)) { // if have all the inputs, go to work.
    //         SetWorkTile(p.tile);
    //         GoTo(p.tile);
    //         return true;
    //     }
    //     else { // if missing some inputs, fetch the first missing input to your inventory.
    //         foreach (ItemQuantity input in recipe.inputs){
    //             if (!inv.ContainsItem(input, numRounds)){
    //                 if (input == null) { Debug.LogError("recipe input is null??"); }
    //                 deliveryTarget = DeliveryTarget.Self; // note: unsure abt this
    //                 StartFetching(input.item, input.quantity * numRounds);
    //                 return false;
    //             } 
    //         }
    //         Debug.LogError("can't find crafting input!");
    //         return false;
    //     }
    // }


    public bool Collect() {
        if (recipe == null) { Debug.LogError("lost recipe!"); return false; }
        foreach (ItemQuantity input in recipe.inputs){
            if (!inv.ContainsItem(input, numRounds))
            {
                deliveryTarget = DeliveryTarget.Storage;
                return StartFetching(input.item);
            }
        }
        GoTo(workTile); // have all ingredients, go to work.
        return true;
        // don't think this handles the case where you can't find any of the items. may be infinite looping
    }

    // -----------------------
    // ITEM MOVING LOOP 
    // -----------------------

    // Combine Fetch methods into one clear entry point
    public bool StartFetching(Item item = null, int quantity = -1){
        Path itemPath = null;
        switch (deliveryTarget){
            // case DeliveryTarget.Storage:
            //     if (item == null) {
            //         itemPath = nav.FindAnyItemToHaul(); // sets desiredItem and desiredquantity and storageTile
            //     }
            //     else {
            //         itemPath = nav.FindItemToHaul(item);
            //         if (itemPath != null){
            //             desiredItem = item;
            //             if (quantity != -1) { desiredItemQuantity = quantity; } // edfault, don't touch desired quantity
            //             storageTile = nav.FindStorage(item).tile;
            //         }
            //     }
            //     break;

            case DeliveryTarget.Blueprint:
                if (item != null) { Debug.LogError("delivery target is blueprint, but item is not null!"); }
                Path blueprintPath = nav.FindReceivingBlueprint(job);
                if (blueprintPath != null) {
                    Blueprint blueprint = blueprintPath.tile.blueprint;
                    for (int i = 0; i < blueprint.costs.Length; i++)  {
                        if (blueprint.deliveredResources[i].quantity < blueprint.costs[i].quantity) {
                            item = blueprint.costs[i].item;
                            itemPath = nav.FindItem(item);
                            if (itemPath != null) {
                                desiredItem = item;
                                desiredItemQuantity = blueprint.costs[i].quantity - blueprint.deliveredResources[i].quantity;
                                storageTile = blueprint.tile;
                                SetWorkTile(blueprint.tile);
                                break;
                            }
                        }
                    }
                }
                break;
            case DeliveryTarget.Self: // picking up for own use.
                if (item == null) { Debug.LogError("picking up null item for own use!"); }
                desiredItem = item;
                if (quantity != -1) { desiredItemQuantity = quantity; }  // default, don't touch desired quantity
                storageTile = null; // storing in own inv
                itemPath = nav.FindItem(item);
                break;
            case DeliveryTarget.None:
                Debug.LogError("delivery target is none!"); return false;
        }

        if (itemPath != null) {
            nav.Navigate(itemPath);
            state = AnimalState.Fetching; // this is not set in animalstate manager!
            return true;
        }
        else {
            Refresh();
            return false;
        }
    }
    public void OnArrivalFetch(){
        TakeItem(desiredItem, desiredItemQuantity);
        desiredItemQuantity = desiredItemQuantity - inv.Quantity(desiredItem);

        if (deliveryTarget == DeliveryTarget.None){
            Debug.LogError("delivery target is none!"); return;
        }
        else if (deliveryTarget == DeliveryTarget.Self)
        { // if taking for self, keep fetching if you need more.
            if (inv.GetStorageForItem(desiredItem) > 5 && desiredItemQuantity > 0){
                StartFetching(desiredItem, desiredItemQuantity);  // Keep collecting if we need more
            }
            else {
                Refresh(); // have enough of the item, do whatever now
            }
        }
        else {
            // Normal fetching behavior
            if (inv.GetStorageForItem(desiredItem) > 5 && desiredItemQuantity > 0){
                StartFetching(desiredItem);
            }
            else{
                StartDelivering();
            }
        }
    }


    public void StartDelivering(){
        switch (deliveryTarget){
            case DeliveryTarget.Storage:
                if (nav.NavigateTo(storageTile)){ // storage tile was saved in storageTile
                    state = AnimalState.Delivering;
                    return;
                }
                break;
            case DeliveryTarget.Blueprint:
                if (nav.NavigateTo(storageTile)){ // Blueprint tile was saved in storageTile
                    state = AnimalState.Delivering;
                    return;
                }
                break;
            case DeliveryTarget.Drop:
                Path dropPath = nav.FindPlaceToDrop(desiredItem);
                if (dropPath == null){
                    // no path found while dropping. can't just call dropping, that's circular. just destroy the items.
                    Refresh();
                    return;
                }
                else{
                    nav.Navigate(dropPath);
                    state = AnimalState.Delivering;
                    return;
                }
        }
        StartDropping();
    }

    public void OnArrivalDeliver(){
        switch (deliveryTarget){
            case DeliveryTarget.Storage:
                DropItem(desiredItem);
                break;
            case DeliveryTarget.Blueprint: // this doesn't really work. might want to drop at inv instead of blueprint
                if (target.blueprint.state == Blueprint.BlueprintState.Receiving){
                    DropAtBlueprint();
                }
                else {
                    // Blueprint no longer accepting resources, drop items elsewhere
                    deliveryTarget = DeliveryTarget.Drop;
                    StartDelivering();
                }
                return;
            case DeliveryTarget.Drop:
                DropItem(desiredItem);
                break;
        }
        int itemInInv = inv.Quantity(desiredItem);
        if (itemInInv > 0){
            deliveryTarget = DeliveryTarget.Drop;
            StartDelivering();
        }  // keep dropping. might caues infinite loop with dropping wheat though you need to keep it.
        else{
            Refresh();
        }
    }

    // this uses the task/objective system!
    public void OnArrival(){
        task?.OnArrival();
    }

    public void Refresh(){ // command to forget stuff and become idle.
        desiredItem = null;
        desiredItemQuantity = 0;
        deliveryTarget = DeliveryTarget.None;
        RemoveWorkTile(); // turn this into a setter/getter thing
        storageTile = null;
        state = AnimalState.Idle;
    }


    //--------------
    // ITEM MOVEMENT (maybe these should be moved into Inventory class??)
    // -----------------------
    // picks up item current location, returns amount *not* taken
    public int TakeItem(Item item, int quantity){ 
        Tile tileHere = TileHere();
        if (tileHere != null && tileHere.inv != null) {
            int leftover = tileHere.inv.MoveItemTo(inv, item, quantity);
            if (tileHere.inv.IsEmpty() && tileHere.inv.invType == Inventory.InvType.Floor){
                tileHere.inv.Destroy();
                tileHere.inv = null; // delete an empty floor inv.
            }
            return leftover;
        }
        return quantity;
    }
    public int TakeItem(ItemQuantity iq){ return(TakeItem(iq.item , iq.quantity)); }

    // moves item to tile here. returns amount *not* dropped
    public int DropItem(Item item, int quantity = -1){ 
        if (quantity == -1) { quantity = inv.Quantity(item); }
        return (inv.MoveItemTo(EnsureFloorInventory(TileHere()), item, quantity));
        // what to do when this fails???
        // need failsafe
    }
    // returns inventory if it's at a tile, otherwise makes a floor inventory.
    public Inventory EnsureFloorInventory(Tile t) {
        if (t.inv == null) {
            t.inv = new Inventory(x: t.x, y: t.y);
        }
        return t.inv;
    }
    public int DropItem(ItemQuantity iq) { return(DropItem(iq.item, iq.quantity)); }

    public void DropAtBlueprint(){      // deliver items to blueprint
        int amountToDeliver = inv.Quantity(desiredItem);
        int delivered = target.blueprint.ReceiveResource(desiredItem, amountToDeliver);
        inv.AddItem(desiredItem, -delivered); // remove item from own inv
        int itemInInv = inv.Quantity(desiredItem);
        if (itemInInv > 0)
        { // if you have excess of the item, drop it somewhere 
            StartDropping(desiredItem);
            return;
        }
    }


    public void StartDropping(Item item, int quantity = -1){ // tries to go drop all of an item at a nearby tile.
        if (item == null) { StartDropping(); return; }
        desiredItem = item;
        desiredItemQuantity = quantity;
        deliveryTarget = DeliveryTarget.Drop;
        StartDelivering(); // will find place to drop.
    }
    public void StartDropping()
    {    // goes to drops all items, tries to keep 5 food on hand.
         // actually keeps 5 of each food stack. kinda sloppy, needs work eventually.
        foreach (ItemStack stack in inv.itemStacks)
        {
            if (stack != null && stack.item != null && stack.quantity > 0)
            { // im not sure why the quantity check isn't working??
                StartDropping(stack.item);
            }
        }
    }

    // produces item in ani inv, dumps at nearby tile if inv full
    public void Produce(Item item, int quantity = 1){
        if (quantity < 0){
            Debug.LogError("called produce with negative quantity, use consume instead");
            Consume(item, -quantity); return;
        }
        int leftover = inv.Produce(item, quantity); 
        //int leftover = quantity;
        if (leftover > 0){
            Debug.Log(aName + " produced without space in inventory");
            Path dropPath = nav.FindPlaceToDrop(item);
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
    public void Produce(Recipe recipe)
    { // checks inv for recipe inputs
        if (recipe != null && inv.ContainsItems(recipe.inputs))
        {
            foreach (ItemQuantity iq in recipe.inputs) { inv.Produce(iq.item, -iq.quantity); }
            Produce(recipe.outputs);
        }
        else { Debug.Log("called produce without having all recipe ingredients! not doing."); }
    }
    public void Consume(Item item, int quantity = 1){
        if (inv.Produce(item, -quantity) < 0){
            Debug.LogError("tried consuming more than you have!");
        }
    }


    // -----------------------
    // THINKING
    // -----------------------
    public Recipe PickRecipe2(){ // randomized selection
        if (job.recipes.Length == 0) { return null; }
        List<Recipe> eligibleRecipes = new List<Recipe>();
        foreach (Recipe recipe in job.recipes){
            if (recipe != null && ginv.SufficientResources(recipe.inputs)){
                float score = recipe.Score();
                eligibleRecipes.Add(recipe);
            }
        }
        if (eligibleRecipes.Count == 0) { return null; }
        int index = UnityEngine.Random.Range(0, eligibleRecipes.Count);
        return eligibleRecipes[index];
    }
    public Recipe PickRecipe()
    { // score based selection
        if (job.recipes.Length == 0) { return null; }
        float maxScore = 0;
        Recipe bestRecipe = null;
        foreach (Recipe recipe in job.recipes){
            if (recipe != null && ginv.SufficientResources(recipe.inputs)){
                float score = recipe.Score();
                if (score > maxScore){
                    maxScore = score;
                    bestRecipe = recipe;
                }
            }
        }
        return bestRecipe;
    }

    public int CalculateWorkPossible(Recipe recipe){
        // looks at inputs in gInv, and first available storage you can find in animal range, at least 1. 
        // the storage thing makes it a bit conservative. 
        if (recipe.inputs.Length == 0) { return -1; }
        int numRounds = 10; // will try to gather this amount of input at once.
        int n;
        foreach (ItemQuantity input in recipe.inputs){
            n = ginv.Quantity(input.id) / input.quantity;
            if (n < numRounds) { numRounds = n; }
        }
        foreach (ItemQuantity output in recipe.outputs){
            Path storePath = nav.FindStorage(output.item);
            if (storePath == null) { n = 0; }
            else{
                n = storePath.tile.GetStorageForItem(output.item) / output.quantity;
            }
            if (n < numRounds) { numRounds = Math.Max(n, 1); }
        }
        return numRounds;
    }


    // -----------------------
    // UTILS
    // -----------------------
    public void SetJob(Job newJob){
        Job oldJob = this.job;
        this.job = newJob;
        if (cbAnimalChanged != null){
            cbAnimalChanged(this, oldJob);
        }
        Refresh();
        FindWork();
    }
    public void SetJob(string jobStr) { SetJob(Db.GetJobByName(jobStr)); }


    // navigates to t and sets state to walking.
    public bool GoTo(Tile t, Path p = null){
        if (t == null && p == null) { Debug.LogError("destination tile is null!"); return false; }
        if (nav.NavigateTo(t, p))
        {
            state = AnimalState.Walking;
            return true;
        }
        return false;
    }
    public bool GoTo(Path p){
        return GoTo(p.tile, p);
    }
    public bool GoTo(float x, float y) { return GoTo(world.GetTileAt(x, y)); }

    public void GoToEep(){
        if (homeTile == null) { state = AnimalState.Eeping; }
        else { GoTo(homeTile); }
    }

    public void SetWorkTile(Tile t){
        if (workTile != null){
            //workTile.reserved -= 1;  // change this to add WorkSite at each building and blueprint! and have it be reservable
        }
        workTile = t;
        //workTile.reserved += 1;
    }
    public void RemoveWorkTile(){
        if (workTile == null) { return; }
        //workTile.reserved -= 1;
        workTile = null;
    }
    public void FindHome(){
        if (homeTile == null){
            Path homePath = nav.FindBuilding(Db.structTypeByName["house"]);
            if (homePath != null) { homeTile = homePath.tile; }
            if (homeTile != null){
                homeTile.building.reserved += 1;
            }
        }
    }


    public Tile TileHere() { return world.GetTileAt(x, y); }
    public bool AtHome() { return homeTile != null && homeTile == TileHere(); }
    public bool AtWork(){ // are you at the place where you can produce a recipe?
        return workTile != null && workTile == TileHere() &&
        recipe != null && recipe.tile == workTile.type.name;
    }
    public bool CanProduce(Recipe recipe) {
        return inv.ContainsItems(recipe.inputs) && recipe.tile == TileHere().building.structType.name;
    }

    public bool IsMoving(){
        return !(state == AnimalState.Idle || state == AnimalState.Working
            || state == AnimalState.Eeping);
    }

    public float SquareDistance(float x1, float x2, float y1, float y2) { return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2); }
    public void RegisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged += callback; }
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged -= callback; }
}



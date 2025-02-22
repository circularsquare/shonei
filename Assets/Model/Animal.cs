using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;


public class Animal : MonoBehaviour      
{
    public string aName;
    public int id;
    public float x;
    public float y;
    public float timerOffset = 0f; // unused i think
    public float maxSpeed = 2f;
    public bool isMovingRight = true;

    public Tile target;         // where you are currently going
    public enum DeliveryTarget { None, Storage, Blueprint, Drop }
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

    public enum AnimalState {
        Idle,
        Walking,        // Generic movement state
        Working,        // For any work at a location (crafting, building, etc)
        Fetching,       // Getting items
        Delivering,     // Delivering items
        Eeping,
    } 
    private AnimalStateManager stateManager;
    private AnimalState _state;
    public AnimalState state{
        get { return _state; }
        set {
            if (_state != value) {
                _state = value;
                stateManager.OnStateEnter(value);
                if (animationController != null) {
                    animationController.UpdateState();
                }
            }
        }
    }

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
    

    public void TickUpdate(){ // called from animalcontroller each second.
        if (this.eating == null){return;} // animal not fully initted yet
        tickCounter++;
        if (tickCounter % 10 == 0){ 
            SlowUpdate(); 
        }

        HandleNeeds(); 
        UpdateEfficiency();

        energy += efficiency;
        if (energy > 1f){ // if you have enough energy, spend it. also then you can work if you're Working.
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
            } else {
                FetchQuantity(Db.itemByName["wheat"], 5);
            }
        } else if (eeping.Eepy() && state != AnimalState.Eeping) {
            GoToEep();
        }
    }
    private void UpdateEfficiency() {
        efficiency = eating.Efficiency() * eeping.Efficiency();
        maxSpeed = 2f * efficiency;
    }
    public void SlowUpdate(){ // called every 10 or so seconds
        FindHome();
    }

    public void Update(){ // for movement and detecting arrival
        stateManager.UpdateMovement(Time.deltaTime);
    }

    public void FindWork(){
        if (job.name == "none"){ // free past worktile
            StartDropping();
            if (workTile != null){
                RemoveWorkTile();}
            state = AnimalState.Idle;
            return;
        } 

        // 1. First priority: Check for blueprints needing construction
        Path constructionPath = nav.FindConstructingBlueprint(job);
        if (constructionPath != null) {
            SetWorkTile(constructionPath.tile);
            GoTo(constructionPath.tile);
            return;
        }
        // 1. First priority: Check for blueprints that need resources
        Path blueprintPath = nav.FindReceivingBlueprint(job);
        if (blueprintPath != null) {
            deliveryTarget = DeliveryTarget.Blueprint;
            StartFetching();
            return;
        }

        // 2. Second priority: Check for harvestable resources matching job
        Path harvestPath = nav.FindHarvestable(job);
        if (harvestPath != null) {  // this is kinda duplicated in Harvest()
            SetWorkTile(harvestPath.tile); 
            GoTo(workTile);
            return;
        }

        // 3. Third priority: Job-specific behaviors
        switch (job.name) {
            case "hauler":
                // Haulers look for items to move to storage
                StartDropping();
                deliveryTarget = DeliveryTarget.Storage;
                StartFetching();
                return;

            default:
                // Other jobs try to craft recipes
                TryStartCrafting();
                break;
        }

    }
    private void TryStartCrafting(){
        recipe = PickRecipe();
        if (recipe == null){ return; }
        Path p = null;
            if (Db.tileTypeByName.ContainsKey(recipe.tile)){ // if can find unreserved work tile
                p = nav.FindWorkTile(Db.tileTypeByName[recipe.tile]); // maybe get rid of.
            }
            else if (Db.structTypeByName.ContainsKey(recipe.tile)){
                p = nav.FindBuilding(Db.structTypeByName[recipe.tile]);
            } 
            if (p != null){
                numRounds = CalculateWorkPossible(recipe);      // calc numRounds
                SetWorkTile(p.tile);                                 // reserve work tile
                if (inv.ContainsItems(recipe.inputs, numRounds)){ // if have all the inputs, just go there.
                    GoTo(p.tile);
                } else { // if missing some inputs, collect the missing inputs in your inventory.
                    Collect();
                }
            }
    }

    // -----------------------
    // ITEM MOVING LOOP 
    // -----------------------

    // Combine Fetch methods into one clear entry point
    public bool StartFetching(Item item = null, int quantity = -1) {
        
        Path itemPath = null;
        switch(deliveryTarget) {
            case DeliveryTarget.Storage:
                itemPath = (item == null) ? nav.FindAnyItemToHaul() : nav.FindItemToHaul(item);
                if (itemPath != null) {
                    storageTile = nav.FindStorage(item ?? itemPath.tile.inv.GetItemToHaul()).tile;
                }
                break;
                
            case DeliveryTarget.Blueprint:
                Path blueprintPath = nav.FindReceivingBlueprint(job);
                if (blueprintPath != null){
                    Blueprint blueprint = blueprintPath.tile.blueprint;
                    for (int i = 0; i < blueprint.costs.Length; i++) {
                        if (blueprint.deliveredResources[i].quantity < blueprint.costs[i].quantity) {
                            item = blueprint.costs[i].item;
                            itemPath = nav.FindItem(item);
                            if (itemPath != null) {
                                desiredItemQuantity = blueprint.costs[i].quantity - blueprint.deliveredResources[i].quantity;
                                storageTile = blueprint.tile;
                                SetWorkTile(blueprint.tile);
                                break;
                            }
                        }
                    }
                }
                break;
            case DeliveryTarget.None: // picking up for own use.
                if (item == null) {Debug.LogError("picking up null item for own use!");}
                itemPath = nav.FindItem(item);
                // desireditemquantity should already be correct
                break;
        }

        if (itemPath != null) {
            desiredItem = item ?? itemPath.tile.inv.GetItemToHaul();
            nav.Navigate(itemPath);
            state = AnimalState.Fetching;
            return true;
        } else {
            Refresh();
            Debug.Log("can't fetch anything");
            return false;
        }
    }
    public void OnArrivalFetch() {
        TakeItem(desiredItem, desiredItemQuantity);
        desiredItemQuantity = desiredItemQuantity - inv.Quantity(desiredItem);
        
        if (deliveryTarget == DeliveryTarget.None) {
            // For Take(), just go idle after collecting
            if (inv.GetStorageForItem(desiredItem) > 5 && desiredItemQuantity > 0) {
                StartFetching(desiredItem, desiredItemQuantity);  // Keep collecting if we need more
            } else {
                state = AnimalState.Idle;
            }
        } else {
            // Normal fetching behavior
            if (inv.GetStorageForItem(desiredItem) > 5 && desiredItemQuantity > 0) {
                StartFetching(desiredItem);
            } else {
                StartDelivering();
            }
        }
    }

    public Inventory EnsureFloorInventory(Tile t){
        if (t.inv == null){
            t.inv = new Inventory(1, 20, Inventory.InvType.Floor, t.x, t.y);
        }
        return t.inv;
    }
    public void StartDelivering() {
        switch(deliveryTarget) {
            case DeliveryTarget.Storage:
                Path storagePath = nav.FindStorage(desiredItem);
                if (storagePath != null) {
                    nav.Navigate(storagePath);
                    state = AnimalState.Delivering;
                    return;
                }
                break;
                
            case DeliveryTarget.Blueprint:
                nav.NavigateTo(storageTile);  // Blueprint tile was saved in storageTile
                state = AnimalState.Delivering;
                return;
                
            case DeliveryTarget.Drop:
                Path dropPath = nav.FindPlaceToDrop(desiredItem);
                if (dropPath != null) {
                    nav.Navigate(dropPath);
                    state = AnimalState.Delivering;
                    return;
                }
                // if no path found... not sure what to do.
                break;
        }
        
        // If we couldn't find a valid delivery location
        StartDropping();
        // need more robust solution: just destroy items.
        state = AnimalState.Idle;
    }

    public void OnArrivalDeliver() {
        switch(deliveryTarget) {
            case DeliveryTarget.Storage:
                DropItem(desiredItem); 
                break;
            case DeliveryTarget.Blueprint: // this doesn't really work. might want to drop at inv instead of blueprint
                if (target.blueprint.state == Blueprint.BlueprintState.Receiving) {
                    DropAtBlueprint();
                } else {
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
        if (itemInInv > 0) {
            deliveryTarget = DeliveryTarget.Drop;
            StartDelivering();
        }  // keep dropping. might caues infinite loop with dropping wheat though you need to keep it.
        else {
            Refresh();
        }
    }


    public bool FetchQuantity(Item item, int quantity = 1){
        deliveryTarget = DeliveryTarget.None;
        return StartFetching(item, quantity);
    }

    public bool Collect() {
        if (recipe == null) { Debug.LogError("lost recipe!"); return false; }
        foreach (ItemQuantity input in recipe.inputs) {
            if (!inv.ContainsItem(input, numRounds)) {
                deliveryTarget = DeliveryTarget.Storage;
                return StartFetching(input.item);
            }
        }
        GoTo(workTile); // have all ingredients, go to work.
        return true;
        // don't think this handles the case where you can't find any of the items. may be infinite looping
    }

    public bool Harvest(){
        Path p = nav.FindHarvestable(job);
        if (p != null){
            SetWorkTile(p.tile);
            GoTo(p.tile);
            return true;
        }
        return false;
    }
    // -----------------------
    // ITEM MOVEMENT (maybe these should be moved into Inventory class??)
    // -----------------------
    public void TakeItem(Item item, int quantity){  // pick up item from current location
        Tile tileHere = TileHere();
        if (tileHere != null && tileHere.inv != null){ 
            tileHere.inv.MoveItemTo(inv, item, quantity);
            if (tileHere.inv.IsEmpty() && tileHere.inv.invType == Inventory.InvType.Floor){
                tileHere.inv.Destroy(); 
                tileHere.inv = null; // delete an empty floor inv.
            }
        }
    }


    public void DropItem(Item item, int quantity = -1){ // moves item to tile here.
        if (quantity == -1){ quantity = inv.Quantity(item);}
        inv.MoveItemTo(EnsureFloorInventory(TileHere()), item, quantity);
        // what to do when this fails???
        // need failsafe
    }

    public void DropAtBlueprint() {      // deliver items to blueprint
        int amountToDeliver = inv.Quantity(desiredItem);
        int delivered = target.blueprint.ReceiveResource(desiredItem, amountToDeliver);
        inv.AddItem(desiredItem, -delivered); // remove item from own inv
        int itemInInv = inv.Quantity(desiredItem);
        if (itemInInv > 0){ // if you have excess of the item, drop it somewhere 
            StartDropping(desiredItem);
            return;
        }
    }


    public void StartDropping(Item item, int quantity = -1){     // tries to go drop all of an item at a nearby tile.
        desiredItem = item;
        desiredItemQuantity = quantity;
        deliveryTarget = DeliveryTarget.Drop;
        StartDelivering(); // will find place to drop.
    }
    public void StartDropping(){    // goes to drops all items, tries to keep 5 food on hand.
                                // actually keeps 5 of each food stack. kinda sloppy, needs work eventually.
        foreach (ItemStack stack in inv.itemStacks){
            if (stack != null && stack.item != null && stack.quantity > 0){ // im not sure why the quantity check isn't working??
                StartDropping(stack.item);
            }
        }
    }

    // TODO: make this use navigation!!
    // produces item in ani inv, dumps at nearby tile if inv full
    public void Produce(Item item, int quantity = 1){   
        if (quantity < 0){
            Debug.Log("called produce with negative quantity, use consume instead");
            Consume(item, -quantity); return;
        }
        // int leftover = inv.Produce(item, quantity); // disabled producing into inv for now...
        int leftover = quantity;
        if (leftover > 0){
            Tile dTile = nav.FindPlaceToDrop(item).tile;
            if (dTile == null){Debug.LogError("no place to drop item!! excess item disappearing.");}
            if (dTile.inv == null){
                dTile.inv = new Inventory(1, 20, Inventory.InvType.Floor, dTile.x, dTile.y);
            }
            dTile.inv.Produce(item, leftover);
        }        
    }
    public void Produce(string itemName, int quantity = 1){ Produce(Db.itemByName[itemName], quantity); }
    public void Produce(ItemQuantity iq){ Produce(iq.item, iq.quantity); }
    public void Produce(ItemQuantity[] iqs){ Array.ForEach(iqs, iq => Produce(iq)); }
    public void Produce(Recipe recipe){ // checks inv for recipe inputs
        if (recipe != null && inv.ContainsItems(recipe.inputs)){
            foreach (ItemQuantity iq in recipe.inputs){ inv.Produce(iq.item, -iq.quantity); }
            Produce(recipe.outputs);
        } else { Debug.Log("called produce without having all recipe ingredients! not doing."); }
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
        if (job.recipes.Length == 0){ return null;}
        List<Recipe> eligibleRecipes = new List<Recipe>();
        foreach (Recipe recipe in job.recipes){
            if (recipe != null && ginv.SufficientResources(recipe.inputs)){
                float score = recipe.Score();
                eligibleRecipes.Add(recipe);
            }
        }
        if (eligibleRecipes.Count == 0){return null;}
        int index = UnityEngine.Random.Range(0, eligibleRecipes.Count);
        return eligibleRecipes[index];
    }
    public Recipe PickRecipe(){ // score based selection
        if (job.recipes.Length == 0){ return null;}
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
        if (recipe.inputs.Length == 0){return -1;}
        int numRounds = 10; // will try to gather this amount of input at once.
        int n;
        foreach (ItemQuantity input in recipe.inputs){
            n = ginv.Quantity(input.id) / input.quantity;
            if (n < numRounds){numRounds = n;}
        }
        foreach (ItemQuantity output in recipe.outputs){
            Path storePath = nav.FindStorage(output.item);
            if (storePath == null) {n = 0;}
            else {
                n = storePath.tile.GetStorageForItem(output.item) / output.quantity; }
            if (n < numRounds){numRounds = Math.Max(n, 1);}
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
            cbAnimalChanged(this, oldJob);} 
        Refresh();
        FindWork();
    }
    public void SetJob(string jobStr){ SetJob(Db.GetJobByName(jobStr)); }


    // navigates to t and sets state to walking.
    public bool GoTo(Tile t, Path p = null){ 
        if (t == null && p == null){Debug.LogError("destination tile is null!"); return false;}
        if (nav.NavigateTo(t, p)) {
            state = AnimalState.Walking;
            return true;
        }
        return false;
    }
    public bool GoTo(float x, float y){ return GoTo(world.GetTileAt(x, y)); }

    public void GoToEep(){ 
        if (homeTile == null){state = AnimalState.Eeping;}
        else { GoTo(homeTile); }
    }


    public void SetWorkTile(Tile t){
        if (workTile != null){
            workTile.reserved -= 1;
        }
        workTile = t;
        workTile.reserved += 1;
    }
    public void RemoveWorkTile(){
        workTile.reserved -= 1;
        workTile = null; 
    }
    public void FindHome(){
        if (homeTile == null){
            Path homePath = nav.FindBuilding(Db.structTypeByName["house"]);
            if (homePath != null){ homeTile = homePath.tile; }
            if (homeTile != null){
                homeTile.building.reserved += 1;
            }
        }
    }

    public void Refresh(){ 
        desiredItem = null;
        desiredItemQuantity = 0;
        deliveryTarget = DeliveryTarget.None;
        workTile = null; 
        storageTile = null;
        state = AnimalState.Idle;
    }
    
    public Tile TileHere(){return world.GetTileAt(x, y);}
    public bool AtHome(){return homeTile != null && homeTile == TileHere();}

    public bool IsMoving(){
        return !(state == AnimalState.Idle || state == AnimalState.Working 
            || state == AnimalState.Eeping);
    }

    public float SquareDistance(float x1, float x2, float y1, float y2){return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);}
    public void RegisterCbAnimalChanged(Action<Animal, Job> callback){ cbAnimalChanged += callback;}
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback){ cbAnimalChanged -= callback;}
}




public class Nav {
    public Animal a; // have this be more generic? idk
    public World world;
    
    public Path path; 
    private int pathIndex = 0;
    private Node prevNode = null;
    private Node nextNode = null;


    public Nav (Animal a){
        this.a = a;
        this.world = a.world;
        path = null;
    }

    // sets target tile and sets path, starting navigation.
    public bool NavigateTo(Tile t, Path iPath = null){  // this should be the only way you set target!
        if (t == null && iPath == null){Debug.LogError("navigating without destination or path!"); return false;}
        if (iPath != null){path = iPath; t = iPath.tile;} // if fed a path, just use it.
        else {path = world.graph.Navigate(a.TileHere().node, t.node);} // otherwise find a path
        if (path == null){ return false; } // if still no path exists, leave

        pathIndex = 0; prevNode = path.nodes[0];        // set prevNode, pathindex
        if (path.length != 0) {nextNode = path.nodes[1];} else{nextNode = prevNode;} // set nextNode
        a.target = t;   // set target, starting navigation
        return true;
    }
    public bool Navigate(Path p){return NavigateTo(p.tile, p);}
    public void EndNavigation(){ path = null; pathIndex = 0; prevNode = null; nextNode = null;} // should stop you from moving
    public bool Move(float deltaTime){ // called by animal every frame!! returns whether you're done
        if (path == null || pathIndex >= path.length){return true;}  // no path... return true, give up
        if (SquareDistance(a.x, nextNode.x, a.y, nextNode.y) < 0.001f){
            if (pathIndex + 1 >= path.length){
                EndNavigation();
                return true;
            } else {
                pathIndex++;
                prevNode = nextNode;
                nextNode = path.nodes[pathIndex + 1];
            }
        } 

        Vector2 newPos = Vector2.MoveTowards(
            new Vector2(a.x, a.y), new Vector2(nextNode.x, nextNode.y), 
            a.maxSpeed * deltaTime);

        a.x = newPos.x; a.y = newPos.y;
        a.go.transform.position = new Vector3(a.x, a.y, 0);

        a.isMovingRight = (nextNode.x - a.x > 0);
        return false;
    }

    public Path FindItem(Item item, int r = 50){ return Find(t => t.ContainsItem(item), r); }
    public Path FindItemToHaul(Item item, int r = 50){ return Find(t => t.HasItemToHaul(item), r); }
    public Path FindStorage(Item item, int r = 50){ return Find(t => t.HasStorageForItem(item), r); }
    public Path FindPlaceToDrop(Item item, int r = 3){ return Find(t => t.HasSpaceForItem(item), r, true); }
    public Tile FindTileToDrop(Item item, int r = 3){ 
        Path p = FindPlaceToDrop(item, r);
        if (p != null){ return p.tile; }
        return null;
    }
    public Path FindBuilding(BuildingType buildingType, int r = 50){
        return Find(t => t.building != null && t.building.buildingType == buildingType && 
            t.building.capacity - t.building.reserved > 0, r);
    }
    public Path FindBuilding(StructType structType, int r = 50){
        if (structType is BuildingType){ return FindBuilding(structType as BuildingType, r);}
        else {return null;}
    }
    public Path FindWorkTile(TileType tileType, int r = 50){
        return Find(t => t.type == tileType && (t.capacity - t.reserved > 0) && 
            !(t.building != null && t.building is Plant), r);
    }
    public Path FindWorkTile(string tileTypeStr, int r = 30){ return FindWorkTile(Db.tileTypeByName[tileTypeStr], r); }
    public Path FindReceivingBlueprint(Job job, int r = 50){return Find(t => t.blueprint != null 
        && t.blueprint.structType.job == job 
        && t.blueprint.state == Blueprint.BlueprintState.Receiving, r);}
    public Path FindConstructingBlueprint(Job job, int r = 50){return Find(t => t.blueprint != null 
        && t.blueprint.structType.job == job 
        && t.blueprint.state == Blueprint.BlueprintState.Constructing, r);}
    public Path FindHarvestable(Job job, int r = 40){
        return Find(t => t.building != null && t.building is Plant 
        && (t.building as Plant).harvestable
        && (t.building.buildingType.job == job), r); // something about jobs here?
    }
    public Path Find(Func<Tile, bool> condition, int r, bool persistent = false){
        Path closestPath = null;
        float closestDistance = 100000f;
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(a.x + x, a.y + y);
                if (tile != null && condition(tile)) {
                    // wasn't this reversed for a while but it seemed to work? vvv
                    Path cPath = world.graph.Navigate(a.TileHere().node, tile.node);
                    if (cPath == null) { continue; }
                    float distance = cPath.cost; // try cost later.
                    //float distance = SquareDistance((float)tile.x, a.x, (float)tile.y, a.y);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestPath = cPath;
                    }
                }
            }
        } // should check in a wider radius if none found...
        if (persistent && closestPath == null && r < 60){ 
            Debug.Log("no tile found. expanding radius to " + (r + 3));
            return (Find(condition, r + 4, persistent));
        }
        return closestPath;
    }

    public Path FindAnyItemToHaul(int r = 50){ 
        float closestDistance = float.MaxValue;
        Path closestItemPath = null;
        Tile closestStorage = null;
        Item closestItem = null;
        Path itemPath = Find(t => t.HasItemToHaul(null), r);
        if (itemPath != null){
            Item item = itemPath.tile.inv.GetItemToHaul();
            if (item != null){
                Path storagePath = FindStorage(item, r=50);
                if (storagePath != null){
                    float distance = itemPath.length;
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestItemPath = itemPath;
                        closestStorage = storagePath.tile;
                        closestItem = item;
                    }
                }
            }
        }
        if (closestItemPath != null){
            Navigate(closestItemPath);
            a.storageTile = closestStorage;
            a.desiredItem = closestItem;
            a.desiredItemQuantity = Math.Min(closestItemPath.tile.inv.Quantity(closestItem),
                closestStorage.GetStorageForItem(closestItem)); // don't take more than u can store
            return closestItemPath;
        } else {
            a.Refresh();
            return null;
        }
    }

    // ========= utils =============
    public float SquareDistance(float x1, float x2, float y1, float y2){return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);}


}


public class Eating {
    public float maxFood = 100f;
    public float food = 90f;
    public float hungerRate = 0.5f;

    public Eating(){ }
    
    public float Fullness(){ return food / maxFood; }
    public bool Hungry(){ return food / maxFood < 0.5f; }

    public float Efficiency(){
        if (Fullness() > 0.5f){
            return 1f;
        } else {
            return Fullness() * 2f * 0.8f + 0.2f; // 20% at worst.
        }
    }
    public void Eat(float nFood){
        food += nFood;
    }
    public void Update(float t = 1f){
        food -= hungerRate * t;
        if (food < 0f){food = 0f;}
    }
}

public class Eeping {
    public float maxEep = 100f;
    public float eep = 90f;
    public static float tireRate = 0.3f;
    public static float eepRate = 5f;
    public static float outsideEepRate = 2f;

    public Eeping(){}
    public bool Eepy(){
        return eep / maxEep < 0.5f;
    }
    public float Efficiency(){
        if (eep / maxEep > 0.5f){
            return 1f;
        } else {
            return eep / maxEep * 2f * 0.8f + 0.2f; // 20% at worst.
        }
    }
    public float Eepness(){ return eep / maxEep; }
    public void Eep(float t, bool atHome){
        if (atHome){ eep += t * eepRate; }
        else { eep += t * outsideEepRate; }
    }
    public void Update(float t = 1f){
        eep -= tireRate * t;
        if (eep < 0f){eep = 0f;}
    }

}



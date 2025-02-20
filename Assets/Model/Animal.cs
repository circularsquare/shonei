using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;
using Pathfinding;


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
        Walking,                // going somewhere unburdened OR going to work station?
        Collecting,             // for producers, collecting inputs for recipe
        Working,                // for producers, making recipe
        Eeping,
        Fetching, Delivering,   // for hauling, getting and delivering.
        Taking,                 // for taking like food for yourself.
        WalkingToHarvest,
        WalkingToWork,
        WalkingToEep,
    } 
    private AnimalState _state;
    public AnimalState state{
        get { return _state; }
        set {
            _state = value;
            if (animationController != null){
                animationController.UpdateState();
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

        eating.Update();
        eeping.Update();
        efficiency = eating.Efficiency() * eeping.Efficiency();
        maxSpeed = 2f * efficiency;


        if (eating.Hungry()){
            if (inv.ContainsItem(Db.itemByName["wheat"])){ // if have food in inv
                Consume(Db.itemByName["wheat"], 1);
                eating.Eat(20f);
            }
            else { // else find food
                Take(Db.itemByName["wheat"], 5); // look for 5 food to take 
            } 
        }
        else if (eeping.Eepy() && state != AnimalState.Eeping){
            GoToEep();
        }
        
        if (state == AnimalState.Eeping){
            eeping.Eep(1f, AtHome());
            if (eeping.eep >= eeping.maxEep){
                state = AnimalState.Idle;
            }
            if (AtHome() && homeTile.building.reserved < homeTile.building.capacity && homeTile.building.reserved > 2){
                if (random.Next(0, 50) == 0){
                    AnimalController.instance.AddAnimal(x, y);
                }
            }
        }

        energy += efficiency;

        if (energy > 1f){ // if you have enough energy, spend it. also then you can work if you're Working.
            energy -= 1f; 

            if (state == AnimalState.Working){ // if working, do your recipe
                if (recipe != null && inv.ContainsItems(recipe.inputs)){
                    Produce(recipe); 
                } else { state = AnimalState.Idle; }
            }
            if (state == AnimalState.Idle){     // if can't work, find work
                FindWork(); 
            }
            if (state == AnimalState.Idle){ // if still can't find work, pace around
                if (UnityEngine.Random.Range(0, 5) == 0){
                    GoTo(x + (float)UnityEngine.Random.Range(-1, 2), y); }
            }
        }
    }
    public void SlowUpdate(){ // called every 10 or so seconds
        FindHome();
    }

    public void Update(){ // for movement and detecting arrival
        if (!TileHere().node.standable){             // fall
            this.go.transform.position = new Vector3(this.go.transform.position.x, 
                this.go.transform.position.y - maxSpeed*Time.deltaTime, this.go.transform.position.z);
        }

        if (IsMoving()){
            if (target == null){ 
                // this shouldnt happen i think!
                    // seem sto happen maybe when someone construct partially then someone else finishes.
                    // also, why am i using tile gameobject anyways? maybe better to just use tile coords.
                Debug.LogError("movement target null! " + this.state.ToString());

                DropItems();
                state = AnimalState.Idle;
            }
            else {  // move toward target
                bool done = nav.Move(Time.deltaTime);
                if (done && SquareDistance(x, target.x, y, target.y) < 0.001f){ // arrived at target
                    this.x = target.x;
                    this.y = target.y;
                    this.go.transform.position = new Vector3(x, y, 0);

                    if (state == AnimalState.Walking)        { state = AnimalState.Idle; }
                    else if (state == AnimalState.Collecting){ OnArrivalCollect(); }
                    else if (state == AnimalState.WalkingToWork){ state = AnimalState.Working; }
                    else if (state == AnimalState.WalkingToHarvest){ OnArrivalHarvest(); }
                    else if (state == AnimalState.Fetching)  { OnArrivalFetch(); }
                    else if (state == AnimalState.Delivering){ OnArrivalDeliver(); }
                    else if (state == AnimalState.Taking)    { OnArrivalTake(); }
                    else if (state == AnimalState.WalkingToEep){ OnArrivalEep(); } 
                }
                sr.flipX = !isMovingRight;  
            }
         } 
        // else if (state == AnimalState.Eeping){ // this might be wastefully updating too much.
        //     eeping.Eep(Time.deltaTime);
        //     if (eeping.eep >= eeping.maxEep){
        //         state = AnimalState.Idle;
        //     }
        // }
    }

    public void FindWork(){
        if (job.name == "none"){ // free past worktile
            DropItems();
            if (workTile != null){
                RemoveWorkTile();}
            state = AnimalState.Idle;
            return;
        } 
        if (job.name == "hauler"){
            DropItems();
            if (random.Next(3) > 0 && FetchForBlueprint()){}
            else {Fetch();}
            return;
        } 
        if (job.name == "farmer" || job.name == "logger"){
            // find something to harvest or plant
            if (Harvest() || FetchForBlueprint()){
                return;
            }
        } 
        // generic work: make recipe.
        recipe = PickRecipe(); 
        if (recipe != null){ // if can find recipe 
            Path p = null;
            if (Db.tileTypeByName.ContainsKey(recipe.tile)){ // if can find unreserved work tile
                p = nav.FindWorkTile(Db.tileTypeByName[recipe.tile]);
            }
            else if (Db.structTypeByName.ContainsKey(recipe.tile)){
                p = nav.FindBuilding(Db.structTypeByName[recipe.tile]);
            } 
            if (p != null){
                numRounds = CalculateWorkPossible(recipe);      // calc numRounds
                SetWorkTile(p.tile);                                 // reserve work tile
                if (inv.ContainsItems(recipe.inputs, numRounds)){ // if have all the inputs, just go there.
                    GoToWork();
                } else { // if missing some inputs, collect the missing inputs in your inventory.
                    Collect();
                }
            }
        }        
        // TODO: drop unuseful items
    }

    // -----------------------
    // ITEM MOVING LOOP 
    // -----------------------
    public bool Fetch(Item item = null){ // fetch items to haul (hauler)
        if (item == null){  // if fetching any item
            if (nav.FindAnyItemToHaul() != null){  // this Find actually sets animal values..  
                state = AnimalState.Fetching; // on arrival, will HaulBack()
                return true;
            } 
            return false;
        } else {    // if fetching specific item
            Path p = nav.FindItemToHaul(item);
            if (p != null){
                return (GoTo(null, p, AnimalState.Fetching));
            }
            return false; // nothing to fetch
        } 
    }
    
    public void OnArrivalFetch(){      
        TakeItem(desiredItem, desiredItemQuantity); // pick up items
        desiredItemQuantity = desiredItemQuantity - inv.Quantity(desiredItem);
        if (inv.GetStorageForItem(desiredItem) > 5 && desiredItemQuantity > 0){
            Fetch(desiredItem); /* keep fetching the same item if you have space and can find stuff to fetch and can store it */  }
        else{GoToDeliver();}
    }

    public bool FetchForBlueprint(){
        Path blueprintPath = nav.FindBlueprint(job); // finds a blueprint appropriate to your job
        if (blueprintPath == null){return false;} 
        Tile blueprintTile = blueprintPath.tile;
        Blueprint blueprint = blueprintTile.blueprint;

        for (int i = 0; i < blueprint.costs.Length; i++){
            if (blueprint.deliveredResources[i].quantity < blueprint.costs[i].quantity){
                Path p = nav.FindItem(blueprint.costs[i].item);
                if (p != null){
                    if (GoTo(null, p, AnimalState.Fetching)){
                        desiredItem = blueprint.costs[i].item;
                        desiredItemQuantity = blueprint.costs[i].quantity - blueprint.deliveredResources[i].quantity;
                        storageTile = blueprintTile;
                        return true;
                    } else {return false;}
        }}}
        return false;
    }
    public void OnArrivalBlueprint() {      // deliver items to blueprint
        int amountToDeliver = inv.Quantity(desiredItem);
        int delivered = target.blueprint.ReceiveResource(desiredItem, amountToDeliver);
        inv.AddItem(desiredItem, -delivered); // remove item from own inv
        int itemInInv = inv.Quantity(desiredItem);
        if (itemInInv > 0){ // if you have excess of the item, drop it somewhere immediately
            DropItem(desiredItem);
            state = AnimalState.Idle;
            return;
        }
        state = AnimalState.Idle;
    }

    public void OnArrivalDeliver(){     // deliver items (to storage, etc.)
        if (target.blueprint != null){ OnArrivalBlueprint(); return; }
        DropItem(desiredItem);
        int itemInInv = inv.Quantity(desiredItem);
        if (itemInInv > 0){ // if you have excess of the item, drop it somewhere.
            TakeToDrop();
        }
        state = AnimalState.Idle;
    }
    public void TakeToDrop(){ // delivers items to drop somewhere. TODO: don't use desiredItem, feed specific values.
        Path p = nav.FindPlaceToDrop(desiredItem, desiredItemQuantity);
        if (p == null){ Debug.LogError("couldn't find a place to drop!"); }
        nav.Navigate(p); 
        state = AnimalState.Delivering;
    }

    public bool Take(Item item, int quantity = 1){
        desiredItem = item;
        desiredItemQuantity = quantity;
        Path p = nav.FindItem(item);
        if (p != null){
            nav.Navigate(p);
            state = AnimalState.Taking;
            return true;
        }
        return false;
    }
    public void OnArrivalTake(){      
        TakeItem(desiredItem, desiredItemQuantity); // pick up items
        desiredItemQuantity = desiredItemQuantity - inv.Quantity(desiredItem); // extra desired
        // if still desire more, look for more
        if (inv.GetStorageForItem(desiredItem) > 5 && desiredItemQuantity > 0 && Take(desiredItem, desiredItemQuantity)){ } // look for more
        else{ state = AnimalState.Idle; }
    }

    public bool Collect(){ // pick up recipe inputs and decide what to do next
        if (recipe != null){ Debug.LogError("lost recipe!");}
        desiredItem = null;
        foreach (ItemQuantity input in recipe.inputs){
            if (!inv.ContainsItem(input, numRounds)){
                desiredItem = input.item; // you want it. 
                desiredItemQuantity = input.quantity * numRounds;
                Path p = nav.FindItem(desiredItem);
                if (p != null){  // you can find it, go get it.
                    nav.Navigate(p);
                    state = AnimalState.Collecting;
                    return true;
                }
            }
        }
        if (desiredItem == null){  // have all desired items! go to work
            GoToWork();
            return true; 
        } else { // want items but you can't find any of them. give up and go to work
            desiredItem = null;
            desiredItemQuantity = 0;
            GoToWork();
            Debug.Log("can't find a recipe input");
            return false;
        }
    }
    public void OnArrivalCollect(){      
        TakeItem(desiredItem, desiredItemQuantity); // pick up items
        desiredItemQuantity = desiredItemQuantity - inv.Quantity(desiredItem);
        if (inv.GetStorageForItem(desiredItem) > 5 && desiredItemQuantity > 0){
            /* ?????? keep collecting the same item if you have space and can find stuff to fetch and can store it */  }
        Collect();
    }
    public void OnArrivalEep(){
        state = AnimalState.Eeping;
    }


    public bool Harvest(){
        Path p = nav.FindHarvestablePlant(job);
        if (p != null){
            GoToHarvest(null, p);
            return true;
        }
        return false;
    }
    public void OnArrivalHarvest(){      // deliver items to blueprint
        Plant plant = TileHere().building as Plant;
        if (plant.harvestable){ Produce(plant.Harvest()); }
        state = AnimalState.Idle;
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

    // TODO: make these use navigation!!! instead of insta moving to tile inv.
    public void DropItem(Item item, Path dPath = null, int quantity = -1){     // tries to drop all of an item at a nearby tile.
        Tile dTile;
        if (dPath != null){dTile = dPath.tile;}
        else {dTile = TileHere();}
        if (dTile.inv == null){ // create new floor inv
            dTile.inv = new Inventory(1, 20, Inventory.InvType.Floor, dTile.x, dTile.y);}
        if (quantity == -1){ inv.MoveItemTo(dTile.inv, item, inv.Quantity(item)); } // default: dropall
        else { quantity = inv.MoveItemTo(dTile.inv, item, quantity); } // drop into inv here
        if (inv.Quantity(item) > 0 && (quantity == -1 || quantity > 0)){
            DropItem(item, nav.FindPlaceToDrop(item));  // if can't drop here, drop nearby
            // TODO: make this require the animal to actually deliver it unless they cant fit it into their inv
        }
    }
    public void DropItems(){    // drops all items, tries to keep 5 food on hand.
                                // actually keeps 5 of each food stack. kinda sloppy, needs work eventually.
        foreach (ItemStack stack in inv.itemStacks){
            if (stack != null && stack.item != null && stack.quantity > 0){ // im not sure why the quantity check isn't working??
                if (stack.item.name == "wheat"){
                    DropItem(stack.item, nav.FindPlaceToDrop(stack.item), stack.quantity - 5);
                }
                else { DropItem(stack.item, nav.FindPlaceToDrop(stack.item)); }
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
        FindWork();
    }
    public void SetJob(string jobStr){ SetJob(Db.GetJobByName(jobStr)); }


    // navigates to t and sets state to some form of walking. returns whether found a path.
    public bool GoTo(Tile t, Path p = null, AnimalState state = AnimalState.Walking){ 
        if (t == null && p == null){Debug.LogError("destination tile is null!"); return false;}
        if (nav.NavigateTo(t, p)){
            this.state = state;
            return true;
        } else {return false;}
    }
    public void GoTo(float x, float y){GoTo(world.GetTileAt(x, y)); }
    public void GoToWork(Path p = null){ GoTo(workTile, p, AnimalState.WalkingToWork); }
    public void GoToHarvest(Tile t, Path p = null){ GoTo(t, p, AnimalState.WalkingToHarvest); }
    public void GoToDeliver(Path p = null){ GoTo(storageTile, p, AnimalState.Delivering); }
    public void GoToEep(Path p = null){ 
        if (homeTile == null){state = AnimalState.Eeping;}
        else { GoTo(homeTile, p, AnimalState.WalkingToEep); }
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
        return Find(t => t.type == tileType && (t.capacity - t.reserved > 0), r);
    }
    public Path FindWorkTile(string tileTypeStr, int r = 30){ return FindWorkTile(Db.tileTypeByName[tileTypeStr], r); }
    public Path FindBlueprint(Job job, int r = 50){return Find(t => t.blueprint != null && t.blueprint.structType.job == job, r);}
    public Path FindHarvestablePlant(Job job, int r = 40){
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
                    float distance = cPath.length; // try cost later.
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
            a.storageTile = null; 
            a.desiredItem = null;
            a.desiredItemQuantity = 0;
            a.state = Animal.AnimalState.Idle;
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

    public Eating(){ 
    }
    
    public float Fullness(){
        return food / maxFood;
    }
    public bool Hungry(){
        return food / maxFood < 0.5f;
    }

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



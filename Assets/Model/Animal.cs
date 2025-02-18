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
    private bool isMovingRight = true;

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
    public AnimalState state;

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
        this.go.name = "animal" + aName;
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
        if (IsMoving()){
            if (target == null || target.go == null){ 
                // this shouldnt happen i think!
                    // seem sto happen maybe when someone construct partially then someone else finishes.
                    // also, why am i using tile gameobject anyways? maybe better to just use tile coords.
                Debug.LogError("movement target disappeared!!");
                DropItems();
                state = AnimalState.Idle;
            }
            // arrived at target
            else if (Vector3.Distance(this.go.transform.position, target.go.transform.position) < 0.02f){
                this.go.transform.position = target.go.transform.position;
                SyncPosition(); 

                if (state == AnimalState.Walking)        { state = AnimalState.Idle; }
                else if (state == AnimalState.Collecting){ OnArrivalCollect(); }
                else if (state == AnimalState.WalkingToWork){ state = AnimalState.Working; }
                else if (state == AnimalState.WalkingToHarvest){ OnArrivalHarvest(); }
                else if (state == AnimalState.Fetching)  { OnArrivalFetch(); }
                else if (state == AnimalState.Delivering){ OnArrivalDeliver(); }
                else if (state == AnimalState.Taking)    { OnArrivalTake(); }
                else if (state == AnimalState.WalkingToEep){ OnArrivalEep(); } 
            }
            else {  // move toward target
                this.go.transform.position = Vector3.MoveTowards(this.go.transform.position, 
                    target.go.transform.position, maxSpeed * Time.deltaTime);
                // set facing direction
                isMovingRight = (target.go.transform.position.x - this.go.transform.position.x >= 0);
                sr.flipX = !isMovingRight;
                SyncPosition();
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
            Tile t = null;
            if (Db.tileTypeByName.ContainsKey(recipe.tile)){ // if can find unreserved work tile
                t = nav.FindWorkTile(Db.tileTypeByName[recipe.tile]);
            }
            else if (Db.buildingTypeByName.ContainsKey(recipe.tile)){
                t = nav.FindBuilding(Db.buildingTypeByName[recipe.tile]);
            } 
            if (t != null){
                numRounds = CalculateWorkPossible(recipe);      // calc numRounds
                SetWorkTile(t);                                 // reserve work tile
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
            Tile itemTile = nav.FindItemToHaul(item);
            if (itemTile != null){
                target = itemTile;
                state = AnimalState.Fetching;
                return true;
            }
            return false; // nothing to fetch
        } 
    }
    
    public void OnArrivalFetch(){      
        TakeItem(desiredItem, desiredItemQuantity); // pick up items
        desiredItemQuantity = desiredItemQuantity - inv.Quantity(desiredItem);
        if (inv.GetStorageForItem(desiredItem) > 5 && desiredItemQuantity > 0){
            Fetch(desiredItem); /* keep fetching the same item if you have space and can find stuff to fetch and can store it */  }
        else{Deliver();}
    }

    public bool FetchForBlueprint(){
        Tile blueprintTile = nav.FindBlueprint(job); // finds a blueprint appropriate to your job
        if (blueprintTile == null){return false;}
        Blueprint blueprint = blueprintTile.blueprint;

        for (int i = 0; i < blueprint.costs.Length; i++){
            if (blueprint.deliveredResources[i].quantity < blueprint.costs[i].quantity){
                Tile itemTile = nav.FindItem(blueprint.costs[i].item);
                if (itemTile != null){
                    desiredItem = blueprint.costs[i].item;
                    desiredItemQuantity = blueprint.costs[i].quantity - blueprint.deliveredResources[i].quantity;
                    storageTile = blueprintTile;
                    target = itemTile;
                    state = AnimalState.Fetching;
                    return true;
        }}}
        return false;
    }
    public void OnArrivalBlueprint() {      // deliver items to blueprint
        int amountToDeliver = inv.Quantity(desiredItem);
        int delivered = target.blueprint.ReceiveResource(desiredItem, amountToDeliver);
        inv.AddItem(desiredItem, -delivered); // remove item from own inv
        int itemInInv = inv.Quantity(desiredItem);
        if (itemInInv > 0){ // if you have excess of the item, drop it somewhere.
            target = nav.FindPlaceToDrop(desiredItem, itemInInv); 
            if (target == null){ Debug.LogError("couldn't find a place to drop!"); }
            state = AnimalState.Delivering;
            return;
        }
        state = AnimalState.Idle;
    }

    public void Deliver(){ // move items to storagetile.
        target = storageTile;
        state = AnimalState.Delivering;
    }
    public void OnArrivalDeliver(){     // deliver items (to storage, etc.)
        if (target.blueprint != null){ OnArrivalBlueprint(); return; }
        DropItem(desiredItem, target);
        int itemInInv = inv.Quantity(desiredItem);
        if (itemInInv > 0){ // if you have excess of the item, drop it somewhere.
            target = nav.FindPlaceToDrop(desiredItem, itemInInv); 
            state = AnimalState.Delivering;
        }
        state = AnimalState.Idle;
    }

    public bool Take(Item item, int quantity = 1){
        desiredItem = item;
        desiredItemQuantity = quantity;
        Tile itemTile = nav.FindItem(item);
        if (itemTile != null){
            target = itemTile;
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
                Tile itemTile = nav.FindItem(desiredItem);
                if (itemTile != null){  // you can find it, go get it.
                    target = itemTile; 
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
        Tile plantTile = nav.FindHarvestablePlant(job);
        if (plantTile != null){
            GoToHarvest(plantTile);
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
    public void DropItem(Item item, Tile dTile = null, int quantity = -1){     // tries to drop all of an item at a nearby tile.
        if (dTile == null){dTile = world.GetTileAt(x, y); }
        if (dTile.inv == null){
            dTile.inv = new Inventory(1, 20, Inventory.InvType.Floor, dTile.x, dTile.y);
        }
        if (quantity == -1){ inv.MoveItemTo(dTile.inv, item, inv.Quantity(item)); } // default: dropall
        else { quantity = inv.MoveItemTo(dTile.inv, item, quantity); }
        if (inv.Quantity(item) > 0 && (quantity == -1 || quantity > 0)){
            DropItem(item, nav.FindPlaceToDrop(item));} // if can't drop here, drop nearby
            // TODO: make this require the animal to actually deliver it unless they cant fit it into their inv
    }
    public void DropItems(){    // drops all items, tries to keep 5 food on hand.
                                // actually keeps 5 of each food stack. kinda sloppy, needs work eventually.
        foreach (ItemStack stack in inv.itemStacks){
            if (stack != null && stack.quantity > 0){
                if (stack.item.name == "wheat"){
                    DropItem(stack.item, nav.FindPlaceToDrop(stack.item), stack.quantity - 5);
                }
                DropItem(stack.item, nav.FindPlaceToDrop(stack.item));
            }
        }
    }

    // produces item in ani inv, dumps at nearby tile if inv full
    public void Produce(Item item, int quantity = 1){   
        if (quantity < 0){
            Debug.Log("called produce with negative quantity, use consume instead");
            Consume(item, -quantity); return;
        }
        // int leftover = inv.Produce(item, quantity); // disabled producing into inv for now...
        int leftover = quantity;
        if (leftover > 0){
            Tile dTile = nav.FindPlaceToDrop(item);
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
            Tile store = nav.FindStorage(output.item);
            if (store == null){ n = 0;}
            else {n = store.GetStorageForItem(output.item) / output.quantity; }
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
    public void SyncPosition(){
        this.x = this.go.transform.position.x;
        this.y = this.go.transform.position.y;
        bounds.center = go.transform.position;
    }

    public void GoTo(Tile t){ // this sets state to walking. don't use this for stuff like fetching.
        target = t;
        this.state = AnimalState.Walking;
    }
    public void GoTo(float x, float y){GoTo(world.GetTileAt(x, y));}

    public void GoToWork(){
        if (workTile == null){Debug.LogError("work tile doesn't exist!");}
        target = workTile;
        state = AnimalState.WalkingToWork;
    }
    public void GoToHarvest(Tile t){
        target = t;
        SetWorkTile(t); // just to reserve the tile. i don't think worktile is used for harvesters.
        this.state = AnimalState.WalkingToHarvest;
    }
    public void GoToEep(){ 
        if (homeTile != null){target = homeTile; state = AnimalState.WalkingToEep;}
        else { state = AnimalState.Eeping;}
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
            homeTile = nav.FindBuilding(Db.buildingTypeByName["house"]);
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
    public Animal animal; // have this be more generic? idk
    public float x;
    public float y;
    public World world;

    public Nav (Animal animal){
        this.animal = animal;
        this.x = animal.x;
        this.y = animal.y;
        this.world = animal.world;
    }

    public void SyncPosition(){
        this.x = animal.x;
        this.y = animal.y;
    }
    public Tile FindItem(Item item, int r = 50){ return Find(t => t.ContainsItem(item), r); }
    public Tile FindItemToHaul(Item item, int r = 50){ return Find(t => t.HasItemToHaul(item), r); }
    public Tile FindStorage(Item item, int r = 50){ return Find(t => t.HasStorageForItem(item), r); }
    public Tile FindPlaceToDrop(Item item, int r = 3){ return Find(t => t.HasSpaceForItem(item), r, true); }
    public Tile FindBuilding(BuildingType buildingType, int r = 50){
        return Find(t => t.building != null && t.building.buildingType == buildingType && 
            t.building.capacity - t.building.reserved > 0, r);
    }
    public Tile FindWorkTile(TileType tileType, int r = 50){
        return Find(t => t.type == tileType && (t.capacity - t.reserved > 0), r);
    }
    public Tile FindWorkTile(string tileTypeStr, int r = 30){ return FindWorkTile(Db.tileTypeByName[tileTypeStr], r); }
    public Tile FindBlueprint(Job job, int r = 50){return Find(t => t.blueprint != null && t.blueprint.buildingType.job == job, r);}
    public Tile FindHarvestablePlant(Job job, int r = 40){
        return Find(t => t.building != null && t.building is Plant 
        && (t.building as Plant).harvestable
        && (t.building.buildingType.job == job), r); // something about jobs here?
    }
    public Tile Find(Func<Tile, bool> condition, int r, bool persistent = false){
        SyncPosition();
        Tile closestTile = null;
        float closestDistance = float.MaxValue;
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(this.x + x, this.y + y);
                if (tile != null && condition(tile)) {
                    float distance = SquareDistance((float)tile.x, this.x, (float)tile.y, this.y);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestTile = tile;
                    }
                }
            }
        } // should check in a wider radius if none found...
        if (persistent && closestTile == null && r < 100){ 
            Debug.Log("no tile found. expanding radius to " + r + 3);
            closestTile = Find(condition, r + 3, persistent);
        }
        return closestTile;
    }

    public Tile FindAnyItemToHaul(int r = 50){ 
        float closestDistance = float.MaxValue;
        Tile closestTile = null;
        Tile closestStorage = null;
        Item closestItem = null;
        Tile itemTile = Find(t => t.HasItemToHaul(null), r);
        if (itemTile != null){
            Item item = itemTile.inv.GetItemToHaul();
            if (item != null){
                Tile storage = FindStorage(item, r=50);
                if (storage != null){
                    float distance = SquareDistance((float)itemTile.x, this.x, (float)itemTile.y, this.y);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestTile = itemTile;
                        closestStorage = storage;
                        closestItem = item;
                    }
                }
            }
        }
        if (closestTile != null){
            animal.storageTile = closestStorage;
            animal.target = closestTile;
            animal.desiredItem = closestItem;
            animal.desiredItemQuantity = Math.Min(closestTile.inv.Quantity(closestItem),
                closestStorage.GetStorageForItem(closestItem)); // don't take more than u can store
            return closestTile;
        } else {
            animal.storageTile = null; 
            animal.target = null; 
            animal.desiredItem = null;
            animal.desiredItemQuantity = 0;
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
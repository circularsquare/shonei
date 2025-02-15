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

    public enum AnimalState {
        Idle, 
        Walking,                // going somewhere unburdened OR going to work station?
        Collecting,             // for producers, collecting inputs for recipe
        Working,                // for producers, making recipe
        Fetching, Delivering,   // for hauling, getting and delivering.
        Taking,                 // for taking like food for yourself.
        WalkingToHarvest,
        WalkingToWork,
    } 
    public AnimalState state;

    public GameObject go;
    public SpriteRenderer sr;
    public Sprite sprite;
    public System.Random random;
    public Bounds bounds; // a box to click on to select the animal
    World world;
    
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
        this.eating = new Eating();
        this.efficiency = 1f;
        this.energy = 0f;
        ginv = GlobalInventory.instance;
        random = new System.Random();
    }
    
    public void TickUpdate(){ // called from animalcontroller each second.
        eating.Update();
        efficiency = eating.Efficiency(); // will have other factors in efficiency later.
        maxSpeed = 2f * efficiency;

        if (eating.Hungry()){
            if (inv.ContainsItem(Db.itemByName["wheat"])){ // if have food in inv
                Consume(Db.itemByName["wheat"], 1);
                eating.Eat(20f);
            }
            else { // else find food
                Take(Db.itemByName["wheat"]); 
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
        if (job.name == "farmer"){
            // find something to harvest or plant
            if (Harvest() || FetchForBlueprint("farmer")){
                return;
            }
        }
        // generic work: make recipe.
        recipe = PickRecipe(); 
        if (recipe != null){ // if can find recipe 
            Tile t = null;
            if (Db.tileTypeByName.ContainsKey(recipe.tile)){ // if can find unreserved work tile
                t = FindWorkTile(Db.tileTypeByName[recipe.tile]);
            }
            else if (Db.buildingTypeByName.ContainsKey(recipe.tile)){
                t = FindWorkBuilding(Db.buildingTypeByName[recipe.tile]);
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

        // if can't do anything, just stay idle
        // TODO: drop unuseful items
    }

    // -----------------------
    // ITEM MOVING LOOP 
    // -----------------------
    public bool Fetch(Item item = null){ // fetch items to haul (hauler)
        if (item == null){  // if fetching any item
            if (FindAnyItemToHaul()){   
                state = AnimalState.Fetching; // on arrival, will HaulBack()
                return true;
            } 
            return false;
        } else {    // if fetching specific item
            Tile itemTile = FindItemToHaul(item);
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

    public bool FetchForBlueprint (string jobName = "hauler"){
        Tile blueprintTile = null;
        if (jobName == "hauler"){blueprintTile = FindHaulerBlueprint();} // called if normal building blueprint
        if (jobName == "farmer"){blueprintTile = FindPlantBlueprint();} // called if it's plant blueprint
        if (blueprintTile == null){return false;}
        Blueprint blueprint = blueprintTile.blueprint;

        for (int i = 0; i < blueprint.costs.Length; i++){
            if (blueprint.deliveredResources[i].quantity < blueprint.costs[i].quantity){
                Tile itemTile = FindItem(blueprint.costs[i].item);
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
            target = FindPlaceToDrop(desiredItem, itemInInv); 
            if (target == null){
                Debug.LogError("couldn't find a place to drop!");
            }
            state = AnimalState.Delivering;
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
            target = FindPlaceToDrop(desiredItem, itemInInv); 
            state = AnimalState.Delivering;
        }
        state = AnimalState.Idle;
    }

    public bool Take(Item item, int quantity = 5){
        desiredItem = item;
        desiredItemQuantity = quantity;
        Tile itemTile = FindItem(item);
        if (itemTile != null){
            target = itemTile;
            state = AnimalState.Taking;
            return true;
        }
        return false;
    }
    public void OnArrivalTake(){      
        TakeItem(desiredItem, desiredItemQuantity); // pick up items
        desiredItemQuantity = desiredItemQuantity - inv.Quantity(desiredItem);
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
                Tile itemTile = FindItem(desiredItem);
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




    public bool Harvest(){
        Tile plantTile = FindHarvestablePlant();
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
    // ITEM MOVEMENT 
    // -----------------------
    public void TakeItem(Item item, int quantity){  // pick up item from current location
        Tile tileHere = TileHere();
        if (tileHere != null && tileHere.inv != null){ 
            tileHere.inv.MoveItemTo(inv, item, quantity);
            if (tileHere.inv.IsEmpty() && tileHere.inv.invType == Inventory.InvType.Floor){
                Destroy(tileHere.inv.go);
                tileHere.inv = null; // delete an empty floor inv.
            }
        }
    }
    public void DropItem(Item item, Tile dTile = null){     // tries to drop all of an item at a nearby tile.
        if (dTile == null){dTile = world.GetTileAt(x, y); }
        if (dTile.inv == null){
            dTile.inv = new Inventory(1, 20, Inventory.InvType.Floor, dTile.x, dTile.y);
        }
        inv.MoveItemTo(dTile.inv, item, inv.Quantity(item));
        if (inv.Quantity(item) > 0){
            DropItem(item, FindPlaceToDrop(item));} // if can't drop here, drop nearby
            // TODO: make this require the animal to actually deliver it unless they cant fit it into their inv
    }
    public void DropItems(){    // drops all items. maybe change to not drop food?
        foreach (ItemStack stack in inv.itemStacks){
            if (stack != null && stack.quantity > 0){
                DropItem(stack.item, FindPlaceToDrop(stack.item));
            }
        }
    }

    public void Produce(string itemName, int quantity = 1){ Produce(Db.itemByName[itemName], quantity); }
    public void Produce(ItemQuantity iq){ Produce(iq.item, iq.quantity); }
    public void Produce(ItemQuantity[] iqs){ Array.ForEach(iqs, iq => Produce(iq)); }
    public void Produce(Item item, int quantity = 1){   // instantly produces item at a nearby tile
        ginv.AddItem(item.id, quantity);
        Tile dTile = FindPlaceToDrop(item);
        if (dTile == null){Debug.Log("no place to drop item!!");}
        if (dTile.inv == null){
            dTile.inv = new Inventory(1, 20, Inventory.InvType.Floor, dTile.x, dTile.y);
        }
        dTile.inv.AddItem(item, quantity);
    }
    // ideally... produce recipe would check both inv of tileHere and of animal. how would that work?
    public void Produce(Recipe recipe){ // different from produce iq! 
        //only safe to call if you are sure the inv has all inputs!!
        ginv.AddItems(recipe.inputs, true);
        inv.AddItems(recipe.inputs, true);
        foreach (ItemQuantity iq in recipe.outputs){
            Produce(iq);
        }
    }
    public void Consume(Item item, int quantity = 1){
        ginv.AddItem(item, -quantity);
        inv.AddItem(item, -quantity);
    }

    // -----------------------
    // FIND
    // -----------------------
    public Tile FindItem(Item item, int r = 50){ return Find(t => t.ContainsItem(item), r); }
    public Tile FindItemToHaul(Item item, int r = 50){ return Find(t => t.HasItemToHaul(item), r); }
    public Tile FindStorage(Item item, int r = 50){ return Find(t => t.HasStorageForItem(item), r); }
    public Tile FindPlaceToDrop(Item item, int r = 3){ return Find(t => t.HasSpaceForItem(item), r, true); }
    public Tile FindWorkBuilding(BuildingType buildingType, int r = 50){
        return Find(t => t.building != null && t.building.buildingType == buildingType && 
            t.building.capacity - t.building.reserved > 0, r);
    }
    public Tile FindWorkTile(TileType tileType, int r = 50){
        return Find(t => t.type == tileType && (t.capacity - t.reserved > 0), r);
    }
    public Tile FindWorkTile(string tileTypeStr, int r = 30){ return FindWorkTile(Db.tileTypeByName[tileTypeStr], r); }
    public Tile FindHaulerBlueprint(int r = 50){return Find(t => t.blueprint != null && !(t.blueprint.buildingType is PlantType), r);}
    public Tile FindPlantBlueprint(int r = 40){return Find(t => t.blueprint != null && t.blueprint.buildingType is PlantType, r);}
    public Tile FindHarvestablePlant(int r = 40){
        return Find(t => t.building != null && t.building is Plant && (t.building as Plant).harvestable, r);}
    public Tile Find(Func<Tile, bool> condition, int r, bool persistent = false){
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
    public bool FindAnyItemToHaul(int r = 50){
        float closestDistance = float.MaxValue;
        Tile closestTile = null;
        Tile closestStorage = null;
        Item closestItemToHaul = null;
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(this.x + x, this.y + y);
                if (tile != null) {
                    Item itemToHaul = tile.GetItemToHaul();
                    if (itemToHaul != null) { 
                        Tile storage = FindStorage(itemToHaul, r=80); // expensive
                        if (storage != null){
                            float distance = SquareDistance((float)tile.x, this.x, (float)tile.y, this.y);
                            if (distance < closestDistance) {
                                closestDistance = distance;
                                closestTile = tile;
                                closestStorage = storage;
                                closestItemToHaul = itemToHaul;
                            }
                        }
                    }
                }
            }
        } // no persistent
        if (closestTile != null){
            storageTile = closestStorage;
            target = closestTile;
            desiredItem = closestItemToHaul;
            desiredItemQuantity = Math.Min(closestTile.inv.Quantity(closestItemToHaul),
                storageTile.GetStorageForItem(desiredItem)); // don't take more than u can store
            return true;
        } else {
            storageTile = null; 
            target = null; 
            desiredItem = null;
            desiredItemQuantity = 0;
            return false;
        }
    }


    // -----------------------
    // THINKING
    // -----------------------
    public Recipe PickRecipe2(){
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
    public Recipe PickRecipe(){
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
            Tile store = FindStorage(output.item);
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



    public void GoToWork(){
        if (workTile == null){Debug.LogError("work tile doesn't exist!");}
        target = workTile;
        state = AnimalState.WalkingToWork;
    }
    public void GoToHarvest(Tile t){
        target = t;
        this.state = AnimalState.WalkingToHarvest;
    }

    public void GoTo(float x, float y){
        GoTo(world.GetTileAt(x, y));
    }
    public void GoTo(Tile t){ // this sets state to walking. don't use this for stuff like fetching.
        target = t;
        this.state = AnimalState.Walking;
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
    public Tile TileHere(){
        return world.GetTileAt(x, y);
    }
    

    public bool IsMoving(){
        return !(state == AnimalState.Idle || state == AnimalState.Working);
    }

    public float SquareDistance(float x1, float x2, float y1, float y2){
        return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);
    }

    
    public void RegisterCbAnimalChanged(Action<Animal, Job> callback){ cbAnimalChanged += callback;}
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback){ cbAnimalChanged -= callback;}
}



public class Eating {
    public float maxFood = 100f;
    public float food = 90f;
    public float hungerRate = 1f;

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
            return Fullness() * 2f * 0.9f + 0.1f; // 10% at worst.
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
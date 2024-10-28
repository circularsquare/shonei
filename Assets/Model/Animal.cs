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

    public Tile target;         // where you are currently going
    public Tile workTile;       // where production happens
    public Item desiredItem;    // item you're fetching
    public int desiredItemQuantity;
    public Tile storageTile;    // tile to store fetched items in (for haulers)

    public Recipe recipe;

    public Job job;
    public Inventory inv; 
    public GlobalInventory ginv;
    // list of skills goes here?
    // maybe one for each job?

    public enum AnimalState {Idle, Walking, Working, Fetching, Delivering}

    public AnimalState state;
    public GameObject go;
    public SpriteRenderer sr;
    public System.Random random;

    public int[][] map;

    public Sprite sprite;
    public Bounds bounds; // a box to click on to select the animal
    World world;
    
    Action<Animal, Job> cbAnimalChanged;

    public void Start(){
        world = World.instance;
        this.aName = "mouse" + id.ToString();
        this.state = AnimalState.Idle;
        this.job = Db.jobs[0];
        this.go = this.gameObject;
        this.go.name = "animal" + aName;
        this.inv = new Inventory(5, 10, Inventory.InvType.Animal);
        ginv = GlobalInventory.instance;
        random = new System.Random();
    }

    public void SetJob(Job newJob){
        Job oldJob = this.job;
        this.job = newJob;
        if (cbAnimalChanged != null){
            cbAnimalChanged(this, oldJob);} 
        FindWork();
    }
    public void SetJob(string jobStr){ SetJob(Db.GetJobByName(jobStr)); }
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
            if (random.Next(3) >= 1 && Fetch()){ // randomly choose between fetch and fetch to build
            } else {
                FetchForBlueprint();
            }
            return;
        } 
        recipe = PickRecipe();
        if (recipe != null){
            Tile t = null;
            if (Db.tileTypeByName.ContainsKey(recipe.tile)){
                t = FindWorkTile(Db.tileTypeByName[recipe.tile]);
            }
            else if (Db.buildingTypeByName.ContainsKey(recipe.tile)){
                t = FindWorkBuilding(Db.buildingTypeByName[recipe.tile]);
            } 
            if (t != null){
                SetWorkTile(t); // reserves tile too.
                if (inv.ContainsItems(recipe.inputs)){ // if have all the inputs, just go there.
                    GoToWork();
                } else { // if missing some inputs, collect the missing inputs in your inventory.
                    Debug.Log("collecting input");
                    Collect();
                }
            }
        }        
        // TODO: drop unuseful items
    }

    public void FastUpdate(){ // called from animalcontroller each second.
        if (state == AnimalState.Working){ // if working, do your recipe
            if (recipe != null && inv.ContainsItems(recipe.inputs)){
                Produce(recipe); 
            }
            else { 
                state = AnimalState.Idle; 
            }
        }
        if (state == AnimalState.Idle){     // if can't work, find work
            FindWork(); 
        }
        if (state == AnimalState.Idle){ // if still can't find work, pace around
            if (UnityEngine.Random.Range(0, 5) == 0){
                MoveTo(x + (float)UnityEngine.Random.Range(-1, 2), y);
            }
        }
    }

    public void Update(){
        if ((state == AnimalState.Walking) || (state == AnimalState.Fetching) || (state == AnimalState.Delivering)){
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
                if (state == AnimalState.Walking){ // have reached destination.
                    if (target == workTile){
                        state = AnimalState.Working; // if arrived at work, start working
                    } else {
                        state = AnimalState.Idle;
                    }
                }
                else if (state == AnimalState.Fetching){
                    OnArrivalAtFetchTarget();                    
                }
                else if (state == AnimalState.Delivering){
                    OnArrivalAtDeliverTarget();
                }
            }
            else {  // move toward target
                this.go.transform.position = Vector3.MoveTowards(this.go.transform.position, 
                    target.go.transform.position, maxSpeed * Time.deltaTime);
                SyncPosition();
            }
        }
    }
    public void SyncPosition(){
        this.x = this.go.transform.position.x;
        this.y = this.go.transform.position.y;
        bounds.center = go.transform.position;
    }
    public void MoveTo(float x, float y){
        if (x < 0 || x >= world.nx || y < 0 || y >= world.ny) { return; }
        if (/*world.GetTileAt(x, y).type.solid |*/ true){
            MoveTo(world.GetTileAt(x, y));
        }
    }
    public void MoveTo(Tile t){ // this sets state to walking. don't use this for stuff like fetching.
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

    public bool FetchForBlueprint(){
        Tile blueprintTile = FindBlueprint();
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
                }
            }
        }
        return false;
    }
    public void Collect(){ // collect recipe inputs (non hauler)
        if (recipe == null){ Debug.LogError("lost recipe!");}
        // TODO: collect multiple lots of input if they're available in a wide radius?
        desiredItem = null;
        foreach (ItemQuantity input in recipe.inputs){
            Debug.Log("need " + input.item.name + input.quantity.ToString());
            Debug.Log("have " + inv.Quantity(input.item).ToString());
            if (!inv.ContainsItem(input)){
                desiredItem = input.item;
                desiredItemQuantity = input.quantity;
                break;
            }
        }
        if (desiredItem == null){  // already have all desired items
            Debug.Log("collected items");
            target = workTile;
            state = AnimalState.Walking;
        } 
        else{
            Debug.Log("collecting " + desiredItem.name);
            Tile itemTile = FindItem(desiredItem);
            if (itemTile == null){Debug.Log("can't find a recipe input"); return;}
            target = itemTile;
            state = AnimalState.Fetching;
        }

    }
    public void GoToWork(){
        if (workTile == null){Debug.LogError("work tile doesn't exist!");}
        target = workTile;
        state = AnimalState.Walking;
    }

    public void OnArrivalAtFetchTarget(){      
        TakeItem(desiredItem, desiredItemQuantity); // pick up items
        desiredItemQuantity = desiredItemQuantity - inv.Quantity(desiredItem);
        if (inv.GetStorageForItem(desiredItem) > 5 && desiredItemQuantity > 0 && Fetch(desiredItem)){
            /* keep fetching the same item if you have space and can find stuff to fetch and can store it */  }
        else if (job.name == "hauler"){ Deliver(); }
        else {Collect();}
    }
    public void Deliver(){ // move items to storagetile.
        target = storageTile;
        state = AnimalState.Delivering;
    }
    public void OnArrivalAtDeliverTarget(){     // deliver items (to storage, etc.)
        if (target.blueprint != null){ OnArrivalDeliverToBlueprint(); return; }
        DropItem(desiredItem, target);
        int itemInInv = inv.Quantity(desiredItem);
        if (itemInInv > 0){ // if you have excess of the item, drop it somewhere.
            target = FindPlaceToDrop(desiredItem, itemInInv); 
            state = AnimalState.Delivering;
        }
        state = AnimalState.Idle;
    }
    public void OnArrivalDeliverToBlueprint(){      // deliver items to blueprint
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
    
    public void TakeItem(Item item, int quantity){  // pick up item from current location
        Tile tileHere = world.GetTileAt(x, y);
        if (tileHere != null && tileHere.inv != null){ 
            tileHere.inv.MoveItemTo(inv, item, quantity);
            if (tileHere.inv.IsEmpty() && tileHere.inv.invType == Inventory.InvType.Floor){
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
    public void DropItems(){    // drops all items.
        foreach (ItemStack stack in inv.itemStacks){
            if (stack != null && stack.quantity > 0){
                DropItem(stack.item, FindPlaceToDrop(stack.item));
            }
        }
    }

    public void Produce(string itemName, int quantity = 1){
        Produce(Db.itemByName[itemName], quantity);
    }
    public void Produce(ItemQuantity iq){ 
        if (iq == null){Debug.LogError("null iq!");}
        Produce(iq.item, iq.quantity);}
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
    public void Produce(Recipe recipe){ // only safe to call if you are sure the inv has all inputs!!
        ginv.AddItems(recipe.inputs, true);
        inv.AddItems(recipe.inputs, true);
        foreach (ItemQuantity iq in recipe.outputs){
            Produce(iq);
        }
    }


    public Tile FindItem(Item item, int r = 50){
        return Find(t => t.ContainsItem(item), r);
    }
    public Tile FindItemToHaul(Item item, int r = 50){
        return Find(t => t.HasItemToHaul(item), r);
    }
    public Tile FindStorage(Item item, int r = 50){ // finds inv to store
        return Find(t => t.HasStorageForItem(item), r); 
    }
    public Tile FindPlaceToDrop(Item item, int r = 3){ 
        return Find(t => t.HasSpaceForItem(item), r, true);
    }
    public Tile FindWorkBuilding(BuildingType buildingType, int r = 50){
        return Find(t => t.building != null && t.building.buildingType == buildingType && 
            t.building.capacity - t.building.reserved > 0, r);
    }
    public Tile FindWorkTile(TileType tileType, int r = 50){
        return Find(t => t.type == tileType && (t.capacity - t.reserved > 0), r);
    }
    public Tile FindWorkTile(string tileTypeStr, int r = 30){
        if (Db.tileTypeByName.ContainsKey(tileTypeStr)){
            return FindWorkTile(Db.tileTypeByName[tileTypeStr], r);
        } else {Debug.Log("tile type doesn't exist" + tileTypeStr); return null;}
    }
    public Tile FindBlueprint(int r = 50){
        return Find(t => t.blueprint != null, r);
    }
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

    public Recipe PickRecipe(){
        if (job.recipes.Length == 0){ return null;}
        List<Recipe> eligibleRecipes = new List<Recipe>();
        foreach (Recipe recipe in job.recipes){
            if (recipe != null && ginv.SufficientResources(recipe.inputs)){
                eligibleRecipes.Add(recipe);
            }
        }
        if (eligibleRecipes.Count == 0){return null;}
        int index = UnityEngine.Random.Range(0, eligibleRecipes.Count);
        return eligibleRecipes[index];
    }

    public int CalculateWorkPossible(Recipe recipe){
        foreach (ItemQuantity input in recipe.inputs){
             // need to get input capacities..
            // also maybe this function should be in Recipe instead of animal? but then can't use location data.
        }
        return 0;

    }


    // utils
    public float SquareDistance(float x1, float x2, float y1, float y2){
        return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);
    }


    public void RegisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged += callback;}
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged -= callback;}
}

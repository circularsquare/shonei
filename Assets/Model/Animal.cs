using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using Pathfinding;

public class Animal : MonoBehaviour      
{
    public string aName;
    public float x;
    public float y;
    public float timerOffset = 0f;
    public float maxSpeed = 1f;

    public Tile target;
    public Tile workTile;
    public Item desiredItem;
    public int desiredItemQuantity;
    public Tile storageTile; // tile to store fetched items in

    public Job job;
    public Inventory inv; 
    public GlobalInventory ginv;
    // list of skills goes here?
    // maybe one for each job?

    public enum AnimalState {Idle, Walking, Working, Fetching, Delivering}

    public AnimalState state;
    public GameObject go;
    public SpriteRenderer sr;
    public Astar astar;
    public System.Random random;

    public int[][] map;

    public Sprite sprite;
    public Bounds bounds; // a box to click on to select the animal
    World world;
    
    Action<Animal, Job> cbAnimalChanged;

    public void Start(){
        world = World.instance;
        this.aName = "mouse";
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
        Tile t = null;
        if (job.name == "none"){
            if (workTile != null){
                workTile.reserved = false;}
            workTile = null;
            state = AnimalState.Idle;
            return;
        } else if (job.name == "hauler"){
            if (random.Next(2) == 0){
                Fetch();
            } else {
                FetchForBlueprint();
            }
            return;
        } else if (job.name == "logger"){
            t = FindWorkTile("tree");
        } else if (job.name == "digger"){
            t = FindWorkTile("soil");
        } else if (job.name == "miner"){ 
            t = FindWorkTile("stone");
        } else if (job.name == "farmer"){
            t = FindWorkTile("wheat");
        } 
        if (t != null){
            workTile = t;
            workTile.reserved = true;
            MoveTo(workTile);
        }
    }

    public void FastUpdate(){
        if (state == AnimalState.Working){ // haulers are never Working
            switch (job.name) {
            case "none":
                Debug.LogError("working without a job!");
                break;
            case "logger":
                Produce("wood", 1);
                break;
            case "miner":
                Produce("stone", 1);
                break;
            case "farmer":
                Produce("wheat", 1);
                break;
            case "digger":
                Produce("soil", 1);
                break;
            default:
                Debug.LogError("unknown job!");
                break;
            }
        }
        if (state == AnimalState.Idle) {
            DropItems();
            FindWork(); // want to move this into slow update, once i make it
        }
        if (state == AnimalState.Idle){
            //MoveTo(x + (float)UnityEngine.Random.Range(-1, 2), y);
        }
    }
    public void Produce(string itemName, int amount = 1){
        //inv.AddItem(itemName, amount);
        ginv.AddItem(itemName, amount);
        ProduceAndDrop(Db.itemByName[itemName], amount);
    }

    public void Update(){
        if ((state == AnimalState.Walking) || (state == AnimalState.Fetching) || (state == AnimalState.Delivering)){
            // arrived at target
            if (Vector3.Distance(this.go.transform.position, target.go.transform.position) < 0.02f){
                this.go.transform.position = target.go.transform.position;
                SyncPosition(); 


                if (state == AnimalState.Walking){ // have reached destination.
                    if (target == workTile){
                        state = AnimalState.Working;
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
            else {
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
    public void MoveTo(Tile t){
        target = t;
        this.state = AnimalState.Walking;
    }

    public bool Fetch(Item item = null, int quantity = 40){ 
        Tile itemTile = FindFloorItem(item);
        if (itemTile == null){Debug.Log("nothing to fetch"); return false;} 
        if (item != null){ desiredItem = item; }
        else { desiredItem = itemTile.inv.itemStacks[0].item; }
        desiredItemQuantity = quantity; // TODO: should juts fetch all probably? or itll juts fetch one as it's produced.
        storageTile = FindStorage(desiredItem); // TODO: need to reserve space
        if (storageTile == null){Debug.Log("nowhere to store");return false;} 
        target = itemTile;
        state = AnimalState.Fetching; // on arrival, will HaulBack()
        return true;
    }
    public bool FetchForBlueprint(){
        Tile blueprintTile = FindBlueprint();
        if (blueprintTile == null){return false;}
        Blueprint blueprint = blueprintTile.blueprint;
        for (int i = 0; i < blueprint.costs.Length; i++){
            if (blueprint.deliveredResources[i].quantity < blueprint.costs[i].quantity){
                Tile itemTile = FindItem(Db.items[blueprint.costs[i].id]);
                if (itemTile != null){
                    desiredItem = Db.items[blueprint.costs[i].id];
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

    public void OnArrivalAtFetchTarget(){
        TakeItem(desiredItem, desiredItemQuantity);
        int itemInInv = inv.GetItemAmount(desiredItem);
        if (itemInInv < desiredItemQuantity){
            if( Fetch(desiredItem, desiredItemQuantity - itemInInv)){
                state = AnimalState.Fetching;   // keep fetching more item
            } else { Deliver(); }
        } else { Deliver(); }
    }
    public void Deliver(){ // move items to storagetile.
        target = storageTile;
        state = AnimalState.Delivering;
    }
    public void OnArrivalAtDeliverTarget(){
        if (target.blueprint != null){ OnArrivalDeliverToBlueprint(); return; }
        DropItem(desiredItem, target);
        int itemInInv = inv.GetItemAmount(desiredItem);
        if (itemInInv > 0){ // if you have excess of the item, drop it somewhere.
            target = FindPlaceToDrop(desiredItem, itemInInv); // at the moment they seem to just hold onto it!
            state = AnimalState.Delivering;
        }
        state = AnimalState.Idle;

    }
    public void OnArrivalDeliverToBlueprint(){
        int extra = target.blueprint.RecieveResource(desiredItem, desiredItemQuantity);
        int delivered = desiredItemQuantity - extra;
        inv.AddItem(desiredItem, -delivered); // remove item from own inv
        
        int itemInInv = inv.GetItemAmount(desiredItem);
        if (itemInInv > 0){ // if you have excess of the item, drop it somewhere.
            target = FindPlaceToDrop(desiredItem, itemInInv); // at the moment they seem to just hold onto it!
            state = AnimalState.Delivering;
        }
        state = AnimalState.Idle;
    }
    
    public void TakeItem(Item item, int quantity){
        Tile tileHere = world.GetTileAt(x, y);
        if (tileHere != null && tileHere.inv != null){ 
            tileHere.inv.MoveItemTo(inv, item, quantity);
        }
        if (tileHere.inv.IsEmpty() && tileHere.inv.invType == Inventory.InvType.Floor){
            tileHere.inv = null;
        }
    }
    // tries to drop all of an item at a nearby tile.
    public void DropItem(Item item, Tile dTile = null){ 
        if (dTile == null){
            dTile = world.GetTileAt(x, y);
        }
        if (dTile.inv == null){
            dTile.inv = new Inventory(1, 20, Inventory.InvType.Floor, dTile.x, dTile.y);
        }
        inv.MoveItemTo(dTile.inv, item, inv.GetItemAmount(item));
    }
    // drops all items.
    public void DropItems(){
        foreach (ItemStack stack in inv.itemStacks){
            if (stack != null && stack.quantity > 0){
                DropItem(stack.item, FindPlaceToDrop(stack.item));
            }
        }
    }

    // instantly produces item at a nearby tile. only allowed for producers like miners
    public void ProduceAndDrop(Item item, int quantity = 1){
        Tile dTile = FindPlaceToDrop(item);
        if (dTile == null){Debug.Log("no place to drop item!!");}
        if (dTile.inv == null){
            dTile.inv = new Inventory(1, 20, Inventory.InvType.Floor, dTile.x, dTile.y);
        }
        dTile.inv.AddItem(item, quantity);
    }

    // if item == null, finds any floor item
    public Tile FindFloorItem(Item item = null, int r = 50){
        return Find(t => t.ContainsFloorItem(item), r);
    }
    public Tile FindItem(Item item, int r = 50){
        return Find(t => t.ContainsItem(item), r);
    }
    public Tile FindStorage(Item item, int r = 50){ // finds inv to store
        return Find(t => t.HasStorageForItem(item), r); 
    }
    public Tile FindPlaceToDrop(Item item, int r = 3){ 
        return Find(t => t.HasSpaceForItem(item), r, true);
    }
    public Tile FindWorkTile(TileType tileType, int r = 50){
        return Find(t => t.type == tileType && !t.reserved, r);
    }
    public Tile FindWorkTile(string tileTypeStr, int r = 30){
        if (Db.tileTypeByName.ContainsKey(tileTypeStr)){
            return FindWorkTile(Db.tileTypeByName[tileTypeStr], r);
        } else {Debug.Log("tile type doesn't exist"); return null;}
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


    public float SquareDistance(float x1, float x2, float y1, float y2){
        return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);
    }


    public void RegisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged += callback;}
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback){
        cbAnimalChanged -= callback;}
}

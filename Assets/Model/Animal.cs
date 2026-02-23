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
    public float maxSpeed = 2f;
    public bool isMovingRight = true;

    public Tile target;         // where you are currently going
    public Tile homeTile;

    public Recipe recipe;
    public int numRounds = 0;
    public float workProgress = 0f;

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
        Working,        // For any work at a location (crafting, building, etc)
        Eeping,
        Moving,         // any type of moving, under the task system
    }
    private AnimalStateManager stateManager;
    private AnimalState _state;
    public AnimalState state{
        get { return _state; }
        set { if (_state != value) {
                _state = value;
                stateManager.OnStateEnter(value);
                animationController?.UpdateState();
            }
        }
    }

    public System.Random random;
    public int tickCounter = 0;

    public GameObject go;
    public SpriteRenderer sr;
    public Sprite sprite;
    public World world;

    Action<Animal, Job> cbAnimalChanged;



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


    public void ChooseTask() {
        if (task != null){ return; } // TODO: change when this func is called to just whenever task is null?

        if (inv.GetFreeStacks() <= 2){ // drop inventory if two or fewer open stacks!
            task = new DropTask(this); 
            if (task.Start()) return; 
            else Debug.Log("inventory near full and can't drop!"); }

        if (job.name == "none") { return; }

        if (eating.Hungry()) {
            task = new ObtainTask(this, Db.itemByName["wheat"], 5); 
            if (task.Start()) return; }
        if (eeping.Eepy()) { 
            task = new EepTask(this); 
            if (task.Start()) return; }

        Path harvestPath = nav.FindPathToHarvestable(job);    // harvest
        if (harvestPath != null) { 
            task = new HarvestTask(this, harvestPath.tile);
            if (task.Start()) return;}

        task = new CraftTask(this);             // craft
        if (task.Start()) return;
        task = new ConstructTask(this);         // construct blueprints
        if (task.Start()) return;
        task = new SupplyBlueprintTask(this);   // supply blueprints
        if (task.Start()) return;
        if (job.name == "hauler") {             // haul
            task = new HaulTask(this);
            if (task.Start()) return; }

        task = null; // none of the above tasks started successfully...
        return;
    }


    // this uses the task/objective system!
    public void OnArrival(){
        task?.OnArrival();
    }

    public void Refresh(){ // end task, go idle, AND drop items
        // Debug.Log(aName + " refreshed! interrupting current task");
        task?.Fail();
        task = new DropTask(this);
        if (!task.Start()){
            task = null;
            state = AnimalState.Idle;
        }
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
    public int DropItem(ItemQuantity iq) { return(DropItem(iq.item, iq.quantity)); }
    public Inventory EnsureFloorInventory(Tile t) { // returns inventory at a tile
        if (t.inv == null) { t.inv = new Inventory(x: t.x, y: t.y); }
        return t.inv;
    }

    // produces item in ani inv, dumps at nearby tile if inv full
    public void Produce(Item item, int quantity = 1){
        if (quantity < 0){
            Debug.LogError("called produce with negative quantity, use consume instead");
            Consume(item, -quantity); return;
        }
        int leftover = inv.Produce(item, quantity); 
        if (leftover > 0){
            Debug.LogError(aName + " produced without space in inventory");
            Path dropPath = nav.FindPathToDrop(item);
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
        else { Debug.Log("called produce without having all recipe ingredients! not doing."); }
    }
    public bool CanProduce(Recipe recipe) {
        Building b = TileHere()?.building;
        return b != null && inv.ContainsItems(recipe.inputs) && recipe.tile == b.structType.name;
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
    public Recipe PickRecipe(){ // score based selection
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
        int numRounds = 10; // will try to gather this amount of input at once.
        int n;
        foreach (ItemQuantity input in recipe.inputs){
            n = ginv.Quantity(input.id) / input.quantity;
            if (n < numRounds) { numRounds = n; }
        }
        foreach (ItemQuantity output in recipe.outputs){
            Path storePath = nav.FindPathToStorage(output.item);
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
    }
    public void SetJob(string jobStr) { SetJob(Db.GetJobByName(jobStr)); }


    public void FindHome(){
        // if you have no home tile, or your hometile does not have a house,
            // if u can find a house, set ur hometile to that
        if (homeTile == null || !(homeTile?.building?.structType.name == "house")){
            Path housePath = nav.FindPathToBuilding(Db.structTypeByName["house"]);
            if (housePath != null && housePath.tile.building?.structType.name == "house") { 
                // should maybe also unreserve previous house, if you can? 
                if (homeTile?.building?.structType.name == "house") homeTile.building.res.Unreserve();
                homeTile = housePath.tile; 
                homeTile.building.res.Reserve();
            }
        }
    }

    public Tile TileHere() { return world.GetTileAt(x, y); }

    public bool AtHome() { 
        return homeTile != null && homeTile == TileHere() && homeTile.building?.structType.name == "house"; 
    }

    public bool IsMoving(){
        return !(state == AnimalState.Idle || state == AnimalState.Working
            || state == AnimalState.Eeping);
    }

    public float SquareDistance(float x1, float x2, float y1, float y2) { return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2); }
    public void RegisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged += callback; }
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged -= callback; }
}



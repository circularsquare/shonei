using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class Animal : MonoBehaviour {
    // Core properties
    public string aName;
    public int id;
    public float x;
    public float y;
    public float maxSpeed = 2f;
    public bool isMovingRight = true;

    // State and objectives
    public Tile target;
    public enum DeliveryTarget { None, Storage, Blueprint, Drop, Self }
    public DeliveryTarget deliveryTarget = DeliveryTarget.None;
    public Tile workTile;
    public Item desiredItem;
    public int desiredItemQuantity;
    public Tile storageTile;
    public Tile homeTile;

    // Work related
    public Recipe recipe;
    public int numRounds = 0;
    public Job job;
    public Inventory inv;
    public GlobalInventory ginv;

    // Status
    public float energy;
    public float efficiency;

    // Components
    public AnimalNeeds needs;
    public AnimalNavigation nav;
    public AnimalStateMachine stateMachine;
    public AnimalBehaviors behaviors;
    public AnimationController animationController;

    // State enums
    public enum AnimalState {
        Idle,
        Walking,
        Working,
        Fetching,
        Delivering,
        Eeping,
    }

    public enum Objective {
        None,
        Construct,
    }

    private AnimalState _state;
    public AnimalState state {
        get { return _state; }
        set {
            if (_state != value) {
                _state = value;
                stateMachine.OnStateEnter(value);
                if (animationController != null) {
                    animationController.UpdateState();
                }
            }
        }
    }
    public Objective objective;

    // Misc
    public System.Random random;
    public int tickCounter = 0;
    public GameObject go;
    public SpriteRenderer sr;
    public Sprite sprite;
    public Bounds bounds;
    public World world;

    Action<Animal, Job> cbAnimalChanged;

    public void Start() {
        world = World.instance;
        this.aName = "mouse" + id.ToString();
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

        // Initialize components
        this.needs = new AnimalNeeds(this);
        this.nav = new AnimalNavigation(this);
        this.stateMachine = new AnimalStateMachine(this);
        this.behaviors = new AnimalBehaviors(this);
        
        ginv = GlobalInventory.instance;
        random = new System.Random();

        FindHome();
    }

    public void TickUpdate() {
        if (this.needs == null) { return; }
        tickCounter++;
        if (tickCounter % 10 == 0) {
            SlowUpdate();
        }

        needs.Update();
        UpdateEfficiency();

        energy += efficiency;
        if (energy > 1f) {
            energy -= 1f;
            stateMachine.UpdateState();
        }
    }

    private void UpdateEfficiency() {
        efficiency = needs.GetEfficiency();
        maxSpeed = 2f * efficiency;
    }

    public void SlowUpdate() {
        FindHome();
    }

    public void Update() {
        stateMachine.UpdateMovement(Time.deltaTime);
    }

    // Item movement methods
    public void TakeItem(Item item, int quantity) {
        Tile tileHere = TileHere();
        if (tileHere != null && tileHere.inv != null) {
            tileHere.inv.MoveItemTo(inv, item, quantity);
            if (tileHere.inv.IsEmpty() && tileHere.inv.invType == Inventory.InvType.Floor) {
                tileHere.inv.Destroy();
                tileHere.inv = null;
            }
        }
    }

    public void DropItem(Item item, int quantity = -1) {
        if (quantity == -1) { quantity = inv.Quantity(item); }
        inv.MoveItemTo(EnsureFloorInventory(TileHere()), item, quantity);
    }

    public Inventory EnsureFloorInventory(Tile t) {
        if (t.inv == null) {
            t.inv = new Inventory(x: t.x, y: t.y);
        }
        return t.inv;
    }

    public void DropAtBlueprint() {
        int amountToDeliver = inv.Quantity(desiredItem);
        int delivered = target.blueprint.ReceiveResource(desiredItem, amountToDeliver);
        inv.AddItem(desiredItem, -delivered);
        int itemInInv = inv.Quantity(desiredItem);
        if (itemInInv > 0) {
            StartDropping(desiredItem);
            return;
        }
    }

    public void StartDropping(Item item, int quantity = -1) {
        if (item == null) { StartDropping(); return; }
        desiredItem = item;
        desiredItemQuantity = quantity;
        deliveryTarget = DeliveryTarget.Drop;
        StartDelivering();
    }

    public void StartDropping() {
        foreach (ItemStack stack in inv.itemStacks) {
            if (stack != null && stack.item != null && stack.quantity > 0) {
                StartDropping(stack.item);
            }
        }
    }

    public void Produce(Item item, int quantity = 1) {
        if (quantity < 0) {
            Debug.LogError("called produce with negative quantity, use consume instead");
            Consume(item, -quantity);
            return;
        }
        int leftover = quantity;
        if (leftover > 0) {
            Path dropPath = nav.FindPlaceToDrop(item);
            if (dropPath == null) {
                Debug.Log("no place to drop " + item.name + "!! excess item disappearing.");
                return;
            }
            Tile dTile = dropPath.tile;
            if (dTile.inv == null) {
                dTile.inv = new Inventory(x: dTile.x, y: dTile.y);
            }
            dTile.inv.Produce(item, leftover);
        }
    }

    public void Produce(string itemName, int quantity = 1) { Produce(Db.itemByName[itemName], quantity); }
    public void Produce(ItemQuantity iq) { Produce(iq.item, iq.quantity); }
    public void Produce(ItemQuantity[] iqs) { Array.ForEach(iqs, iq => Produce(iq)); }
    public void Produce(Recipe recipe) {
        if (recipe != null && inv.ContainsItems(recipe.inputs)) {
            foreach (ItemQuantity iq in recipe.inputs) { inv.Produce(iq.item, -iq.quantity); }
            Produce(recipe.outputs);
        } else { Debug.Log("called produce without having all recipe ingredients! not doing."); }
    }

    public void Consume(Item item, int quantity = 1) {
        if (inv.Produce(item, -quantity) < 0) {
            Debug.LogError("tried consuming more than you have!");
        }
    }

    // Navigation methods
    public bool GoTo(Tile t, Path p = null) {
        if (t == null && p == null) { Debug.LogError("destination tile is null!"); return false; }
        if (nav.NavigateTo(t, p)) {
            state = AnimalState.Walking;
            return true;
        }
        return false;
    }

    public bool GoTo(Path p) { return GoTo(p.tile, p); }
    public bool GoTo(float x, float y) { return GoTo(world.GetTileAt(x, y)); }

    public void GoToEep() {
        if (homeTile == null) { state = AnimalState.Eeping; }
        else { GoTo(homeTile); }
    }

    // Work tile management
    public void SetWorkTile(Tile t) {
        if (workTile != null) {
            //workTile.reserved -= 1;
        }
        workTile = t;
        //workTile.reserved += 1;
    }

    public void RemoveWorkTile() {
        if (workTile == null) { return; }
        //workTile.reserved -= 1;
        workTile = null;
    }

    public void FindHome() {
        if (homeTile == null) {
            Path homePath = nav.FindBuilding(Db.structTypeByName["house"]);
            if (homePath != null) { homeTile = homePath.tile; }
            if (homeTile != null) {
                //homeTile.building.reserved += 1;
            }
        }
    }

    // Utility methods
    public Tile TileHere() { return world.GetTileAt(x, y); }
    public bool AtHome() { return homeTile != null && homeTile == TileHere(); }
    public bool AtWork() {
        return workTile != null && workTile == TileHere() &&
        recipe != null && recipe.tile == workTile.type.name;
    }

    public bool IsMoving() {
        return !(state == AnimalState.Idle || state == AnimalState.Working
            || state == AnimalState.Eeping);
    }

    public float SquareDistance(float x1, float x2, float y1, float y2) {
        return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);
    }

    // Job management
    public void SetJob(Job newJob) {
        Job oldJob = this.job;
        this.job = newJob;
        if (cbAnimalChanged != null) {
            cbAnimalChanged(this, oldJob);
        }
        Refresh();
        behaviors.FindWork();
    }

    public void SetJob(string jobStr) { SetJob(Db.GetJobByName(jobStr)); }

    // Callback registration
    public void RegisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged += callback; }
    public void UnregisterCbAnimalChanged(Action<Animal, Job> callback) { cbAnimalChanged -= callback; }

    // State management
    public void Refresh() {
        desiredItem = null;
        desiredItemQuantity = 0;
        deliveryTarget = DeliveryTarget.None;
        RemoveWorkTile();
        storageTile = null;
        state = AnimalState.Idle;
    }
}



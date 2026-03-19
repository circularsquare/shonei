using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    WorkOrderManager centralises work prioritisation. Instead of each animal independently
    scanning for tasks in a hardcoded order, work is registered here as prioritised WorkOrders.
    Animals call FindBestTask() to get the highest-priority task they can do.

    Priority:  1 = highest (hauls unblocking a pending deconstruct)
               2 = construct, supply blueprint, harvest
               3 = haul, consolidate, haul to/from market
               4 = deconstruct

    Blueprint-based work (Construct, Supply, Deconstruct) is registered explicitly when
    blueprint state changes.  Haul orders are registered when items land on floor inventories.
    Harvest orders are registered when plants become harvestable.
    Craft and Research are NOT in this system (handled as fallbacks in ChooseTask).
*/
public class WorkOrderManager : MonoBehaviour {
    public static WorkOrderManager instance { get; private set; }

    public enum OrderType { Haul, Harvest, Construct, SupplyBlueprint, Deconstruct, HaulToMarket, HaulFromMarket }

    public class WorkOrder {
        public OrderType type;
        public int priority;             // 1=highest, 4=lowest
        public Func<Animal, Task> factory;
        public Blueprint blueprint;      // nullable; for blueprint-based orders & cleanup
        public ItemStack stack;          // nullable; for haul orders (dedup & priority promote)
        public Tile tile;                // nullable; for harvest orders (dedup & cleanup)
        public Inventory inv;            // nullable; for market haul orders (dedup & staleness)
        public Func<Animal, bool> canDo;        // null = any animal; job gate checked before factory
        public Func<Animal, float> getDistance; // null = 0; Chebyshev approx, used for within-tier sorting
    }

    // Sorted: ascending priority (1 first), FIFO within the same priority.
    // Maintained sorted by Insert().
    private readonly List<WorkOrder> orders = new();

    void Awake() {
        if (instance != null) { Debug.LogError("WorkOrderManager: multiple instances"); return; }
        instance = this;
    }

    void OnDestroy() { instance = null; }

    // ── QUERY ──────────────────────────────────────────────────────────────────────

    // Builds a candidate list filtered by canDo, sorted by (priority ASC, distance ASC),
    // then returns the first task this animal can start. Removes the claimed order.
    public Task FindBestTask(Animal animal) {
        PruneStaleHauls();
        PruneStaleMarketOrders();

        var candidates = new List<(WorkOrder order, float dist)>();
        foreach (WorkOrder order in orders) {
            if (order.canDo != null && !order.canDo(animal)) continue;
            float dist = order.getDistance?.Invoke(animal) ?? 0f;
            candidates.Add((order, dist));
        }
        candidates.Sort((a, b) => {
            int pc = a.order.priority.CompareTo(b.order.priority);
            return pc != 0 ? pc : a.dist.CompareTo(b.dist);
        });

        foreach (var (order, _) in candidates) {
            Task task = order.factory(animal);
            if (task.Start()) {
                orders.Remove(order);
                return task;
            }
        }
        return null;
    }

    // ── REGISTRATION ───────────────────────────────────────────────────────────────

    public bool RegisterConstruct(Blueprint bp) {
        // No reserved-guard here — PromoteToConstruct calls this while the prior supply task
        // is still cleaning up (res still reserved). Guard lives in Reconcile/AuditOrders only.
        if (HasOrderFor(OrderType.Construct, bp)) return false;
        Insert(new WorkOrder {
            type = OrderType.Construct,
            priority = 2,
            factory = a => new ConstructTask(a, bp),
            blueprint = bp,
            canDo = a => a.job == bp.structType.job,
            getDistance = a => Mathf.Max(Mathf.Abs(bp.tile.x - a.x), Mathf.Abs(bp.tile.y - a.y))
        });
        return true;
    }

    public bool RegisterSupplyBlueprint(Blueprint bp) {
        // No reserved-guard — see RegisterConstruct comment.
        if (HasOrderFor(OrderType.SupplyBlueprint, bp)) return false;
        Insert(new WorkOrder {
            type = OrderType.SupplyBlueprint,
            priority = 2,
            factory = a => new SupplyBlueprintTask(a, bp),
            blueprint = bp,
            canDo = a => a.job == bp.structType.job,
            getDistance = a => Mathf.Max(Mathf.Abs(bp.tile.x - a.x), Mathf.Abs(bp.tile.y - a.y))
        });
        return true;
    }

    // Called when the last supply delivery transitions a blueprint Receiving → Constructing.
    public void PromoteToConstruct(Blueprint bp) {
        orders.RemoveAll(o => o.blueprint == bp && o.type == OrderType.SupplyBlueprint);
        RegisterConstruct(bp);
    }

    public bool RegisterDeconstruct(Blueprint bp) {
        // No reserved-guard — see RegisterConstruct comment.
        if (HasOrderFor(OrderType.Deconstruct, bp)) return false;
        Insert(new WorkOrder {
            type = OrderType.Deconstruct,
            priority = 4,
            factory = a => new ConstructTask(a, bp, deconstructing: true),
            blueprint = bp,
            canDo = a => a.job == bp.structType.job,
            getDistance = a => Mathf.Max(Mathf.Abs(bp.tile.x - a.x), Mathf.Abs(bp.tile.y - a.y))
        });
        PromoteHaulsFor(bp);
        return true;
    }

    // Registers a haul order for a floor ItemStack (priority 3).
    // Deduplicates by stack reference; no-ops on null/empty/actively-reserved stacks.
    // Returns true if a new order was inserted.
    public bool RegisterHaul(ItemStack stack) {
        if (!StackNeedsHaulOrder(stack)) return false;
        if (orders.Exists(o => o.type == OrderType.Haul && o.stack == stack)) return false;
        Insert(new WorkOrder {
            type = OrderType.Haul,
            priority = 3,
            factory = a => {
                if (a.nav.FindPathToStorage(stack.item) != null) return new HaulTask(a, stack);
                return new ConsolidateTask(a, stack);
            },
            stack = stack,
            canDo = a => a.job.name == "hauler",
            getDistance = a => Mathf.Max(Mathf.Abs(stack.inv.x - a.x), Mathf.Abs(stack.inv.y - a.y))
        });
        return true;
    }

    // Promotes haul orders for any items blocking a deconstruct (items above the tile that would
    // fall, or items inside the building's storage). Called when a deconstruct order is registered,
    // and again by ConstructTask if it detects a block mid-Initialize.
    public void PromoteHaulsFor(Blueprint bp) {
        if (bp == null) return;
        World world = World.instance;
        // Items on tile(s) above that would fall
        for (int i = 0; i < bp.structType.nx; i++) {
            Tile above = world.GetTileAt(bp.tile.x + i, bp.tile.y + 1);
            if (above?.inv == null || above.inv.IsEmpty()) continue;
            foreach (ItemStack s in above.inv.itemStacks) {
                if (s.item == null || s.quantity == 0) continue;
                PromoteOrCreateHaul(s, bp);
            }
        }
        // Items still in storage that would be deconstructed
        Inventory storage = bp.tile.building?.storage;
        if (storage != null && !storage.IsEmpty()) {
            foreach (ItemStack s in storage.itemStacks) {
                if (s.item == null || s.quantity == 0) continue;
                PromoteOrCreateHaul(s, bp);
            }
        }
    }

    // Promotes an existing p3 haul order for this stack to p1, or creates a new p1 haul order
    // if none exists. P1 hauls block deconstruct and use the same haul-or-consolidate factory.
    private void PromoteOrCreateHaul(ItemStack stack, Blueprint source) {
        WorkOrder existing = orders.Find(o => o.type == OrderType.Haul && o.stack == stack);
        if (existing != null) {
            if (existing.priority == 1) return; // already priority 1
            orders.Remove(existing);
            existing.priority = 1;
            existing.blueprint = source;
            Insert(existing);
        } else {
            Insert(new WorkOrder {
                type = OrderType.Haul,
                priority = 1,
                factory = a => {
                    if (a.nav.FindPathToStorage(stack.item) != null) return new HaulTask(a, stack);
                    return new ConsolidateTask(a, stack);
                },
                stack = stack,
                blueprint = source,
                canDo = a => a.job.name == "hauler",
                getDistance = a => Mathf.Max(Mathf.Abs(stack.inv.x - a.x), Mathf.Abs(stack.inv.y - a.y))
            });
        }
    }

    public void RegisterMarketHauls(Inventory marketInv) {
        if (MarketNeedsHaulTo(marketInv) && !orders.Exists(o => o.type == OrderType.HaulToMarket && o.inv == marketInv))
            Insert(new WorkOrder {
                type = OrderType.HaulToMarket,
                priority = 3,
                factory = a => new HaulToMarketTask(a),
                inv = marketInv,
                canDo = a => a.job.name == "merchant"
            });
        if (MarketNeedsHaulFrom(marketInv) && !orders.Exists(o => o.type == OrderType.HaulFromMarket && o.inv == marketInv))
            Insert(new WorkOrder {
                type = OrderType.HaulFromMarket,
                priority = 3,
                factory = a => new HaulFromMarketTask(a),
                inv = marketInv,
                canDo = a => a.job.name == "merchant"
            });
    }

    public bool RegisterHarvest(Plant plant) {
        if (plant == null || plant.tile == null) return false;
        if (!PlantNeedsOrder(plant)) return false;
        if (orders.Exists(o => o.type == OrderType.Harvest && o.tile == plant.tile)) return false;
        Tile tile = plant.tile;
        Job harvestJob = plant.plantType.job;
        Insert(new WorkOrder {
            type = OrderType.Harvest,
            priority = 2,
            factory = a => new HarvestTask(a, tile),
            tile = tile,
            canDo = a => a.job == harvestJob,
            getDistance = a => Mathf.Max(Mathf.Abs(tile.x - a.x), Mathf.Abs(tile.y - a.y))
        });
        return true;
    }

    // ── REMOVAL ────────────────────────────────────────────────────────────────────

    // Remove all orders associated with this blueprint (used on blueprint cancel/destroy).
    public void RemoveForBlueprint(Blueprint bp) {
        orders.RemoveAll(o => o.blueprint == bp);
    }

    // Remove harvest order for a tile (used when plant is destroyed before harvest).
    public void RemoveForTile(Tile tile) {
        orders.RemoveAll(o => o.tile == tile);
    }

    // Clears all orders — call from ClearWorld() before loading a new save.
    public void ClearAllOrders() { orders.Clear(); }

    // Remove stale haul orders whose stacks have been emptied.
    // Called opportunistically; not required for correctness (Initialize returns false on stale).
    public void PruneStaleHauls() {
        orders.RemoveAll(o => o.type == OrderType.Haul &&
            (o.stack == null || o.stack.item == null || o.stack.quantity == 0));
    }

    // Remove market haul orders where the need has gone away (market restocked/emptied since registration).
    private void PruneStaleMarketOrders() {
        orders.RemoveAll(o => o.type == OrderType.HaulToMarket   && o.inv != null && !MarketNeedsHaulTo(o.inv));
        orders.RemoveAll(o => o.type == OrderType.HaulFromMarket && o.inv != null && !MarketNeedsHaulFrom(o.inv));
    }

    // ── RECONCILE / AUDIT ──────────────────────────────────────────────────────────

    // "Needs a haul order" predicate: has items and no active task holding a reservation.
    // resAmount == 0 means no task is currently working on this stack; any reservation (even partial)
    // means a task was claimed and will re-register via Cleanup() when it finishes or fails.
    // IMPORTANT: same rule used by RegisterHaul, Reconcile, and AuditOrders — update here, not each site.
    // ORDERING: any Cleanup that calls RegisterHaul must call base.Cleanup() (which unreserves)
    //           BEFORE calling RegisterHaul, or resAmount will still be > 0 and this returns false.
    private static bool StackNeedsHaulOrder(ItemStack stack) =>
        stack != null && stack.item != null && stack.quantity > 0 && stack.resAmount == 0;

    // "Needs a harvest order" predicate.
    // IMPORTANT: Reconcile, AuditOrders, and every RegisterHarvest call site must all use this.
    // ORDERING: any Cleanup that calls RegisterHarvest must release res.Unreserve() BEFORE calling
    //           RegisterHarvest, or res.reserved will still be > 0 and this returns false.
    private static bool PlantNeedsOrder(Plant p) => p.harvestable && p.res.reserved == 0;

    // "Needs a blueprint order" predicate (same rule).
    private static bool BlueprintNeedsOrder(Blueprint bp) => bp.res == null || bp.res.reserved == 0;

    private static bool MarketNeedsHaulTo(Inventory inv) =>
        inv.targets != null && inv.targets.Any(kvp => inv.Quantity(kvp.Key) < kvp.Value);
    private static bool MarketNeedsHaulFrom(Inventory inv) =>
        inv.targets != null && inv.targets.Any(kvp => inv.Quantity(kvp.Key) > kvp.Value);

    // Called periodically as a safety net. Register* methods return true only when a new order
    // is actually inserted (false = already existed, no-op). Logs a warning on any real fix.
    public void Reconcile() {
        foreach (Plant p in PlantController.instance.Plants)
            if (RegisterHarvest(p))
                Debug.LogWarning($"WOM reconcile: registered missing harvest order for {p.plantType.name} at ({p.x},{p.y})");

        foreach (Blueprint bp in StructController.instance.GetBlueprints()) {
            if (!BlueprintNeedsOrder(bp)) continue;
            bool inserted = bp.state switch {
                Blueprint.BlueprintState.Constructing   => RegisterConstruct(bp),
                Blueprint.BlueprintState.Receiving      => RegisterSupplyBlueprint(bp),
                Blueprint.BlueprintState.Deconstructing => RegisterDeconstruct(bp),
                _ => false
            };
            if (inserted)
                Debug.LogWarning($"WOM reconcile: registered missing {bp.state} order for {bp.structType.name} at ({bp.x},{bp.y})");
        }

        if (InventoryController.instance.byType.TryGetValue(Inventory.InvType.Floor, out var floors)) {
            foreach (Inventory inv in floors)
                foreach (ItemStack stack in inv.itemStacks)
                    if (RegisterHaul(stack))
                        Debug.LogWarning($"WOM reconcile: registered missing haul order for {stack.item?.name} at ({inv.x},{inv.y})");
        }

        if (InventoryController.instance.byType.TryGetValue(Inventory.InvType.Market, out var markets)) {
            foreach (Inventory inv in markets)
                RegisterMarketHauls(inv);
        }
    }

    // Ctrl+D dev tool: checks invariants in both directions and logs violations.
    public void AuditOrders() {
        // Harvest — direction 1: every harvestable plant with no active worker must have an order
        foreach (Plant p in PlantController.instance.Plants)
            if (PlantNeedsOrder(p) && !orders.Exists(o => o.type == OrderType.Harvest && o.tile == p.tile))
                Debug.LogError($"WOM audit: harvestable plant at ({p.x},{p.y}) has no harvest order");
        // Harvest — direction 2: every harvest order must have a harvestable plant
        foreach (WorkOrder o in orders)
            if (o.type == OrderType.Harvest && !(o.tile?.building is Plant p2 && p2.harvestable))
                Debug.LogError($"WOM audit: harvest order at ({o.tile?.x},{o.tile?.y}) has no harvestable plant");

        // Blueprints — direction 1: every active blueprint must have a matching order
        var bps = StructController.instance.GetBlueprints();
        foreach (Blueprint bp in bps) {
            OrderType expected = bp.state switch {
                Blueprint.BlueprintState.Constructing   => OrderType.Construct,
                Blueprint.BlueprintState.Receiving      => OrderType.SupplyBlueprint,
                Blueprint.BlueprintState.Deconstructing => OrderType.Deconstruct,
                _ => (OrderType)(-1)
            };
            if (expected != (OrderType)(-1) && BlueprintNeedsOrder(bp) && !orders.Exists(o => o.type == expected && o.blueprint == bp))
                Debug.LogError($"WOM audit: blueprint {bp.structType.name} at ({bp.x},{bp.y}) missing {expected} order");
        }
        // Blueprints — direction 2: every blueprint order must reference a live blueprint
        foreach (WorkOrder o in orders)
            if (o.blueprint != null && !bps.Contains(o.blueprint))
                Debug.LogError($"WOM audit: {o.type} order references a blueprint not in StructController");

        // Prune stale haul orders before checking direction-2, so timing artifacts don't fire.
        PruneStaleHauls();

        // Hauls — direction 1: every haulable floor stack must have an order
        if (InventoryController.instance.byType.TryGetValue(Inventory.InvType.Floor, out var floors)) {
            foreach (Inventory inv in floors)
                foreach (ItemStack stack in inv.itemStacks)
                    if (StackNeedsHaulOrder(stack) && !orders.Exists(o => o.type == OrderType.Haul && o.stack == stack))
                        Debug.LogError($"WOM audit: floor stack {stack.item?.name} at ({inv.x},{inv.y}) has no haul order");
        }
        // Hauls — direction 2: every haul order must reference a valid stack
        foreach (WorkOrder o in orders)
            if (o.type == OrderType.Haul && !StackNeedsHaulOrder(o.stack))
                Debug.LogError($"WOM audit: haul order references stale/empty stack ({o.stack?.item?.name})");

        Debug.Log($"WOM audit complete. {orders.Count} orders.");
    }

    // ── INTERNAL ───────────────────────────────────────────────────────────────────

    private void Insert(WorkOrder order) {
        // Find insertion point: after all orders with strictly lower priority (higher number).
        // This preserves FIFO within the same priority.
        int i = orders.Count;
        while (i > 0 && orders[i - 1].priority > order.priority) i--;
        orders.Insert(i, order);
    }

    private bool HasOrderFor(OrderType type, Blueprint bp) =>
        orders.Exists(o => o.type == type && o.blueprint == bp);

    // ── DEBUG ──────────────────────────────────────────────────────────────────────

    public void LogOrders() {
        Debug.Log($"WorkOrderManager: {orders.Count} orders");
        foreach (var o in orders)
            Debug.Log($"  p{o.priority} {o.type} bp={o.blueprint?.tile.x},{o.blueprint?.tile.y} stack={o.stack?.item?.name}");
    }
}

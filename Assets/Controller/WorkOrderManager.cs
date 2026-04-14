using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
    WorkOrderManager centralises work prioritisation. Instead of each animal independently
    scanning for tasks in a hardcoded order, work is registered here as prioritised WorkOrders.
    Animals call ChooseOrder(animal, priority) to get the best task at exactly that priority tier.

    Priority:  1 = highest (hauls unblocking a pending deconstruct)
               2 = construct, supply blueprint, harvest
               3 = haul, consolidate, haul to market, craft
               4 = deconstruct, research, haul from market
                   (HaulFromMarket sits below HaulToMarket so merchants deliver first
                    and opportunistically piggyback a pickup on the return leg; a pure
                    pickup trip only fires when there's nothing to deliver.)

    In Animal.ChooseTask the tiers are:
        ChooseOrder(1) → ChooseOrder(2) → ChooseOrder(3) → ChooseOrder(4)

    Blueprint-based work (Construct, Supply, Deconstruct) is registered explicitly when
    blueprint state changes.  Haul orders are registered when items land on floor inventories.
    Harvest orders are registered when plants are placed. isActive suppresses the order between grow cycles.
    Research is maintained as a single standing order whenever a lab building exists.
    Craft orders are registered for each placed workstation building (isWorkstation=true in buildingsDb).
    isActive on craft orders gates the building (e.g. pump inactive when no water below).
*/
public class WorkOrderManager : MonoBehaviour {
    public static WorkOrderManager instance { get; private set; }

    public enum OrderType { Haul, Harvest, Construct, SupplyBlueprint, Deconstruct, HaulToMarket, HaulFromMarket, Research, Craft, SupplyBuilding }

    public class WorkOrder {
        public OrderType type;
        public int priority;             // 1=highest, 4=lowest (also used as tier index: priority-1)
        public Func<Animal, Task> factory;
        public Blueprint blueprint;      // nullable; for blueprint-based orders & cleanup
        public ItemStack stack;          // nullable; for haul orders (dedup & priority promote)
        public Tile tile;                // nullable; for harvest orders (dedup & cleanup)
        public Inventory inv;            // nullable; for market haul orders (dedup & staleness)
        public Func<Animal, bool> canDo;        // null = any animal; job gate checked before factory
        public Func<Animal, float> getDistance; // null = 0; Chebyshev approx, used for within-tier sorting
        // Orders stay in the queue when claimed; res tracks how many animals are working this order.
        // capacity defaults to 1; set to N for multi-slot buildings (e.g. a 2-scientist lab).
        public Reservable res = new(1);
        // Optional: returns false to temporarily suppress this order without removing it from the queue.
        // Null means always active. ChooseOrder checks this before calling factory.
        // Example: harvest orders use () => plant.harvestable so the order stays alive across grow cycles
        // (plant.harvestFlagged gates the order's existence instead — Plant.SetHarvestFlagged registers /
        // unregisters as the flag flips, so unflagged plants carry no order at all).
        public Func<bool> isActive;
        public Building building;            // nullable; for Craft orders (dedup & cleanup)
    }

    // orders[0] = priority 1 (highest), orders[3] = priority 4 (lowest).
    // Each list is FIFO within its tier; distance sort happens at query time in ChooseOrder.
    private readonly List<WorkOrder>[] orders = Enumerable.Range(0, 4).Select(_ => new List<WorkOrder>()).ToArray();

    void Awake() {
        if (instance != null) { Debug.LogError("WorkOrderManager: multiple instances"); return; }
        instance = this;
    }

    void OnDestroy() { instance = null; }

    // ── QUERY ──────────────────────────────────────────────────────────────────────

    // Returns the best (distance-sorted) startable task at exactly this priority tier, or null.
    // Removes the claimed order on success. Call PruneStale() once before a ChooseOrder sequence.
    // Pass exclude to skip a specific order type (e.g. exclude craft orders when ChooseCraftTask handles them separately).
    public Task ChooseOrder(Animal animal, int priority, OrderType? exclude = null) {
        List<WorkOrder> tier = orders[priority - 1];
        var candidates = tier
            .Where(o => exclude == null || o.type != exclude)
            .Where(o => o.isActive == null || o.isActive())
            .Where(o => o.res.Available())
            .Where(o => o.canDo == null || o.canDo(animal))
            .Where(o => o.stack == null || o.stack.Available()) // skip haul orders whose stack is fully reserved
            .OrderBy(o => o.getDistance?.Invoke(animal) ?? 0f)
            .ToList();
        foreach (WorkOrder order in candidates) {
            Task task = order.factory(animal);
            if (task.Start()) {
                order.res.Reserve(animal.aName);
                task.workOrder = order;
                return task;
            }
        }
        return null;
    }

    // Returns the nearest available craft WorkOrder for the given building type name, without reserving.
    // Used by Animal.ChooseCraftTask() after recipe scoring selects which building type to target.
    // Caller reserves after task.Start() succeeds.
    public (WorkOrder order, Building building)? FindCraftOrder(string buildingTypeName, Animal animal) {
        foreach (var order in orders[2]
            .Where(o => o.type == OrderType.Craft)
            .Where(o => o.building.structType.name == buildingTypeName)
            .Where(o => o.isActive == null || o.isActive())
            .Where(o => o.res.Available())
            .Where(o => o.canDo == null || o.canDo(animal))
            .OrderBy(o => o.getDistance?.Invoke(animal) ?? 0f)) {
            return (order, order.building);
        }
        return null;
    }

    // Prune stale orders. Call once per ChooseTask before the ChooseOrder sequence.
    // Goal: get these warnings to 0 by fixing upstream registration/cleanup gaps.
    public void PruneStale() {
        PruneStaleHauls();
        PruneStaleMarketOrders();
    }

    // ── REGISTRATION ───────────────────────────────────────────────────────────────

    public bool RegisterConstruct(Blueprint bp) {
        // No reserved-guard here — PromoteToConstruct calls this while the prior supply task
        // is still cleaning up (res still reserved). Guard lives in Reconcile/AuditOrders only.
        if (HasOrderFor(OrderType.Construct, bp)) return false;
        Add(new WorkOrder {
            type = OrderType.Construct,
            priority = 2,
            factory = a => new ConstructTask(a, bp),
            blueprint = bp,
            canDo = a => a.job == bp.structType.job,
            getDistance = a => Mathf.Abs(bp.tile.x - a.x) + Mathf.Abs(bp.tile.y - a.y),
            isActive = () => !bp.IsSuspended()
        });
        return true;
    }

    public bool RegisterSupplyBlueprint(Blueprint bp) {
        // No reserved-guard — see RegisterConstruct comment.
        if (HasOrderFor(OrderType.SupplyBlueprint, bp)) return false;
        Add(new WorkOrder {
            type = OrderType.SupplyBlueprint,
            priority = 2,
            factory = a => new SupplyBlueprintTask(a, bp),
            blueprint = bp,
            canDo = a => a.job == bp.structType.job,
            getDistance = a => Mathf.Abs(bp.tile.x - a.x) + Mathf.Abs(bp.tile.y - a.y),
            isActive = () => !bp.IsSuspended()
        });
        return true;
    }

    // Called when the last supply delivery transitions a blueprint Receiving → Constructing.
    // Note: the SupplyBlueprint order may still have res.reserved == 1 at this point (the task
    // that delivered is still running). That's fine — we remove the order intentionally here, and
    // SupplyBlueprintTask.Cleanup will call workOrder.res.Unreserve() on the now-orphaned order
    // object, which is harmless.
    public void PromoteToConstruct(Blueprint bp) {
        orders[1].RemoveAll(o => o.blueprint == bp && o.type == OrderType.SupplyBlueprint);
        RegisterConstruct(bp);
    }

    public bool RegisterDeconstruct(Blueprint bp) {
        // No reserved-guard — see RegisterConstruct comment.
        if (HasOrderFor(OrderType.Deconstruct, bp)) return false;
        // For plants, structType.job is the *harvest* job (logger / farmer), not a construction
        // job — there's no hauler fallback on PlantType.OnDeserialized like there is on StructType.
        // Without the hauler clause below, an unmanned job (e.g. no logger) would strand the
        // deconstruct order indefinitely. Haulers can always pitch in on plant removal.
        Add(new WorkOrder {
            type = OrderType.Deconstruct,
            priority = 4,
            factory = a => new ConstructTask(a, bp, deconstructing: true),
            blueprint = bp,
            canDo = a => a.job == bp.structType.job
                      || (bp.structType.isPlant && a.job.name == "hauler"),
            getDistance = a => Mathf.Abs(bp.tile.x - a.x) + Mathf.Abs(bp.tile.y - a.y)
        });
        PromoteHaulsFor(bp);
        return true;
    }

    // Registers a haul order for a floor ItemStack (priority 3).
    // Deduplicates by stack reference; no-ops on null/empty/actively-reserved stacks.
    // Returns true if a new order was inserted.
    public bool RegisterHaul(ItemStack stack) {
        if (!StackNeedsHaulOrder(stack)) return false;
        // Haul orders live at p1 or p3 — check both tiers.
        if (orders[0].Exists(o => o.type == OrderType.Haul && o.stack == stack)) return false;
        if (orders[2].Exists(o => o.type == OrderType.Haul && o.stack == stack)) return false;
        Add(new WorkOrder {
            type = OrderType.Haul,
            priority = 3,
            factory = a => {
                if (a.nav.FindPathToStorage(stack.item).path != null) return new HaulTask(a, stack);
                return new ConsolidateTask(a, stack);
            },
            stack = stack,
            canDo = a => a.job.name == "hauler",
            getDistance = a => Mathf.Abs(stack.inv.x - a.x) + Mathf.Abs(stack.inv.y - a.y)
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

    // Promotes an existing haul order for this stack to p1, or creates a new p1 haul order
    // if none exists. P1 hauls block deconstruct and use the same haul-or-consolidate factory.
    private void PromoteOrCreateHaul(ItemStack stack, Blueprint source) {
        // Haul orders can be at p1 or p3; search both tiers.
        WorkOrder existing = orders[0].Find(o => o.type == OrderType.Haul && o.stack == stack)
            ?? orders[2].Find(o => o.type == OrderType.Haul && o.stack == stack);
        if (existing != null) {
            if (existing.priority == 1) return; // already p1
            orders[existing.priority - 1].Remove(existing);
            existing.priority = 1;
            existing.blueprint = source;
            orders[0].Add(existing);
        } else {
            orders[0].Add(new WorkOrder {
                type = OrderType.Haul,
                priority = 1,
                factory = a => {
                    if (a.nav.FindPathToStorage(stack.item).path != null) return new HaulTask(a, stack);
                    return new ConsolidateTask(a, stack);
                },
                stack = stack,
                blueprint = source,
                canDo = a => a.job.name == "hauler",
                getDistance = a => Mathf.Abs(stack.inv.x - a.x) + Mathf.Abs(stack.inv.y - a.y)
            });
        }
    }

    // Registers a haul order for a storage ItemStack whose item is disallowed (priority 3).
    // Unlike floor hauls, no ConsolidateTask fallback — the order stays in queue until
    // destination storage exists (HaulTask.Initialize returns false until then).
    // Returns true if a new order was inserted.
    public bool RegisterStorageEvictionHaul(ItemStack stack) {
        if (!StackNeedsHaulOrder(stack)) return false;
        if (stack.inv.invType != Inventory.InvType.Storage) {
            Debug.LogError($"WOM RegisterStorageEvictionHaul: called on non-storage stack (type={stack.inv.invType})");
            return false;
        }
        if (orders[0].Exists(o => o.type == OrderType.Haul && o.stack == stack)) return false;
        if (orders[2].Exists(o => o.type == OrderType.Haul && o.stack == stack)) return false;
        Add(new WorkOrder {
            type = OrderType.Haul,
            priority = 3,
            factory = a => new HaulTask(a, stack),
            stack = stack,
            canDo = a => a.job.name == "hauler",
            getDistance = a => Mathf.Abs(stack.inv.x - a.x) + Mathf.Abs(stack.inv.y - a.y)
        });
        return true;
    }

    // Creates or removes market haul orders to match current inventory vs targets.
    // Call immediately whenever the market inventory changes or a target is updated.
    // Orders are removed eagerly even if in-flight: the active task still holds a workOrder
    // reference and base.Cleanup() will Unreserve() it safely even after removal from the queue.
    //
    // Market haul orders are suppressed for 3s after the player manually edits a target,
    // so multiple target edits can settle before merchants are dispatched. Applies to both
    // directions — an edit can flip an item from "below target" to "above" or vice versa.
    const float marketHaulDelayAfterTargetChange = 3f;
    public void UpdateMarketOrders(Inventory marketInv) {
        float timeSinceTargetEdit = World.instance != null
            ? World.instance.timer - marketInv.lastTargetManualUpdateTimer
            : float.MaxValue;
        bool targetRecentlyEdited = timeSinceTargetEdit < marketHaulDelayAfterTargetChange;

        if (!targetRecentlyEdited && MarketNeedsHaulTo(marketInv) && !orders[2].Exists(o => o.type == OrderType.HaulToMarket && o.inv == marketInv))
            Add(new WorkOrder {
                type = OrderType.HaulToMarket,
                priority = 3,
                factory = a => new HaulToMarketTask(a),
                inv = marketInv,
                canDo = a => a.job.name == "merchant"
            });
        // HaulFromMarket sits at priority 4 so merchants exhaust all outbound delivery work
        // at p3 before considering a pure pickup trip. Combined with HaulToMarketTask's
        // opportunistic piggyback, most excess gets hauled back on the return leg of a
        // delivery — a pure HaulFromMarket only fires when there is genuinely nothing to
        // deliver. Merchants are the only job that matches this order's canDo, so sharing
        // the p4 tier with Research/Deconstruct is collision-free.
        if (!targetRecentlyEdited && MarketNeedsHaulFrom(marketInv) && !orders[3].Exists(o => o.type == OrderType.HaulFromMarket && o.inv == marketInv))
            Add(new WorkOrder {
                type = OrderType.HaulFromMarket,
                priority = 4,
                factory = a => new HaulFromMarketTask(a),
                inv = marketInv,
                canDo = a => a.job.name == "merchant"
            });
        // Remove orders whose need has gone away — HaulTo lives at p3 (index 2), HaulFrom at p4 (index 3).
        orders[2].RemoveAll(o => o.inv == marketInv && o.type == OrderType.HaulToMarket   && !MarketNeedsHaulTo(marketInv));
        orders[3].RemoveAll(o => o.inv == marketInv && o.type == OrderType.HaulFromMarket && !MarketNeedsHaulFrom(marketInv));
    }

    // Returns the HaulFromMarket work order for this market (at any tier), or null.
    // Used by HaulToMarketTask.TryAppendPickup to claim the order so a second merchant
    // won't race the piggyback pickup. Scans all tiers defensively — the order's canonical
    // tier is p4 (index 3), but keep this agnostic so a tier change doesn't break the lookup.
    public WorkOrder FindMarketHaulFromOrder(Inventory marketInv) {
        foreach (var tier in orders) {
            WorkOrder o = tier.Find(x => x.type == OrderType.HaulFromMarket && x.inv == marketInv);
            if (o != null) return o;
        }
        return null;
    }

    public bool RegisterHarvest(Plant plant) {
        if (plant == null || plant.tile == null) return false;
        if (orders[1].Exists(o => o.type == OrderType.Harvest && o.tile == plant.tile)) return false;
        Tile tile = plant.tile;
        Job harvestJob = plant.plantType.job;
        Add(new WorkOrder {
            type = OrderType.Harvest,
            priority = 2,
            factory = a => new HarvestTask(a, tile),
            tile = tile,
            res = new(plant.plantType.capacity > 0 ? plant.plantType.capacity : 1),
            // Gated on ripeness only — harvest orders only exist while the plant is flagged,
            // so no need to re-check harvestFlagged here. SetHarvestFlagged(false) removes
            // the order; dormancy across grow cycles comes from `harvestable`.
            isActive = () => plant.harvestable,
            canDo = a => a.job == harvestJob,
            getDistance = a => Mathf.Abs(tile.x - a.x) + Mathf.Abs(tile.y - a.y)
        });
        return true;
    }

    // If a HarvestTask is currently running on this plant, the task keeps its reservation
    // and finishes this cycle (same graceful-completion behavior as cancelling a blueprint
    // mid-construction) — only the standing queue entry is cleaned up.
    public bool UnregisterHarvest(Plant plant) {
        if (plant == null || plant.tile == null) return false;
        return orders[1].RemoveAll(o => o.type == OrderType.Harvest && o.tile == plant.tile) > 0;
    }

    // Registers a Research order for a specific lab building if it's unreserved and no order exists for it.
    // Returns true if a new order was inserted.
    public bool RegisterResearch(Building lab) {
        if (lab == null) return false;
        if (orders[3].Exists(o => o.type == OrderType.Research && o.tile == lab.tile)) return false;
        Add(new WorkOrder {
            type = OrderType.Research,
            priority = 4,
            factory = a => {
                var task = new ResearchTask(a, lab);
                task.maintenanceTargetId = ResearchSystem.instance?.ClaimMaintenanceTarget(a) ?? -1;
                return task;
            },
            tile = lab.tile,
            res = new(Mathf.Max(1, lab.structType.capacity)),
            isActive = () => !lab.disabled,
            canDo = a => a.job.name == "scientist",
            getDistance = a => Mathf.Abs(lab.tile.x - a.x) + Mathf.Abs(lab.tile.y - a.y)
        });
        return true;
    }

    // Registers a Craft order for a workstation building. One order per building instance.
    // The order stays in the queue permanently; isActive gates it when the building is inactive.
    // Reads building.workstation for the player-adjustable worker limit.
    // Returns true if a new order was inserted.
    public bool RegisterWorkstation(Building building) {
        if (building?.workstation == null) return false;
        if (orders[2].Exists(o => o.type == OrderType.Craft && o.building == building)) return false;
        // canDo checks recipe job, not construction job (structType.job = njob = who builds it).
        // PickRecipeForBuilding filters by recipe.tile, so the correct gate is: does this animal's
        // job have any recipe for this building type?
        string buildingName = building.structType.name;
        var ws = building.workstation;
        var res = new Reservable(ws.capacity);
        res.effectiveCapacity = ws.workerLimit;
        Add(new WorkOrder {
            type = OrderType.Craft,
            priority = 3,
            factory = a => new CraftTask(a, building),
            building = building,
            res = res,
            isActive = () => !building.disabled && building.IsActive(),
            canDo = a => Array.Exists(a.job.recipes, r => r != null && r.tile == buildingName),
            getDistance = a => Mathf.Abs(building.workTile.x - a.x) + Mathf.Abs(building.workTile.y - a.y)
        });
        return true;
    }

    // Sets the player-adjustable worker limit for a workstation. Syncs both the WOM order's
    // Reservable (runtime enforcement) and building.workstation (persistence). Clamped to [0, capacity].
    public void SetWorkstationCapacity(Building building, int effectiveCapacity) {
        if (building?.workstation == null || building.workstation.capacity <= 1) return;
        var order = FindOrdersForBuilding(building)
            .FirstOrDefault(o => o.type == OrderType.Craft);
        if (order == null) {
            Debug.LogError($"SetWorkstationCapacity: no Craft order for {building.structType.name}");
            return;
        }
        int clamped = Mathf.Clamp(effectiveCapacity, 0, order.res.capacity);
        order.res.effectiveCapacity = clamped;
        building.workstation.workerLimit = clamped;
    }

    // Removes the Craft order for a specific building (call when building is deconstructed).
    public void RemoveWorkstationOrders(Building building) {
        orders[2].RemoveAll(o => o.type == OrderType.Craft && o.building == building);
    }

    // Registers a standing SupplyBuilding order for a building that has a Reservoir.
    // The order is always present in the queue; isActive suppresses it when the reservoir is already at target.
    // Haulers fulfill it by delivering items to building.reservoir.inv.
    // Returns true if a new order was inserted.
    public bool RegisterFuelSupply(Building building) {
        if (building?.reservoir == null) return false;
        if (orders[2].Exists(o => o.type == OrderType.SupplyBuilding && o.building == building)) return false;
        var reservoir = building.reservoir;
        Add(new WorkOrder {
            type       = OrderType.SupplyBuilding,
            priority   = 3,
            factory    = a => new SupplyFuelTask(a, building),
            building   = building,
            isActive   = () => !building.disabled && reservoir.NeedsSupply(),
            canDo      = a => a.job.name == "hauler",
            getDistance = a => Mathf.Abs(building.x - a.x) + Mathf.Abs(building.y - a.y)
        });
        return true;
    }

    // Removes the fuel supply order for a building (call when building is destroyed).
    public void RemoveFuelSupplyOrders(Building building) {
        orders[2].RemoveAll(o => o.type == OrderType.SupplyBuilding && o.building == building);
    }

    // Registers all standing WOM orders appropriate for a building (research, craft, fuel supply).
    // Called from StructController.Construct after a building is placed. Each Register* method
    // self-guards with dedup checks, so this is safe to call unconditionally.
    // Note: Plant harvest orders are registered via Plant.OnPlaced() on the gameplay path, or Reconcile() on load/worldgen.
    public void RegisterOrdersFor(Building building) {
        if (building == null) return;
        if (building.structType.name == "laboratory")
            RegisterResearch(building);
        if (building.workstation != null)
            RegisterWorkstation(building);
        if (building.reservoir != null)
            RegisterFuelSupply(building);
    }

    // ── REMOVAL ────────────────────────────────────────────────────────────────────

    // Remove haul orders for a specific stack (call when the stack becomes empty).
    public void RemoveHaulForStack(ItemStack stack) {
        foreach (var tier in orders)
            tier.RemoveAll(o => o.type == OrderType.Haul && o.stack == stack);
    }

    // Remove all orders associated with this blueprint (used on blueprint cancel/destroy).
    public void RemoveForBlueprint(Blueprint bp) {
        foreach (var tier in orders) tier.RemoveAll(o => o.blueprint == bp);
    }

    // Remove harvest order for a tile (used when plant is destroyed before harvest).
    public void RemoveForTile(Tile tile) {
        foreach (var tier in orders) tier.RemoveAll(o => o.tile == tile);
    }

    // Clears all orders — call from ClearWorld() before loading a new save.
    public void ClearAllOrders() {
        foreach (var tier in orders) tier.Clear();
    }

    // Remove stale haul orders whose stacks have been emptied. Skip in-flight orders (res.reserved > 0)
    // so we don't interrupt an active task; it will release when its Cleanup runs.
    // Goal: fix upstream gaps so this never fires (LogWarning will tell you when it does).
    private void PruneStaleHauls() {
        foreach (var tier in orders)
            tier.RemoveAll(o => {
                if (o.type != OrderType.Haul || o.res.reserved != 0) return false;
                if (o.stack == null || o.stack.item == null || o.stack.quantity == 0) {
                    string stackDesc = o.stack == null ? "null stack"
                        : $"item={o.stack.item?.name ?? "null"} qty={o.stack.quantity} res={o.stack.resAmount} inv=({o.stack.inv?.x},{o.stack.inv?.y})";
                    string bpDesc = o.blueprint == null ? "" : $" bp={o.blueprint.structType?.name}@({o.blueprint.x},{o.blueprint.y})";
                    Debug.LogWarning($"WOM prune: stale haul order — {stackDesc}{bpDesc} — order was never cleaned up");
                    return true;
                }
                return false;
            });
    }

    // Remove market haul orders where the need has gone away (safety net for edge cases
    // like a market being destroyed mid-task). UpdateMarketOrders now removes eagerly, so
    // this should rarely fire; LogWarning will tell you when it does.
    // HaulToMarket lives at p3 (index 2); HaulFromMarket lives at p4 (index 3).
    private void PruneStaleMarketOrders() {
        orders[2].RemoveAll(o => {
            if (o.inv == null) return false;
            if (o.type == OrderType.HaulToMarket && !MarketNeedsHaulTo(o.inv)) {
                Debug.LogWarning($"WOM prune: stale HaulToMarket order for market at ({o.inv.x},{o.inv.y})");
                return true;
            }
            return false;
        });
        orders[3].RemoveAll(o => {
            if (o.inv == null) return false;
            if (o.type == OrderType.HaulFromMarket && !MarketNeedsHaulFrom(o.inv)) {
                Debug.LogWarning($"WOM prune: stale HaulFromMarket order for market at ({o.inv.x},{o.inv.y})");
                return true;
            }
            return false;
        });
    }

    // ── RECONCILE / AUDIT ──────────────────────────────────────────────────────────

    // "Needs a haul order" predicate: stack has items and no order already exists for it.
    // The dedup check in RegisterHaul (Exists o.stack == stack) handles in-flight orders.
    private static bool StackNeedsHaulOrder(ItemStack stack) =>
        stack != null && stack.item != null && stack.quantity > 0;

    // Group items are never physical (see SPEC-trading: "Market targets are leaf-only").
    // Filter out group keys so their 0-default targets don't spuriously trigger haul orders.
    private static bool MarketNeedsHaulTo(Inventory inv) =>
        inv.targets != null && inv.targets.Any(kvp => !kvp.Key.IsGroup && inv.Quantity(kvp.Key) < kvp.Value);
    private static bool MarketNeedsHaulFrom(Inventory inv) =>
        inv.targets != null && inv.targets.Any(kvp => !kvp.Key.IsGroup && inv.Quantity(kvp.Key) > kvp.Value);

    private enum ScanMode { Repair, Audit }

    // Unified scan: direction 1 checks that every world object that needs an order has one.
    //   Repair mode: registers missing orders (Reconcile). silent=true suppresses warnings (used at load time).
    //   Audit mode:  logs errors for violations AND checks direction 2 (orphaned orders).
    // When adding a new order type, add both direction-1 and direction-2 checks here.
    private void ScanOrders(ScanMode mode, bool silent = false) {
        bool repair = mode == ScanMode.Repair;

        // ── Harvest ──
        // Only flagged plants should have an order. Unflagged plants legitimately have none.
        foreach (Plant p in PlantController.instance.Plants) {
            if (!p.harvestFlagged) continue;
            bool has = orders[1].Exists(o => o.type == OrderType.Harvest && o.tile == p.tile);
            if (!has) {
                if (repair) {
                    RegisterHarvest(p);
                    if (!silent) Debug.LogWarning($"WOM reconcile: registered missing harvest order for flagged {p.plantType.name} at ({p.x},{p.y})");
                } else {
                    Debug.LogError($"WOM audit: flagged plant at ({p.x},{p.y}) has no harvest order");
                }
            }
        }

        // ── Blueprints ──
        var bps = StructController.instance.GetBlueprints();
        foreach (Blueprint bp in bps) {
            if (bp.IsSuspended() || bp.disabled) continue; // suspended/disabled blueprints intentionally have no orders
            if (repair) {
                // For Receiving blueprints, heal state to Constructing if fully delivered (can happen
                // after save/load when LockGroupCostsAfterDelivery isn't re-run).
                bool inserted;
                if (bp.state == Blueprint.BlueprintState.Receiving && bp.IsFullyDelivered()) {
                    bp.state = Blueprint.BlueprintState.Constructing;
                    inserted = RegisterConstruct(bp);
                } else {
                    inserted = bp.state switch {
                        Blueprint.BlueprintState.Constructing   => RegisterConstruct(bp),
                        Blueprint.BlueprintState.Receiving      => RegisterSupplyBlueprint(bp),
                        Blueprint.BlueprintState.Deconstructing => RegisterDeconstruct(bp),
                        _ => false
                    };
                }
                if (inserted && !silent)
                    Debug.LogWarning($"WOM reconcile: registered missing {bp.state} order for {bp.structType.name} at ({bp.x},{bp.y})");
            } else {
                OrderType expected = bp.state switch {
                    Blueprint.BlueprintState.Constructing   => OrderType.Construct,
                    Blueprint.BlueprintState.Receiving      => OrderType.SupplyBlueprint,
                    Blueprint.BlueprintState.Deconstructing => OrderType.Deconstruct,
                    _ => (OrderType)(-1)
                };
                if (expected != (OrderType)(-1) && !HasOrderFor(expected, bp))
                    Debug.LogError($"WOM audit: blueprint {bp.structType.name} at ({bp.x},{bp.y}) missing {expected} order");
            }
        }

        // ── Floor hauls ──
        if (InventoryController.instance.byType.TryGetValue(Inventory.InvType.Floor, out var floors)) {
            foreach (Inventory inv in floors)
                foreach (ItemStack stack in inv.itemStacks) {
                    if (!StackNeedsHaulOrder(stack)) continue;
                    bool has = orders[0].Exists(o => o.type == OrderType.Haul && o.stack == stack)
                            || orders[2].Exists(o => o.type == OrderType.Haul && o.stack == stack);
                    if (!has) {
                        if (repair) {
                            RegisterHaul(stack);
                            if (!silent) Debug.LogWarning($"WOM reconcile: registered missing haul order for {stack.item?.name} at ({inv.x},{inv.y})");
                        } else {
                            Debug.LogError($"WOM audit: floor stack {stack.item?.name} at ({inv.x},{inv.y}) has no haul order");
                        }
                    }
                }
        }

        // ── Storage eviction hauls (includes liquid storage) ──
        foreach (var evictType in new[] { Inventory.InvType.Storage }) {
            if (InventoryController.instance.byType.TryGetValue(evictType, out var storages))
                foreach (Inventory inv in storages)
                    foreach (ItemStack stack in inv.itemStacks) {
                        if (stack.item == null || stack.quantity <= 0 || inv.allowed[stack.item.id] != false) continue;
                        bool has = orders[0].Exists(o => o.type == OrderType.Haul && o.stack == stack)
                                || orders[2].Exists(o => o.type == OrderType.Haul && o.stack == stack);
                        if (!has) {
                            if (repair) {
                                RegisterStorageEvictionHaul(stack);
                                if (!silent) Debug.LogWarning($"WOM reconcile: registered missing eviction haul for {stack.item.name} at ({inv.x},{inv.y}) [{evictType}]");
                            } else {
                                Debug.LogError($"WOM audit: disallowed {evictType} stack {stack.item.name} at ({inv.x},{inv.y}) has no haul order");
                            }
                        }
                    }
        }

        // ── Market hauls ──
        if (InventoryController.instance.byType.TryGetValue(Inventory.InvType.Market, out var markets)) {
            foreach (Inventory inv in markets)
                UpdateMarketOrders(inv);
        }

        // ── Research ──
        if (Db.structTypeByName.TryGetValue("laboratory", out var labSt)) {
            var allLabs = StructController.instance.GetByType(labSt) ?? new List<Structure>();
            foreach (Structure s in allLabs) {
                if (s is not Building lab) continue;
                bool has = orders[3].Exists(o => o.type == OrderType.Research && o.tile == lab.tile);
                if (!has) {
                    if (repair) {
                        RegisterResearch(lab);
                        if (!silent) Debug.LogWarning($"WOM reconcile: registered missing Research order for lab at ({lab.tile.x},{lab.tile.y})");
                    } else {
                        Debug.LogError($"WOM audit: lab at ({lab.tile.x},{lab.tile.y}) has no Research order");
                    }
                }
            }
        }

        // ── Craft (workstations) ──
        foreach (Structure s in StructController.instance.GetStructures()) {
            if (!s.structType.isWorkstation || s is not Building ws) continue;
            bool has = orders[2].Exists(o => o.type == OrderType.Craft && o.building == ws);
            if (!has) {
                if (repair) {
                    RegisterWorkstation(ws);
                    if (!silent) Debug.LogWarning($"WOM reconcile: registered missing Craft order for {ws.structType.name} at ({ws.x},{ws.y})");
                } else {
                    Debug.LogError($"WOM audit: workstation {ws.structType.name} at ({ws.x},{ws.y}) has no Craft order");
                }
            }
        }

        // ── Fuel supply ──
        foreach (Structure s in StructController.instance.GetStructures()) {
            if (s is not Building fb || fb.reservoir == null) continue;
            bool has = orders[2].Exists(o => o.type == OrderType.SupplyBuilding && o.building == fb);
            if (!has) {
                if (repair) {
                    RegisterFuelSupply(fb);
                    if (!silent) Debug.LogWarning($"WOM reconcile: registered missing SupplyBuilding order for {fb.structType.name} at ({fb.x},{fb.y})");
                } else {
                    Debug.LogError($"WOM audit: fuel building {fb.structType.name} at ({fb.x},{fb.y}) has no SupplyBuilding order");
                }
            }
        }

        // ── Direction 2: orphaned orders (audit only) ──
        if (mode == ScanMode.Audit) {
            // Prune stale haul orders first so timing artifacts don't fire.
            PruneStaleHauls();

            // Harvest: every harvest order must reference a tile with a living, flagged plant.
            // Unflagged plants should have had their order removed by SetHarvestFlagged(false).
            foreach (WorkOrder o in orders[1]) {
                if (o.type != OrderType.Harvest) continue;
                Plant p = o.tile?.plant;
                if (p == null) Debug.LogError($"WOM audit: harvest order at ({o.tile?.x},{o.tile?.y}) has no plant");
                else if (!p.harvestFlagged) Debug.LogError($"WOM audit: harvest order at ({o.tile?.x},{o.tile?.y}) references unflagged plant");
            }

            // Blueprints: every blueprint order must reference a live blueprint
            foreach (WorkOrder o in AllOrders())
                if (o.blueprint != null && !bps.Contains(o.blueprint))
                    Debug.LogError($"WOM audit: {o.type} order references a blueprint not in StructController");

            // Hauls: every haul order must reference a valid stack
            foreach (WorkOrder o in AllOrders())
                if (o.type == OrderType.Haul && !StackNeedsHaulOrder(o.stack))
                    Debug.LogError($"WOM audit: haul order references stale/empty stack ({o.stack?.item?.name})");

            // Research: every Research order must reference a live lab
            if (Db.structTypeByName.TryGetValue("laboratory", out var labStAudit)) {
                foreach (WorkOrder o in orders[3])
                    if (o.type == OrderType.Research
                        && !(o.tile?.building is Building b && b.structType == labStAudit))
                        Debug.LogError($"WOM audit: Research order at ({o.tile?.x},{o.tile?.y}) has no valid lab");
            }

            // Craft: every Craft order must reference a live building
            foreach (WorkOrder o in orders[2])
                if (o.type == OrderType.Craft && (o.building == null || o.building.go == null))
                    Debug.LogError($"WOM audit: Craft order references a destroyed building ({o.building?.structType?.name})");

            // FuelSupply: every SupplyBuilding order must reference a live building
            foreach (WorkOrder o in orders[2])
                if (o.type == OrderType.SupplyBuilding && (o.building == null || o.building.go == null))
                    Debug.LogError($"WOM audit: SupplyBuilding order references a destroyed building ({o.building?.structType?.name})");

            int total = orders.Sum(t => t.Count);
            Debug.Log($"WOM audit complete. {total} orders.");
        }
    }

    // Called periodically as a safety net, and once at load time to register all orders.
    // silent=true suppresses warnings (used during load where every registration is expected).
    public void Reconcile(bool silent = false) => ScanOrders(ScanMode.Repair, silent);

    // Ctrl+D dev tool: checks invariants in both directions and logs violations.
    public void AuditOrders() => ScanOrders(ScanMode.Audit);

    // ── INSPECT ────────────────────────────────────────────────────────────────────

    // Returns the first active WorkOrder associated with a blueprint, or null.
    public WorkOrder FindOrderForBlueprint(Blueprint bp) {
        foreach (var tier in orders)
            foreach (var o in tier)
                if (o.blueprint == bp) return o;
        return null;
    }

    // Returns the first active WorkOrder associated with a floor ItemStack, or null.
    public WorkOrder FindOrderForStack(ItemStack stack) {
        foreach (var tier in orders)
            foreach (var o in tier)
                if (o.stack == stack) return o;
        return null;
    }

    // Returns all active WorkOrders keyed to a specific tile (harvest, research).
    public IEnumerable<WorkOrder> FindOrdersForTile(Tile tile) {
        foreach (var tier in orders)
            foreach (var o in tier)
                if (o.tile == tile) yield return o;
    }

    // Returns all active WorkOrders keyed to a specific inventory (market hauls).
    public IEnumerable<WorkOrder> FindOrdersForInv(Inventory inv) {
        foreach (var tier in orders)
            foreach (var o in tier)
                if (o.inv == inv) yield return o;
    }

    // Returns all active WorkOrders keyed to a specific building (craft orders).
    public IEnumerable<WorkOrder> FindOrdersForBuilding(Building building) {
        foreach (var tier in orders)
            foreach (var o in tier)
                if (o.building == building) yield return o;
    }

    // ── INTERNAL ───────────────────────────────────────────────────────────────────

    private void Add(WorkOrder order) => orders[order.priority - 1].Add(order);

    private IEnumerable<WorkOrder> AllOrders() => orders.SelectMany(t => t);

    private bool HasOrderFor(OrderType type, Blueprint bp) {
        int tier = type switch {
            OrderType.Construct       => 1,   // p2
            OrderType.SupplyBlueprint => 1,   // p2
            OrderType.Deconstruct     => 3,   // p4
            _ => -1
        };
        if (tier < 0) return false;
        return orders[tier].Exists(o => o.type == type && o.blueprint == bp);
    }

    // ── DEBUG ──────────────────────────────────────────────────────────────────────

    public void LogOrders() {
        int total = orders.Sum(t => t.Count);
        Debug.Log($"WorkOrderManager: {total} orders");
        foreach (var o in AllOrders())
            Debug.Log($"  p{o.priority} {o.type} res={o.res.reserved}/{o.res.capacity} bp={o.blueprint?.tile.x},{o.blueprint?.tile.y} stack={o.stack?.item?.name}");
    }
}

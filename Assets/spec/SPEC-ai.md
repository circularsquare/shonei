# Shonei — Animal AI & WorkOrderManager

## Animal AI

### States

```
Idle → Working
     → Moving → (arrives) → back to Working or Idle
Idle → Eeping (sleeping)
Any  → Falling (involuntary; interrupts current task) → Idle on landing
```

- **Idle**: calls `ChooseTask()`, selects best recipe by score
- **Working**: executes current objective (craft, harvest, build, sleep)
- **Moving**: navigates path via A*; calls `OnArrival()` on completion
- **Eeping**: sleeps at home, restores sleep meter; can trigger reproduction
- **Falling**: triggered when `!preventFall && !standable`; moves mouse straight down each frame using gravity (`World.fallGravity`); snaps to tile center on landing; bypasses task and nav systems entirely

### Needs

| Need | Effect |
|------|--------|
| Hunger | Reduces efficiency; eating wheat restores |
| Sleep | Reduces efficiency; sleeping at home restores |
| Efficiency | `eating.Efficiency() * eeping.Efficiency()` — scales move speed and work rate |

### Task dispatch (ChooseTask priority order)

`Animal.ChooseTask()` runs top-to-bottom when an animal is Idle:

1. **Survival** (always first): drop inventory → eat if hungry → sleep if eepy at night → equip tool/food
2. **Work orders, interleaved with CraftTask:**
   - `wom.PruneStale()` — call once before the tier sequence
   - `wom.ChooseOrder(this, 1)` — hauls unblocking a deconstruct
   - `wom.ChooseOrder(this, 2)` — construct / supply / harvest
   - `wom.ChooseOrder(this, 3)` — haul / market
   - `CraftTask` (not in WOM — runs between p3 and p4)
   - `wom.ChooseOrder(this, 4)` — deconstruct, research

`ChooseOrder(animal, priority)` only considers the single requested tier, filters to `res.Available()` orders, additionally skips haul orders where `stack.Available()` is false (stack fully reserved by in-flight tasks), distance-sorts remaining candidates, and on success calls `order.res.Reserve()` and assigns `task.workOrder = order`. Orders are **never removed when claimed** — they stay in the queue so they can be re-claimed after the task ends. WOM tasks are job-filtered via the `canDo` predicate on each `WorkOrder`.

### Task System

Tasks decompose into an ordered queue of Objectives. Each task:
1. **Initialize** — validates feasibility, creates objective queue, reserves resources. Return `false` to abort; objectives added before the check are discarded (see `Task.Start()`).
2. **Execute** — runs objectives sequentially via `StartNextObjective()`
3. **Complete** — cleanup (unreserve), return to Idle
4. **Fail** — same cleanup, return to Idle

`Task.Start()` only calls `StartNextObjective()` if `Initialize()` returned `true`. This prevents half-built objective queues from executing when a late reservation check fails.

**Tasks (implemented):**

| Task | Source | Job | Description |
|------|--------|-----|-------------|
| `CraftTask` | between p3/p4 | any | Navigate to station, fetch inputs, work, drop outputs |
| `HarvestTask` | WOM p2 | plant's `njob` | Navigate to plant, harvest when ready, drop products |
| `HaulTask` | WOM p1/p3 | hauler | Move a floor stack to storage (or consolidate if no storage) |
| `HaulToMarketTask` | WOM p3 | merchant | Haul items from storage to the market building to meet targets |
| `HaulFromMarketTask` | WOM p3 | merchant | Haul excess items from market back to storage |
| `ConstructTask` | WOM p2/p4 | building's `njob` | Build or deconstruct a blueprint |
| `SupplyBlueprintTask` | WOM p2 | building's `njob` | Deliver materials to an incomplete blueprint |
| `ResearchTask` | WOM p4 | scientist | Navigate to a specific lab, work in loops |
| `ObtainTask` | survival | any | Fetch a specific item (food/equip) |
| `EepTask` | survival | any | Navigate home and sleep |
| `DropTask` | survival | any | Drop excess main inventory |
| `GoTask` | survival | any | Navigate to a tile |

**Objectives (atomic steps):**
`GoObjective`, `FetchObjective`, `DeliverObjective`, `DeliverToBlueprintObjective`, `WorkObjective`, `HarvestObjective`, `ConstructObjective`, `EepObjective`, `DropObjective`, `ResearchObjective`

### Job System

Each animal has one Job. Jobs filter which WOM orders and fallback tasks an animal can take. For crafting, recipe selection uses a score that balances global item quantities against configurable targets.

---

## WorkOrderManager

`WorkOrderManager` (singleton MonoBehaviour) is the central registry of pending work. Instead of animals scanning the world themselves, work is pushed in as prioritised `WorkOrder` objects.

Orders are stored as `List<WorkOrder>[] orders` — an array of four lists, one per priority tier (index = priority - 1). Within each tier, FIFO order is preserved.

Each `WorkOrder` carries a `Reservable res` (default capacity 1). **Orders stay in the queue permanently** — they are only removed when the underlying need goes away (blueprint destroyed, plant gone, etc.), not when claimed. `ChooseOrder` filters to `o.res.Available()`, tries each factory, and on success calls `order.res.Reserve(); task.workOrder = order`. When the task ends (success or fail), `Task.Cleanup()` calls `workOrder?.res.Unreserve(); workOrder = null`, making the order available for the next animal.

This replaces the old pattern where claiming removed the order and `Cleanup` had to re-register it.

### Priority tiers

| Priority | Order types |
|----------|-------------|
| 1 | Haul unblocking a pending deconstruct |
| 2 | Construct, SupplyBlueprint, Harvest |
| 3 | Haul (general floor items), HaulToMarket, HaulFromMarket |
| 4 | Deconstruct, Research |

CraftTask is not in WOM — it runs in `ChooseTask` between tiers 3 and 4.

### Registration rules

Orders are created once when the need first arises and removed only when the need permanently goes away. While a task is executing, the order stays in the queue with `res.reserved > 0`; when the task ends, `Cleanup()` releases it back to `res.reserved == 0` and it becomes claimable again.

| Order type | Registered when | Removed when |
|------------|----------------|--------------|
| `Construct` | Blueprint created with no costs; or `PromoteToConstruct` when last supply delivered | Blueprint completed or destroyed (`RemoveForBlueprint`) |
| `SupplyBlueprint` | Blueprint created with costs | Promoted to Construct; blueprint destroyed |
| `Deconstruct` | `Blueprint.CreateDeconstructBlueprint()` called | Blueprint completed or destroyed |
| `Haul` (priority 3) | Items land on a floor inventory (`Inventory.Produce` or `MoveItemTo` destination hooks) | Eagerly: `MoveItemTo` source scan when a stack goes null; `Inventory.Decay()` when decay empties a stack; `Inventory.Destroy()` when floor inv is torn down. `PruneStaleHauls()` is a warning-level safety net only. |
| `Haul` (priority 1) | `PromoteHaulsFor(bp)` promotes or creates a p1 haul for blocking items during deconstruct | `RemoveForBlueprint` on cancel/complete |
| `Harvest` | Plant placed; `isActive = () => plant.harvestable` suppresses the order between grow cycles | Plant destroyed (`RemoveForTile`) |
| `HaulToMarket` | `UpdateMarketOrders(inv)` when any item is below its target | `UpdateMarketOrders(inv)` when target is met; called from `MoveItemTo` (market dest/source) and `ItemDisplay` target buttons. `PruneStaleMarketOrders()` is a warning-level safety net only. |
| `HaulFromMarket` | `UpdateMarketOrders(inv)` when any item exceeds its target | `UpdateMarketOrders(inv)` when excess is cleared |
| `Research` | `StructController.Construct()` when a lab is placed | Lab deconstructed (`RemoveForTile`) |

Research and Harvest orders are **per-source** (one order per lab/plant, keyed by `o.tile`). `ResearchTask` is constructed with the specific `Building lab` so it doesn't re-pathfind at init time.

**`PromoteToConstruct` edge case:** removes the `SupplyBlueprint` order while the delivering task's `workOrder.res.reserved == 1` (task still running). `SupplyBlueprintTask.Cleanup` later calls `workOrder.res.Unreserve()` on the now-orphaned order object — harmless.

### "Needs order" predicates

Used by `Register*`, `Reconcile`, and `AuditOrders` to decide whether an order should exist. Because orders persist while claimed, these predicates no longer need to check reservation state — dedup guards (`orders.Exists(o => ...)`) prevent double-registration while an order is already in the queue.

```csharp
// True if a floor stack should have a haul order
private static bool StackNeedsHaulOrder(ItemStack stack) =>
    stack != null && stack.item != null && stack.quantity > 0;

// True if a plant should have a harvest order (used by Reconcile)
private static bool PlantNeedsOrder(Plant p) => p.harvestable;

// True if a market inventory has at least one item below its target
private static bool MarketNeedsHaulTo(Inventory inv) =>
    inv.targets != null && inv.targets.Any(kvp => inv.Quantity(kvp.Key) < kvp.Value);

// True if a market inventory has at least one item above its target
private static bool MarketNeedsHaulFrom(Inventory inv) =>
    inv.targets != null && inv.targets.Any(kvp => inv.Quantity(kvp.Key) > kvp.Value);
```

All `Register*` methods are safe to call unconditionally — they self-guard with the predicate and a dedup check.

### Safety net: Reconcile + Audit

- **`Reconcile()`**: iterates plants, blueprints, floor stacks, market inventories, and labs. Calls the appropriate `Register*` for anything that passes its predicate but has no order (reserved or not — dedup prevents double-insertion). Logs a warning on any real insertion. Since orders persist while claimed, this is purely a "was the order ever created?" check — it no longer needs to catch re-registration failures.
- **`AuditOrders()`** (Ctrl+D in-game): bidirectional invariant check. For each order type: every object that passes the predicate has an order (reserved or not), and every order references a live valid object. `Debug.LogError`s any violation.

### Stale orders on world clear / load

`WorldController.ClearWorld()` calls `WorkOrderManager.ClearAllOrders()` at the start before destroying any objects. This prevents stale `WorkOrder` references (pointing at pre-load `ItemStack`/`Blueprint` objects) from surviving into the new session.

`Inventory.Destroy()` eagerly removes haul orders for all floor stacks, then zeros `stack.quantity` / `stack.resAmount` as a safety net for non-floor inventories.

`PruneStaleHauls()` and `PruneStaleMarketOrders()` are called together via `wom.PruneStale()`, which `ChooseTask` calls once before the `ChooseOrder` tier sequence. Both methods are **safety nets only** — they log `LogWarning` on any removal, which indicates a gap in the eager-removal hooks above. In normal play they should never fire.

### De minimis haul threshold

`Task.MinHaulQuantity = 20` (0.20 liang). `HaulTask`, `ConsolidateTask`, `HaulToMarketTask`, and `HaulFromMarketTask` all skip a haul move if the quantity to move is below this threshold **and** it would not drain the source stack entirely. This prevents animals spending time on trivial trickle-hauls. Hauling that clears the source stack is always allowed regardless of quantity.

### Blueprint constructor: autoRegister

`new Blueprint(st, x, y)` auto-registers the appropriate order by default (`autoRegister = true`). Pass `autoRegister: false` when the caller will handle registration explicitly:
- `Blueprint.CreateDeconstructBlueprint()` — sets state to Deconstructing and calls `RegisterDeconstruct` directly
- `SaveSystem.RestoreBlueprint()` — sets state from save data and registers via a switch statement

### Adding a new WOM-based task type

1. **Define the task** in `Task.cs`: implement `Initialize()` (add objectives, reserve resources, check job via `animal.job`). Only override `Cleanup()` when needed — always call `base.Cleanup()` first (it handles `workOrder.res.Unreserve()` and item stack unreserves). Two cases that require a `Cleanup()` override: (a) the task holds an extra reservation on a building/plant `res`; (b) the order uses a predicate-guarded removal that skips in-flight orders (like `UpdateMarketOrders`) — in that case, call the removal method again *after* `base.Cleanup()` so the order is now unreserved when the predicate check runs. See `HaulToMarketTask` for an example of case (b). Do **not** remove the WOM order or re-register in `Cleanup` for normal order types — the order persists in the queue.
2. **Job check**: add `if (animal.job.name != "myjob") return false;` as the first line of `Initialize()`, or check against the target object's `structType.job` field.
3. **Add `OrderType`** to the `WorkOrderManager.OrderType` enum.
4. **Add `Register*` method** with a predicate self-guard and a dedup guard (`orders.Exists(...)`). Returns `bool` (true = new order inserted).
5. **Add a "needs order" predicate** (`private static bool XNeedsOrder(...)`) — shared by `Register*`, `Reconcile`, and `AuditOrders`. Does **not** need to check reservation state.
6. **Set capacity if needed**: for multi-slot sources (e.g. a building that can have 2 workers), set `res = new Reservable(N)` in the `new WorkOrder { ... }` initializer. Default is 1.
7. **Add removal**: use `RemoveForBlueprint`, `RemoveForTile`, or add a new `RemoveFor*` method for when the underlying need permanently disappears.
8. **Register at the right moment**: push-register whenever the triggering condition first becomes true (state change, structure placed, etc.).
9. **Save/load**: in `SaveSystem.Restore*`, register the order if the saved state requires it.
10. **Reconcile**: add a scan loop in `WOM.Reconcile()` over the relevant state holder, guarded by the predicate.
11. **Audit**: add bidirectional checks in `WOM.AuditOrders()` — "every X that passes predicate has an order (reserved or not)" and "every order of this type points to a valid X".

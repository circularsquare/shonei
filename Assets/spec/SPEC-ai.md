# Shonei — Animal AI & WorkOrderManager

## Animal AI

### States

```
Idle → Working
     → Moving → (arrives) → back to Working or Idle
Idle → Eeping (sleeping)
Idle → Leisuring (chatting, tea house, etc.)
Any  → Falling (involuntary; interrupts current task) → Idle on landing
```

- **Idle**: calls `ChooseTask()`, selects best recipe by score
- **Working**: executes current objective (craft, harvest, build, sleep)
- **Moving**: navigates path via A*; calls `OnArrival()` on completion
- **Eeping**: sleeps at home, restores sleep meter; can trigger reproduction
- **Leisuring**: ticks `workProgress` until `LeisureObjective.duration` reached, then completes
- **Falling**: triggered when `!preventFall && !standable`; moves mouse straight down each frame using gravity (`World.fallGravity`); snaps to tile center on landing; bypasses task and nav systems entirely

### Needs

| Need | Effect |
|------|--------|
| Hunger | Reduces efficiency; eating restores |
| Sleep | Reduces efficiency; sleeping at home restores |
| Temperature | Reduces efficiency when outside comfort range (default 10–25°C). Clothing expands the range by ±3°C. |
| Efficiency | `eating.Efficiency() * eeping.Efficiency() * happiness.TemperatureEfficiency()` — scales move speed and work rate |

### Task dispatch (ChooseTask priority order)

`Animal.ChooseTask()` runs top-to-bottom when an animal is Idle:

1. **Survival** (always first): drop inventory → eat if hungry → sleep if eepy at night → equip tool → equip clothing
2. **Leisure** (4–9 pm only): 50% try `FindChatPartner()` (idle if no partner), 30% idle, 20% fall through to work
3. **Work orders:**
   - `wom.PruneStale()` — call once before the tier sequence
   - `wom.ChooseOrder(this, 1)` — hauls unblocking a deconstruct
   - `wom.ChooseOrder(this, 2)` — construct / supply / harvest
   - `wom.ChooseOrder(this, 3, exclude: Craft)` — haul / market (distance-sorted)
   - `ChooseCraftTask()` — craft (recipe-score sorted; see below)
   - `wom.ChooseOrder(this, 4)` — deconstruct, research

`ChooseOrder(animal, priority, exclude?)` only considers the single requested tier, optionally filters out a specific `OrderType`, filters to `res.Available()` orders, additionally skips haul orders where `stack.Available()` is false (stack fully reserved by in-flight tasks), distance-sorts remaining candidates, and on success calls `order.res.Reserve()` and assigns `task.workOrder = order`. Orders are **never removed when claimed** — they stay in the queue so they can be re-claimed after the task ends. WOM tasks are job-filtered via the `canDo` predicate on each `WorkOrder`.

**Recipe-first craft selection (`Animal.ChooseCraftTask`):**
Craft tasks are separated from `ChooseOrder` p3 so that recipe economic score — not building proximity — drives which workstation an animal visits. The algorithm:
1. Score all of the animal's recipes (filtered by `IsAllowed` and `SufficientResources`) using `Recipe.Score(targets)`.
2. Sort descending by score.
3. For each recipe, call `wom.FindCraftOrder(recipe.tile, animal)` — returns the nearest available craft `WorkOrder` for that building type, without reserving.
4. Try `new CraftTask(animal, building, recipe).Start()`. On success, reserve the order and return the task.
5. Fall through to the next recipe if no building is available or pathfinding fails.

`CraftTask` accepts an optional `preChosenRecipe` parameter. When set, `Initialize()` uses it directly instead of calling `PickRecipeForBuilding` — avoiding redundant re-evaluation. `PickRecipeForBuilding` remains as a fallback for any caller that creates `CraftTask` without a pre-chosen recipe.

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
| `CraftTask` | WOM p3 | recipe's job | Navigate to station, fetch inputs, work, drop outputs |
| `HarvestTask` | WOM p2 | plant's `njob` | Navigate to plant, harvest when ready, drop products |
| `HaulTask` | WOM p1/p3 | hauler | Move a floor stack to storage (or consolidate if no storage) |
| `HaulToMarketTask` | WOM p3 | merchant | Haul items from storage to the market building to meet targets |
| `HaulFromMarketTask` | WOM p3 | merchant | Haul excess items from market back to storage |
| `ConstructTask` | WOM p2/p4 | building's `njob` | Build or deconstruct a blueprint |
| `SupplyBlueprintTask` | WOM p2 | building's `njob` | Deliver materials to an incomplete blueprint |
| `ResearchTask` | WOM p4 | scientist | Navigate to a specific lab, work in loops |
| `ObtainTask` | survival | any | Fetch a specific item (food/equip) |
| `EepTask` | survival | any | Navigate home and sleep |
| `DropTask` | survival | any | Drop excess main inventory — prefers nearby storage/tank (10-tile bonus) over floor |
| `GoTask` | survival | any | Navigate to a tile |
| `ChatTask` | leisure | any | Walk to idle partner, both leisure 20 ticks, grants socialization happiness |

**Objectives (atomic steps):**
`GoObjective`, `FetchObjective`, `DeliverObjective`, `DeliverToBlueprintObjective`, `WorkObjective`, `HarvestObjective`, `ConstructObjective`, `EepObjective`, `DropObjective`, `ResearchObjective`, `LeisureObjective`

**`FetchObjective` behaviour:**
- Navigates to a source tile and picks up `iq.quantity` of an item into the animal's inventory (or an equip slot if `targetInv` is set).
- If `sourceTile` is not specified at construction, pathfinds to the nearest available stack and reserves it.
- On arrival: takes as many items as possible; if the stack was partially depleted by another animal, clears `sourceTile` and calls `Start()` again to find a new source tile — unless `softFetch` is true (see below).
- **Cross-tile retry**: if the animal still doesn't have enough after arrival, `sourceTile` is cleared and `Start()` re-runs to locate a new source tile. This repeats until `iq.quantity` is satisfied or no path exists.
- **`softFetch = true`**: `Complete` (never `Fail`) when no path is found or nothing was taken. Used by `CraftTask` and `ObtainTask` where partial or zero delivery is acceptable.
- **Partial-delivery fallback**: if no path to more items exists but the animal already holds a partial amount (e.g., the original stack was raided mid-task), `FetchObjective` calls `Complete` instead of `Fail`. This avoids a tight drop-and-re-fetch loop where the animal would otherwise drop the partial amount, see it on the floor, and immediately pick it up again.

### Job System

Each animal has one Job. Jobs filter which WOM orders and fallback tasks an animal can take. For crafting, `ChooseCraftTask` scores all of the animal's recipes globally against configurable inventory targets, then finds the nearest building for the top-scoring recipe — so economic need drives building selection rather than proximity.

---

## Skill System

Animals accumulate XP in skill domains as they work, gaining permanent speed bonuses that persist across saves.

### Skill domains

| Skill | Granted by |
|-------|-----------|
| `Farming` | `HarvestTask` (all jobs — farmer, logger, etc.) |
| `Mining` | `CraftTask` with digger/miner job (via `defaultSkill`) |
| `Construction` | `ConstructTask` (build and deconstruct) |
| `Science` | `ResearchTask` (scientist job) |
| `Woodworking` | `CraftTask` with sawyer/smith job (via `defaultSkill`) |

Recipes can override the default with an explicit `skill` JSON field. At load time, `Db.ReadJson()` propagates each job's `defaultSkill` to any recipe that didn't specify its own.

### XP and levelling

- **Rate**: 0.1 XP per unit of base work efficiency per tick (base = `hunger×sleep × toolBonus`, excluding the skill bonus itself so the bonus doesn't accelerate its own gain)
- **Threshold**: doubles each level — 10 XP to reach lv1, 20 to reach lv2, 40 for lv3, etc. (`XpThreshold(n) = 10 × 2ⁿ`)
- **Bonus**: `+5%` work speed per level, multiplicative with tool and efficiency (`1 + level × 0.05`)

At full efficiency with a tool (1.25× base), an animal gains 0.125 XP/tick. Level 1 takes ~80 ticks at this rate, level 2 ~160 more, and so on.

### Key classes

| Class | File | Role |
|-------|------|------|
| `Skill` | `Animal/Skills.cs` | Enum of 5 domains |
| `SkillSet` | `Animal/Skills.cs` | Per-animal XP/level container; `GainXp`, `GetBonus`, `Deserialize` |

`Animal.skills` (`SkillSet`) is initialized on construction. `ModifierSystem.GetWorkMultiplier(animal, skill?)` incorporates the level bonus; `GetBaseWorkEfficiency(animal)` is used for the XP calculation specifically.

### Save data

`AnimalSaveData.skillXp` (`float[]`) and `skillLevel` (`int[]`), indexed by `(int)Skill`. Both are `null` on old saves — `Deserialize` handles this gracefully by leaving arrays at zero.

---

## WorkOrderManager

`WorkOrderManager` (singleton MonoBehaviour) is the central registry of pending work. Instead of animals scanning the world themselves, work is pushed in as prioritised `WorkOrder` objects.

Orders are stored as `List<WorkOrder>[] orders` — an array of four lists, one per priority tier (index = priority - 1). Within each tier, FIFO order is preserved.

Each `WorkOrder` carries a `Reservable res` (default capacity 1). **Orders stay in the queue permanently** — they are only removed when the underlying need goes away (blueprint destroyed, plant gone, etc.), not when claimed. `ChooseOrder` filters to `o.res.Available()`, tries each factory, and on success calls `order.res.Reserve(); task.workOrder = order`. When the task ends (success or fail), `Task.Cleanup()` calls `workOrder?.res.Unreserve(); workOrder = null`, making the order available for the next animal.

`Reservable` has two capacity fields: `capacity` (hard max, set at registration from `structType.capacity`) and `effectiveCapacity` (player-adjustable; defaults to `capacity`). `Available()` gates on `effectiveCapacity`. For Craft orders, the player can lower the limit via InfoPanel +/- buttons to restrict how many workers use a workstation at once. The player-set limit is stored on `Building.workstation.workerLimit` (defaults to `capacity`), persisted via `StructureSaveData.workOrderEffectiveCapacity`, and synced to the WOM order via `WorkOrderManager.SetWorkstationCapacity()`. `Structure.res` is **not** created for workstations — the WOM Craft order's `res` is the sole reservation tracker for them.

This replaces the old pattern where claiming removed the order and `Cleanup` had to re-register it.

### Priority tiers

| Priority | Order types |
|----------|-------------|
| 1 | Haul unblocking a pending deconstruct |
| 2 | Construct, SupplyBlueprint, Harvest |
| 3 | Haul (floor items + storage evictions), HaulToMarket, HaulFromMarket, Craft |
| 4 | Deconstruct, Research |

### Registration rules

Orders are created once when the need first arises and removed only when the need permanently goes away. While a task is executing, the order stays in the queue with `res.reserved > 0`; when the task ends, `Cleanup()` releases it back to `res.reserved == 0` and it becomes claimable again.

| Order type | Registered when | Removed when |
|------------|----------------|--------------|
| `Construct` | Blueprint created with no costs (if not suspended); or `PromoteToConstruct` when last supply delivered; or `RegisterOrdersIfUnsuspended()` when support below completes | Blueprint completed or destroyed (`RemoveForBlueprint`) |
| `SupplyBlueprint` | Blueprint created with costs (if not suspended); or `RegisterOrdersIfUnsuspended()` when support below completes | Promoted to Construct; blueprint destroyed |
| `Deconstruct` | `Blueprint.CreateDeconstructBlueprint()` called | Blueprint completed or destroyed |
| `Haul` (priority 3, floor) | Items land on a floor inventory (`Inventory.Produce` or `MoveItemTo` destination hooks) | Eagerly: `MoveItemTo` source scan when a stack goes null; `Inventory.Decay()` when decay empties a stack; `Inventory.Destroy()` when floor inv is torn down. `PruneStaleHauls()` is a warning-level safety net only. |
| `Haul` (priority 3, storage eviction) | Item is disallowed in a storage inventory while the stack is non-empty — triggered by `DisallowItem`, `ToggleAllowItem`, force-`AddItem`, or `Reconcile`. Uses `RegisterStorageEvictionHaul` — `HaulTask` only, no `ConsolidateTask` fallback. | Eagerly: stack empties (`AddItem` source hook); item re-allowed (`AllowItem`/`ToggleAllowItem`); storage destroyed (`Inventory.Destroy`). `PruneStaleHauls()` is a warning-level safety net. |
| `Haul` (priority 1) | `PromoteHaulsFor(bp)` promotes or creates a p1 haul for blocking items during deconstruct | `RemoveForBlueprint` on cancel/complete |
| `Harvest` | Plant placed; `isActive = () => plant.harvestable` suppresses the order between grow cycles | Plant destroyed (`RemoveForTile`) |
| `HaulToMarket` | `UpdateMarketOrders(inv)` when any item is below its target | `UpdateMarketOrders(inv)` when target is met; called from `MoveItemTo` (market dest/source) and `ItemDisplay` target buttons. `PruneStaleMarketOrders()` is a warning-level safety net only. |
| `HaulFromMarket` | `UpdateMarketOrders(inv)` when any item exceeds its target | `UpdateMarketOrders(inv)` when excess is cleared |
| `Research` | `StructController.Construct()` when a lab is placed | Lab deconstructed (`RemoveForTile`) |
| `Craft` | `RegisterWorkstation(building)` when an `isWorkstation` building is placed or loaded | Building deconstructed (`RemoveWorkstationOrders`) |

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

### Safety net: ScanOrders (Reconcile + Audit unified)

Both reconciliation and auditing are handled by a single `ScanOrders(mode, silent)` method. Each order type's direction-1 check (world object → order) and direction-2 check (order → valid world object) appears once.

- **`Reconcile(silent)`**: calls `ScanOrders(Repair, silent)`. Registers missing orders. During gameplay, logs warnings on any insertion (indicates a gap in push-registration). Called once at load time with `silent=true` — this is the **sole mechanism** for registering all WOM orders after a save is loaded (no Register* calls in SaveSystem).
- **`AuditOrders()`**: calls `ScanOrders(Audit)`. Reports direction-1 violations as `LogError` without repairing, then checks direction-2 (orphaned orders). Ctrl+D in-game.

### Stale orders on world clear / load

`WorldController.ClearWorld()` calls `WorkOrderManager.ClearAllOrders()` at the start before destroying any objects. This prevents stale `WorkOrder` references (pointing at pre-load `ItemStack`/`Blueprint` objects) from surviving into the new session. After all world objects are restored, `SaveSystem.ApplySaveData` calls `Reconcile(silent: true)` to register all orders in one pass.

`Inventory.Destroy()` eagerly removes haul orders for all floor and storage stacks, then zeros `stack.quantity` / `stack.resAmount` as a safety net for other inventory types.

`PruneStaleHauls()` and `PruneStaleMarketOrders()` are called together via `wom.PruneStale()`, which `ChooseTask` calls once before the `ChooseOrder` tier sequence. Both methods are **safety nets only** — they log `LogWarning` on any removal, which indicates a gap in the eager-removal hooks above. In normal play they should never fire.

### De minimis haul threshold

`Task.MinHaulQuantity = 20` (0.20 liang). `HaulTask`, `ConsolidateTask`, `HaulToMarketTask`, and `HaulFromMarketTask` all skip a haul move if the quantity to move is below this threshold **and** it would not drain the source stack entirely. This prevents animals spending time on trivial trickle-hauls. Hauling that clears the source stack is always allowed regardless of quantity.

### Blueprint constructor: autoRegister

`new Blueprint(st, x, y)` auto-registers the appropriate order by default (`autoRegister = true`). Pass `autoRegister: false` when the caller will handle registration explicitly:
- `Blueprint.CreateDeconstructBlueprint()` — sets state to Deconstructing and calls `RegisterDeconstruct` directly
- `SaveSystem.RestoreBlueprint()` — sets state from save data; all WOM orders are registered by `Reconcile(silent: true)` at the end of `ApplySaveData()` once the graph is fully built

### Blueprint stacking & suspension

Blueprints can be placed on top of other blueprints at different depths if the lower blueprint has `solidTop` (`StructPlacement.SupportedByBlueprintBelow()`). The upper blueprint is **suspended** (`IsSuspended() == true`) until the support below is actually built.

**Suspended blueprints have no work orders.** The constructor skips registration when `IsSuspended()`. When the lower structure completes, `StructController.Construct()` calls `RefreshColor()` on blueprints above, which calls `RegisterOrdersIfUnsuspended()` — this registers the appropriate order (Supply or Construct) now that the blueprint is unsuspended. The `isActive = () => !bp.IsSuspended()` lambda on orders is kept as defense-in-depth. `Reconcile` and `AuditOrders` both skip suspended blueprints.

### Blueprint costs: deep copy & group item locking

Each blueprint deep-copies its `StructType.costs` array so that `LockGroupCostsAfterDelivery()` — which mutates `cost.item` from a parent (e.g. "wood") to a specific leaf (e.g. "pine") — only affects that individual blueprint, not every blueprint of the same type. Called in `DeliverToBlueprintObjective.Start()` after the first delivery.

### Adding a new WOM-based task type

1. **Define the task** in `Task.cs`: implement `Initialize()` (add objectives, reserve resources, check job via `animal.job`). Only override `Cleanup()` when needed — always call `base.Cleanup()` first (it handles `workOrder.res.Unreserve()` and item stack unreserves). Two cases that require a `Cleanup()` override: (a) the task holds an extra reservation on a building/plant `res`; (b) the order uses a predicate-guarded removal that skips in-flight orders (like `UpdateMarketOrders`) — in that case, call the removal method again *after* `base.Cleanup()` so the order is now unreserved when the predicate check runs. See `HaulToMarketTask` for an example of case (b). Do **not** remove the WOM order or re-register in `Cleanup` for normal order types — the order persists in the queue.
2. **Job check**: add `if (animal.job.name != "myjob") return false;` as the first line of `Initialize()`, or check against the target object's `structType.job` field. **Note:** for Craft orders specifically, `canDo` uses `Array.Exists(a.job.recipes, r => r != null && r.tile == buildingName)` — *not* `structType.job` (which is the construction job, not the crafting job).
3. **Add `OrderType`** to the `WorkOrderManager.OrderType` enum.
4. **Add `Register*` method** with a predicate self-guard and a dedup guard (`orders.Exists(...)`). Returns `bool` (true = new order inserted).
5. **Add a "needs order" predicate** (`private static bool XNeedsOrder(...)`) — shared by `Register*` and `ScanOrders`. Does **not** need to check reservation state.
6. **Set capacity if needed**: for multi-slot sources (e.g. a building that can have 2 workers), set `res = new Reservable(N)` in the `new WorkOrder { ... }` initializer. Default is 1. If the player should be able to reduce slots at runtime, store `workerLimit` on the `Building.workstation` component — `RegisterWorkstation` reads it. Use `SetWorkstationCapacity()` for runtime changes. `Available()` gates on `res.effectiveCapacity` rather than `res.capacity`.
7. **Add removal**: use `RemoveForBlueprint`, `RemoveForTile`, or add a new `RemoveFor*` method for when the underlying need permanently disappears.
8. **Register at the right moment**: push-register whenever the triggering condition first becomes true (state change, structure placed, etc.). For standing building orders (research, craft, fuel supply), add the registration to `WorkOrderManager.RegisterOrdersFor(Building)` and it will be called automatically via `Building.OnPlaced()`. For non-Building structures, override `Structure.OnPlaced()` directly (see `Plant.OnPlaced()` for an example).
9. **Add scan in `ScanOrders()`**: add direction-1 check (world object → order exists) and direction-2 check (order → valid world object). `ScanOrders` handles both reconciliation and auditing — no need to touch SaveSystem or write separate Reconcile/Audit logic. `Reconcile()` calls `ScanOrders(Repair)` and is called once at load time to register all orders.

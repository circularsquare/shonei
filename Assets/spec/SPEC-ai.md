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

### Happiness satisfactions

`Happiness.cs` tracks a `Dictionary<string, float> satisfactions` keyed by need name (e.g. "wheat", "fruit", "fountain", "social", "fireplace"). All need keys are collected at startup in `Db.happinessNeeds` from three data sources plus one hardcoded source:

| Source | JSON field | Example |
|--------|-----------|---------|
| Food items | `Item.happinessNeed` | wheat → "wheat", apple → "fruit", soymilk/tofu → "soymilk" |
| Decoration buildings | `StructType.decorationNeed` | fountain → "fountain" |
| Leisure buildings | `StructType.leisureNeed` | fireplace → "fireplace" |
| ChatTask (hardcoded) | — | "social" |

Each satisfaction decays exponentially each SlowUpdate (`×0.9044`). Score = count of satisfied needs (≥1.0 threshold) + housing (bool) + temperature (−1/5°C to +2). `Db.happinessMaxScore` = need count + 1 (housing) + 2 (temp max).

Adding a new food or building happiness source: JSON changes auto-register the need, and both UI panels discover it from `Db.happinessNeedsSorted`. Also update `Db.happinessNeedsDisplayOrder` (the manual ordering array in `Db.cs`) to place it in the correct display group — if omitted, it appears alphabetically at the end.

**Special**: `warmth` is separate from satisfactions — it's a cold-tolerance buff granted by fireplace leisure, not a happiness need.

### Social satisfaction

Social satisfaction is granted **gradually** at `Happiness.socialTickGrant` (0.2) per tick, not as a lump sum. This applies to both standalone chatting and fireplace co-leisure.

**Thresholds** (apply to both chat and fireplace socializing):
- **Initiate**: a mouse only seeks chat / starts fireplace socializing when social < 2.0
- **Accept**: a mouse won't accept chat recruitment (or participate in fireplace socializing) when social > 4.0
- **Interrupt**: a chatting mouse completes early when social reaches the 5.0 cap

**Standalone chat** (`ChatTask` / `HandleChatting`): both mice run `HandleChatting` independently, each granting themselves 0.2/tick. Partner search radius is 6 tiles. `FindIdleAnimalNear` filters out animals with social > 4.0.

**Fireplace co-leisure** (`LeisureTask` / `HandleLeisure`): each tick, `HandleLeisure` checks if a `socialWhenShared` building has another mouse present. Either mouse having social < 2.0 is enough to spark conversation (symmetric — the chatty one draws the other in). Both mice get the grant and show chat bubbles. The socializing aspect stops naturally once both mice exceed 2.0, but the fireplace leisure session continues for its warmth/need benefit.

### Task dispatch (ChooseTask priority order)

`Animal.ChooseTask()` runs top-to-bottom when an animal is Idle:

1. **Survival** (always first): drop inventory → eat if hungry → sleep (see thresholds below) → equip tool → equip clothing. Sleep gate is `Eeping.ShouldSleep(isNighttime)`: eep < 85 % **at night** (9 pm–6 am) OR eep < 50 % **any time** (mid-shift exhaustion nap).
2. **Time-of-day behavior roll** — before work orders, a random roll determines whether the animal tries leisure, idles, or falls through to work. The weights depend on the time window:
   - **Leisure window (5–9 pm)**: 40% try leisure, 40% idle, 20% work.
   - **Work time (rest of day)**: 5% try leisure, 15% idle, 80% work.
   Leisure pick is need-based: `TryPickLeisure()` enqueues one candidate per unique `structType.leisureNeed` across placed leisure buildings (plus chat = "social" and reading when a fiction book exists), sorts by the animal's current satisfaction for that need (ascending), and tries each in order — so the mouse targets its least-satisfied need first. Each candidate's `LeisureTask.Initialize` then scans all suitable buildings for that need and picks the nearest-by-path available seat. Falls back to idle if nothing is available.
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

### Reservation system (source + destination)

Tasks reserve both **source items** (`ItemStack.resAmount`) and **destination space** (`ItemStack.resSpace`) during `Initialize()` via `Task.ReserveStack` / `Task.ReserveSpace`. Both are auto-released by `base.Cleanup()`. For the full mechanism (FreeSpace logic, empty-stack tracking, staleness sweep) see SPEC-systems.md §Reservation Systems.

**Tasks using destination reservation:** `HaulTask`, `ConsolidateTask`, `HaulToMarketTask`, `HaulFromMarketTask`, `SupplyFuelTask`, `DropObjective` (best-effort).

**Tasks (implemented):**

| Task | Source | Job | Description |
|------|--------|-----|-------------|
| `CraftTask` | WOM p3 | recipe's job | Navigate to station, fetch inputs, work, drop outputs |
| `HarvestTask` | WOM p2 | plant's `njob` | Navigate to plant, harvest when ready, drop products. Harvest orders only exist while `plant.harvestFlagged` is true (set by the player via the Harvest tool in the build bar). `Plant.SetHarvestFlagged` registers / unregisters the order; `isActive = () => plant.harvestable` gates dormancy across grow cycles. |
| `HaulTask` | WOM p1/p3 | hauler | Fetch floor stack → Go to storage tile → DeliverToInventoryObjective into `building.storage` |
| `HaulToMarketTask` | WOM p3 | merchant | Haul items from storage to the market building to meet targets |
| `HaulFromMarketTask` | WOM p3 | merchant | Haul excess items from market back to storage |
| `ConstructTask` | WOM p2/p4 | building's `njob` | Build or deconstruct a blueprint |
| `SupplyBlueprintTask` | WOM p2 | building's `njob` | Deliver materials to an incomplete blueprint |
| `ResearchTask` | WOM p4 | scientist | Navigate to a specific lab, work in loops. Optionally borrows the matching tech book (via `bookSlotInv`) before research and returns it after — book grants 3× research progress per tick while equipped (see SPEC-books.md). |
| `ObtainTask` | survival | any | Fetch a specific item (food/equip) |
| `EepTask` | survival | any | Navigate home and sleep |
| `DropTask` | survival | any | Drop excess main inventory — prefers nearby storage/tank (10-tile bonus) over floor. On no-reachable-target, logs a warning and sets `animal.dropCooldownUntil = timer + 3f` so `ChooseTask` falls through to other branches instead of respawning `DropTask` every tick |
| `GoTask` | survival | any | Navigate to a tile |
| `ChatTask` | leisure | any | Walk to idle partner, both leisure 20 ticks, grants socialization happiness |
| `LeisureTask` | leisure | any | Constructed with a `leisureNeed` string. `Initialize` delegates to `Nav.FindPathToLeisureSeat(filter)` — filter combines `leisureNeed` match + `Building.CanHostLeisureNow()` (disabled/broken/fuel/active-hour). Uses the standard Chebyshev-sort + first-fit-within-radius pattern (see §Path-cost radius gate), so it's consistent with `FindPathToStruct` etc. rather than a bespoke scan. Reserves the returned `seatRes[i]`. Leisure 15 ticks. On Complete grants `Happiness.NoteLeisure(need, structType.leisureGrant)` — `leisureGrant` lets cheap/always-on buildings (bench = 0.5) grant less than premium ones (fireplace = 1.0). |
| `ReadBookTask` | leisure | any | Fetch fiction book → walk to reading spot (prefers a reserved `bench` seat; falls back to any nearby unoccupied tile) → read 10 ticks (per-tick `NoteRead` grant) → return book to shelf. When seated at a bench, also grants the `"bench"` leisure need on Complete. See SPEC-books.md. |
| `MaintenanceTask` | WOM p2 | mender | Fetch repair materials (¼ × build cost, scaled by repair amount) → walk to work tile → `MaintenanceObjective` ticks up `condition` by 0.05 × efficiency per tick, capped at +40 % per visit. Grants Construction XP. See SPEC-systems.md §Maintenance System. |

**Objectives (atomic steps):**
`GoObjective`, `FetchObjective`, `DeliverObjective`, `DeliverToBlueprintObjective`, `DeliverToInventoryObjective`, `ReceiveFromInventoryObjective`, `WorkObjective`, `HarvestObjective`, `ConstructObjective`, `EepObjective`, `DropObjective`, `ResearchObjective`, `LeisureObjective`, `MaintenanceObjective`, `UnequipObjective`

**`FetchObjective` behaviour:**
- Navigates to a source tile and picks up `iq.quantity` of an item into the animal's inventory (or an equip slot if `targetInv` is set).
- Tracks `sourceInv` (the inventory to take from). Set explicitly by callers via `FetchAndReserve` / constructor, or discovered by `FindPathItemStack` during `Start()`. This allows fetching from both floor and storage inventories (since storage is on `building.storage`, not `tile.inv`).
- If `sourceTile` is not specified at construction, pathfinds to the nearest available stack, reserves it, and sets `sourceInv = stack.inv`.
- On arrival: moves items from `sourceInv` into the destination (falls back to `tile.inv` if `sourceInv` is null). Cleans up empty floor inventories after taking.
- **Cross-tile retry**: if the animal still doesn't have enough after arrival, `sourceTile` and `sourceInv` are cleared and `Start()` re-runs to locate a new source. This repeats until `iq.quantity` is satisfied or no path exists.
- **`softFetch = true`**: `Complete` (never `Fail`) when no path is found or nothing was taken. Used by `CraftTask` and `ObtainTask` where partial or zero delivery is acceptable.
- **Partial-delivery fallback**: if no path to more items exists but the animal already holds a partial amount (e.g., the original stack was raided mid-task), `FetchObjective` calls `Complete` instead of `Fail`. This avoids a tight drop-and-re-fetch loop where the animal would otherwise drop the partial amount, see it on the floor, and immediately pick it up again.

**`DeliverToInventoryObjective`**: moves items from animal inventory into a specific target inventory (used by `HaulTask` for storage delivery, `HaulFromMarketTask`, `SupplyFuelTask`). Always queued after `GoObjective`. Fails with log if target is null or animal has nothing to deliver.

### Path-cost radius gate

Every task pathfind is gated by an absolute cap on the **actual A\* path cost** so a mouse never commits to a journey that looks close crow-flies but winds endlessly around terrain (e.g. a plant 5 tiles away across a chasm whose path is 150 tiles around the cave perimeter).

**Constants** (top of `Task` class in [Task.cs](../Model/Task.cs)):

```csharp
public const int   MediumFindRadius   = 40;    // default for almost every task
public const int   MarketFindRadius   = 120;   // market portal only — intentionally long
public const float FindRadiusTolerance = 1.2f; // path cost may exceed radius by this factor
```

A candidate is rejected when `path.cost > r × FindRadiusTolerance`.

**Gate helper** — `Nav.WithinRadius(Path p, int r)` in [Nav.cs](../Model/Animal/Nav.cs): returns `true` iff `p != null && p.cost <= r × tolerance`. WOM-dispatched tasks call this after their single `PathTo*` in `Initialize()`; if it returns false, the task aborts before reserving.

**Applied in `Initialize()`** of: `CraftTask`, `HarvestTask`, `HaulTask`, `ConstructTask`, `SupplyBlueprintTask`, `SupplyFuelTask`, `ResearchTask`, `EepTask` (conditional — falls back to sleeping in place if home is too far), `ChatTask`, `LeisureTask`. `GoTask` is the sole unconditional exception — explicit player/system intent has no radius concept.

**Built into `Nav.Find*` methods** — `FindPathTo`, `FindPathToInv`, `FindPathToStruct` all use a **sort-by-Chebyshev + first-fit** pattern:

1. Collect candidate tiles matching the filter within the `r` bounding box.
2. Sort by Chebyshev distance (crow-flies lower bound on path cost).
3. Pathfind candidates in order; return the **first** whose `path.cost ≤ r × tolerance`.

This is not guaranteed minimum-cost across all candidates, but in practice the nearest crow-flies candidate is almost always also the shortest walk, and it avoids pathfinding the rest of the box — typical Find* call now runs ~1 A\* invocation instead of N.

**Two shared primitives back these methods** (both in `Nav.cs`):

- `Nav.TilesAroundByDistance(world, cx, cy, r)` — *static*. Returns all tiles in the `(2r+1)²` box around `(cx,cy)` sorted by ascending Chebyshev distance. Used by `FindPathTo` and any task-local spatial search around an arbitrary anchor (e.g. `ReadBookTask.FindReadingTileNearShelf` anchors on the shelf tile, not the animal).
- `Nav.FindPathToCandidate<T>(candidates, xFn, yFn, nodeFn, filter, r)` — *instance*. Counterpart for callers whose candidates come from a pre-built object list (inventories, structures). Applies the same cheb-sort + first-fit pathfind. Used by `FindPathToInv` and `FindPathToStruct`.

`FindPathToStorageMostSpace` is deliberately not unified — it sorts by **descending free space** (best-fit), not Chebyshev, so it's a different algorithm despite the similar shape.

Market tasks pass `r = Task.MarketFindRadius` via `FindMarketPath`. Drop searches (`FindPathToDrop`) use a tight `r = 10` without the tolerance multiplier. The legacy `persistent` expansion mechanism (retry with doubling radius) has been removed — the single widened search is simpler and the gate makes it sufficient.

### Job System

Each animal has one Job. Jobs filter which WOM orders and fallback tasks an animal can take. For crafting, `ChooseCraftTask` scores all of the animal's recipes globally against configurable inventory targets, then finds the nearest building for the top-scoring recipe — so economic need drives building selection rather than proximity.

**Automatic job swapping** ([JobSwapper.cs](../Model/Animal/JobSwapper.cs)): when an idle animal has skills better suited to another *idle* animal's job (and vice versa), `JobSwapper.TrySwap` swaps them. Fires from `HandleIdle` every 2 ticks (stagger-phased by `tickCounter`). Score = `Σ (skillWeight × skillLevel)` using the job's `skillWeights` profile; swap commits when the combined score strictly improves. Only idle partners are considered — `SetJob` calls `Refresh()` which fails the current task, so swapping with a busy mouse would discard in-progress work.

---

## Skill System

Animals accumulate XP in skill domains as they work, gaining permanent speed bonuses that persist across saves.

### Skill domains

| Skill | Granted by |
|-------|-----------|
| `Farming` | `HarvestTask` (all jobs — farmer, logger, etc.) |
| `Mining` | `CraftTask` with digger/miner job (via `defaultSkill`) |
| `Construction` | `ConstructTask` (build and deconstruct), `MaintenanceTask` (mender) |
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

`WorkOrderManager` (singleton MonoBehaviour) is the central registry of pending work. Orders are stored as `List<WorkOrder>[] orders` — four lists, one per priority tier (index = priority - 1), FIFO within each tier.

Each `WorkOrder` carries a `Reservable res` (default capacity 1). **Orders stay in the queue permanently** — removed only when the underlying need goes away (blueprint destroyed, plant gone, etc.), not when claimed. `ChooseOrder` filters to `o.res.Available()`, tries each factory, and on success calls `order.res.Reserve(); task.workOrder = order`. On task end, `Task.Cleanup()` calls `workOrder?.res.Unreserve()`, re-opening the slot.

Player-adjustable workstation slot count flows: `Building.workstation.workerLimit` → `WorkOrderManager.SetWorkstationCapacity()` → `WorkOrder.res.effectiveCapacity` (Available() gates on this). Persisted via `StructureSaveData.workOrderEffectiveCapacity`. See SPEC-systems.md §Reservation Systems for the full landscape of `Structure.res` vs `seatRes[]` vs `WorkOrder.res`.

### Priority tiers

| Priority | Order types |
|----------|-------------|
| 1 | Haul unblocking a pending deconstruct |
| 2 | Construct, SupplyBlueprint, Harvest, Maintenance |
| 3 | Haul (floor items + storage evictions), HaulToMarket, Craft |
| 4 | Deconstruct, Research, HaulFromMarket |

### Registration rules

Orders are created once when the need first arises and removed only when the need permanently goes away. While a task is executing, the order stays in the queue with `res.reserved > 0`; when the task ends, `Cleanup()` releases it back to `res.reserved == 0` and it becomes claimable again.

| Order type | Registered when | Removed when |
|------------|----------------|--------------|
| `Construct` | Blueprint cost-free; last supply delivered (`PromoteToConstruct`); support below completes (unsuspension) | Blueprint completed or destroyed |
| `SupplyBlueprint` | Blueprint created with costs; or unsuspension | Promoted to Construct; blueprint destroyed |
| `Deconstruct` | `Blueprint.CreateDeconstructBlueprint()` | Blueprint completed or destroyed |
| `Haul` (p3 floor) | Items land on a floor inventory | Stack empties (eager); floor inv destroyed |
| `Haul` (p3 eviction) | Item disallowed while stack non-empty (via `RegisterStorageEvictionHaul`; `HaulTask` only, no consolidation fallback) | Stack empties; item re-allowed; storage destroyed |
| `Haul` (p1) | `PromoteHaulsFor(bp)` for items blocking a deconstruct | Parent blueprint resolved |
| `Harvest` | Plant placed (`isActive` suppresses between grow cycles) | Plant destroyed |
| `HaulToMarket` | `UpdateMarketOrders` sees item below target | `UpdateMarketOrders` sees target met |
| `HaulFromMarket` | `UpdateMarketOrders` sees item above target | `UpdateMarketOrders` sees excess cleared |
| `Research` | Lab placed | Lab deconstructed |
| `Craft` | `isWorkstation` building placed (via `RegisterWorkstation`) | Building deconstructed |
| `Maintenance` | Structure's `condition` drops below `RegisterThreshold` (0.75) — registered by `MaintenanceSystem` on first downward crossing, or by `Reconcile`/`ScanOrders` at load. `isActive = () => s.WantsMaintenance` suppresses when fully repaired (no removal/churn on every decay tick). | Structure destroyed (`RemoveMaintenanceOrders` from `Structure.Destroy()`) |

Exact eager-removal hook sites live in code comments next to the relevant `Remove*` call — check them before editing. Research and Harvest orders are **per-source** (one per lab/plant, keyed by `o.tile`); `ResearchTask` is constructed with the specific `Building lab` so it doesn't re-pathfind at init.

**`PromoteToConstruct` edge case:** removes the `SupplyBlueprint` order while the delivering task's `workOrder.res.reserved == 1` (task still running). `SupplyBlueprintTask.Cleanup` later calls `workOrder.res.Unreserve()` on the now-orphaned order object — harmless.

**Plant deconstruct `canDo`:** Construct / SupplyBlueprint / Deconstruct orders normally gate on `a.job == bp.structType.job`. For plants, `structType.job` is the *harvest* job (logger / farmer), not a construction job, and PlantType's `OnDeserialized` has no hauler fallback. `RegisterDeconstruct` therefore adds an `|| (bp.structType.isPlant && a.job.name == "hauler")` clause so haulers can chop/uproot plants even when the harvest-job queue is unstaffed or backed up. The same category mismatch exists in `RegisterConstruct` / `RegisterSupplyBlueprint` for plants (planting a tree requires a logger) — intentionally *not* relaxed there; planting is deliberate.

### "Needs order" predicates

Used by `Register*`, `Reconcile`, and `AuditOrders` to decide whether an order should exist. Because orders persist while claimed, these predicates no longer need to check reservation state — dedup guards (`orders.Exists(o => ...)`) prevent double-registration while an order is already in the queue.

```csharp
// True if a floor stack should have a haul order
private static bool StackNeedsHaulOrder(ItemStack stack) =>
    stack != null && stack.item != null && stack.quantity > 0;

// True if a plant should have a harvest order (used by Reconcile).
// Only flagged plants carry orders — Plant.SetHarvestFlagged registers / unregisters
// as the flag flips. Unflagged plants legitimately have no order, so Reconcile skips them.
private static bool PlantNeedsOrder(Plant p) => p.harvestFlagged;

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

`Task.MinHaulQuantity = 20` (0.20 liang). `HaulTask` and `ConsolidateTask` skip a haul if the quantity is below this threshold **and** it would not drain the source stack entirely.

`Task.MinMarketHaulQuantity = 100` (1.0 liang). `HaulToMarketTask` and `HaulFromMarketTask` use this stricter threshold with **no exceptions** — not for stack-clearing or topping off. Merchants shouldn't make a trip for a trickle.

### Blueprint stacking & suspension

Blueprints can be placed on top of other blueprints at different depths if the lower blueprint has `solidTop` (`StructPlacement.SupportedByBlueprintBelow()`). The upper blueprint is **suspended** (`IsSuspended() == true`) until the support below is actually built.

**Suspended blueprints have no work orders.** The constructor skips registration when `IsSuspended()`. When the lower structure completes, `StructController.Construct()` calls `RefreshColor()` (pure visual refresh) and then `RegisterOrdersIfUnsuspended()` on blueprints above — the latter registers the appropriate order (Supply or Construct) now that the blueprint is unsuspended. The `isActive = () => bp.ConditionsMet()` lambda on orders is kept as defense-in-depth. `Reconcile` and `AuditOrders` both skip suspended blueprints.

`Blueprint.ConditionsMet()` returns `!IsSuspended()`, mirroring the `Structure.ConditionsMet()` convention by name (Blueprint is a sibling class, not a Structure subclass — there's no polymorphism, just a shared call-site shape: `!disabled && ConditionsMet()` reads the same on both). `IsSuspended()` is retained as the named reason in blueprint-only paths (UI tinting, `RegisterOrdersIfUnsuspended`).

### Blueprint costs: deep copy & group item locking

Each blueprint deep-copies its `StructType.costs` array so that `LockGroupCostsAfterDelivery()` — which mutates `cost.item` from a parent (e.g. "wood") to a specific leaf (e.g. "pine") — only affects that individual blueprint, not every blueprint of the same type. Called in `DeliverToBlueprintObjective.Start()` after the first delivery.

### Adding a new WOM-based task type

1. **Define the task** in a new file `Assets/Model/Tasks/MyTask.cs` (one class per file; base class is in `Task.cs`): implement `Initialize()` (add objectives, reserve resources, check job via `animal.job`). Only override `Cleanup()` when needed — always call `base.Cleanup()` first (it handles `workOrder.res.Unreserve()` and item stack unreserves). Two cases that require a `Cleanup()` override: (a) the task holds an extra reservation on a building/plant `res`; (b) the order uses a predicate-guarded removal that skips in-flight orders (like `UpdateMarketOrders`) — in that case, call the removal method again *after* `base.Cleanup()` so the order is now unreserved when the predicate check runs. See `HaulToMarketTask` for an example of case (b). Do **not** remove the WOM order or re-register in `Cleanup` for normal order types — the order persists in the queue.
2. **Job check**: add `if (animal.job.name != "myjob") return false;` as the first line of `Initialize()`, or check against the target object's `structType.job` field. **Note:** for Craft orders specifically, `canDo` uses `Array.Exists(a.job.recipes, r => r != null && r.tile == buildingName)` — *not* `structType.job` (which is the construction job, not the crafting job).
3. **Add `OrderType`** to the `WorkOrderManager.OrderType` enum.
4. **Add `Register*` method** with a predicate self-guard and a dedup guard (`orders.Exists(...)`). Returns `bool` (true = new order inserted).
5. **Add a "needs order" predicate** (`private static bool XNeedsOrder(...)`) — shared by `Register*` and `ScanOrders`. Does **not** need to check reservation state.
6. **Set capacity if needed**: for multi-slot sources (e.g. a building that can have 2 workers), set `res = new Reservable(N)` in the `new WorkOrder { ... }` initializer. Default is 1. If the player should be able to reduce slots at runtime, store `workerLimit` on the `Building.workstation` component — `RegisterWorkstation` reads it. Use `SetWorkstationCapacity()` for runtime changes. `Available()` gates on `res.effectiveCapacity` rather than `res.capacity`.
7. **Add removal**: use `RemoveForBlueprint`, `RemoveForTile`, or add a new `RemoveFor*` method for when the underlying need permanently disappears.
8. **Register at the right moment**: push-register whenever the triggering condition first becomes true (state change, structure placed, etc.). For standing building orders (research, craft, fuel supply), add the registration to `WorkOrderManager.RegisterOrdersFor(Building)` and it will be called automatically via `Building.OnPlaced()`. For non-Building structures, override `Structure.OnPlaced()` directly (see `Plant.OnPlaced()` for an example).
9. **Add scan in `ScanOrders()`**: add direction-1 check (world object → order exists) and direction-2 check (order → valid world object). `ScanOrders` handles both reconciliation and auditing — no need to touch SaveSystem or write separate Reconcile/Audit logic. `Reconcile()` calls `ScanOrders(Repair)` and is called once at load time to register all orders.

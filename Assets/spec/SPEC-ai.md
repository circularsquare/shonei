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
| Hunger | Reduces efficiency; eating restores. A full in-game day at zero food is fatal — see **Starvation death** below. Food choice is scored in `Animal.FindFood` (foodValue × craving × distance-discount); a **seed** item (one that's also a planting cost — `Db.seedItems`, derived from `PlantType.costs`) under 300 fen world-total is held to a fallback tier and eaten only when no other food is reachable, so mice don't strand farmers by eating the last replanting stock. |
| Sleep | Reduces efficiency; sleeping at home restores |
| Temperature | Reduces efficiency when outside comfort range (default 10–25°C). Clothing expands the range by ±3°C. |
| Efficiency | `eating.Efficiency() * eeping.Efficiency() * happiness.TemperatureEfficiency()` — scales move speed and work rate |

**Needs tick wall-clock; only work ticks on efficiency.** Need depletion *and* recovery (hunger, sleep) run every tick in `Animal.HandleNeeds()`, independent of efficiency. The energy/efficiency throttle (`energy += efficiency; if (energy > 1) UpdateState()`) paces only *work* — craft/build/harvest/research progress, and the per-sleep-tick events in `HandleEeping` (reproduction roll, wake-up check). Sleep **recovery** must stay in `HandleNeeds`. Driving it from the throttled path would scale recovery with efficiency while depletion stays unthrottled. A low-efficiency mouse (hungry + exhausted) would then lose eep faster than it regains it and never wake — a death spiral that locks the whole colony. Do not move `eeping.Eep()` back into `HandleEeping`.

**Colony rescue spawn.** `AnimalController.MaybeRescueSpawn()` (called after `RemoveDeadAnimals()`) prevents a starvation softlock: if `na < 4` and the current season is not Winter, the colony is topped back up to 4 — typically 1–2 mice after a death or two. New arrivals spawn at the first `isHousing` building it finds — or, if none exist, at the left surface edge (`x=1, y=surfaceY[1]`; `x=0` is the worldgen market column). A 120-frame cooldown gates the trigger so the same drop doesn't re-fire while the queued animals are still registering (Animal.Start runs the frame after AddAnimal). Winter is excluded so the player isn't handed a relief wave during the lean season; help comes in spring/summer/fall.

**Starvation death.** A mouse whose `food` hits 0 starts a countdown: `Eating.starvingTicks` is incremented every tick `Eating.Update()` finds food at 0, and reset to 0 by `Eat()` or any tick with food remaining. Once it reaches `World.ticksInDay` — a full in-game day at zero food — `Eating.StarvedToDeath()` trips and `Animal.TickUpdate()` sets the transient `pendingDeath` flag (skipping the rest of that mouse's tick). `AnimalController.RemoveDeadAnimals()`, run once **after** the per-animal tick loop so `animals[]` is never compacted mid-iteration, sweeps for the flag and calls `HandleDeath`: it posts an EventFeed alert, drops the mouse's inventory + all four equip slots to the floor via `Animal.DropInventoryToFloor()` (recoverable by the player), fixes the job count, deselects the InfoPanel if it was showing the mouse, and tears the mouse down via `Animal.Destroy()` — which now also releases the home reservation and destroys the equip-slot inventories. A separate EventFeed warning fires the moment the countdown first starts (`starvingTicks == 1`). With the default `hungerRate` it takes a couple in-game days from a full belly to death. `starvingTicks` is persisted in `AnimalSaveData` (0 on old saves = not starving).

### Happiness satisfactions

`Happiness.cs` tracks a `Dictionary<string, float> satisfactions` keyed by need name (e.g. "wheat", "fruit", "fountain", "social", "fireplace"). All need keys are collected at startup in `Db.happinessNeeds` from three data sources plus one hardcoded source:

| Source | JSON field | Example |
|--------|-----------|---------|
| Food items | `Item.happinessNeed` | wheat → "wheat", apple → "fruit", soymilk/tofu → "soymilk" |
| Decoration buildings | `StructType.decorationNeed` | fountain → "fountain" |
| Leisure buildings | `StructType.leisureNeed` | fireplace → "fireplace" |
| ChatTask (hardcoded) | — | "social" |

Each satisfaction decays exponentially each SlowUpdate (`×0.9044`). Score = sum over satisfied needs (≥1.0 threshold) — `+1` each, except `"alcohol"` which is `+2` (a satisfied tipple is worth more than a tidy bench) — plus housing (bool) + temperature (−1/5°C to +2) + **colony food-storage bonus** (0..4, scales linearly with days-of-food up to 10 days; same value applied to every mouse). `Db.happinessMaxScore` = need count + 1 (housing) + 2 (temp max) + 4 (food storage) + 1 (alcohol's extra point above, added when the need is registered) + ceil(maxFurnishingPerMouse).

**Colony food-storage bonus.** `AnimalController.UpdateColonyStats` (every 10 ticks) recomputes `daysOfFoodInStorage = Σ (qty_fen/100 × foodValue) / (hungerRate × ticksInDay × na)` over `Db.edibleItems`, then sets `foodStorageHappinessBonus = clamp01(days/10) × 4`. `Happiness.SlowUpdate` reads the field and adds it to each mouse's score uniformly — it's a colony-level pressure, not per-mouse. The same `daysOfFoodInStorage` field drives the reproduction gate in `HandleEeping`: hard floor at 2 days (no births), then linear taper `× clamp(days/10)` on the per-sleep-tick birth chance up to full rate at 10 days.

**Population capacity** = `floor(avgHappiness / Db.happinessMaxScore × AnimalController.MaxPopulationCap)`. `MaxPopulationCap` (default 40) is the cap when every mouse has every happiness need fully satisfied. The formula auto-rescales when needs are added or removed.

Adding a new food or building happiness source: JSON changes auto-register the need, and both UI panels discover it from `Db.happinessNeedsSorted`. Also update `Db.happinessNeedsDisplayOrder` (the manual ordering array in `Db.cs`) to place it in the correct display group — if omitted, it appears alphabetically at the end.

**Special**: `warmth` is separate from satisfactions — it's a cold-tolerance buff granted by fireplace leisure, not a happiness need.

### Social satisfaction

Social satisfaction is granted **gradually** at `Happiness.socialTickGrant` (0.2) per tick, not as a lump sum. This applies to both standalone chatting and fireplace co-leisure.

**Initial value**: freshly-created mice — the starting colony and any born via reproduction — seed `social` to a random value in `[3, 5)` in `Animal.Start()`'s fresh-spawn branch, so a new colony doesn't begin life lonely. Given the thresholds below, those mice won't actively seek socializing until decay drops `social` under 2.0. Loaded mice restore `social` from save data instead. All other satisfactions start at 0.

**Thresholds** (apply to both chat and fireplace socializing):
- **Initiate**: a mouse only seeks chat / starts fireplace socializing when social < 2.0
- **Accept**: a mouse won't accept chat recruitment (or participate in fireplace socializing) when social > 4.0
- **Interrupt**: a chatting mouse completes early when social reaches the 5.0 cap

**Standalone chat** (`ChatTask` / `HandleChatting`): both mice run `HandleChatting` independently, each granting themselves 0.2/tick. Partner search radius is 6 tiles. `FindIdleAnimalNear` filters out animals with social > 4.0.

**Fireplace co-leisure** (`LeisureTask` / `HandleLeisure`): each tick, `HandleLeisure` checks if a `socialWhenShared` building has another mouse present. Either mouse having social < 2.0 is enough to spark conversation (symmetric — the chatty one draws the other in). Both mice get the grant and show chat bubbles. The socializing aspect stops naturally once both mice exceed 2.0, but the fireplace leisure session continues for its warmth/need benefit.

### Task dispatch (ChooseTask — unified urgency picker)

`Animal.ChooseTask()` runs when an animal is Idle. It scores every **category** of action with a
0..1 **urgency**, then attempts categories in descending-urgency order, taking the first that
starts a task. This replaced the older hard priority ladder + random leisure/idle dice (mid-2026):
the goal is smooth, need-scaled behaviour — a slightly-hungry mouse finishes nearby work while a
starving one drops everything to eat, and urgent work can beat evening leisure instead of being
skipped by an unlucky roll. Design + tuning rationale: `plans/urgency-system.md`.

**Hard pre-step (not urgency-ranked):** drop carried inventory if non-empty (policy: idle mice keep
main inv empty, food/tools in equip slots). Has a `dropCooldownUntil` fallback so a boxed-in mouse
doesn't loop. (A mouse that just delivered a blueprint's last material finishes the build via plain
urgency — the construct order it's standing on scores ≈0.70, no special flag needed.)

**Categories and their urgency sources:**
- **eat** — `Eating.HungerUrgency()`: two-regime curve. 0 at/above `seekFoodThreshold` (0.6); a
  CONCAVE rise from 0.6→0.3 (clearly seeking by ~0.5 fullness ≈ 0.41); below `dominateThreshold`
  (0.3) a ramp from the `dominateFloor` (0.8, above the realistic work ceiling ~0.70) to 1.0 at
  empty, so a genuinely-low mouse reliably out-scores all work/leisure. Internal food choice still
  via `FindFood` (foodValue × craving × distance-discount).
- **sleep** — `Eeping.SleepUrgency(bedtimeUrgency)`: 0 at/above the bedtime-shifted threshold
  (`0.4 + bedtimeUrgency*0.5`), linear pull below. Same trigger boundary as the retained
  `ShouldSleep`. **Bedtime urgency** ramps 0→1 across 7–11 pm via `Animal.BedtimeUrgency()`.
- **equip / clothing** — fixed urgency (0.45, same for both) only when the slot is empty (else 0).
  Above the idle ceiling so a tool-less mouse gears up over loitering. Rarely fires (usually nothing
  to equip).
- **work** — `WorkOrderManager.BestWorkUrgency(animal)`: max over the animal's pickable orders
  (excl. craft) of `tierBase[priority] + proximityBonus + finishBonus`. Side-effect-free; an UPPER
  BOUND — when work wins, the existing tier-by-tier `ChooseOrder(1→2→3-excl-craft→4)` sequence does
  the actual reserve-on-commit pick (possibly a different order). Urgency decides *whether* to work,
  not which order. `IsPickable` mirrors `ChooseOrder`'s filters exactly — keep in sync.
- **craft** — `CraftUrgency()`: best recipe score `s` (Recipe.Score, unbounded 0..+∞) mapped into a
  fixed `[CraftFloor, CraftCeil]` band via `s/(1+s)`, so it competes on the same scale as tier
  urgencies and a needed recipe can never sink below the idle floor. `Recipe.Score` is a ratio of
  geometric means — `GM(input qty/target) / GM(output qty/target)` — so input count doesn't penalise
  a recipe. Internal recipe-first station selection still via `ChooseCraftTask` (shares
  `ScoreCraftRecipes` with the urgency calc).
- **leisure** — `LeisureUrgency()`: time-of-day bias × least-satisfied available need's pull. Internal
  pick via `TryPickLeisure` (shares `GatherLeisureCandidates`).
- **idle** — `IdleUrgency()`: always-available time-of-day baseline — the floor that low-value
  work/leisure must clear; when nothing presses, a mouse sometimes idles/chats.

**Score jitter:** every category score is passed through `Animal.Jitter(s) = s + (1-s)·N(0, stdev)`
— two-directional Gaussian, seeded `random`, save-reproducible — before ranking. The `(1-s)` headroom
factor means urgent scores barely move (pressing decisions stay deterministic) while low scores get
real variety. The normal tail occasionally produces a large nudge, so a mouse rarely does something
well off the obvious pick — and that tail is the probabilistic release valve that stops a busy mouse
from being permanently locked out of leisure. A nudge can push a low score below 0; `ChooseTask` then
skips that category for the tick (harmless). A 0 score stays 0.

**Current numeric ranges/values for every category live in the landscape block at the top of
`UrgencyConfig.cs`** (single source of truth — this section describes the *mechanism*, not the
numbers, to avoid drift). Tuning rationale: `plans/urgency-system.md`.

`ChooseOrder(animal, priority, exclude?)` (still used by the work category) only considers the single requested tier, optionally filters out a specific `OrderType`, filters to `res.Available()` orders, additionally skips haul orders where `stack.Available()` is false (stack fully reserved by in-flight tasks), distance-sorts remaining candidates, and on success calls `order.res.Reserve()` and assigns `task.workOrder = order`. Orders are **never removed when claimed** — they stay in the queue so they can be re-claimed after the task ends. WOM tasks are job-filtered via the `canDo` predicate on each `WorkOrder`.

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

### Reservation system

Tasks reserve sources and destinations during `Initialize()` via `Task.ReserveStack` / `Task.ReserveSpace`; both auto-released by `base.Cleanup()`. See SPEC-systems.md §Reservation Systems for the full mechanism.

**Tasks using destination reservation:** `HaulTask`, `ConsolidateTask`, `HaulToMarketTask`, `HaulFromMarketTask`, `SupplyFuelTask`, `DropObjective` (best-effort).

**Tasks (implemented):**

| Task | Source | Job | Description |
|------|--------|-----|-------------|
| `CraftTask` | WOM p3 | recipe's job | Navigate to station, fetch inputs, work, drop outputs |
| `HarvestTask` | WOM p2 | plant's `njob` | Navigate to plant, harvest when ready, drop products. Harvest orders only exist while `plant.harvestFlagged` is true. The flag is set by the player via the Harvest tool in the build bar, and also auto-set in `Plant.OnPlaced` so any plant the player blueprinted comes out flagged on completion (worldgen / save-load skip OnPlaced and keep their authored flag). `Plant.SetHarvestFlagged` registers / unregisters the order; `isActive = () => plant.IsDoneGrowing()` gates dormancy across grow cycles (and holds multi-tile plants until full height). |
| `HaulTask` | WOM p1/p3 | hauler | Fetch floor stack → Go to storage tile → DeliverToInventoryObjective into `building.storage` |
| `HaulToMarketTask` | WOM p3 | merchant | Haul items from storage to the market building to meet targets |
| `HaulFromMarketTask` | WOM p3 | merchant | Haul excess items from market back to storage |
| `ConstructTask` | WOM p2 | building's `njob` | Build or deconstruct a blueprint (both order types live at p2) |
| `SupplyBlueprintTask` | WOM p2 | building's `njob` | Deliver materials to an incomplete blueprint |
| `ResearchTask` | WOM p4 | scientist | Navigate to a specific lab, work in loops. Optionally borrows the matching tech book (via `bookSlotInv`) before research and returns it after — book grants 3× research progress per tick while equipped (see SPEC-books.md). |
| `ObtainTask` | survival | any | Fetch a specific item (food/equip) |
| `EepTask` | survival | any | Navigate home and sleep |
| `DropTask` | survival | any | Drop excess main inventory — prefers nearby storage/tank (10-tile bonus) over floor. `DropObjective` (shared by Craft/Harvest output drops + book returns) **retries across targets until the item is fully offloaded** — tops off the nearest storage, then spills the remainder to the next storage/floor (floor is the guaranteed sink), so a near-full crate no longer leaves a partial load stuck in the carrier's inventory. On no-reachable-target, or a visit that deposits 0 (discrete item / full floor tile), it stops: logs a warning and sets `animal.dropCooldownUntil = timer + 3f` so `ChooseTask` falls through instead of respawning every tick. The stuck-remainder give-up uses `Complete` (not `Fail`) so chained/best-effort callers aren't torn down |
| `GoTask` | survival | any | Navigate to a tile |
| `ChatTask` | leisure | any | Walk to idle partner, both leisure 20 ticks, grants socialization happiness |
| `LeisureTask` | leisure | any | Constructed with a `leisureNeed` string. `Initialize` delegates to `Nav.FindPathToLeisureSeat(filter)` — filter combines `leisureNeed` match + `Building.CanHostLeisureNow()` (disabled/broken/fuel/active-hour). Uses the standard Chebyshev-sort + first-fit-within-radius pattern (see §Path-cost radius gate), so it's consistent with `FindPathToStruct` etc. rather than a bespoke scan. Reserves the returned `seatRes[i]`. Leisure 15 ticks. On Complete grants `Happiness.NoteLeisure(need, structType.leisureGrant)` — `leisureGrant` lets cheap/always-on buildings (bench = 0.5) grant less than premium ones (fireplace = 1.0). |
| `ReadBookTask` | leisure | any | Fetch fiction book → walk to reading spot (prefers a reserved `bench` seat; falls back to any nearby unoccupied tile) → read 10 ticks (per-tick `NoteRead` grant) → return book to shelf. When seated at a bench, also grants the `"bench"` leisure need on Complete. See SPEC-books.md. |
| `DrinkTask` | leisure | any | Finds `rice wine` wherever it's stored (`Nav.FindPathItemStack` — a tank, the brewery's processor output, a floor pile), reserves 1 liang, walks there, drinks it in place, and grants `Happiness.NoteLeisure("alcohol", 1.5)`. No building, no seat — decoupled. Spawned by `TryPickLeisure` whenever rice wine exists in the world (same item-quantity trigger `ReadBookTask` uses). Rice wine has no `foodValue`, so hungry mice never pursue it. |
| `FillProcessorTask` | WOM p3 | cook | Loads a building's `Processor` with its input recipe — fetches the missing remainder of each input (resumable), delivers into `processor.inputBuffer`, and on completion starts fermentation (`Empty`→`Filling`→`Working`). WOM `FillProcessor` order, `isActive` while the processor is `Empty`. |
| `TapProcessorTask` | WOM p3 | cook | Walks to a building whose `Processor` has finished fermenting (`Ready`), taps it (`Processor.Tap()` drains the inputs, produces the output, → `Tapped`), and registers haul-out orders for the finished goods. WOM `TapProcessor` order, `isActive` while the processor is `Ready`. |
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

### Doored buildings & path start

Doored buildings (housing today; future production with interiors) declare an `interiorTiles[]` array in JSON. At construction time `Structure` allocates one off-grid `Node` waypoint per entry, edges adjacent interior nodes together, and edges each `doors[]` entry to its **approach tile** — the tile outside the door on the named side. `mirrored` flips both the entry `dx` (`nx-1-dx`) and `side` (left↔right) so the same JSON works in both orientations. The approach edge is the single graph bridge between outside and inside — A* routes mice in/out without Task code knowing about doors.

**`insideBuilding`** on `Animal` tracks the current logical container. It is a derived property — `TileHere()?.interiorBuilding` — never cached, so it cannot go stale when the animal is displaced by a fall / snap / elevator / load. `Tile.interiorBuilding` is the tile-level back-ref set and cleared by `Structure`'s interior-node setup/teardown; a mouse is "inside" exactly while it stands on an interior tile. Two consequences pivot on this flag:

- **Fall gate** — [AnimalStateManager.UpdateMovement](../Model/Animal/AnimalStateManager.cs) skips its "tile not standable → Fall" trigger when `insideBuilding != null`. The interior waypoint logically supports the mouse even though the underlying tile may be non-standable — critical for `preservesTile` buildings (burrow) whose interior tiles are solid dirt.
- **Path start** — `Animal.PathStartNode()` returns the nearest interior waypoint when `insideBuilding != null`, else `TileHere()?.node`. **All Nav `FindPath*` / `Navigate` callers use `PathStartNode()`**, not `TileHere().node` — using the raw tile node would orphan path requests from inside-the-building mice (solid-dirt nodes have no edges). Same helper is used by idle wander (`PickRandomNavNeighbour`) so an idle mouse inside the burrow steps out via the door edge instead of getting stuck.

**Placement gate** — `StructPlacement.CanPlaceHere` requires every door's approach tile to be `standable` at build time. Prevents players from placing a doored building (especially burrow) facing solid dirt or empty air and finding it unenterable post-build.

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

**Automatic job swapping** ([JobSwapper.cs](../Model/Animal/JobSwapper.cs)): when an idle animal has skills better suited to another *idle* animal's job (and vice versa), `JobSwapper.TrySwap` swaps them. Fires from `HandleIdle` every 2 ticks (stagger-phased by `tickCounter`). Score = `Σ (skillWeight × skillLevel)` using the job's `skillWeights` profile; swap commits when the combined score strictly improves. Only idle partners are considered — swapping with a busy mouse would discard in-progress work.

**`SetJob` task-interruption rule**: when the player (or a swap) changes an animal's job, `Animal.SetJob` only calls `Refresh()` if the current task is job-tied (`task.IsWork == true`, the default). Personal-needs tasks override `IsWork => false` — `EepTask`, `LeisureTask`, `ChatTask`, `ReadBookTask`, `DropTask` — and continue uninterrupted across a job change. Mixed-use tasks (e.g. `ObtainTask`, used for both food and equipment) stay work by default; the wasted fetch on a job swap is rare and self-corrects. New personal tasks should override `IsWork => false`.

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
| 2 | Construct, SupplyBlueprint, Deconstruct, Harvest, Maintenance |
| 3 | Haul (floor items + storage evictions), HaulToMarket, Craft |
| 4 | Research, HaulFromMarket |

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
| `Harvest` | Plant placed (`isActive` suppresses between grow cycles AND when every product item is at/above its global target — same gate as recipe `AllOutputsSatisfied`, via shared `Recipe.AllItemsSatisfied` static helper) | Plant destroyed |
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

`Task.MinHaulQuantity = 20` (0.20 liang). A move is skipped if it's below this threshold **and** wouldn't take the whole amount in play (drain the source stack / fit the entire carried load). This rule is centralised in `Task.MeetsHaulMinimum(amount, wholeAmount)` — every haul / consolidate / fuel / drop-target site (`HaulTask`, `ConsolidateTask`, `SupplyFuelTask`, `Nav.FindPathToDrop` / `FindPathToDropTarget` / `FindFloorConsolidation`) tests through it, so the threshold stays consistent. Use it rather than re-inlining the comparison.

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

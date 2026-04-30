# Shonei — Navigation, Inventory & Units

## Navigation

- **Algorithm**: A* with Euclidean heuristic. Edge costs vary by traversal type (see below).
- **Locomotion**: `speed = GetTravelSpeedMultiplier(animal) * edgeLength / edgeCost`. Edge info comes from `Graph.GetRawEdgeInfo()` (excludes road cost — road bonus is tile-based via `ModifierSystem`). A* pathfinding still uses `GetEdgeInfo()` with road-reduced costs so paths prefer roads.
- **Standability**: tile is standable if tile below is solid, has a platform/building, or has a ladder. Exception: a tile that itself contains a `solidTop` structure also occupying the tile below (i.e. the SAME structure body extends through both) is non-standable — see §Variable-shape structures. Per-tile override: a structure can declare specific local tiles as standable internal floors via `Structure.HasInternalFloorAt(localDx, localDy)`. Default is `false` (no change). Used by `Elevator` to make its bottom and top stops walkable surfaces inside the chassis even though the multi-tile body would otherwise block them. Future improvement: data-driven per-tile / per-shape config replacing the binary `solidTop`.
- **Vertical movement**: ladders produce direct node-to-node vertical edges (cost 2.0). Cliff climbing and stairs use **waypoint chains** (see below).
- **Road speed boost**: road bonus is per-tile — only the tile the mouse is currently standing on contributes its `pathCostReduction` (doubled to match old two-endpoint feel). No bonus from adjacent road tiles.
- **Floor item slowdown**: tiles with floor items reduce movement speed by 25% (×0.75).
- **Crowding slowdown**: tiles with multiple mice reduce movement speed by 25% (×0.75, flat regardless of count). All speed modifiers are multiplicative.
- **Tile occupancy tracking**: `AnimalController` maintains a `Dictionary<Tile, int>` for O(1) crowding queries. Animals register/unregister via `UpdateCurrentTile()` after position changes.
- **Helper queries**: `FindPathToBuilding`, `FindPathItemStack`, `FindPathToStorage`, `FindPathToLeisureSeat`, `FindPathToDropTarget`

### Connected-components reachability cache

`Graph` maintains a component ID on every `Node` (`node.componentId: int`, -1 = impassable). `Graph.RebuildComponents()` runs a BFS flood-fill over all standable nodes and assigns integer IDs; waypoint nodes get IDs transitively via neighbor edges. `Graph.SameComponent(a, b)` is an O(1) check.

- **Rebuild triggers**: `StructController.Construct()` (end of method, after all `UpdateNeighbors` calls) and `Graph.AddNeighborsInitial()` (startup/load). Cost ≈ 0.1–0.2 ms for a 100×50 map.
- **Usage as pre-filter**: `Graph.Navigate()` itself checks `SameComponent` before running A*, so all pathfinding automatically rejects unreachable targets in O(1). Individual search loops no longer need their own `SameComponent` calls.
- **`Nav.CanReachBuilding(StructType, r)`**: checks whether any building of a given type is in the same component — used by `PickRecipe`/`PickRecipeRandom` instead of a full A* scan.

### Edge dispatch (`EdgePolicy`)

Every edge whose cost or traversal differs from "plain horizontal walk" is governed by an `EdgePolicy` ([Assets/Model/EdgePolicy.cs](../Model/EdgePolicy.cs)). A single resolver, `Graph.ResolveEdgePolicy(from, to)`, returns the policy for any edge — or `null` for plain horizontal, which falls through to `ComputeEdge`'s default branch (road bonus + water modifiers). Both `GetEdgeInfo` (A*-facing, road bonus included) and `GetRawEdgeInfo` (locomotion-facing, road bonus excluded) collapse into the same `ComputeEdge(from, to, useRoadBonus)` helper.

Two dispatch sources:
- **Per-instance**: both endpoints carry the same policy reference (`from.edgePolicy == to.edgePolicy`). Today only `Elevator` uses this (its `ElevatorEdgePolicy` wrapper, set on both stop tile-nodes in the constructor). Future transit types (trains, trams) will follow the same pattern with their own wrapper subclasses.
- **Geometric**: ladder, cliff vertical / stair diagonal middle legs, and cliff/stair/workspot approach edges resolve to four singleton constants (`LadderPolicy.Instance`, `CliffPolicy.Instance`, `StairPolicy.Instance`, `WaypointApproachPolicy.Instance`) based on the `isWaypoint` flag and `dx`/`dy` between endpoints.

`EdgePolicy`'s contract: `GetEdgeInfo` (cost, length), `PreventFall` (default `true` — every special edge suppresses falls), `SuspendsLerp` (default `false` — true only for transits where an external system drives position), `OnApproach(animal, from, to)` (called every Nav.Move while this edge is the next step; idempotent), `OnPathCommit(animal)` / `OnPathRelease(animal)` (called once when a path scan adds/drops this edge — used by transits for tentative queue-depth reservations).

`Nav.MoveCore` reads the policy, applies `preventFall = policy.PreventFall`, calls `OnApproach`, and bails the lerp if `SuspendsLerp`. The `ridingElevator != null` early-return stays at the top of `MoveCore` (it's animal state, not edge state — and it must run before any policy lookup so a one-frame race during `Elevator.Destroy` where `edgePolicy == null` while `ridingElevator` is still set stays safe).

### Waypoint system (stairs and cliff climbs)

Both stairs and one-block cliff climbs are represented as **waypoint chains** — intermediate `Node` objects with fractional world positions, not backed by tiles. They are stored in `stairWaypoints` and `cliffWaypoints` dictionaries and rebuilt whenever nearby tiles change.

**Cliff climb** (one solid block beside a standable tile): `base → wp1 → wp2 → cliffTop`
- `wp1` at `(base.x + dir×0.25, base.y)` — 0.25 tiles from base
- `wp1 → wp2` vertical (governed by `CliffPolicy`)
- `wp2` at `(base.x + dir×0.25, base.y+1)` — 0.75 tiles from cliff top

**Stair** (stair tile): `left → entry → exit → right`, where entry/exit are 0.5-tile offsets from their endpoints; the diagonal entry→exit step is governed by `StairPolicy`.

`preventFall` in `Nav.Move()` is set from `policy.PreventFall` — true for every waypoint, ladder, and transit edge. Direct non-standable tile traversal no longer occurs — all such paths go through waypoints.

### Workspot waypoints (off-grid worker pose)

A third use of the waypoint system: structures that want their worker to stand at a fractional position rather than on an integer tile centre. Authored via `StructType.workSpotX/workSpotY` (see SPEC-data.md). The wheel uses this so the runner sits centred between the 2×2 footprint columns, slightly above the ground.

Mechanism:
- `Structure.workNode: Node` is set in the constructor. If a workSpot is declared, a fresh waypoint Node is created at `(anchor + (mirrored)workSpotX, workSpotY)` and edged bidirectionally to the bottom-row tile-node closest to `workSpotX` (tie → lower x). Otherwise `workNode = workTile.node` (today's behaviour, no graph change).
- Path-targeting code (`Nav.FindPathToStruct`, `Nav.CanReachBuilding`, `CraftTask`) routes to `s.workNode` instead of `s.workTile.node`. `WorkOrderManager.RegisterWorkstation`'s distance heuristic uses `workNode.wx/wy`.
- `Animal.target` is a `Node` (not `Tile`); the arrival snap in `AnimalStateManager.UpdateMovement` reads `target.wx/wy`. Works for both tile-backed and waypoint endpoints.
- Cleanup: `Structure.Destroy()` removes the waypoint's reciprocal edges and clears `workNode` so neighbour tile-nodes don't dangle.

**Critical load invariant**: waypoint registration runs in the `Structure` constructor — *not* `OnPlaced()`, which is gameplay-only. `Structure.Create()` runs the constructor on both gameplay and load paths. Standability isn't checked when picking the connecting tile because at load time the constructor runs in `SaveSystem` Phase 2 *before* `graph.Initialize()` (Phase 4) sets `node.standable`. The picked tile is implicitly standable post-placement (placement validates it), and Phase 4's `RebuildComponents` BFS picks up the workspot via its neighbour edge.

Out of scope (v1):
- Per-seat workSpots (multi-tile leisure buildings still use integer seats).
- Standability-change refresh: if the connecting tile becomes non-standable, the waypoint stays edged to it. For the wheel this can't happen without destroying the wheel itself (in which case `Destroy()` cleans up).

### Transit (elevators)

Capacity-1 vertical lifts that A* sees as a single graph edge. The two stop tiles (anchor and `y+ny-1`) are the elevator's footprint endpoints, both made standable via the new `Structure.HasInternalFloorAt` per-tile override. Middle column tiles stay non-standable, so the chassis is an obstacle except via the stops.

Mechanism:
- `Elevator` constructor instantiates one `ElevatorEdgePolicy(this)` (a sister class wrapping the elevator), assigns it to both stop tile-nodes' `Node.edgePolicy`, and adds a direct neighbour edge between them — the "transit edge". `RebuildComponents` picks it up automatically. `Graph.IsNeighbor` recognises the shared policy reference and preserves the edge across `UpdateNeighbors` filtering even though the two nodes aren't geometrically adjacent.
- Edge cost flows through the `EdgePolicy` dispatch (see §Edge dispatch above): `Graph.ResolveEdgePolicy` returns the `ElevatorEdgePolicy`, whose `GetEdgeInfo` calls back into `Elevator.EstimatedTransitCost`. Cost is `travelTicks + queueDepth × avgTrip`, with `avgTrip` from a rolling 20-sample buffer (`recentTripTicks`) and an optimistic cold-start fallback. Returns `+∞` when the elevator's network can't supply nominal demand → A* drops it from the candidate set.
- Riding handoff in `Nav.Move`: when the next path step is a transit edge, the resolver returns the elevator's policy; `Nav.MoveCore` calls `policy.OnApproach` (which calls `Elevator.RequestRide` if not already reserved), sets `preventFall = policy.PreventFall` (= true), and bails the lerp because `policy.SuspendsLerp` is true. The elevator's per-tick state machine (Idle → MovingToBoardingFloor → Riding → Unloading → Idle) eventually loads the passenger and drives their `(x, y)` from the platform's position. On arrival it calls `Nav.OnTransitComplete`, the rider snaps to the destination stop, and normal navigation resumes. **The boarding-tile `preventFall = true` is load-bearing** even though the boarding tile is itself standable: there's a one-frame seam between `Elevator.Tick` setting `ridingElevator = this` and the next `Nav.Move` re-asserting preventFall via the riding branch. `ElevatorPlatform.Update` runs per-frame and may drag the passenger into the non-standable chassis interior before `Animal.Update` runs for that mouse, so `UpdateMovement`'s fall check would read stale-false on the first riding frame without it.
- Cascading Tick: a `do-while` loop processes instant transitions (Idle → trip start, Unloading → Idle → next trip) in the same tick, while movement-bearing cases (MovingToBoardingFloor, Riding) always return after a single `AdvanceTowards` so the platform never advances more than `PlatformSpeed` tiles per tick. Net effect: a mouse arriving at a parked platform boards AND gets the first riding step in one tick, vs. the ~2-tick delay we'd otherwise see from sequential state transitions.
- Boarding gate (visual settlement): `MovingToBoardingFloor` only fires `BoardPassenger` when *both* discrete arrival (`currentY ≈ targetY`) and visual settlement (`IsPlatformVisuallySettled`: platform child's `transform.localPosition.y` within ~0.05 of `currentY − 1`) are true. The per-frame visual lerp lags discrete `currentY` by up to ~1 tile, so without the visual gate the rider — driven by the lagged platform position the moment `passenger` is set — would jolt up to the still-descending visual and visibly fall back to the parked tile as the lerp catches up. Adds ~1 extra discrete tick of post-arrival wait at 1× game speed; the platform finishes descending normally, then the mouse is loaded with no visual jolt. The parked-already path through `StartTrip` skips the gate (the visual is already settled).
- Power gating is inclusive everywhere: cost gate, Idle→Trip start, and Riding advance all use `IsPowerAvailable` (network's raw supply + storage discharge ≥ `MaxTickDemand`). The strict "actually allocated this tick" check (`PowerSystem.IsBuildingPowered`) is intentionally NOT used for the per-tick advance gate — it caused mid-trip platform freezes during normal allocator-rotation gaps. Trade-off: in pathological tight-network setups the platform may "advance without strict allocation" for one tick before catching up, which we accept.
- Demand semantics: `CurrentDemand` is proportional to *this tick's intended motion* — `PowerPerTile (= 0.5) × min(PlatformSpeed, |targetY − currentY|)`. Both `MovingToBoardingFloor` (empty-cabin fetch) and `Riding` draw; `Idle` and `Unloading` draw 0. Total power for a trip = `0.5 × tilesTravelled` exactly (the partial-last-tick is naturally proportional). InfoPanel may show fractional values like `consuming 0.6` mid-trip and `consuming 0.3` on the partial last tick.
- Tentative reservations: `Nav.Navigate` resolves each path edge's policy and calls `OnPathCommit(animal)` on it; `EndNavigation` drains by calling `OnPathRelease(animal)` on each. `ElevatorEdgePolicy` routes both to `AddTentativeReservation` / `RemoveTentativeReservation` on the elevator's `pendingAnimals` set, counted into `EstimatedTransitCost`'s queue depth so simultaneous planners see realistic wait. Constant policies (cliff/stair/ladder/approach) default to no-ops.
- Mid-flight abort: each `RideRequest` carries an `abortAtTick = currentTick + max(30, 3 × queueDepth × avgTrip)`. `AbortStaleQueueEntries` (top of `Tick`) bails any non-actively-served mouse whose patience expired. Covers queued mice on unpowered or stuck elevators.
- Sustained-stall abort: the active head is deliberately *not* bailed by `abortAtTick` — once we've committed to that trip, we don't want to drop a mouse mid-ride just because the patience budget ran out before they boarded. Instead `AbortStalledActiveHead` (also top of `Tick`) tracks `lastAdvanceTick` (updated on real platform motion in `AdvanceTowards` and on entry to `MovingToBoardingFloor` / `Riding`) and bails the head if `currentTick − lastAdvanceTick > StallAbortTicks` (60) while in a moving state. Catches indefinite stuck-rider scenarios on a permanently-frozen network.
- Animation: `Nav.IsLocomoting` is true only when `Move()` is actively translating the animal — false while parked at a transit edge or loaded on the platform. `AnimationController.UpdateState` reads it for the `Moving` state and picks the idle clip in those cases (state stays `Moving` because the task is still in progress, but the mouse isn't actually walking). Refresh fires at all navigation lifecycle boundaries.
- Visuals: `ElevatorPlatform` and `ElevatorCounterweight` MonoBehaviours (in `Components/`) lerp child GameObjects' `localPosition.y` per frame at `PlatformSpeed` tiles/sec. Platform drags the loaded passenger so the rider moves smoothly between ticks; counterweight tracks `ny - 1 - currentY`. The platform sprite sits one tile *below* the rider (it's the floor they're standing on, not the floor they're standing in). Sprites: `elevator_platform.png`, `elevator_counterweight.png`.
- Save/load: `currentY` and the two history buffers (`recentTripTicks`, `recentEndToEndTicks`) persist via `StructureSaveData.elevatorCurrentY` / `elevatorRecentTripTicks` / `elevatorRecentEndToEndTicks`. Dispatch state, queue, and pending reservations are NOT persisted — they reset to Idle/empty on load (animals lose their tasks across save boundaries anyway).

Out of scope (v1):
- Multi-stop elevators (intermediate floors).
- Capacity > 1 (cabin elevators).
- Horizontal transit (trains, trams) — the `EdgePolicy` abstraction is ready to host these via parallel wrapper classes (`TrainEdgePolicy` etc.); a shared `ITransitVehicle` interface might emerge once a second concrete transit type exists.
- Component-connectivity teardown when unpowered: the transit edge stays in the neighbour list regardless of power, so `Graph.SameComponent` reports both stops as connected even when the cost is `+∞`. `WithinRadius`-gated callers reject the doomed path; `PathTo` callers without a cost gate accept it. Acceptable for now; revisit if a use case actually breaks.

---

## Inventory System

Six inventory types:

| Type | Slots | Stack Size | Decay Rate | Notes |
|------|-------|-----------|-----------|-------|
| Animal | 5 | 1000 fen | none | General-purpose carry inventory |
| Storage | varies | varies | normal | `allowed` dict restricts item types |
| Floor | 4 | 1000 fen | 5× normal | Created/destroyed dynamically; up to 4 item types can share a tile |
| Equip | 1 | varies | normal | Animal equip slots (food, tool, clothing) |
| Market | varies | varies | none | Market building only; set via `SetMarket()` on a Storage inv |
| Fuel | 1 | varies | none | Internal building resource (torch wood, furnace coal). No sprite, no tile. See below. |

### Inventory ownership

- **`tile.inv`** is always a Floor inventory or null. Never Storage/Market.
- **Storage/Market inventories** live on `building.storage`. Created in Building constructor, auto-registered with `InventoryController.byType` via the Inventory constructor. Not placed on `tile.inv`.
- **`FindPathToInv`** iterates `InventoryController.byType[type]` — not tiles — so pathfinding works for all inventory types regardless of tile ownership.
- **`FindPathToStorage`** returns `(Path, Inventory)` — callers destructure to get both the navigation target and the storage inventory directly.
- **`FindPathItemStack`** returns `(Path, ItemStack)` using the inventory from `FindPathToInv`, not `path.tile.inv`.

### Fuel Inventory (`InvType.Reservoir`)

Created by `Building` constructor when `structType.hasFuelInv = true`. Fields:
- Not tied to a tile position (x/y = building anchor, used for nav targeting only)
- No sprite, no decay
- All item types accepted (no `allowed` filter at inv level)
- Not searchable as a haul source (`GetItemToHaul`, `HasItemToHaul`, `GetStorageForItem`, and `FindPathItemStack` all exclude Fuel invs)
- Registered with InventoryController for GlobalInventory tracking

WOM registers a standing `SupplyBuilding` (priority 3) order when a fuel building is placed (via `Building.OnPlaced()`). `isActive` suppresses the order when `building.reservoir.NeedsSupply()` is false. Haulers fulfill it via `SupplyFuelTask` + `DeliverToInventoryObjective`. All runtime state lives in the `Reservoir` component (`building.reservoir`), not on Building directly.

JSON fields on StructType:
- `hasFuelInv: bool` — opt-in
- `fuelItemName: string` — group or leaf item (e.g. `"wood"`)
- `fuelCapacity: float` — max fen (liang in JSON, × 100 in `OnDeserialized`)
- `fuelBurnRate: float` — liang/day consumed; `Reservoir.Burn()` converts to fen/sec using `World.ticksInDay`

`LightSource` holds a back-ref to its owning `Building` (`ls.building`, set in `Building.cs` when the light is attached). While `building.disabled` is true, `LightSource.Update()` skips `Reservoir.Burn()` and sets `isLit=false` — so disabling a torch immediately stops fuel consumption and emission. Refuel hauls are also gated off via the WOM `isActive` check, so a disabled fuel building neither receives nor consumes fuel.

`isBuilding: true` on StructType makes StructController use the `Building` class for any depth (e.g. foreground torches at depth 2). `tile.building` (= `structs[0] as Building`) is still depth-0 specific; fuel buildings at other depths are accessed directly via task/WOM references.

- Items decay over time (Floor fastest; Animal/Market never; Equip at normal rate)
- **Discrete items** (`Item.discrete = true`, e.g. tools): always stored/moved in whole-liang (100 fen) multiples; decay removes whole items only; display shows integer count. Adding a non-multiple-of-100 quantity logs a warning.
- `allowed` dict filters what item types a storage accepts (all allowed by default for other types)
- `Reservable` (capacity-based) prevents multiple animals targeting same resource. Has two fields: `capacity` (hard max from JSON) and `effectiveCapacity` (player-adjustable; defaults to `capacity`); `Available()` gates on `effectiveCapacity`. **Not** created for workstation buildings — WOM Craft orders own their reservation directly.
- `Produce()` adds to inventory and global inventory simultaneously; `MoveItemTo()` moves between inventories without touching global inventory
- `AddItem()` is private — always use `Produce` or `MoveItemTo` externally
- **Group-item wildcard**: `Quantity`, `ContainsAvailableItem`, `GetItemStack`, `AvailableQuantity`, and `MoveItemTo` all expand group items to their leaf descendants (`MatchesItem` helper). Passing `"wood"` to any of these matches oak/maple/pine transparently. `AddItem` and `GlobalInventory.AddItem` reject group items with a `LogError` — only leaf items may physically exist in inventories. `MatchesItem(candidate, query)` is `public static` — use it externally when matching a leaf iq.item against a group cost.item (e.g. in `DeliverToBlueprintObjective`). `MoveItemTo` uses the private `GetLeafStack` (not `GetItemStack`) for group resolution: `GetLeafStack` does not require `Available()` (the caller already holds the reservation) and picks the leaf type with the highest combined quantity, then the smallest individual stack of that type.
- **`InventoryController.byType`**: `Dictionary<InvType, List<Inventory>>` maintained alongside the flat `inventories` list. Use for type-filtered lookups (e.g. iterate only Storage invs) instead of tile scans. All add/remove/type-change paths go through `AddInventory`, `RemoveInventory`, `MoveInventoryType`.
- **`ValidateGlobalInventory()`**: sums all registered inventory stacks and compares against `GlobalInventory.itemAmounts`; called at end of save load. `LogError`s any mismatch.

### Item Falling

When a tile or building change reduces standability, items on the tile above that is no longer standable fall straight down to the nearest standable tile.

- **Trigger**: `StructController.Construct()` and `Structure.Destroy()` both call `World.FallIfUnstandable(x, y+1)` after updating the nav graph. This covers tile mining, building placement, and building removal.
- **`World.FallIfUnstandable(x, y)`**: no-op if the tile has no items or is still standable; otherwise calls `FallItems`.
- **`World.FallItems(tile)`**: scans downward from `tile.y−1` for the first standable tile (`landing`). Moves all stacks via `MoveItemTo` (no GlobalInventory double-count). Fires `World.OnItemFall` to trigger the fall animation (subscribed in `WorldController`). Any items that can't fit in the landing inventory are subtracted from GlobalInventory and logged as a warning before the source inventory is destroyed. Same applies if no landing tile exists at all (e.g. items at y=0).
- **Mixing**: `PutOnFloor` / `ProduceAtTile` prevent different item types from being placed on the same floor tile normally. `FallItems` bypasses this deliberately — a floor tile can hold up to 4 types after a fall.
- **GlobalInventory**: `MoveItemTo` is used (not `Produce`) so no double-counting occurs. Lost items are explicitly removed from GlobalInventory before destruction.
- **Fall physics constants** (`World.cs`): `fallSecondsPerTile = 0.4f` (time to fall one tile), `fallGravity = 2 / fallSecondsPerTile²` (12.5 tiles/s²). Both item animation (t² ease-in over `fallSecondsPerTile × dist` seconds) and mouse falling (velocity accumulation) are derived from these constants. Animation spawned in `WorldController.ItemFallAnimCoroutine`.

### Equip Slots

Each animal has three `InvType.Equip` inventory instances (1 stack each, registered with InventoryController for GlobalInventory tracking, no sprite, decay at normal rate):

| Slot | Field | Capacity | Purpose |
|------|-------|----------|---------|
| Food | `foodSlotInv` | 500 fen (5 liang) | Carries food for eating |
| Tool | `toolSlotInv` | 1000 fen | Equipped tool (work speed bonus) |
| Clothing | `clothingSlotInv` | 200 fen | Equipped clothing (temperature comfort bonus) |
| Book | `bookSlotInv` | 100 fen (1 liang) | Carries a book during research / leisure reading. Class-restricted: only accepts `ItemClass.Book`. See SPEC-books.md for the borrow/return flow. |

**Clothing system**: `Db.clothingItems` lists all items whose parent chain includes `"clothing"`. `FindClothing()` in `ChooseTask()` equips one clothing item into `clothingSlotInv` when idle (after tool equip, before work orders). `Happiness.UpdateComfortRange()` adjusts `comfortTempLow`/`comfortTempHigh` by ±3°C when any clothing is equipped, and additionally widens `comfortTempLow` by up to 5°C from the fireplace warmth buff. Clothing items are discrete (like tools) and decay at normal rate in equip slots.

**Clothing overlay**: equipped clothing renders as a child `SpriteRenderer` ("ClothingOverlay") on the Animal prefab, assigned to `AnimationController.clothingRenderer`. Sprites loaded by item name from `Resources/Sprites/Animals/Clothing/{itemName}/` (`idle`, `walk`, `eep` — walk and eep fall back to idle if missing). `AnimationController.UpdateClothingOverlay()` swaps sprite on state change; `LateUpdate` syncs `flipX`. Adding a new clothing visual = add sprites to a new folder, no code changes.

**Food acquisition flow:**
1. Animal gets hungry → `FindFood()` checks `foodSlotInv` for room
2. If room exists, creates `ObtainTask(food, amount, foodSlotInv)` — item goes to slot, not main inventory
3. `HandleNeeds()` eats from `foodSlotInv`: full meals (≥100 fen) restore `foodValue` and grant full satisfaction; partial meals (remaining fen) scale both nutrition and satisfaction proportionally

**Key methods on `Animal`:**
- `Unequip(slotInv)` — moves slot contents back to main inventory (leftover stays in slot if inv full)

**`ObtainTask` / `FetchObjective`** both accept an optional `Inventory targetInv` to route pickup into an equip slot instead of main inventory.

**Partial fills:** Equip slot fetches (`targetInv != null`) accept whatever quantity was available on the source tile and complete without retrying across multiple tiles. This is intentional — food/tools are useful at any amount, and the mouse will re-fetch next time it's hungry/unequipped. Crafting fetches (`targetInv == null`) do retry across tiles until the full amount is collected.

### Blueprint inventory

`Blueprint` has its own `Inventory inv` (`InvType.Blueprint`, not registered with InventoryController — no decay, no tick overhead). Materials are delivered into it via `MoveItemTo` from the animal's inventory. On `Complete()`, `inv.Produce(item, -qty)` is called for each cost item to decrement GlobalInventory (the items were already counted in GlobalInventory when originally harvested). On cancel (`BuildPanel.Remove`), materials are returned to the floor via `MoveItemTo`.

One slot per cost item (`stackSize = cost.quantity`). Because a slot can only hold one leaf type, `LockGroupCostsAfterDelivery()` is called after the first delivery of each group cost: it updates `blueprint.costs[i].item` from the group (e.g. "wood") to the specific leaf delivered (e.g. "pine"). Subsequent `SupplyBlueprintTask` initializations read the locked type and fetch only that leaf, avoiding slot conflicts.

Stacks are bound to their cost slot via `Inventory.slotConstraints[i] = costs[i].item` (set in the `Blueprint` ctor). `Inventory.AddItem` consults this when adding (positive quantity, non-`force`) and skips any stack whose constraint doesn't match the incoming item via `MatchesItem` — so a small-quantity cost item delivered first can't squat in a slot sized for a different, larger cost. The constraint stays as the original (often group) item even after `LockGroupCostsAfterDelivery` swaps `costs[i].item` to a leaf — the locked leaf still matches the group constraint, so no update is needed. Subtraction (`quantity < 0`) and `force: true` paths bypass the filter so misrouted items can still be removed and overflow returns can land anywhere.

`SaveSystem.RestoreBlueprint` routes each saved stack to the matching cost slot (by `Inventory.MatchesItem(item, bp.costs[j].item)`) rather than trusting the saved stack index. This heals saves written before slot-constraint routing existed, where order-dependent delivery could leave items in the wrong stack.

`SupplyBlueprintTask.Initialize()` commits to a specific leaf *before* pathfinding via `PickSupplyLeaf`: it walks the group's leaf tree and picks the leaf with the highest `GlobalInventory` quantity. This prevents collecting a mix of leaf types that would lock the blueprint to whichever happened to be delivered first — potentially a scarce one (e.g. 2 oak when 20 pine is available). `DeliverToBlueprintObjective` uses `Inventory.MatchesItem(iq.item, cost.item)` for the cost-slot lookup so a leaf `iq.item` correctly matches an unlocked group `cost.item`.

---

## Water System (added 2026-03-19, rendering overhauled 2026-03-20)

`Assets/Controller/WaterController.cs` — singleton MonoBehaviour; must be in the scene.

**Data**: `Tile.water` (`ushort`, 0–160). `WaterController.WaterMax = 160` = fully filled tile. The 10× internal scale (instead of 0–16) eliminates the integer truncation dead zone in the spread formula (`diff/2 == 0` when `diff == 1`), which would otherwise leave water visibly stuck in a staircase. The dead zone shrinks to 1/10 of a visual unit — sub-pixel. Only non-solid tiles hold water.

**Simulation**: `TickUpdate()` called every 0.2 s from `World.Update()`. Three passes, bottom-to-top:
1. **Fall** — pour water straight down (`flow = min(tile.water, WaterMax - below.water)`).
2. **Spread** — equalize with one horizontal neighbor (`flow = (tile.water - neighbor.water) / 2`). Direction alternates left/right each tick to avoid directional bias.
3. **Look-ahead equalization** — fixes diff-1 slopes that Pass 2 can't resolve (truncates to 0). When a tile is exactly 1 unit below its sweep-direction neighbor, scans further for a tile at +2 or higher and pulls 1 unit from it.

Volume is conserved exactly (integer math, explicit transfers).

**Rendering**: GPU shader pipeline — zero per-frame CPU work.
- `Assets/Lighting/Water.shader` (`Water/WaterSurface`) — URP 2D unlit sprite shader. Reads a 1-byte-per-pixel R8 surface mask, returns: transparent (0) / shimmer lerp (0.5) / surface highlight (1.0). Per-pixel shimmer uses `_Time.y` (frame-rate driven on GPU).
- Surface mask texture (`TextureFormat.R8`, 1600×800 for 100×50 world): rebuilt on the CPU every 0.2 s (sim tick only). Encodes: `0`=no water, `127`=interior water, `255`=surface pixel (any of 8 orthogonal+diagonal neighbours is open air — non-solid, non-water). Water touching solid walls is NOT highlighted.
- World-spanning `WaterSprite` GameObject: 1×1 white pixel sprite at PPU=1, scaled to `(nx, ny)` Unity units, placed at `(−0.5, −0.5)`. sortingOrder=2. Must be on the **`Water`** Unity layer, excluded from `LightFeature` litLayers and SkyCamera culling mask.

**Pump draining**: `PumpBuilding` (`Assets/Model/Structure/PumpBuilding.cs`) is a depth-0 Building subclass for the pump (id 140, nx=2). It overrides `ConditionsMet()` to suppress the WOM Craft order when the source tile has no water. After each completed craft round, `AnimalStateManager` calls `pump.DrainForCraft()`, which subtracts `WaterDrainPerRound` units from the tile at `(x+1, y-1)` (directly below the pump head). Drain only happens when a mouse is actively pumping — not on a passive timer. `WaterDrainPerRound` is a private const; see the file for the current value.

**Mouse speed**: Water on either endpoint of a horizontal nav edge doubles the A* edge cost (→ 0.5× speed). Applied in `Graph.GetEdgeInfo()`.

**World gen**: `WorldController.GenerateDefault()` seeds `water=WaterMax` at y=9 for x=[0,3] and x=[30,40], first clearing those tiles to empty.

**Save/load**: `WorldSaveData.waterLevels` — flat `byte[]`, index `y * nx + x`. Omitted (null) if all-dry. Restored in `SaveSystem.ApplySaveData()` before tile types are applied.

**ClearWorld**: `WaterController.ClearWater()` zeros all `tile.water`, clears the surface mask texture, and calls `UpdateSurfaceMask()`.

## Soil Moisture (added 2026-04-22)

Distinct from liquid `tile.water` — moisture represents damp **soil**. Lives on `Tile.moisture` as a `byte` (0–100 percent) and is only meaningful on **solid** tiles (dirt/stone). Air tiles keep their moisture at 0 by convention; all moisture sweeps skip non-solid tiles. Drives plant growth gating; see SPEC-data.md `plantsDb.json` for the per-plant comfort-range fields.

**Per in-game second** (1 s real-time, dispatched from the 1 s block in `World.Update` — both run before `PlantController.TickUpdate` so plants' `Grow()` sees freshly-updated soil):
- **Rain uptake** (`MoistureSystem.RainUptakePerSecond()`, soil whose immediate tile-above isn't a ceiling): gains `round(rainAmount × MoistureRainGainPerHour / TicksPerInGameHour)` (currently 10 at full rain — i.e. 100/h spread over 10 one-second slices). "Ceiling" = tile directly above is solid ground OR carries any `solidTop` structure; see `MoistureSystem.CapsSoilFromAbove`. This is *not* a full sky-trace — a detached overhang higher up doesn't block rain, only the tile immediately at y+1 matters. Simpler + avoids the asymmetry where a single ceiling layer would slip through a sky-trace (its tile type is non-solid; its `solidTop` struct flag would be missed).
- **Water-neighbour seep** (`MoistureSystem.SeepPerSecond()`): each solid tile with moisture headroom picks its wettest 4-orthogonal water neighbour and converts `MoistureSeepWaterPerSec` water units (currently 1) into `MoistureSeepGainPerWater` moisture each (currently 10). Water is actually drained from the source tile — unlike rain, seep is conservative. A dry soil tile next to a full water tile saturates in ~10 s real-time; a 1-tile pond feeding 4 solid neighbours drains to empty in ~40 s under load. Partial-fill soil (near saturation) still pays the same water-per-moisture rate via ceil-divide — no free moisture. Pump-irrigated farms stay wet only while the pump keeps refilling water.

**Per in-game hour** (10 s real-time, via `MoistureSystem.HourlyUpdate()`, called from `WeatherSystem.OnHourElapsed()`). Single snapshot-and-sweep so no step biases by sweep direction, followed by a plant-iteration pass:
- **Soil-to-soil diffusion** (all solid tiles): pull `round(diff × MoistureDiffusionPerHour)` toward the wettest solid neighbour's snapshot value, where `diff = maxNeighbour − cur`. One-way (never lowers). Currently `MoistureDiffusionPerHour = 0.05` (5%/h). Approximates capillary spread — a water-adjacent stone wall's moisture slowly propagates inward, a rained-on surface row slowly wets the column below.
- **Evaporation** (same "not capped" gate as rain): `−MoistureEvaporationPerHour` (currently 1). Not temperature-scaled. Clamped ≥ 0. Capped soil (under buildings / stone / platforms) holds baseline without drying, so cave farms / deep nurseries / covered growhouses stay viable without irrigation.
- **Plant passive draw**: each live plant pulls `round(plantType.moistureDrawPerHour)` from the soil tile directly below (clamped ≥ 0; no penalty when undersupplied — only the advancement cost gates growth). Default 2; overridable per `plantsDb.json` entry via the `moistureDrawPerHour` field.

**Worldgen seed**: `WorldGen.SeedMoisture` sets every solid tile to `StartingMoisture = 50` at world generation so plants can grow from turn 1. Surface soil then drifts from this baseline under rain/decay; underground holds unless a water neighbour bumps it higher.

## Plant Growth

Plants advance through discrete growth stages, stored as `growthStage` on `Plant`. Ticked once per in-game second by `PlantController.TickUpdate → Plant.Grow(1)`.

**Gates on every tick**:
1. **Comfort**: `plantType.IsComfortableAt(soilTile, weather)` — ambient temp AND the moisture of the tile directly below the plant must fall within the JSON-authored `[tempMin, tempMax]` / `[moistureMin, moistureMax]` ranges. Out-of-range returns early, freezing both age and stage.
2. **Stage advancement** (only when the tick would push `growthStage` higher): costs `2 × plantType.moistureDrawPerHour` from the soil tile below. Can't afford → freeze.
3. **Height extension** (only when advancement lands in a new height band): every new tile above must be non-solid with `structs[0] == null`. Any blocker → freeze.

**Height mechanic** (multi-tile plants with `maxHeight > 1`):
- Max stage = `4 × maxHeight − 1`. One height tile per 4 growth stages.
- `height = 1 + growthStage / 4`. Derived, not persisted — rebuilt on load by `Plant.RebuildExtensionTiles()`.
- When a stage crossing triggers height increase, Plant claims the tile at `y + h` via `tile.structs[0] = this` and spawns a child `GameObject` + `SpriteRenderer` for rendering. Placement code (`StructPlacement.CanPlaceHere`, `StructController.Construct`) sees this and blocks new *depth-0* placements there. Other depths (shafts, roads, foreground decorations) are allowed to coexist with the plant — visual clipping inside the trunk is accepted as a trade-off for the freedom.
- Rendering: the topmost occupied tile shows `g{stage % 4}`, every tile below shows `g4` (stalk continuation). Bamboo requires `g0..g4` in `Sprites/Plants/Split/bamboo/`.
- Harvest yield scales linearly with `height` at harvest time. Harvest releases all extension tiles and resets `age = 0`, `growthStage = 0`.
- `Plant.Destroy()` releases all extension tiles.

**`Mature()` shortcut** (worldgen): sets age + stage directly to max, calls `RebuildExtensionTiles()` which claims as many upper tiles as the geometry allows. Skips the moisture advancement cost (fresh soil isn't guaranteed wet yet) and silently tops-out below `maxHeight` if the world above the anchor is blocked.

**Plant growth gate**: a plant occupies an air tile; `Plant.Grow()` reads moisture from the **solid tile directly below** and calls `plantType.IsComfortableAt(soilTile, weather)`. Returns early (skips the age increment) if `WeatherSystem.temperature` or `soilTile.moisture` is outside the plant's authored `[tempMin,tempMax]` / `[moistureMin,moistureMax]`. Null bounds = "no limit" on that side, so a plant with no ranges grows unconditionally (back-compat for content authored before this system). If there is no tile below (world bottom edge), the moisture check is skipped — not failed.

**Save/load**: `WorldSaveData.moistureLevels` — flat `byte[]`, index `y * nx + x`. Omitted (null) when every tile is 0, mirroring `waterLevels`. Restored in Phase 1 of `ApplySaveData` alongside water.

**ClearWorld**: `MoistureSystem.Clear()` zeros `tile.moisture` on every tile. Called from `WorldController.ClearWorld()` right after `WaterController.ClearWater()`.

**InfoPanel display**: `TileInfoView` shows `moisture: N/100` on any solid tile. `StructureInfoView` (for a Plant) shows `temp: now°C  comfort: lo–hi°C` and `moisture: now/100  comfort: lo–hi`, with the current moisture read from the soil tile below — same source the growth gate uses. Null comfort bounds render as `—`.

### Plant slowdown estimation (vs. an unmoisturized baseline)

Use this whenever you're balancing a plant against the moisture gate and want a quick "how much slower than `growthTime` ticks?" number for an isolated outdoor plant under average rain. Plug in current parameters — the *shape* of the calculation stays valid even if specific constants change.

**Inputs from elsewhere** (look up at time of derivation):
- Rain Markov per hour: `pClearToRain`, `pRainToClear` (currently 0.04 / 0.12) → steady-state `pRain = pClearToRain / (pClearToRain + pRainToClear)` (currently 0.25). Mean rain bout `1/pRainToClear` (8.3 h); mean dry stretch `1/pClearToRain` (25 h).
- Hourly inflow at full rain `R` = `MoistureRainGainPerHour` (100). Outflow per planted tile `D` = `MoistureEvaporationPerHour + plantType.moistureDrawPerHour` (currently 1 + 2 = 3).
- Plant comfort window `[mLo, mHi]` from `plantsDb.json`.

**Trajectory model.** During a dry stretch, soil drains from cap (100) at `D`/h, hitting 0 after `100 / D` hours, then sits at 0. During a rain bout, soil gains at `R − D`/h from 0, hitting cap after `100 / (R − D)` hours, then sits at 100. So one full cycle of length `T = 1/pClearToRain + 1/pRainToClear` hours decomposes into four phases with known durations:

| Phase | Duration (h) | Soil range |
|---|---|---|
| Dry drain | `100 / D` | 100 → 0 |
| Dry floor | `1/pClearToRain − 100/D` (clamp ≥ 0) | 0 |
| Rain ramp | `100 / (R − D)` | 0 → 100 |
| Rain cap  | `1/pRainToClear − 100/(R − D)` (clamp ≥ 0) | 100 |

**Happy time per cycle.** Within each phase, soil is moving linearly (or constant), so time spent inside `[mLo, mHi]` is just the fraction of the moisture span that overlaps the comfort window, scaled by the phase's duration. For `mHi = 100` the cap and the upper part of the ramp/drain are entirely happy; for `mLo = 0` the floor and lower tail are happy too. Sum the happy time across all four phases.

**Slowdown.** `happyFraction = happyHoursPerCycle / T`. Effective grow time ≈ `growthTime / happyFraction` ticks. Trees (`[10, 100]`) currently come out around 0.7× speed (~1.4× slower); a `[20, 80]` plant comes out around 0.32× speed (~3× slower) — most of the loss is the `mHi < 100` cap forcing the entire "rain cap" phase to be unhappy. As of 2026-04-28, all default plants set `moistureMax = 100` for this reason; only `moistureMin` is varied to differentiate species.

**Caveats.** Steady-state only — the **first** cycle on a fresh world is worse because `StartingMoisture = 50` and weather starts clear. Diffusion (5%/h of neighbour gap) softens troughs slightly. Temperature gate is independent and multiplies on top. Stage-crossing cost (`2 × moistureDrawPerHour`, currently 4) is essentially free vs. the comfort gate as long as `mLo ≥ 2 × moistureDrawPerHour`.

## Variable-shape structures

Some StructTypes can be placed in multiple footprint variants — e.g. the platform can be 1, 2, or 3 tiles tall. The player cycles between variants with **Q** (-1) / **E** (+1) during build placement; `Esc` cancels. Variants are declared in `buildingsDb.json` via the `shapes` field (see SPEC-data.md).

**Data model**:
- `StructType.shapes: Shape[]?` — null = single fixed shape (legacy behaviour). When set, each entry is a `{nx, ny}` pair. `shapes[0]` is the *authored* baseline (`ncosts` is sized for it).
- `StructType.GetShape(int)` returns the chosen entry clamped to the array, or a synthetic shape mirroring base `nx`/`ny` when `shapes` is null. Use this everywhere instead of branching on `HasShapes` directly.
- `Structure.shapeIndex` and `Blueprint.shapeIndex` carry the player's choice through placement → save → load. `BuildPanel.shapeIndex` is reset to 0 when the StructType selection changes.

**Cost scaling**: Blueprint ctor multiplies each `cost.quantity` by `shape.TileCount / shapes[0].TileCount` (rounded). Platform shapes `[1×1, 1×2, 1×3]` → 1×, 2×, 3× the wood per height step.

**Multi-tile claim**: every tile in the visual footprint claims the structure / blueprint at its depth — `shape.nx × shape.ny` for shape-aware types, `structType.nx × structType.ny` for legacy multi-tile (windmill 2×4, wheel 2×2, flywheel 2×2). This keeps tile→structure lookup (selection, collision, `tile.building`) symmetric with what's rendered: clicking any tile of a 2×4 windmill resolves to the windmill, and `StructPlacement.CanPlaceHere` correctly rejects new structures stacked into a windmill's upper rows. `Mathf.Max(1, st.ny)` guards against StructTypes that omit `ny` (default 0).

**Sprite composition** (vertical extension only, `nx=1, ny>1`): `StructureVisuals.LoadShapeSprite` resolves per-tile sprites — `_b` (anchor), `_m` (middle), `_t` (top). 1-tall shapes use the base `{name}.png` sprite directly so the existing 1×1 platform render is unchanged. Anchor SR renders the bottom tile; child SRs are spawned at local `(0, dy)` for `dy=1..ny-1` (mirrors `Plant.ClaimExtensionTile`). Missing variant sprites log once and fall back to the base sprite. Center-pivot sprites are assumed (matches the existing platform.png convention) — child SRs at integer dy align correctly.

Lookup order for the suffixed sprite (first match wins): (1) slice named `{stem}_<suffix>` inside `{name}_s.png`, (2) slice named `{stem}_<suffix>` inside `{name}.png`, (3) standalone file `{name}_<suffix>.png`. Path 1 lets `platform_s.png` (sliced sheet) coexist with the legacy 1×1 `platform.png`. Path 2 is for structures with no 1-tile form (e.g. `elevator.png`). Path 3 is the legacy per-file convention — works unchanged for buildings that haven't been consolidated. Use `Assets → Slice Vertical Building Sheet` (see SPEC-rendering.md *Sprite normal maps*) to set up a sliced sheet; it also sets the merged-normals flag so per-tile boundaries don't get false bevels.

**Standability rule** (`Navigation.GetStandability`): a tile is *not* standable if it contains a `solidTop` structure that ALSO occupies the tile directly below. This treats a tall platform's body as an obstacle (mice can't stand inside the column) while preserving existing behaviour for separate stacked 1×1 platforms (different `Structure` instances → rule doesn't fire). The very top of a tall column remains standable as before. Applied at depths 0 and 1 (buildings + platforms).

**Build preview**: `MouseController` composes the cursor-following ghost from per-tile preview SRs (pooled across builds) so a height-3 platform appears as `_b` + `_m` + `_t` before placement, matching what will be built.

**Placement**: `StructPlacement.CanPlaceHere` iterates the full visual footprint (matching the multi-tile claim above) when checking for blocking structs / blueprints / plants at the chosen depth. Standability/support is anchored to the bottom row only — only the base of the column needs to rest on something solid.

**Save/load**: `shapeIndex` is persisted on both `StructureSaveData` and `BlueprintSaveData`; defaults to 0 for old saves and non-shape types.

**Construction reach for multi-tile blueprints**: `Nav.PathToOrAdjacentBlueprint(bp)` extends the usual "stand on the centerTile or any of its 8 neighbours" pattern to consider neighbours of EVERY footprint tile. A hauler standing on a cliff that's level with the *top* of a 2-tall platform can now supply/construct it, even though the bottom (centerTile) is unreachable (cliff too tall to descend, water moat below). `ConstructTask` and `SupplyBlueprintTask` both route through this helper.

## Weather & Temperature

`Assets/Model/WeatherSystem.cs` — singleton, created by `World.Awake()`. Ticked every frame by `World.Update()`.

**Temperature** is a global ambient value in Celsius, driven by two additive sine waves:
- **Yearly**: peaks midsummer (day 7.5/20), troughs midwinter. Amplitude ±12.5°C around 13.5°C mean.
- **Daily**: peaks at 2pm, amplitude ±4°C.
- Formula: `T = 13.5 + 12.5·sin(yearly) + 4·sin(daily)`
- Range: ~−3°C (midwinter night) to ~30°C (midsummer afternoon).

**Seasons** (time 0 = first day of spring, `daysInYear = 20`): Spring 0–4, Summer 5–9, Fall 10–14, Winter 15–19. `GetSeason()` returns the name, `GetDayOfYear()` returns the fractional day.

**Temperature comfort** (on `Happiness`): each animal has `comfortTempLow` (default 10°C) and `comfortTempHigh` (25°C).
- In range → +2 happiness, 100% efficiency.
- Outside range → `2 − deviation/5` happiness (smooth falloff from +2, crosses zero at 10°C deviation); efficiency = `max(0.7, 1.0 − deviation × 0.04)`.
- Clothing expands the comfort range: `UpdateComfortRange()` shifts both bounds by ±3°C when any clothing item is equipped (7–28°C with a ramie shirt).
- Fireplace warmth buff: leisuring at a fireplace grants a `warmth` value (0–5) that widens `comfortTempLow` by up to 5°C. Decays slowly over ~2 days (`×0.94` per SlowUpdate).

**Rain/wind**: see header comment in `WeatherSystem.cs`. Rain also affects sun/ambient light multipliers and replenishes water via `WaterController.RainReplenish()`.

---

## Reservation Systems

`Reservable` (`Reservable.cs`) is the shared primitive — a capacity counter with `Reserve()`/`Unreserve()`/`Available()`. `Reserve` has two overloads: `Reserve(string by)` for callers without a task context (home, WOM orders) and `Reserve(Task by)` which additionally stores a task reference so `ExpireIfStale` can suppress expiry while the owning task is still the animal's active task (see "Staleness expiry" below). It appears in three conceptually different roles:

### Structure-level capacity ("can I go here?")

| Mechanism | Created by | Used by | What it gates |
|-----------|------------|---------|---------------|
| `Structure.res` | Structure constructor (`capacity > 0`, not workstation, not leisure) | `Animal.FindHome()` | House sleeping slots |
| `Structure.seatRes[]` | Structure constructor (leisure buildings only) | `LeisureTask` | Per-seat leisure access — each work tile gets its own `Reservable(1)` so two mice sit on different seats |
| `WorkOrder.res` (craft) | `RegisterWorkstation()` | `ChooseOrder()` / `Task.Cleanup()` | Workstation worker slots (player-adjustable via `workerLimit`) |

Workstations don't use `Structure.res` — the WOM Craft order's `res` is the sole reservation tracker. Leisure buildings don't use `Structure.res` — they use `seatRes[]` instead. Houses use `Structure.res`.

### WOM dispatch gating ("can I take this work?")

`WorkOrder.res` defaults to `Reservable(1)`. Harvest, research, and fuel-supply orders set capacity from the building/plant. `ChooseOrder()` reserves on dispatch; `Task.Cleanup()` unreserves. Orders stay in the queue permanently — reservation state determines availability.

### Item reservation

| Mechanism | Where | What it gates |
|-----------|-------|---------------|
| `ItemStack.resAmount` | Per-stack int counter (source) | Prevents two tasks from fetching the same items. Reserved via `Task.ReserveStack()` / `FetchAndReserve()`. Stale reservations expire via `Inventory.TickUpdate()` — see "Staleness expiry" below. |
| `ItemStack.resSpace` | Per-stack int counter (destination) | Prevents two tasks from delivering to the same space. Reserved via `Task.ReserveSpace(inv, item, amount)`. `FreeSpace(item)` returns `stackSize - quantity - resSpace`. All space-checking methods (`GetStorageForItem`, `GetMergeSpace`, `HasSpaceForItem`) account for it. Empty stacks track `resSpaceItem` to prevent conflicting item claims. Stale reservations expire via `Inventory.TickUpdate()` — see "Staleness expiry" below. |

### Staleness expiry

Both `Reservable` and `ItemStack` have a safety-net `ExpireIfStale(maxAge)` that clears reservations held longer than `maxAge` in-game seconds (timestamps use `World.instance.timer`, so they scale with `timeScale` and don't advance while paused). The guard is **AND-gated** on the owning task being inactive: a reservation whose `reservedByTask` (on Reservable) or `resTask`/`resSpaceTask` (on ItemStack) is still the animal's `task` is never expired, regardless of age. This prevents false-positive expiry on legitimately long-running tasks (e.g. `ReadBookTask` with fetch + walk + read + return). If the owning task was registered via a string-only `Reserve` overload (home assignment), or no task context exists, the guard falls through and the time-only path fires.

Called from `StructController.TickUpdate` (every 120 × 0.2s = 24s, threshold 60s) for leisure seats, and from `Inventory.TickUpdate` (per-tick, threshold 60s) for item stacks.

### Save/load invariant

Reservations are **never persisted**. On load, `ItemStack.resAmount`/`resSpace` and every `Reservable.reserved` start at 0 (fresh construction + explicit `= 0` in `SaveSystem.Restore*`). Non-resumable tasks are implicitly aborted at save — safe because their reservations vanish with the recreated world. Resumable tasks (`HaulToMarketTask`, `HaulFromMarketTask`) must re-make every reservation in their `InitializeResume()`. Any new resumable task type must do the same.

---

## When a structure isn't running

Multiple orthogonal mechanisms can cause a Building / Blueprint / Plant to skip work or be skipped over. They split cleanly into **player intent** (toggles set via UI) and **world state** (runtime conditions). The universal WOM-order gate is `!disabled && ConditionsMet()` on both Building and Blueprint (Blueprint mirrors the method by convention — it is *not* a Structure subclass).

### Player-toggled

| Mechanism | Owner | What it suppresses | Notes |
|-----------|-------|--------------------|-------|
| `Building.disabled` | `Building` | All WOM orders for this building (craft + research + supply hauls) | Order stays in queue, `isActive` returns false. Also gates LightSource burn/emission. |
| `Blueprint.disabled` | `Blueprint` | Supply + construct orders for this blueprint | Order is *removed* on `SetDisabled(true)` and re-registered on re-enable (via `RegisterOrdersIfUnsuspended`). Asymmetric with Building — see "Disabled-enforcement asymmetry" below. |
| `Blueprint.cancelled` | `Blueprint` | All orders, terminal | Set when blueprint is being torn down; not user-reversible. |
| `Workstation.workerLimit` | `Building.workstation` | Reduces effective craft capacity (set to 0 = no workers assigned) | Read by WOM at order registration as `effectiveCapacity`. Only affects craft, not supply/research. |
| `Plant.harvestFlagged` | `Plant` | Harvest order existence | Order only exists while flagged. `SetHarvestFlagged(false)` removes the order; `true` registers it. |

### Runtime-gated (world state)

| Mechanism | Owner | What it suppresses | Notes |
|-----------|-------|--------------------|-------|
| `Structure.ConditionsMet()` | `Structure` (virtual, default `true`) | Building craft order via `isActive` lambda | Override for runtime preconditions. Currently overridden only by `PumpBuilding` (requires water below pump head). |
| `Blueprint.ConditionsMet()` | `Blueprint` | Supply + construct orders via `isActive` lambda | Returns `!IsSuspended()`. Same call-site shape as Structure but no inheritance. |
| `Blueprint.IsSuspended()` | `Blueprint` | Order *registration* (constructor + `RegisterOrdersIfUnsuspended`); also drives UI tint | True when tile requirements fail or blueprint sits on unbuilt support. The reason behind `Blueprint.ConditionsMet()`. |
| `Building.reservoir.HasFuel()` | `Building` (when `reservoir != null`) | Building skipped by animal AI work-finding, water routing, and light emission | Not a WOM gate — these are direct checks at use sites (`Animal.cs`, `WaterController.cs`, `LightSource.cs`). |
| `LightSource.IsInActiveWindow()` | `LightSource` | Fuel burn + light emission outside `activeStartHour..activeEndHour` | StructType-driven schedule, e.g. torches lit only at night. |
| `uses >= depleteAt` | `Workstation` (`uses`) + `StructType` (`depleteAt`) | Triggers building destruction at craft completion | Not technically a "skip" — the building gets removed. Checked in `AnimalStateManager` after each craft round. |
| `Structure.IsBroken` | `Structure` (`condition < 0.5`) | Craft / research / fuel supply orders; decoration happiness; fountain decorative water; clock hand rotation; leisure seats; house sleep; road speed bonus; light burn + emission | Driven by the Maintenance System (see below). Gating sites mirror `disabled` — WOM `isActive` lambdas, plus direct checks in `Animal.cs`, `Navigation.cs`, `ModifierSystem.cs`, `LightSource.cs`, `WaterController.cs`, `ClockHand.cs`. |

### Disabled-enforcement asymmetry

Building and Blueprint enforce `disabled` differently:
- **Building**: order *kept* in queue; `isActive = () => !building.disabled && building.ConditionsMet()` returns false when disabled, so the dispatch loop skips it.
- **Blueprint**: order *removed* entirely on `SetDisabled(true)` (via `RemoveForBlueprint`); re-registered on re-enable.

The asymmetry exists because building craft orders carry `res` (worker-seat reservations) that are awkward to tear down and rebuild. Blueprints have no comparable per-seat state, so remove/re-register is cleaner. Reconciling this is a deferred goal — see the broader reservation-unification work.

---

## Maintenance System

Structures slowly deteriorate over time and must be repaired by a dedicated **Mender** job. Purely-structural nav pieces (platform, stairs, ladder) are exempt so a neglected map doesn't cut mice off from parts of the world.

### Condition model

Every `Structure` carries a `condition` float in `[0, 1]` (1 = pristine, 0 = fully broken). Three thresholds govern behaviour:

| Constant | Value | Meaning |
|----------|-------|---------|
| `BreakThreshold` | `0.5` | Below this → `IsBroken` → function gated off + grey tint. |
| `RegisterThreshold` | `0.75` | Below this → `WantsMaintenance` → WOM Maintenance order is active. |
| `MaxRepairPerTask` | `0.40` | A single mender visit can restore at most +40 % condition. Fully-broken (0 → 1) therefore requires 3 visits. |
| `RepairWorkPerTick` | `0.05` | Base condition gained per work tick (≈ 8 ticks for a full-cap repair). |
| `RepairCostFraction` | `0.25` | A full 0→1 repair costs ¼ × `StructType.ncosts`. A single visit scales by `repairAmount` (e.g. 40 % repair = 10 % of build cost for each cost item). |
| `DaysToBreak` | `30` | In-game days from 1.0 down to `BreakThreshold` (0.5). Full 1.0 → 0.0 takes ~60 days. Decay per tick = `(1 - BreakThreshold) / (DaysToBreak × World.ticksInDay)`. |

**Opt-out**: `StructType.noMaintenance = true` (JSON flag) exempts a type entirely. Plants and zero-cost structures are also auto-exempt (`NeedsMaintenance` is false). The three nav types (platform, stairs, ladder) and market carry the flag in `buildingsDb.json`.

**Predicates on Structure**:
- `NeedsMaintenance` — opt-in gate: non-plant, has build cost, not `noMaintenance`.
- `IsBroken` — `NeedsMaintenance && condition < 0.5`.
- `WantsMaintenance` — `NeedsMaintenance && condition < 0.75`.

### Decay ticker (`MaintenanceSystem`)

`MaintenanceSystem` is a singleton instantiated by `World.Awake` and ticked once per in-game second from `World.Update` (same cadence as `WeatherSystem`). Each tick:

1. Iterate `StructController.GetStructures()`.
2. For any `NeedsMaintenance` structure, decrement `condition` by the per-tick decay rate (clamped at 0).
3. Track threshold crossings so edge callbacks fire exactly once:
   - **Downward across `RegisterThreshold`**: `WorkOrderManager.RegisterMaintenance(s)` — Maintenance order enters the queue.
   - **Downward across `BreakThreshold`**: `OnBroken(s)` — calls `s.RefreshTint()` (grey tint). WOM gates suppress craft/research/supply automatically via `IsBroken` in their `isActive` lambdas; no removal needed.
   - **Upward across `BreakThreshold`**: `OnRepaired(s)` — restores normal tint. Order's `isActive = () => s.WantsMaintenance` suppresses it automatically once condition ≥ 0.75 (no removal, no churn on every tick).

The Maintenance WOM order is **not removed** when condition climbs back into the "fine" band — it just becomes inactive. Removal only happens when the structure is destroyed (`WorkOrderManager.RemoveMaintenanceOrders` is called from `Structure.Destroy()`).

### Mender job

`mender` is a dedicated crafter-type job with `defaultSkill: construction`. No recipes — menders respond only to Maintenance orders (plus survival). Construction skill accelerates the work-tick rate.

### MaintenanceTask flow

`MaintenanceTask` lives in `Assets/Model/Tasks/MaintenanceTask.cs` and follows the same Initialize/Objective pattern as other supply-then-work tasks. At start it snapshots `startCondition` and `repairAmount = min(MaxRepairPerTask, 1 - condition)`.

1. **Job gate**: `animal.job.name == "mender"`. 
2. **Cost computation**: for each `ItemQuantity` in `structType.costs`, `needed = ceil(cost.quantity × RepairCostFraction × repairAmount)`.
3. **Pathfind**: `Nav.FindPathTo(target.workTile)`; aborts if unreachable.
4. **Leaf resolution**: `Task.PickSupplyLeaf(group)` picks the highest-stock leaf per group cost item (single-leaf commit; no mixed-leaf delivery like blueprints). Shared with `SupplyBlueprintTask`.
5. **Fetch chain**: one `FetchObjective` per cost item with reservations held by the task.
6. **GoObjective** → **MaintenanceObjective** — ticks condition up by `RepairWorkPerTick × workEfficiency` per tick, grants Construction XP, stops at `startCondition + repairAmount` or `1.0`.
7. **Completion**: consume fetched materials from mender inventory; call `MaintenanceSystem.OnRepaired(target)` + `target.RefreshTint()`.

**Nearest-below-75-%** target selection is emergent — `WorkOrderManager.ChooseOrder` distance-sorts within a priority tier, and `isActive = () => s.WantsMaintenance` narrows the candidate pool to qualifying structures. No bespoke selection code.

### Visual

`Structure.RefreshTint()` swaps `sr.sharedMaterial` between two materials:
- **Healthy** — the renderer's original material (captured on first `RefreshTint` call into `Structure.defaultMat`). This is URP 2D's Sprite-Lit-Default, which carries the `LightMode = Universal2D` tag that the `NormalsCapturePass` filter matches on. We capture and restore by reference rather than `Shader.Find("Sprites/Default")` because the latter is the *legacy* CG sprite shader and would silently drop the renderer out of the lighting pipeline (no ambient, no sun).
- **Broken** — `Resources/Materials/CrackedSprite.mat`, which drives `Assets/Resources/Shaders/CrackedSprite.shader`. The shader has both `Universal2D` and `UniversalForward` passes so broken buildings continue to participate in NormalsCapture and LightComposite. It composites a tileable world-space crack texture on top of the base sprite's RGB, alpha-masked by the base sprite's own alpha (cracks never appear in transparent gaps). Full-structure lighting (sun, torches, fireplaces, ambient) continues to apply on top via the normal composite path.

Called on threshold crossings (`OnBroken` / `OnRepaired`) and on every structure at load (`SaveSystem` Phase 6). Deconstruct tints run via `sr.color` and compose multiplicatively with either material — broken + deconstructing renders correctly.

### Per-building broken effects

Beyond the universal cracked-material tint, specific building types have additional visual/functional responses to `IsBroken`. All are implemented as polling checks (not edge callbacks) at the site that already controls the behaviour:

| Building | Broken effect | Poll site |
|----------|---------------|-----------|
| Workstations | Craft orders halt | WOM `isActive` lambda |
| Laboratory | Research orders halt | WOM `isActive` lambda |
| Torch / fireplace | Light + fuel burn stop | `LightSource.UpdateLitState()` |
| Fountain | Decorative water overlay hidden; decoration happiness lost | `WaterController.UpdateSurfaceMask()`, `Animal.ScanForNearbyDecorations()` |
| Clock | Hand freezes; catches up on repair (rotation derived from current time, not accumulated) | `ClockHand.Update()` |
| House | Animals abandon and find new homes | `Animal.FindHome()` |
| Road | Speed bonus drops to 0 | `Structure.EffectivePathCostReduction` |

**Exempt from decay** (`noMaintenance: true`): platform, stairs, ladder, market.

### Save/load

- `StructureSaveData.condition` (float) persists per-structure. Old saves missing the field deserialize to 0.0 which `RestoreStructure` treats as "default to 1.0" so pre-maintenance saves don't load every structure as broken.
- Maintenance WOM orders are **not** persisted. `WorkOrderManager.Reconcile` registers them from world state at load via `ScanOrders`, same mechanism as every other order type.
- `MaintenanceSystem.RebuildFromWorld()` runs in `SaveSystem` Phase 6 (after Reconcile) to rebuild the internal `registered`/`broken` sets from restored condition values. Tint refresh for every structure follows in the same block.

### Audit (`Ctrl+D`)

`ScanOrders` includes bi-directional Maintenance coverage:
- **Direction 1**: every structure with `WantsMaintenance` must have a Maintenance order registered.
- **Direction 2**: every Maintenance order must reference a live structure that still `NeedsMaintenance`.

---

## Unit System — Fen / Liang

All item quantities are stored as **fen** (integers), where **100 fen = 1 liang**. Display uses `ItemStack.FormatQ(int fen, bool discrete = false)` — drops trailing zeros, shows no decimals for exact integers. Overload `FormatQ(ItemQuantity iq)` uses `iq.item.discrete` automatically.

- **JSON data** is authored in liang (can be decimal, e.g. `0.5`). The field type is `float` (`ItemNameQuantity.quantity`).
- **Conversion** to fen happens at all `ItemNameQuantity → ItemQuantity` sites (Db.cs, Structure.cs, Tile.cs, Plant.cs) via `ItemStack.LiangToFen(q)`. User-typed input uses `ItemStack.TryParseQ` instead (adds overflow/validation).
- **Stack sizes**: animal inv = 5 × 1000 fen; floor/default = 1000 fen; storage = `storageStackSize * 100` (converted in `StructType.OnDeserialized`).
- Old saves are **incompatible** (quantities were in the old unit). Fresh start required after this change.

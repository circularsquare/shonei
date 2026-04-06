# Shonei — Navigation, Inventory & Units

## Navigation

- **Algorithm**: A* with Euclidean heuristic. Edge costs vary by traversal type (see below).
- **Locomotion**: `speed = GetTravelSpeedMultiplier(animal) * edgeLength / edgeCost`. Edge info comes from `Graph.GetRawEdgeInfo()` (excludes road cost — road bonus is tile-based via `ModifierSystem`). A* pathfinding still uses `GetEdgeInfo()` with road-reduced costs so paths prefer roads.
- **Standability**: tile is standable if tile below is solid, has a platform/building, or has a ladder.
- **Vertical movement**: ladders produce direct node-to-node vertical edges (cost 2.0). Cliff climbing and stairs use **waypoint chains** (see below).
- **Road speed boost**: road bonus is per-tile — only the tile the mouse is currently standing on contributes its `pathCostReduction` (doubled to match old two-endpoint feel). No bonus from adjacent road tiles.
- **Floor item slowdown**: tiles with floor items reduce movement speed by 25% (×0.75).
- **Crowding slowdown**: tiles with multiple mice reduce movement speed by 25% (×0.75, flat regardless of count). All speed modifiers are multiplicative.
- **Tile occupancy tracking**: `AnimalController` maintains a `Dictionary<Tile, int>` for O(1) crowding queries. Animals register/unregister via `UpdateCurrentTile()` after position changes.
- **Helper queries**: `FindPathToBuilding`, `FindPathToItem`, `FindPathToStorage`, `FindPathAdjacentToBlueprint`, `FindPathToHarvestable`

### Connected-components reachability cache

`Graph` maintains a component ID on every `Node` (`node.componentId: int`, -1 = impassable). `Graph.RebuildComponents()` runs a BFS flood-fill over all standable nodes and assigns integer IDs; waypoint nodes get IDs transitively via neighbor edges. `Graph.SameComponent(a, b)` is an O(1) check.

- **Rebuild triggers**: `StructController.Construct()` (end of method, after all `UpdateNeighbors` calls) and `Graph.AddNeighborsInitial()` (startup/load). Cost ≈ 0.1–0.2 ms for a 100×50 map.
- **Usage as pre-filter**: `Graph.Navigate()` itself checks `SameComponent` before running A*, so all pathfinding automatically rejects unreachable targets in O(1). Individual search loops no longer need their own `SameComponent` calls.
- **`Nav.CanReach(Tile t)`**: O(1) reachability check from the animal's current position. Used as an early-out in task Initialize methods (e.g. `HaulTask`) before running heavier searches.
- **`Nav.CanReachBuilding(StructType, r)`**: checks whether any building of a given type is in the same component — used by `PickRecipe`/`PickRecipeRandom` instead of a full A* scan.

### Waypoint system (stairs and cliff climbs)

Both stairs and one-block cliff climbs are represented as **waypoint chains** — intermediate `Node` objects with fractional world positions, not backed by tiles. They are stored in `stairWaypoints` and `cliffWaypoints` dictionaries and rebuilt whenever nearby tiles change.

**Cliff climb** (one solid block beside a standable tile): `base → wp1 → wp2 → cliffTop`
- `wp1` at `(base.x + dir×0.25, base.y)` — 0.25 tiles from base, normal speed
- `wp1 → wp2` vertical, cost 3.0 (slow both up and down)
- `wp2` at `(base.x + dir×0.25, base.y+1)` — 0.75 tiles from cliff top, normal speed

**Stair** (stair tile): `left → entry → exit → right`, where entry/exit are 0.5-tile offsets from their endpoints; the diagonal entry→exit step has cost 1.8 / length √2.

`preventFall` in `Nav.Move()` suppresses the fall check while on any waypoint edge or a ladder edge. Direct non-standable tile traversal no longer occurs — all such paths go through waypoints.

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
- `fuelTarget: float` — refill trigger level (liang in JSON)
- `fuelBurnRate: float` — liang/day consumed; `Reservoir.Burn()` converts to fen/sec using `World.ticksInDay`

`isBuilding: true` on StructType makes StructController use the `Building` class for any depth (e.g. foreground torches at depth 2). `tile.building` (= `structs[0] as Building`) is still depth-0 specific; fuel buildings at other depths are accessed directly via task/WOM references.

- Items decay over time (Floor fastest; Animal/Market never; Equip at normal rate)
- **Discrete items** (`Item.discrete = true`, e.g. tools): always stored/moved in whole-liang (100 fen) multiples; decay removes whole items only; display shows integer count. Adding a non-multiple-of-100 quantity logs a warning.
- `allowed` dict filters what item types a storage accepts (all allowed by default for other types)
- `Reservable` (capacity-based) prevents multiple animals targeting same resource. Has two fields: `capacity` (hard max from JSON) and `effectiveCapacity` (player-adjustable; defaults to `capacity`); `Available()` gates on `effectiveCapacity`. **Not** created for workstation buildings — WOM Craft orders own their reservation directly.
- `Produce()` adds to inventory and global inventory simultaneously; `MoveItemTo()` moves between inventories without touching global inventory
- `AddItem()` is private — always use `Produce`, `MoveItemTo`, or `TakeItem` externally
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

**Clothing system**: `Db.clothingItems` lists all items whose parent chain includes `"clothing"`. `FindClothing()` in `ChooseTask()` equips one clothing item into `clothingSlotInv` when idle (after tool equip, before work orders). `Happiness.UpdateComfortRange()` adjusts `comfortTempLow`/`comfortTempHigh` by ±3°C when any clothing is equipped, and additionally widens `comfortTempLow` by up to 5°C from the fireplace warmth buff. Clothing items are discrete (like tools) and decay at normal rate in equip slots.

**Clothing overlay**: equipped clothing renders as a child `SpriteRenderer` ("ClothingOverlay") on the Animal prefab, assigned to `AnimationController.clothingRenderer`. Sprites loaded by item name from `Resources/Sprites/Animals/Clothing/{itemName}/` (`idle`, `walk`, `eep` — walk and eep fall back to idle if missing). `AnimationController.UpdateClothingOverlay()` swaps sprite on state change; `LateUpdate` syncs `flipX`. Adding a new clothing visual = add sprites to a new folder, no code changes.

**Food acquisition flow:**
1. Animal gets hungry → `FindFood()` checks `foodSlotInv` for room
2. If room exists, creates `ObtainTask(food, amount, foodSlotInv)` — item goes to slot, not main inventory
3. `HandleNeeds()` eats from `foodSlotInv`: full meals (≥100 fen) restore `foodValue` and grant full satisfaction; partial meals (remaining fen) scale both nutrition and satisfaction proportionally

**Key methods on `Animal`:**
- `TakeItem(iq, targetInv = null)` — picks up from floor tile; pass `foodSlotInv` to equip directly
- `Unequip(slotInv)` — moves slot contents back to main inventory (leftover stays in slot if inv full)

**`ObtainTask` / `FetchObjective`** both accept an optional `Inventory targetInv` to route pickup into an equip slot instead of main inventory.

**Partial fills:** Equip slot fetches (`targetInv != null`) accept whatever quantity was available on the source tile and complete without retrying across multiple tiles. This is intentional — food/tools are useful at any amount, and the mouse will re-fetch next time it's hungry/unequipped. Crafting fetches (`targetInv == null`) do retry across tiles until the full amount is collected.

### Blueprint inventory

`Blueprint` has its own `Inventory inv` (Animal type, not registered with InventoryController — no decay, no tick overhead). Materials are delivered into it via `MoveItemTo` from the animal's inventory. On `Complete()`, `inv.Produce(item, -qty)` is called for each cost item to decrement GlobalInventory (the items were already counted in GlobalInventory when originally harvested). On cancel (`BuildPanel.Remove`), materials are returned to the floor via `MoveItemTo`.

One slot per cost item (`stackSize = cost.quantity`). Because a slot can only hold one leaf type, `LockGroupCostsAfterDelivery()` is called after the first delivery of each group cost: it updates `blueprint.costs[i].item` from the group (e.g. "wood") to the specific leaf delivered (e.g. "pine"). Subsequent `SupplyBlueprintTask` initializations read the locked type and fetch only that leaf, avoiding slot conflicts.

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
- World-spanning `WaterSprite` GameObject: 1×1 white pixel sprite at PPU=1, scaled to `(nx, ny)` Unity units, placed at `(−0.5, −0.5)`. sortingOrder=2. Must be on the **`Water`** Unity layer, excluded from `LightFeature` litLayers and BackgroundCamera culling mask.

**Pump draining**: `PumpBuilding` (`Assets/Components/PumpBuilding.cs`) is a depth-0 Building subclass for the pump (id 140, nx=2). It overrides `IsActive()` to suppress the WOM Craft order when the source tile has no water. After each completed craft round, `AnimalStateManager` calls `pump.DrainForCraft()`, which subtracts `WaterDrainPerRound` units from the tile at `(x+1, y-1)` (directly below the pump head). Drain only happens when a mouse is actively pumping — not on a passive timer. `WaterDrainPerRound` is a private const; see the file for the current value.

**Mouse speed**: Water on either endpoint of a horizontal nav edge doubles the A* edge cost (→ 0.5× speed). Applied in `Graph.GetEdgeInfo()`.

**World gen**: `WorldController.GenerateDefault()` seeds `water=WaterMax` at y=9 for x=[0,3] and x=[30,40], first clearing those tiles to empty.

**Save/load**: `WorldSaveData.waterLevels` — flat `byte[]`, index `y * nx + x`. Omitted (null) if all-dry. Restored in `SaveSystem.ApplySaveData()` before tile types are applied.

**ClearWorld**: `WaterController.ClearWater()` zeros all `tile.water`, clears the surface mask texture, and calls `UpdateSurfaceMask()`.

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

`Reservable` (`Reservable.cs`) is the shared primitive — a capacity counter with `Reserve()`/`Unreserve()`/`Available()`. It appears in three conceptually different roles:

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
| `ItemStack.resAmount` | Per-stack int counter (source) | Prevents two tasks from fetching the same items. Reserved via `Task.ReserveStack()` / `FetchAndReserve()`. Stale reservations expire after 60s via `Inventory.TickUpdate()`. |
| `ItemStack.resSpace` | Per-stack int counter (destination) | Prevents two tasks from delivering to the same space. Reserved via `Task.ReserveSpace(inv, item, amount)`. `FreeSpace(item)` returns `stackSize - quantity - resSpace`. All space-checking methods (`GetStorageForItem`, `GetMergeSpace`, `HasSpaceForItem`) account for it. Empty stacks track `resSpaceItem` to prevent conflicting item claims. Stale reservations expire after 60s. |

---

## Unit System — Fen / Liang

All item quantities are stored as **fen** (integers), where **100 fen = 1 liang**. Display uses `ItemStack.FormatQ(int fen, bool discrete = false)` — drops trailing zeros, shows no decimals for exact integers. Overload `FormatQ(ItemQuantity iq)` uses `iq.item.discrete` automatically.

- **JSON data** is authored in liang (can be decimal, e.g. `0.5`). The field type is `float` (`ItemNameQuantity.quantity`).
- **Conversion** to fen happens at all `ItemNameQuantity → ItemQuantity` sites (Db.cs, Structure.cs, Tile.cs, Plant.cs): `(int)Math.Round(q * 100)`.
- **Stack sizes**: animal inv = 5 × 1000 fen; floor/default = 1000 fen; storage = `storageStackSize * 100` (converted in `StructType.OnDeserialized`).
- Old saves are **incompatible** (quantities were in the old unit). Fresh start required after this change.

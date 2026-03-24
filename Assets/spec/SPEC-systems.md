# Shonei — Navigation, Inventory & Units

## Navigation

- **Algorithm**: A* with Euclidean heuristic. Edge costs vary by traversal type (see below).
- **Locomotion**: `speed = maxSpeed * edgeLength / edgeCost`. Both values come from `Graph.GetEdgeInfo(from, to)`, so speed automatically adjusts for sub-tile and slow edges.
- **Standability**: tile is standable if tile below is solid, has a platform/building, or has a ladder.
- **Vertical movement**: ladders produce direct node-to-node vertical edges (cost 2.0). Cliff climbing and stairs use **waypoint chains** (see below).
- **Road speed boost**: road tiles reduce A* edge cost by `pathCostReduction` (both endpoints contribute), making roads faster to path through. Base movement speed is `1 tile/sec × efficiency`.
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

Five inventory types:

| Type | Slots | Stack Size | Decay Rate | Notes |
|------|-------|-----------|-----------|-------|
| Animal | 5 | 1000 fen | none | General-purpose carry inventory |
| Storage | varies | varies | normal | `allowed` dict restricts item types |
| Floor | 4 | 1000 fen | 5× normal | Created/destroyed dynamically; up to 4 item types can share a tile |
| Equip | 1 | varies | none | Animal equip slots (food, tool) |
| Market | varies | varies | none | Market building only; set via `SetMarket()` on a Storage inv |

- Items decay over time (Floor fastest; Animal/Equip/Market never)
- **Discrete items** (`Item.discrete = true`, e.g. tools): always stored/moved in whole-liang (100 fen) multiples; decay removes whole items only; display shows integer count. Adding a non-multiple-of-100 quantity logs a warning.
- `allowed` dict filters what item types a storage accepts (all allowed by default for other types)
- `Reservable` (capacity-based) prevents multiple animals targeting same resource. Has two fields: `capacity` (hard max from JSON) and `effectiveCapacity` (player-adjustable; defaults to `capacity`); `Available()` gates on `effectiveCapacity`. **Not** created for workstation buildings — WOM Craft orders own their reservation directly.
- `Produce()` adds to inventory and global inventory simultaneously; `MoveItemTo()` moves between inventories without touching global inventory
- `AddItem()` is private — always use `Produce`, `MoveItemTo`, or `TakeItem` externally
- **Group-item wildcard**: `Quantity`, `ContainsAvailableItem`, `GetItemStack`, `AvailableQuantity`, and `MoveItemTo` all expand group items to their leaf descendants (`MatchesItem` helper). Passing `"wood"` to any of these matches oak/maple/pine transparently. `AddItem` and `GlobalInventory.AddItem` reject group items with a `LogError` — only leaf items may physically exist in inventories.
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

Each animal has two `InvType.Equip` inventory instances (1 stack each, registered with InventoryController for GlobalInventory tracking, no sprite, no decay):

| Slot | Field | Capacity | Purpose |
|------|-------|----------|---------|
| Food | `foodSlotInv` | 500 fen (5 liang) | Carries food for eating |
| Tool | `toolSlotInv` | 1000 fen | Reserved for future tool use |

**Food acquisition flow:**
1. Animal gets hungry → `FindFood()` checks `foodSlotInv` for room
2. If room exists, creates `ObtainTask(food, amount, foodSlotInv)` — item goes to slot, not main inventory
3. `HandleNeeds()` eats from `foodSlotInv`: full meals (≥100 fen) restore `foodValue` and trigger happiness; partial meals (remaining fen) scale nutrition proportionally and don't count for happiness

**Key methods on `Animal`:**
- `TakeItem(iq, targetInv = null)` — picks up from floor tile; pass `foodSlotInv` to equip directly
- `Unequip(slotInv)` — moves slot contents back to main inventory (leftover stays in slot if inv full)

**`ObtainTask` / `FetchObjective`** both accept an optional `Inventory targetInv` to route pickup into an equip slot instead of main inventory.

**Partial fills:** Equip slot fetches (`targetInv != null`) accept whatever quantity was available on the source tile and complete without retrying across multiple tiles. This is intentional — food/tools are useful at any amount, and the mouse will re-fetch next time it's hungry/unequipped. Crafting fetches (`targetInv == null`) do retry across tiles until the full amount is collected.

### Blueprint inventory

`Blueprint` has its own `Inventory inv` (Animal type, not registered with InventoryController — no decay, no tick overhead). Materials are delivered into it via `MoveItemTo` from the animal's inventory. On `Complete()`, `inv.Produce(item, -qty)` is called for each cost item to decrement GlobalInventory (the items were already counted in GlobalInventory when originally harvested). On cancel (`BuildPanel.Remove`), materials are returned to the floor via `MoveItemTo`.

One slot per cost item (`stackSize = cost.quantity`). Because a slot can only hold one leaf type, `LockGroupCostsAfterDelivery()` is called after the first delivery of each group cost: it updates `blueprint.costs[i].item` from the group (e.g. "wood") to the specific leaf delivered (e.g. "pine"). Subsequent `SupplyBlueprintTask` initializations read the locked type and fetch only that leaf, avoiding slot conflicts.

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

## Unit System — Fen / Liang

All item quantities are stored as **fen** (integers), where **100 fen = 1 liang**. Display uses `ItemStack.FormatQ(int fen, bool discrete = false)` — drops trailing zeros, shows no decimals for exact integers. Overload `FormatQ(ItemQuantity iq)` uses `iq.item.discrete` automatically.

- **JSON data** is authored in liang (can be decimal, e.g. `0.5`). The field type is `float` (`ItemNameQuantity.quantity`).
- **Conversion** to fen happens at all `ItemNameQuantity → ItemQuantity` sites (Db.cs, Structure.cs, Tile.cs, Plant.cs): `(int)Math.Round(q * 100)`.
- **Stack sizes**: animal inv = 5 × 1000 fen; floor/default = 1000 fen; storage = `storageStackSize * 100` (converted in `StructType.OnDeserialized`).
- Old saves are **incompatible** (quantities were in the old unit). Fresh start required after this change.

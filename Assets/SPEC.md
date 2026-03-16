# Shonei — Project Spec

## Overview

Shonei is a 2D tile-based colony management simulation (in the vein of Dwarf Fortress / RimWorld) where the player oversees a colony of autonomous mice. Players designate jobs, place buildings, and manage resources while mice carry out tasks on their own using a hierarchical AI system. Built in Unity with C#.

## Genre & Core Loop

- **Genre**: Colony sim / base builder
- **Perspective**: 2D side-view, tile-based (100×50 grid)
- **Core loop**: Assign jobs → mice carry out tasks → resources are gathered/processed → new buildings unlock → repeat

## Architecture

### Pattern: MVC + Singleton Controllers

```
World (singleton)
├── Tile[,] grid
├── AnimalController   → manages Animal instances
├── PlantController    → manages Plant instances
├── InventoryController → global item tracking
└── WorldController    → Unity rendering + input
```

All controllers are Unity MonoBehaviours. The `World` singleton provides access to all of them. Rendering is decoupled from model logic through callback registration (tiles fire callbacks on change).

### Directory Structure

```
Assets/
├── Controller/        Game-system MonoBehaviours (rendering, world lifecycle, networking)
│   ├── WorldController.cs   Tile rendering + world setup (GenerateDefault, ClearWorld, item fall animation)
│   ├── AnimalController.cs  Animal spawning + rendering
│   ├── StructController.cs  Structure placement + rendering
│   ├── PlantController.cs   Plant rendering
│   ├── InventoryController.cs  Global inventory tracking + item sprite display + haul logic
│   ├── AnimationController.cs  Animal sprite animation
│   ├── SaveSystem.cs        Save/load/reset — all Gather* and Restore* methods live here
│   ├── TradingClient.cs     WebSocket connection to trading server
│   ├── MouseController.cs   Input handling
│   ├── BackgroundCamera.cs  Background parallax camera
│   └── CloudLayer.cs        Cloud rendering
├── Model/             Pure C# game logic (no MonoBehaviours)
│   ├── World.cs       Tile grid, tick loop, ProduceAtTile, FallItems
│   ├── Animal.cs      Agent data + task dispatch
│   ├── AnimalStateManager.cs  State machine logic
│   ├── AnimalComponents.cs  Nav, movement, pathfinding helpers (Move, Find*, FindPath*)
│   ├── Task.cs        All Task + Objective class definitions
│   ├── Navigation.cs  A* pathfinding
│   ├── Inventory.cs   Item containers (animal/storage/floor)
│   ├── Plant.cs       Growing plants
│   ├── Structure.cs   Placed buildings + StructType
│   ├── Tile.cs        Grid cell
│   ├── Item.cs        Item type definitions
│   ├── Db.cs          JSON database loader
│   ├── ResearchSystem.cs  Research points + unlock logic
│   ├── WorldSaveData.cs   Save data classes (add fields here when extending save)
│   └── Reservable.cs  Resource reservation (capacity-based)
├── UI/                UI panels, displays, and tooltip system
│   ├── BuildPanel.cs, InfoPanel.cs, MenuPanel.cs, SaveMenuPanel.cs
│   ├── TradingPanel.cs, RecipePanel.cs, ResearchPanel.cs
│   ├── ItemDisplay.cs, JobDisplay.cs, OrderDisplay.cs, ResearchDisplay.cs
│   ├── TooltipSystem.cs, Tooltippable.cs
│   └── UI.cs          Static singleton accessor hub
├── Lighting/          Custom lighting pipeline (ScriptableRendererFeature)
└── Resources/
    ├── buildingsDb.json
    ├── plantsDb.json
    ├── recipesDb.json
    ├── itemsDb.json
    ├── jobsDb.json
    └── researchDb.json
```

## Data-Driven Design

All game content is defined in JSON and loaded at startup via `Db.cs` using Newtonsoft.Json. String references (e.g., `"wood"`) are resolved to object references in `[OnDeserialized]` callbacks.

Lookups: `itemByName`, `jobByName`, `structTypeByName`, `plantTypeByName`, `tileTypeByName`

### `buildingsDb.json` — StructTypes

ID ranges (keep entries ordered and thematically grouped):

| Range | Category |
|-------|----------|
| 0 | dig (empty tile designator) |
| 1–9 | storage buildings (house, drawer, crate, market) |
| 10–19 | structural pieces (platform, stairs, ladder) |
| 20–29 | decorations / terrain features (torch, road) |
| 50–59 | placeable tile fills (dirt, stone) |
| 100–119 | material processing (sawmill, workshop, furnace) |
| 120–129 | extraction (dirt pit, quarry) |
| 130–139 | research (laboratory) |

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique; defines ordering |
| `name` | string | lookup key |
| `description` | string? | shown in UI |
| `nx`, `ny` | int | footprint in tiles |
| `storageTileX` | int? | X offset of storage tile within multi-tile buildings |
| `ncosts` | `[{name, quantity}]` | build cost in liang |
| `njob` | string? | job assigned on placement (e.g. `"hauler"`) |
| `isStorage` | bool? | has a storage inventory |
| `nStacks` | int? | inventory slot count (requires `isStorage`) |
| `storageStackSize` | int? | max stack per slot in liang (requires `isStorage`) |
| `capacity` | int? | max simultaneous workers |
| `depth` | string? | render layer: `"b"` building, `"m"` platform, `"f"` front, `"r"` road |
| `solidTop` | bool? | mice can stand on top |
| `isTile` | bool? | placeable tile type rather than a building |
| `category` | string | UI build menu group: `"storage"`, `"structures"`, `"tiles"`, `"production"` |
| `defaultLocked` | bool? | hidden from build menu until researched |
| `requiredTileName` | string? | tile type this building must be placed on |
| `depleteAt` | int? | production count at which this building depletes |
| `pathCostReduction` | float? | reduces A* edge cost (roads) |

### `itemsDb.json` — Item types

ID ranges:

| Range | Category |
|-------|----------|
| 0 | none |
| 1–9 | currency (silver) |
| 5–9 | raw wood |
| 10–19 | raw stone |
| 20–29 | dirt |
| 30–39 | ores (iron ore, coal) |
| 40–49 | metals (iron) |
| 100–119 | processed wood (planks, sawdust) |
| 150–199 | food and seeds |
| 200+ | tools and equipment |

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `name` | string | lookup key |
| `decayRate` | float | decay per tick on floors (0 = no decay) |
| `foodValue` | int? | hunger restored when eaten |
| `discrete` | bool? | stored/moved in whole-liang (100 fen) units only (e.g. tools) |
| `children` | array? | sub-types satisfied by this type in recipes (e.g. `"wood"` matches `"oak"`, `"maple"`) |

### `recipesDb.json` — Recipes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `job` | string | job name required to execute |
| `tile` | string | building name where recipe runs |
| `description` | string | shown in UI |
| `workload` | float | ticks to complete |
| `research` | string? | research node name required to unlock |
| `skillPoints` | float? | skill gained per completion |
| `ninputs` | `[{name, quantity}]` | consumed items in liang |
| `noutputs` | `[{name, quantity, chance?}]` | produced items; `chance` (0–1) = probability of output |

### `jobsDb.json` — Jobs

ID ranges:

| Range | jobType | Examples |
|-------|---------|---------|
| 0 | `"logistics"` | none (idle) |
| 1–2 | `"none"` | hauler, merchant |
| 3–9 | `"gatherer"` | logger, miner, farmer |
| 10 | `"gatherer"` | digger |
| 11–19 | `"crafter"` / `"researcher"` | sawyer, scientist, smith |

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `name` | string | lookup key; matched against `recipe.job` |
| `jobType` | string | category: `"logistics"`, `"none"`, `"gatherer"`, `"crafter"`, `"researcher"` |

### `plantsDb.json` — PlantTypes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique (500s range) |
| `name` | string | lookup key |
| `ncosts` | `[{name, quantity}]` | planting cost in liang |
| `nproducts` | `[{name, quantity}]` | harvest yields in liang |
| `growthTime` | int | ticks to mature |
| `harvestTime` | float | ticks to harvest |
| `njob` | string | job that can harvest this plant |

### `tilesDb.json` — TileTypes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique (0=empty, 1=structure, 2=dirt, 3=stone) |
| `name` | string | lookup key |
| `solid` | int | 0=passable, 1=solid (blocks movement) |
| `nproducts` | `[{name, quantity}]`? | items dropped when mined |

### `researchDb.json` — Research nodes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `name` | string | display name |
| `description` | string | shown in tooltip |
| `type` | string | `"building"`, `"recipe"`, or `"misc"` |
| `unlocks` | string | building name, recipe id, or effect key (e.g. `"research_efficiency"`) |
| `prereqs` | `[int]` | required research node ids |
| `cost` | int | research points to unlock |

## Save / Load / Reset

World state is serialized to JSON via Newtonsoft.Json and stored in `Application.persistentDataPath/saves/<slot>.json`. The entry point is `SaveSystem` (MonoBehaviour singleton).

### Save data classes (`WorldSaveData.cs`)
Plain C# classes — **no `[Serializable]`** (not needed by Newtonsoft.Json; adding it causes Unity's own serializer to materialize default instances on MonoBehaviour fields instead of null).

### Structure creation rules
Two legitimate ways to put a structure into the world:

| Method | When to use |
|--------|-------------|
| `StructController.Construct(st, tile)` | Normal gameplay (via `Blueprint.Complete()`). Consumes `Blueprint.inv` via `Produce(-qty)`, decrementing GlobalInventory. |
| `new Building/Plant/etc.(…)` + `StructController.Place(s)` | Load path and world generation. No cost side-effects. |

Always call `Place()` after direct construction — it registers the structure for tracking so `ClearWorld()` can find and destroy it later. Direct `new X()` without `Place()` is a bug.

### Startup ordering (frame by frame)

All three paths (Initial / Reset / Load) follow the same two-frame handoff:

```
Initial: GenerateDefault()         →  PostLoadInit (next frame)
Reset:   ClearWorld() + GenerateDefault()  →  PostLoadInit (next frame)
Load:    ClearWorld() + SaveSystem.ApplySaveData()  →  PostLoadInit (next frame)
```

**Frame 0** — all `Awake()`s run (order undefined, but before any `Start`):
- `Db.Awake()` — JSON data loaded; all lookups ready
- `World.Awake()` — tiles and `graph.nodes` allocated; `node.standable = false` until `graph.Initialize()`
- `AnimalController.Awake()` — instance set, arrays allocated

**Frame 0** — all `Start()`s run: (cross object initialization)
- `WorldController.Start()` runs up to `yield return null` and **pauses**
- `AnimalController.Start()` — populates `jobCounts` (Db is ready; must finish before frame 1)
- All UI/other controllers initialize

**Frame 1** — `WorldController.Start()` resumes, calls `GenerateDefault()` (or load path):
- Tile types set, structures placed
- **`graph.Initialize()`** — standability calculated; `node.standable` now valid
- Animals spawned (`Animal.Awake` runs immediately; `Animal.Start` queued for next frame)
- `StartCoroutine(DefaultJobSetup())` and `StartCoroutine(PostLoadInit())` — both pause at their yields

**Frame 2** — coroutines resume:
- **`Animal.Start()`** — initializes hunger/sleep/happiness; applies `pendingSaveData` if on load path
- **`DefaultJobSetup`** — assigns jobs, calls `World.ProduceAtTile` (standability and animals both ready)
- **`PostLoadInit`** — calls `AnimalController.Load()` → `SlowUpdate()`, `UpdateColonyStats()`

**Key rule**: use `PostLoadInit` for any initialization that depends on animals being fully ready. It runs on all three paths. Do NOT use the `if (world == null)` guard in `AnimalController.TickUpdate` — unreliable on Reset/Load since `world` is never reset to null.

`pendingSaveData`: `LoadAnimal()` sets this before `Animal.Start()` runs. `Start()` checks it and applies saved state if present; otherwise initializes fresh. Marked `[System.NonSerialized]` to prevent Unity from replacing null with a default instance.

## Time System

The main game clock lives in `World.Update()` (World.cs). Each frame it accumulates `timer += Time.deltaTime` and fires tick events at fixed intervals:

| Interval | What fires |
|----------|-----------|
| 1 second | `AnimalController.TickUpdate()`, `PlantController.TickUpdate()`, `ResearchSystem.TickUpdate()` |
| 0.2 seconds | `InventoryController.TickUpdate()` (item display refresh), `InfoPanel.UpdateInfo()` |

All game logic is intended to be tick-driven. Movement and fall physics are the only things that run per-frame (in `Animal.Update()` → `AnimalStateManager.UpdateMovement(deltaTime)`), because smooth sub-tile animation requires it.

### Time scale

`TimeController.cs` wraps `Time.timeScale`. Setting it to `0` pauses all ticks and movement; `2` doubles everything. Because all code uses `Time.deltaTime`, scaling is automatic — no special handling needed in tick consumers. `TradingClient.ReconnectLoop` uses `WaitForSecondsRealtime` so network reconnection is unaffected by time scale.

### Key time constants

| Constant | Value | Used by |
|----------|-------|---------|
| `ticksInDay` | 240 | Day/night length, research rate |
| `daysInYear` | 20 | Calendar |
| `fallSecondsPerTile` | 0.4s | Item and mouse fall physics |
| `fallGravity` | 12.5 tiles/s² | Derived from above |

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

### Task System

Tasks decompose into an ordered queue of Objectives. Each task:
1. **Initialize** — validates feasibility, creates objective queue, reserves resources
2. **Execute** — runs objectives sequentially
3. **Complete** — cleanup, return to Idle
4. **Fail** — release reservations, return to Idle

**Tasks (implemented):**

| Task | Description |
|------|-------------|
| `CraftTask` | Navigate to station, fetch inputs, work, drop outputs |
| `HarvestTask` | Navigate to plant, harvest when ready, drop products |
| `HaulTask` | Move floor/excess items to proper storage |
| `ConstructTask` | Build a blueprint |
| `SupplyBlueprintTask` | Deliver resources to incomplete blueprint |
| `ObtainTask` | Fetch a specific item |
| `EepTask` | Navigate home and sleep |
| `DropTask` | Drop excess inventory |
| `GoTask` | Navigate to a tile |

**Objectives (atomic steps):**
`GoObjective`, `FetchObjective`, `DeliverObjective`, `WorkObjective`, `EepObjective`

### Job System

Each animal has one Job. Jobs define which Recipes the animal can execute. Recipe selection uses a score that balances global item quantities against configurable targets.

## Navigation

- **Algorithm**: A* with Euclidean heuristic. Edge costs vary by traversal type (see below).
- **Locomotion**: `speed = maxSpeed * edgeLength / edgeCost`. Both values come from `Graph.GetEdgeInfo(from, to)`, so speed automatically adjusts for sub-tile and slow edges.
- **Standability**: tile is standable if tile below is solid, has a platform/building, or has a ladder.
- **Vertical movement**: ladders produce direct node-to-node vertical edges (cost 2.0). Cliff climbing and stairs use **waypoint chains** (see below).
- **Road speed boost**: road tiles reduce A* edge cost by `pathCostReduction` (both endpoints contribute), making roads faster to path through. Base movement speed is `1 tile/sec × efficiency`.
- **Helper queries**: `FindPathToBuilding`, `FindPathToItem`, `FindPathToStorage`, `FindPathAdjacentToBlueprint`, `FindPathToHarvestable`

### Waypoint system (stairs and cliff climbs)

Both stairs and one-block cliff climbs are represented as **waypoint chains** — intermediate `Node` objects with fractional world positions, not backed by tiles. They are stored in `stairWaypoints` and `cliffWaypoints` dictionaries and rebuilt whenever nearby tiles change.

**Cliff climb** (one solid block beside a standable tile): `base → wp1 → wp2 → cliffTop`
- `wp1` at `(base.x + dir×0.25, base.y)` — 0.25 tiles from base, normal speed
- `wp1 → wp2` vertical, cost 3.0 (slow both up and down)
- `wp2` at `(base.x + dir×0.25, base.y+1)` — 0.75 tiles from cliff top, normal speed

**Stair** (stair tile): `left → entry → exit → right`, where entry/exit are 0.5-tile offsets from their endpoints; the diagonal entry→exit step has cost 1.8 / length √2.

`preventFall` in `Nav.Move()` suppresses the fall check while on any waypoint edge or a ladder edge. Direct non-standable tile traversal no longer occurs — all such paths go through waypoints.

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
- `Reservable` (capacity-based) prevents multiple animals targeting same resource
- `Produce()` adds to inventory and global inventory simultaneously; `MoveItemTo()` moves between inventories without touching global inventory
- `AddItem()` is private — always use `Produce`, `MoveItemTo`, or `TakeItem` externally
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

## Unit System — Fen / Liang

All item quantities are stored as **fen** (integers), where **100 fen = 1 liang**. Display uses `ItemStack.FormatQ(int fen, bool discrete = false)` — drops trailing zeros, shows no decimals for exact integers. Overload `FormatQ(ItemQuantity iq)` uses `iq.item.discrete` automatically.

- **JSON data** is authored in liang (can be decimal, e.g. `0.5`). The field type is `float` (`ItemNameQuantity.quantity`).
- **Conversion** to fen happens at all `ItemNameQuantity → ItemQuantity` sites (Db.cs, Structure.cs, Tile.cs, Plant.cs): `(int)Math.Round(q * 100)`.
- **Stack sizes**: animal inv = 5 × 1000 fen; floor/default = 1000 fen; storage = `storageStackSize * 100` (converted in `StructType.OnDeserialized`).
- Old saves are **incompatible** (quantities were in the old unit). Fresh start required after this change.

## Rendering & Layers

Structures render in four depth layers per tile, stored as named fields on `Tile`:

| Depth | Field | Contents | Sprite position |
|-------|-------|----------|----------------|
| `r` | `tile.road` | Roads | `(x, y−1)` — renders on surface of solid tile below |
| `b` | `tile.building` | Buildings, plants | `(x, y)` |
| `m` | `tile.mStruct` | Platforms | `(x, y)` |
| `f` | `tile.fStruct` | Stairs, ladders | `(x, y)` |

Each layer also has a corresponding `Blueprint` field (`roadBlueprint`, `bBlueprint`, `mBlueprint`, `fBlueprint`). Roads use `sortingOrder = 1` to render above the tile ground sprite (order 0). Multiple layers can coexist on the same tile — roads are independent of all other layers.

## Lighting

Custom `ScriptableRendererFeature` pipeline — no URP Light2Ds used. All lights use `BlendOp Max` so overlapping sources take the brightest value. Final result is Multiply-blitted onto the scene.

### Render pipeline (per frame)

1. **NormalsCapturePass** (`BeforeRenderingTransparents`) — draws all sprites with `Hidden/NormalsCapture` override material into `_CapturedNormalsRT`. Outputs world-space normals packed 0–1 (`rgb * 0.5 + 0.5`). Transparent pixels discarded (background stays black = flat fallback).
2. **LightPass** (`AfterRenderingTransparents`) — clears light RT to `SunController.GetAmbientColor()`, then `BlendOp Max`-draws:
   - Sun (directional): fullscreen quad, NdotL with `_SunDir` from `SunController.GetSunDirection()`.
   - Point lights (torches, etc.): per-light quad scaled to `outerRadius×2`, radial falloff × NdotL with `toLight = normalize(lightXY − fragXY, −lightHeight)`.
3. **Composite** — `cmd.Blit(lightRT, scene, LightComposite)` multiplies scene by light map (`Blend DstColor Zero`).

### Key files

All lighting C# scripts and shaders live in `Assets/Lighting/`.

| File | Role |
|------|------|
| `LightFeature.cs` | `ScriptableRendererFeature` containing `NormalsCapturePass` + `LightPass` |
| `LightSource.cs` | Component: `lightColor`, `intensity`, `outerRadius`, `innerRadius`, `lightHeight`, `isDirectional`. Registers itself in a static list read by `LightPass`. |
| `SunController.cs` | Orbiting sun, sky color, `GetAmbientColor()`, `GetSunDirection()`. Sun child has a `LightSource (isDirectional=true)`. |
| `NormalsCapture.shader` | Tangent→world normal transform for flat 2D sprites: `(x, y, z) → (x, y, −z)`. |
| `LightCircle.shader` | Point light pass: radial falloff × NdotL. |
| `LightSun.shader` | Directional sun pass: fullscreen NdotL. |
| `LightComposite.shader` | Multiply blit onto scene. |

`Assets/Editor/SpriteNormalMapGenerator.cs` — sprite normal map batch tool (must stay in `Editor/`).

### Normal maps

**Encoding**: world-space, packed 0–1. Flat camera-facing sprite = `(0,0,−1)` → `(0.5, 0.5, 0.0)`. Black = no sprite, shader uses flat fallback. No Y-flip on screen UV (DrawRenderers and the light pass projection both use OpenGL convention, V=0 at bottom).

**Tile normal maps** (`TileNormalMaps.cs`): 16 procedural variants (4-bit adjacency mask). Exposed edges bevel outward; interior stays flat. Applied via `MaterialPropertyBlock` on tile `SpriteRenderer`s.

**Sprite normal maps** (`SpriteNormalMapGenerator.cs`): editor tool (**Tools → Generate All Sprite Normal Maps**) batch-processes `Assets/Resources/Sprites/`. For each texture:
1. Generates `_n.png` — edge pixels get outward normals, interior gets flat forward normal.
2. Imports as `Default` / `Uncompressed` RGBA32 (not NormalMap type — must stay plain packed 0–1).
3. Auto-assigns as `_NormalMap` secondary texture on the source sprite importer.

## Key Design Decisions

- **Hierarchical tasks over behavior trees**: simple objective queue is easy to reason about and extend
- **Data-driven content**: adding new items/buildings/recipes requires no code changes
- **Global inventory tracking**: enables recipe scoring without querying every tile
- **Floor decay penalty**: incentivizes hauling without enforcing it
- **Reservation before execution**: tasks reserve resources during Initialize to prevent conflicts at runtime

## Multiplayer Trading

Player-to-player marketplace using a WebSocket connection to a separate Go
server. Players can post buy/sell orders, view live order books, execute trades,
and chat with other settlements.

### Architecture

```
Unity Client (TradingClient.cs)
        │  WebSocket  ws://127.0.0.1:8080/ws?name=<PlayerName>
        ▼
  Go Server  (~/projects/shonei-server/main.go)
    └── Hub  (goroutine, central coordinator)
          ├── per-client readPump  (goroutine)
          ├── per-client writePump (goroutine)
          └── Exchange
                └── Book per item  (in-memory, price-time priority)
```

- Hub pattern: all clients communicate through the central Hub; clients never
  address each other directly.
- Player name passed as a query param on connect (`?name=Mouse1`); the server
  stamps it onto outgoing chat and order messages.
- Unity client auto-reconnects every 20 s when disconnected.
- Server binds to `127.0.0.1:8080` (localhost only for now).

### Wire Protocol

All messages use an envelope wrapper:

```json
{ "type": "<message_type>", "payload": { ... } }
```

#### Client → Server

| Type | Purpose | Key fields |
|---|---|---|
| `order` | Place a buy or sell order | `item`, `side` ("b"/"s"), `price`, `quantity` |
| `market_query` | Request the current order book for an item | `item` |
| `chat` | Send a chat message to all players | `text` |

`from` is always injected server-side from the connection name.

#### Server → Client

| Type | Sent to | Purpose | Key fields |
|---|---|---|---|
| `market_response` | Requester only | Order book snapshot | `item`, `buys[]`, `sells[]` |
| `fill` | All clients | Trade executed | `buyer`, `seller`, `item`, `price`, `quantity` |
| `order` | All clients | Order placement broadcast | `from`, `item`, `side`, `price`, `quantity` |
| `chat` | All clients | Chat message | `from`, `text` |

`buys[]` is sorted highest price first (best bid at index 0).
`sells[]` is sorted lowest price first (best ask at index 0).
Fill price is the **resting order's price** (maker price).

### Order Book — Matching Engine

Continuous double auction, price-time priority:

1. Incoming **buy** at price P matches resting sells with `price ≤ P`, best ask
   first.
2. Incoming **sell** at price P matches resting buys with `price ≥ P`, best bid
   first.
3. Fill quantity = `min(incoming.qty, resting.qty)`.
4. After matching, any remaining quantity rests in the book (no expiry).
5. All fills broadcast to all clients.

Insertion maintains sorted order via binary search.

### Unity Client — TradingClient.cs

Singleton (`TradingClient.instance`). Attach to a persistent GameObject.

**Events:**

| Event | Fired when |
|---|---|
| `OnConnectionChanged` | WebSocket connect / disconnect |
| `OnMarketResponse` | Server sends order book snapshot |
| `OnFill` | A trade executes (broadcast to all) |
| `OnChat` | Chat message received |

**Public methods:**

```csharp
TradingClient.instance.QueryMarket(string item);
TradingClient.instance.SendOrder(string item, string side, int price, int qty);
TradingClient.instance.SendChat(string text);
```

All methods are no-ops when offline.

### Unity UI — TradingPanel.cs

Singleton. Subscribes to TradingClient events in `Start()`, unsubscribes in
`OnDestroy()`.

**Inspector fields:**

| Field | Type | Purpose |
|---|---|---|
| `itemInput` | TMP_InputField | Item name (shared by query and order entry) |
| `buysList` | Transform | Scroll content for buy-side book rows |
| `sellsList` | Transform | Scroll content for sell-side book rows |
| `orderPrice` | TMP_InputField | Price input for order entry |
| `orderQty` | TMP_InputField | Quantity input for order entry |
| `chatScroll` | ScrollRect | Wraps chatList; used for auto-scroll to bottom |
| `chatList` | Transform | VLG content for chat + fill messages |
| `chatInput` | TMP_InputField | Chat message input |
| `onlineIndicator` | GameObject | Image + TMP child showing online/offline |

**Button wiring:**

| Button | Method |
|---|---|
| TradingToggle | `TradingPanel.instance.Toggle()` |
| QueryButton | `TradingPanel.instance.OnClickQuery()` |
| BuyButton | `TradingPanel.instance.SetBuy(true)` |
| SellButton | `TradingPanel.instance.SetBuy(false)` |
| PlaceOrderButton | `TradingPanel.instance.OnClickPlaceOrder()` |
| ChatSendButton | `TradingPanel.instance.OnClickSendChat()` |

`itemInput` and `chatInput` also trigger on Enter key (wired in `Start()`).

**Chat/fill display:** capped at 20 entries; fills shown in green. Requires
`chatList` VerticalLayoutGroup with **Control Child Size Width ON, Height OFF**;
each row uses ContentSizeFitter (vertical = PreferredSize).

**Indicator sprites:** loaded from `Resources/Sprites/Misc/indicator/green` and
`Resources/Sprites/Misc/indicator/red`.

### Running the Server

```bash
cd ~/projects/shonei-server
go mod tidy      # first run only
go run main.go   # listens on 127.0.0.1:8080
```

Test client: `go run client/main.go -name=Mouse1`
Commands: `/b <item> <qty> <price>`, `/s <item> <qty> <price>`, `/market <item>`, or plain text for chat.

### Trading Logistics (in-game item flow)

Trades don't happen directly on tiles — all goods and silver pass through a
special **Market building**, which represents a distant city.

#### Market building

- StructType `"market"`, `isMarket = true` on the StructType.
- Auto-spawned once at world gen at tile `(10, 10)`; never player-buildable.
- Does **not** appear in the build menu (enforced via the unlock system — see below).
- The Market inventory starts as `InvType.Storage` then `SetMarket()` flips it to `InvType.Market` and initializes `targets`/`incomingRes`. Normal haul logic (HaulTask) skips market inventories; only merchant mice may target them.

#### Player name

`TradingClient.playerName` — hardcoded `"anita"` for now. Used to identify
which side of a fill belongs to this player.

#### Merchant job

Dedicated `"merchant"` job in jobsDb. Merchant mice only perform
`HaulToMarketTask` and `HaulFromMarketTask`; they do not take craft/harvest
jobs. They are the only mice allowed to path to the market building.

#### Order placement flow

Before calling `TradingClient.SendOrder`, `TradingPanel` validates against the
market inventory:

- **Sell order** (item → silver): market inv must have ≥ `qty` of `item`
  unreserved.
- **Buy order** (silver → item): market inv must have ≥ `price × qty` silver
  unreserved, **and** enough unreserved incoming capacity for `qty` of `item`.

If checks pass:
1. Send order to server.
2. For sells: reserve `qty` of `item` in market inv (existing `Reservable`
   mechanism).
3. For buys: reserve `price × qty` silver outgoing **and** reserve `qty`
   incoming capacity for `item` (new `reservedIncoming` dict on Inventory —
   see below).

#### Fill processing

When `OnFill` fires and `fill.buyer == playerName` or `fill.seller ==
playerName`:

- **We are buyer**: deduct `fill.price × fill.quantity` silver from market
  inv; add `fill.quantity` of `fill.item` to market inv. Release silver
  outgoing reservation and incoming capacity reservation for item.
- **We are seller**: deduct `fill.quantity` of `fill.item` from market inv;
  add `fill.price × fill.quantity` silver to market inv. Release item
  outgoing reservation and incoming capacity reservation for silver.

Partial fills are handled — only the filled quantity is deducted/released.

#### Space (incoming capacity) reservation

New field on `Inventory`: `Dictionary<Item, int> reservedIncoming`.

- Counts how many units of each item are pre-allocated for pending buy orders.
- Available incoming capacity for item X = `(totalSlots - usedSlots) -
  reservedIncoming[X]`.
- Initially only used for the market inventory; can generalize later.

#### Building unlock system

`StructType` has a `defaultLocked` bool (JSON field `"defaultLocked": true`).

- Absent/false → available in build menu from the start (most buildings).
- `true` → hidden from build menu at startup; must be unlocked via research.
- Currently locked: `soil pit`, `quarry`, `market` (market is never unlockable — it's auto-spawned by world gen).
- `BuildPanel.Start()` skips locked buildings when building sub-panels.
- `BuildPanel.UnlockBuilding(name)` adds the entry to the correct sub-panel at runtime, called from `ResearchSystem.ApplyEffect`.

#### Known gaps / TODO

- **Concurrency**: `Exchange.placeOrder` is called from per-client goroutines
  with no mutex on the Exchange — needs to be serialized through the Hub's
  `run()` goroutine.
- **Order cancellation**: not yet implemented on server; resting-order
  reservations cannot be released until this is added.
- **Persistence**: order book is in-memory only; lost on server restart.
- **Player name**: hardcoded as `"anita"`; make configurable later.
- **Authentication**: none; name is trusted from query param.
- **LAN/internet play**: change server bind to `0.0.0.0` and update `WsUrl`.
- **NPC / bot orders**: no server-side liquidity seeding.
- **Redundant order broadcast**: the `order` broadcast after matching is noisy
  since clients already receive `fill` messages; consider removing.

## Research System

Scientists working in **laboratory** buildings generate research points over time. Points can be spent to unlock new buildings, recipes, or misc upgrades.

### Points mechanic

Every `ticksInDay/12` seconds of game time, the system samples how many scientist mice are actively working in a lab (in the `Working` state). That sample (`scientists × 10`) is stored in a 15-entry circular buffer. The player's **available research points** = `max(buffer) − totalSpent`. This gives a stable, peak-based value that doesn't swing when a mouse briefly stops to eat.

### Research nodes (`researchDb.json`)

```json
{ "id": 1, "name": "Excavation", "type": "building", "unlocks": "soil pit", "prereqs": [], "cost": 5 }
{ "id": 2, "name": "Quarry", "type": "building", "unlocks": "quarry", "prereqs": [1], "cost": 5 }
{ "id": 3, "name": "Improved Research", "type": "misc", "unlocks": "research_efficiency", "prereqs": [], "cost": 5 }
```

Types: `"building"`, `"recipe"`, `"misc"`. Prerequisites are node `id` integers. Unlocking permanently adds `cost` to `totalSpent`.

### Key classes

| Class | Role |
|---|---|
| `ResearchSystem` | Singleton model. Holds buffer, `totalSpent`, `unlockedIds`, `researchEfficiencyMultiplier`. Ticked from `World.Update`. |
| `ResearchSystem.ApplyEffect(node)` | Single dispatch point for all research effects. Called on unlock and on load via `ReapplyAllEffects()`. |
| `ResearchPanel` | Full-screen UI. Icon grid (`GridLayoutGroup`). Cards show sprite + cost. Hover → tooltip. Closes on world click. |
| `ResearchDisplay` | Component on the ResearchDisplay prefab. Receives `Setup(node, rs, onUnlock)` to populate icon, cost, button. |
| `TooltipSystem` | Singleton on Canvas. `Show(title, body)` / `Hide()`. Follows mouse. |
| `Tooltippable` | Component on any UI element; fires tooltip on pointer enter/exit. |
| `ResearchTask` | Task for scientist job. Navigates to lab, reserves it, works in 10-tick loops. |

### Save data

`ResearchSaveData`: `float[] pointHistory`, `int historyIndex/tickCounter`, `float totalSpent`, `int[] unlockedIds`.

## Technology

- **Engine**: Unity (2D)
- **Language**: C#
- **JSON**: Newtonsoft.Json (with `[OnDeserialized]` reference resolution)
- **Pathfinding**: Custom A* in `Navigation.cs`
- **Sprites**: Custom pixel art (mouse, plants, buildings)
- **Trading server**: Go, gorilla/websocket, in-memory order book

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
├── Controller/        Unity MonoBehaviours (UI, input, rendering)
├── Model/             Pure C# game logic
│   ├── World.cs       Tile grid, update loops, system access
│   ├── Animal.cs      Agent data + task dispatch
│   ├── AnimalStateManager.cs  State machine logic
│   ├── Task.cs        Task + Objective definitions
│   ├── Navigation.cs  A* pathfinding
│   ├── Inventory.cs   Item containers (animal/storage/floor)
│   ├── Plant.cs       Growing plants
│   ├── Structure.cs   Placed buildings
│   ├── Tile.cs        Grid cell
│   ├── Item.cs        Item type definitions
│   ├── Db.cs          JSON database loader
│   └── Reservable.cs  Resource reservation (capacity-based)
└── Resources/
    ├── buildingsDb.json
    ├── plantsDb.json
    └── recipesDb.json
```

### Update Frequencies

- **Animals + Plants**: every 1 second (game tick)
- **Inventory**: every 0.2 seconds
- **Rendering/Input**: every frame (Unity Update)

## Data-Driven Design

All game content is defined in JSON and loaded at startup via `Db.cs` using Newtonsoft.Json. String references (e.g., `"wood"`) are resolved to object references in `[OnDeserialized]` callbacks.

| File | Content |
|------|---------|
| `buildingsDb.json` | StructTypes: house, drawer, sawmill, soil pit, ladder, stairs, platform |
| `plantsDb.json` | PlantTypes: tree (30 ticks), wheat (15 ticks) |
| `recipesDb.json` | Recipes: sawyer (wood → plank + sawdust), digger (∅ → soil) |

Lookups: `itemByName`, `jobByName`, `structTypeByName`, `plantTypeByName`, `tileTypeByName`

## Save / Load / Reset

World state is serialized to JSON via Newtonsoft.Json and stored in `Application.persistentDataPath/saves/<slot>.json`. The entry point is `SaveSystem` (MonoBehaviour singleton).

### Save data classes (`WorldSaveData.cs`)
Plain C# classes — **no `[Serializable]`** (not needed by Newtonsoft.Json; adding it causes Unity's own serializer to materialize default instances on MonoBehaviour fields instead of null).

### Structure creation rules
Two legitimate ways to put a structure into the world:

| Method | When to use |
|--------|-------------|
| `StructController.Construct(st, tile)` | Normal gameplay (via `Blueprint.Complete()`). Deducts costs from `GlobalInventory`. |
| `new Building/Plant/etc.(…)` + `StructController.Place(s)` | Load path and world generation. No cost side-effects. |

Always call `Place()` after direct construction — it registers the structure for tracking so `ClearWorld()` can find and destroy it later. Direct `new X()` without `Place()` is a bug.

### Startup ordering

Unity `Start()` execution order between MonoBehaviours is undefined. `WorldController.Start()` is an `IEnumerator` that `yield return null`s before calling `GenerateDefault()`, ensuring all other `Start()` methods (notably `AnimalController.Start()` initializing `jobCounts`) have run first.

### Reset / Load flow

```
Reset:  WorldController.ClearWorld()  →  WorldController.GenerateDefault()
Load:   WorldController.ClearWorld()  →  WorldController.ApplySaveData(data)
```

**`ClearWorld()`** tears down in this order:
1. Destroy all structures via `StructController.GetStructures()` → `s.Destroy()` (nulls tile refs, removes from PlantController/StructController, destroys GOs)
2. Destroy all blueprints (iterate tiles, call `bp.Destroy()`)
3. Destroy all animals (call `animal.Destroy()` which destroys their inventory + GO), reset `na = 0`, call `ResetJobCounts()`
4. Destroy remaining inventories (tile floor/storage invs) via `InventoryController.inventories`
5. Zero all `GlobalInventory.itemAmounts`
6. Reset all tiles: null `tile.inv`, set `tile.type = empty` (fires sprite callbacks)
7. Reset `world.timer`, clear `InfoPanel`

**`GenerateDefault()`** rebuilds:
1. Set tile types (soil y<10, stone y<8)
2. Create default plants + `Place()` each with StructController
3. `world.graph.Initialize()`
4. Spawn 4 animals via `AnimalController.AddAnimal()`
5. `StartCoroutine(DefaultJobSetup)` — yields 1 frame (waits for `Animal.Start()`), then assigns jobs and starting wheat

**`ApplySaveData()`** rebuilds from file:
1. Apply tile types + blueprints + structures (via `Place()`) + inventories per saved tile
2. `world.graph.Initialize()`
3. `LoadAnimal()` per saved animal (sets `pendingSaveData`; `Animal.Start()` applies it next frame)

### `pendingSaveData` pattern
`LoadAnimal()` sets `animal.pendingSaveData` before `Animal.Start()` runs. `Start()` checks it: if set, applies saved stats/job; if null, initializes with full hunger/sleep. Marked `[System.NonSerialized]` to prevent Unity from replacing null with a default instance.

## Animal AI

### States

```
Idle → Working
     → Moving → (arrives) → back to Working or Idle
Idle → Eeping (sleeping)
```

- **Idle**: calls `ChooseTask()`, selects best recipe by score
- **Working**: executes current objective (craft, harvest, build, sleep)
- **Moving**: navigates path via A*; calls `OnArrival()` on completion
- **Eeping**: sleeps at home, restores sleep meter; can trigger reproduction

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
| `FallTask` | Fall downward until stable |

**Objectives (atomic steps):**
`GoObjective`, `FetchObjective`, `DeliverObjective`, `WorkObjective`, `EepObjective`, `FallObjective`

### Job System

Each animal has one Job. Jobs define which Recipes the animal can execute. Recipe selection uses a score that balances global item quantities against configurable targets.

## Navigation

- **Algorithm**: A* with Manhattan heuristic
- **Standability**: tile is standable if tile below is solid, has a platform/building, or has a ladder/stairs
- **Vertical movement**: ladders (straight up), stairs (diagonal)
- **Helper queries**: `FindPathToBuilding`, `FindPathToItem`, `FindPathToStorage`, `FindPathAdjacentToBlueprint`, `FindPathToHarvestable`

## Inventory System

Three inventory types:

| Type | Slots | Stack Size | Decay Rate |
|------|-------|-----------|-----------|
| Animal | 5 | 10 | normal |
| Storage | varies | varies | normal |
| Floor | 5 | varies | 5× normal |

- Items decay over time 
- `allowed` dict filters what item types a storage accepts
- `Reservable` (capacity-based) prevents multiple animals targeting same resource
- `Produce()` adds to inventory and global inventory simultaneously

## Rendering & Layers

Structures render in three depth layers per tile:
- **Background (b)**: buildings, plants
- **Midground (m)**: platforms
- **Foreground (f)**: stairs, ladders

This enables stairs and multi-part structures to be placed on the same tiles

## Current State & Known Issues

### In Progress

- **Reservation system**: partially implemented; needs extending to crafting, obtaining tasks
- **Multi-round work**: `WorkObjective` needs to span multiple ticks (affects crafting and harvesting)
- **Blueprint inventories**: need allow-lists for specific resource requirements
- **Blueprint placement**: must not override floor items; overlapping blueprints need conflict detection
- **Animal AI**: prevent infinite job-switching; add leisure state

### Known Bugs

- Building on tile with floor item can delete the item
- Screen jitters when panning
- Mice can get stuck descending slopes
- Stairs don't position animals correctly
- Digger job sometimes loops
- Blueprint overlap validation missing

### Planned Content

- Quarry building + sprites
- Mining floor tiles (separate command)
- Plant moisture/temperature mechanics
- More jobs and recipes

## Key Design Decisions

- **Hierarchical tasks over behavior trees**: simple objective queue is easy to reason about and extend
- **Data-driven content**: adding new items/buildings/recipes requires no code changes
- **Global inventory tracking**: enables recipe scoring without querying every tile
- **Floor decay penalty**: incentivizes hauling without enforcing it
- **Reservation before execution**: tasks reserve resources during Initialize to prevent conflicts at runtime

## Technology

- **Engine**: Unity (2D)
- **Language**: C#
- **JSON**: Newtonsoft.Json (with `[OnDeserialized]` reference resolution)
- **Pathfinding**: Custom A* in `Navigation.cs`
- **Sprites**: Custom pixel art (mouse, plants, buildings)

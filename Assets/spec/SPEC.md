# Shonei — Project Spec (Index)

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
│   ├── InventoryController.cs  Global inventory tracking + item display + selection routing (see SPEC-ui.md)
│   ├── AnimationController.cs  Animal sprite animation
│   ├── WorkOrderManager.cs  Centralised work queue — registers, prioritises, and dispatches tasks
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
│   ├── SkillSystem.cs     Skill enum + SkillSet (XP, levels, work speed bonus)
│   ├── WorldSaveData.cs   Save data classes (add fields here when extending save)
│   └── Reservable.cs  Resource reservation (capacity-based)
├── UI/                UI panels, displays, and tooltip system
│   ├── BuildPanel.cs, InfoPanel.cs, MenuPanel.cs, SaveMenuPanel.cs
│   ├── TradingPanel.cs, RecipePanel.cs, ResearchPanel.cs
│   ├── ItemDisplay.cs, JobDisplay.cs, OrderDisplay.cs, ResearchDisplay.cs
│   ├── StoragePanel.cs        Storage/liquid detail panel (slot view + allow tree, see SPEC-ui.md)
│   ├── SaveSlotEntry.cs   Per-row component for the save slot scroll list
│   ├── ConfirmationPopup.cs  Reusable yes/cancel modal (singleton)
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

### Building subclasses

Depth-0 buildings with custom behaviour subclass `Building` (e.g. `PumpBuilding`). The dispatch lives inline at two call sites — `StructController.Construct` and the `SaveSystem` load path — both marked with a comment to keep them in sync.

---

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
- **Trading server**: Go, gorilla/websocket, in-memory order book

---

## Sub-documents

| File | Contents |
|------|----------|
| [SPEC-data.md](SPEC-data.md) | All JSON database schemas — buildings, items, recipes, jobs, plants, tiles, research |
| [SPEC-lifecycle.md](SPEC-lifecycle.md) | Save/load/reset, startup ordering, time system, tick clock |
| [SPEC-ai.md](SPEC-ai.md) | Animal AI state machine, needs, task system, job system, WorkOrderManager |
| [SPEC-systems.md](SPEC-systems.md) | Navigation (A*, waypoints), inventory types, item falling, equip slots, fen/liang units |
| [SPEC-rendering.md](SPEC-rendering.md) | Depth layers, sprite sorting, custom lighting pipeline, normal maps |
| [SPEC-trading.md](SPEC-trading.md) | WebSocket protocol, matching engine, TradingClient, TradingPanel, in-game market logistics |
| [SPEC-research.md](SPEC-research.md) | Research points mechanic, node structure, key classes, save data |
| [SPEC-ui.md](SPEC-ui.md) | Inventory UI panels — global panel, StoragePanel, ItemDisplay DisplayMode, selection routing |

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
├── Controller/    Game-system MonoBehaviours (rendering, world lifecycle, input, WOM, save)
├── Model/         Pure C# game logic (no MonoBehaviours)
│   ├── Animal/        Animal + state machine + Nav + needs (Eating, Eeping, Happiness, Skills)
│   ├── Structure/     Structure base + Building/Plant subclasses (Windmill, Quarry, PumpBuilding, Flywheel, MouseWheel, MarketBuilding, PowerShaft, …) + Blueprint + StructType + StructureVisuals
│   └── Inventory/     Inventory, GlobalInventory, ItemStack, Item
├── Components/    Single-purpose MonoBehaviours: UI widgets (FillBar, ItemIcon) and building-attached visuals (ClockHand, RotatingPart, PortStubVisuals)
├── UI/            Panels, displays, tooltip system, InfoViews/ for the tabbed InfoPanel
├── Lighting/      Custom ScriptableRendererFeature lighting pipeline (shaders + SkyExposure + BackgroundTile)
├── Editor/        Editor-only tools (sheet splitters, sprite normal map generator)
└── Resources/
    ├── *.json         Game content (buildings, items, recipes, jobs, plants, research)
    ├── Sprites/       Item/plant sheets + split/ runtime-loaded variants
    └── Prefabs/       Runtime-instantiated prefabs (SaveSlot, BuildDisplay, …)
```

Use Glob / sub-specs to discover specific files. See the sub-documents table below for which SPEC covers which system.

### Building subclasses

Depth-0 buildings with custom behaviour subclass `Building` (e.g. `PumpBuilding`). Dispatch goes through the shared factory `Structure.Create(StructType, int, int)` in `Structure.cs` (see CLAUDE.md "Structure creation rules"). New subclasses: add a case to `Structure.Create` — no other dispatch site needed.

**Components**: Building optionally owns `Workstation` (player-adjustable worker slots) and `Reservoir` (consumable-resource inventory, burn rate, supply target — used for fuel, water, etc.). Non-null only when the StructType flags are set (`isWorkstation`, `hasFuelInv`). Both classes live in `Building.cs`.

**OnPlaced() hook**: Virtual method on `Structure`, called by `StructController.Construct()` after `Place()`. Building overrides to register WOM orders (`RegisterOrdersFor`), Plant overrides to register its harvest order. Not called during load — `Reconcile()` handles that.

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
| [SPEC-systems.md](SPEC-systems.md) | Navigation (A*, waypoints), inventory types, item falling, equip slots, water, weather, reservations, non-operational mechanisms, fen/liang units |
| [SPEC-rendering.md](SPEC-rendering.md) | Depth layers, sprite sorting, custom lighting pipeline, normal maps |
| [SPEC-trading.md](SPEC-trading.md) | WebSocket protocol, matching engine, TradingClient, TradingPanel, in-game market logistics |
| [SPEC-research.md](SPEC-research.md) | Research points mechanic, node structure, key classes, save data |
| [SPEC-ui.md](SPEC-ui.md) | Inventory UI panels — global panel, StoragePanel, ItemDisplay DisplayMode, selection routing |
| [SPEC-eventfeed.md](SPEC-eventfeed.md) | EventFeed singleton — in-game alert dispatcher, categories, bindings, renderer contract |
| [SPEC-sound.md](SPEC-sound.md) | Sound system — SoundManager singleton, SFX one-shots, ambient loops |
| [SPEC-worldgen.md](SPEC-worldgen.md) | World generation pipeline — terrain, caves, worm tunnels, water |
| [SPEC-books.md](SPEC-books.md) | Books feature — `ItemClass` enum, runtime tech-book generation, scribe + scriptorium + bookshelf, scientist book-borrow flow, reading leisure |
| [SPEC-power.md](SPEC-power.md) | Mechanical power — `PowerSystem` singleton, shaft connectivity, producers (mouse wheel, windmill), powerBoost on workstations, save/load via Phase 6 reconcile |
| [SPEC-mcp.md](SPEC-mcp.md) | Unity Editor work via MCP — when it's safe, common gotchas (Play mode, codedom, inactive lookups), UI style conventions (font sizes, sprites, spacing, color), workflow recipes |

---

## Canonical examples

When adding new content, read these files first and match their pattern:

| Adding... | Model after | Key file(s) |
|-----------|-------------|-------------|
| New Task type | `HaulTask` (simple) or `CraftTask` (complex) | `Assets/Model/Tasks/` (base in `Task.cs`) — also see 11-step checklist in SPEC-ai.md |
| New Objective | `FetchObjective` (retry) or `WorkObjective` (simple) | `Assets/Model/Objectives/` (base in `Objective.cs`) |
| New WOM order type | `RegisterConstructOrder` / `RegisterWorkstation` | `WorkOrderManager.cs` + SPEC-ai.md checklist |
| New Building subclass | `PumpBuilding` | `Assets/Model/Structure/PumpBuilding.cs` + `Structure.Create` in `Structure.cs` |
| New UI panel (exclusive) | `ResearchPanel` | `Assets/UI/ResearchPanel.cs` |
| New UI panel (detail) | `StoragePanel` | `Assets/UI/StoragePanel.cs` |
| New item/building/recipe | Existing JSON entries | `Assets/Resources/*.json` — see SPEC-data.md |
| New save data | `ResearchSaveData` | `WorldSaveData.cs` + `SaveSystem.cs` checklist |
| New item sprite | Existing sheets | `Sprites/Items/Sheets/` → Tools → Split All → Generate Normal Maps |
| New lit object | Existing sprite setup | Must be on a `litLayers` layer — see SPEC-rendering.md |


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
│   ├── Animal/
│   │   ├── Animal.cs            Agent data + task dispatch
│   │   ├── AnimalStateManager.cs  State machine logic
│   │   ├── Nav.cs               Movement, pathfinding helpers (Move, Find*, FindPath*)
│   │   ├── Eating.cs            Hunger/food state
│   │   ├── Happiness.cs         Happiness tracking (food variety, housing, decorations)
│   │   ├── Eeping.cs            Sleep/tiredness state
│   │   └── Skills.cs            Skill enum + SkillSet (XP, levels, work speed bonus)
│   ├── Structure/
│   │   ├── Structure.cs         Placed buildings + StructType
│   │   ├── Building.cs          Building subclass + Workstation, Reservoir
│   │   ├── Blueprint.cs         Unfinished structures awaiting construction
│   │   └── Plant.cs             Growing plants
│   ├── Inventory/
│   │   ├── Inventory.cs         Item containers (animal/storage/floor)
│   │   ├── GlobalInventory.cs   World-wide item totals
│   │   ├── ItemStack.cs         Single item stack within an inventory
│   │   └── Item.cs              Item type definitions
│   ├── World.cs       Tile grid, tick loop, ProduceAtTile, FallItems
│   ├── Task.cs        All Task + Objective class definitions
│   ├── Navigation.cs  A* pathfinding
│   ├── Tile.cs        Grid cell
│   ├── Db.cs          JSON database loader
│   ├── ResearchSystem.cs  Research points + unlock logic
│   ├── Reservable.cs  Resource reservation (capacity-based)
│   ├── WorldSaveData.cs   Save data classes (add fields here when extending save)
│   └── ModifierSystem.cs  Runtime stat modifiers
├── Components/        Small, single-purpose classes and MonoBehaviours
│   ├── PumpBuilding.cs    Building subclass (depth-0 pump with water check)
│   │   (Workstation and Reservoir classes now live in Building.cs alongside Building)
│   ├── ClockHand.cs       Clock hand rotation MonoBehaviour
│   └── ...                ItemIcon, StorageSlotDisplay, PixelSnapText, MatchCameraZoom, RainParticles
├── UI/                UI panels, displays, and tooltip system
│   ├── BuildPanel.cs, InfoPanel.cs, MenuPanel.cs, SaveMenuPanel.cs
│   ├── TradingPanel.cs, RecipePanel.cs, ResearchPanel.cs
│   ├── ItemDisplay.cs, JobDisplay.cs, OrderDisplay.cs, ResearchDisplay.cs
│   ├── StoragePanel.cs        Storage/liquid detail panel (slot view + allow tree, see SPEC-ui.md)
│   ├── SaveSlotEntry.cs   Per-row component for the save slot scroll list
│   ├── ConfirmationPopup.cs  Reusable yes/cancel modal (singleton)
│   ├── TooltipSystem.cs, Tooltippable.cs
│   └── UI.cs          Static singleton hub; also owns exclusive-panel registry (RegisterExclusive / OpenExclusive)
├── Lighting/          Custom lighting pipeline (ScriptableRendererFeature)
└── Resources/
    ├── buildingsDb.json
    ├── plantsDb.json
    ├── recipesDb.json
    ├── itemsDb.json
    ├── jobsDb.json
    ├── researchDb.json
    └── Sprites/Items/
        ├── Sheets/    ← source sprite sheets (not loaded at runtime)
        └── split/     ← split output loaded by Resources.Load at runtime
```

### Building subclasses

Depth-0 buildings with custom behaviour subclass `Building` (e.g. `PumpBuilding`). Subclass dispatch is handled by a single shared factory method `Structure.Create(StructType, int, int)` in `Structure.cs`, called by both `StructController.Construct` (gameplay) and `SaveSystem.RestoreStructure` (load). When adding a new subclass, add its case to `Structure.Create` — no other dispatch site needed.

**Components**: Building optionally owns `Workstation` (player-adjustable worker slots) and `Reservoir` (consumable-resource inventory, burn rate, supply target — used for fuel, water, etc.). These are non-null only when the StructType flags are set (`isWorkstation`, `hasFuelInv`). Both classes live in `Building.cs`.

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
| [SPEC-systems.md](SPEC-systems.md) | Navigation (A*, waypoints), inventory types, item falling, equip slots, fen/liang units |
| [SPEC-rendering.md](SPEC-rendering.md) | Depth layers, sprite sorting, custom lighting pipeline, normal maps |
| [SPEC-trading.md](SPEC-trading.md) | WebSocket protocol, matching engine, TradingClient, TradingPanel, in-game market logistics |
| [SPEC-research.md](SPEC-research.md) | Research points mechanic, node structure, key classes, save data |
| [SPEC-ui.md](SPEC-ui.md) | Inventory UI panels — global panel, StoragePanel, ItemDisplay DisplayMode, selection routing |

---

## Canonical examples

When adding new content, read these files first and match their pattern:

| Adding... | Model after | Key file(s) |
|-----------|-------------|-------------|
| New Task type | `HaulTask` (simple) or `CraftTask` (complex) | `Task.cs` — also see 11-step checklist in SPEC-ai.md |
| New Objective | `FetchObjective` (retry) or `WorkObjective` (simple) | `Task.cs` |
| New WOM order type | `RegisterConstructOrder` / `RegisterWorkstation` | `WorkOrderManager.cs` + SPEC-ai.md checklist |
| New Building subclass | `PumpBuilding` | `Assets/Components/PumpBuilding.cs` + `Structure.Create` in `Structure.cs` |
| New UI panel (exclusive) | `ResearchPanel` | `Assets/UI/ResearchPanel.cs` |
| New UI panel (detail) | `StoragePanel` | `Assets/UI/StoragePanel.cs` |
| New item/building/recipe | Existing JSON entries | `Assets/Resources/*.json` — see SPEC-data.md |
| New save data | `ResearchSaveData` | `WorldSaveData.cs` + `SaveSystem.cs` checklist |
| New item sprite | Existing sheets | `Sprites/Items/Sheets/` → Tools → Split All → Generate Normal Maps |
| New lit object | Existing sprite setup | Must be on a `litLayers` layer — see SPEC-rendering.md |

---

## Anti-patterns (system-specific)

Non-obvious gotchas that have caused bugs before. Cross-system anti-patterns live in CLAUDE.md.

- **Craft order job check** (AI): Do NOT use `structType.job` for craft eligibility — that's the *construction* job (e.g. "hauler" for a sawmill). Use `Array.Exists(a.job.recipes, r => r != null && r.tile == buildingName)`. See SPEC-ai.md.
- **Normals RT format** (Rendering): Must be **ARGB32**, not the camera's default HDR format (lacks alpha). Alpha encodes the lighting tier.
- **`cmd.DrawMesh` for fullscreen passes** (Rendering): Use `cmd.Blit` for sun pass, not `cmd.DrawMesh` — DrawMesh silently fails on cameras without PixelPerfectCamera.
- **Stale WOM orders after world clear** (AI): `ClearAllOrders()` must be called at the start of `ClearWorld()`, before destroying any objects.

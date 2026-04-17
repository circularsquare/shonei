# Shonei — Save / Load / Reset & Time System

## Save / Load / Reset

World state is serialized to JSON via Newtonsoft.Json and stored in `Application.persistentDataPath/saves/<slot>.json`. The entry point is `SaveSystem` (MonoBehaviour singleton).

### Save data classes (`WorldSaveData.cs`)

Plain C# classes — **no `[Serializable]`** (not needed by Newtonsoft.Json; adding it causes Unity's own serializer to materialize default instances on MonoBehaviour fields instead of null).

### Bulk-teardown warnings (`WorldController.isClearing`)

`ClearWorld()` sets the static flag `WorldController.isClearing = true` for the duration of the teardown and clears it at the end. Destroy paths that emit diagnostics about dangling state (reserved inventory stacks, non-empty building storages, reservoir contents) gate those warnings on `!WorldController.isClearing`, since during bulk teardown everything is destroyed together and the dangling references are expected — not a bug.

Current call sites: `Inventory.Destroy` (reserved/resSpace stacks), `Building.Destroy` (non-empty storage, reservoir drop-to-floor). Add a similar guard to any new destroy-path diagnostic that would otherwise fire during a world reset.

### Load phase ordering

`ApplySaveData` (load path) follows a fixed nine-phase order. New saveable state
belongs in whichever phase fits — not appended at the end. The headers are
mirrored as `// ── Phase N: <name> ──` comments in `ApplySaveData` itself so the
structure is visible at the call site.

`WorldController.GenerateDefault` (worldgen path) loosely follows the same
phases but with freedom — it constructs initial state rather than restoring it,
so e.g. background walls fill happens *after* placing the market (still Phase 1
content, just generated rather than read from a save). The two converge at
Phase 4 (`graph.Initialize`) and run identically through Phases 5–9.

| # | Phase | Contents |
|---|----|----|
| 1 | World skeleton | tiles, walls, water, world timer |
| 2 | Structures | blueprints, then buildings (constructors create containers + defaults), then a RefreshColor pass over deconstruct blueprints so they can tint their now-existing target structure |
| 3 | Contents | floor/tile inventories (storage contents are filled inside Phase 2) |
| 4 | Spatial indexes | `SkyExposure`, `BackgroundTile`, `graph.Initialize` (final standability + lighting caches) |
| 5 | Configuration | targets, allow-filters, disabled recipes, research, weather — overrides on top of Phase 2 defaults |
| 6 | Observers | `WorkOrderManager.Reconcile` — registers WOM orders against final config. Then `MaintenanceSystem.RebuildFromWorld()` rebuilds its registered/broken sets from the restored `condition` values, followed by a `RefreshTint()` pass over every structure. |
| 7 | Agents | animals (frame 1 spawn; `Animal.Start` consumes `pendingSaveData` on frame 2) |
| 8 | Validation | `ValidateGlobalInventory`, optional `AuditOrders` (must run after Phase 7 since animal inventories count) |
| 9 | View | camera, UI panel state |

**Three nested invariants**, ordered from concrete to abstract:

1. **Containers before contents** — inventories must exist before items flow into them. (Why structures come before tile inventories; why `Building` constructors create their own storage in Phase 2.)
2. **Geometry before graph before queries** — tile types and structures set, then `graph.Initialize`, then anything that reads standability or pathing.
3. **All restorable state before any cross-system observer** — config (targets, recipe filters, research, weather) restored before `Reconcile` or `Validate*`. *This is the rule the `marketTargets` ordering bug violated.*

**Single objects can span phases**: e.g. the market `Building` is constructed in Phase 2 with default zero-targets, then its `targets` dict is overwritten in Phase 5. Don't try to do everything for one object in one place.

**Phase 7 is split across two frames.** `LoadAnimal` stages save state on `Animal.pendingSaveData` in frame 1; `Animal.Start` consumes it in frame 2. `PostLoadInit` runs after that and finalises colony-wide state. Anything that needs animals fully ready (cross-animal aggregates, colony stats) belongs in `PostLoadInit` — not directly in `ApplySaveData`.

### Startup ordering (frame by frame)

All three paths (Initial / Reset / Load) follow the same two-frame handoff:

```
Initial: GenerateDefault()         →  PostLoadInit (next frame)
Reset:   ClearWorld() + GenerateDefault()  →  PostLoadInit (next frame)
Load:    ClearWorld() + SaveSystem.ApplySaveData()  →  PostLoadInit (next frame)
```

**Frame 0** — all `Awake()`s run (order undefined, but before any `Start`):
- `Db.Awake()` — JSON data loaded; all lookups ready (including runtime-generated tech books / scribe recipes)
- `World.Awake()` — tiles and `graph.nodes` allocated; `node.standable = false` until `graph.Initialize()`
- `AnimalController.Awake()` — instance set, arrays allocated
- `ResearchSystem.Awake()` — parses `researchDb.json` into `nodes` / `nodeById` / `progress`. Reverse-index building is deferred to `Start()` because it reads `Db.bookRecipeIdByTechId`.

**Frame 0** — all `Start()`s run: (cross object initialization)
- `WorldController.Start()` runs up to `yield return null` and **pauses**
- `AnimalController.Start()` — populates `jobCounts` (Db is ready; must finish before frame 1)
- `ResearchSystem.Start()` — injects scribe-recipe unlock entries, builds `recipeToTechNode` / `jobToTechNode` / `buildingToTechNode`, validates job unlocks against Db.
- All UI/other controllers initialize

**Cross-singleton Awake rule**: if a MonoBehaviour's setup reads state published by another MonoBehaviour's `Awake` (not just scene references), do that reading in `Start` instead. Awake order between MonoBehaviours is undefined, but every Awake runs before any Start. Past bug: `ResearchSystem.Awake` ran before `Db.Awake`, so `Db.bookRecipeIdByTechId` was empty when `InjectBookRecipeUnlocks` read it — scribe book recipes silently slipped the tech gate. Prefer deferring to Start over forcing Script Execution Order, which hides the dependency in project settings.

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

---

## Save Slot UI

- `SaveMenuPanel` (`Assets/UI/SaveMenuPanel.cs`) — singleton. `Toggle()` opens and calls `RefreshSlotList()`, which scans `.json` files in `SaveDir` and instantiates a `SaveSlotEntry` per slot. "New Save" creates a uniquely-named slot and auto-focuses the rename field; "Reset" goes through `ConfirmationPopup` before calling `SaveSystem.Reset()`.
- `SaveSlotEntry` (`Assets/UI/SaveSlotEntry.cs`) — per-row component on `Assets/Resources/Prefabs/SaveSlot.prefab`. Handles inline rename (validates non-empty + no collision), Save/Load buttons, and a Delete button that routes through `ConfirmationPopup`.
- `SaveSystem` slot API: `GetSaveSlots`, `SlotExists`, `RenameSlot`, `DeleteSlot`, `GetAnimalCount` (deserialises just enough to read `animals.Length` for the mice-count label).
- `ConfirmationPopup` (`Assets/UI/ConfirmationPopup.cs`) — reusable singleton modal. **Keep inactive in scene** — `Show` finds it via `FindObjectOfType<ConfirmationPopup>(true)` so the inactive version is reachable. Sets itself last sibling on show; `Blocker` child absorbs background clicks. `Show(msg, onConfirm, confirmLabel?)`.

---

## Time System

The main game clock lives in `World.Update()` (World.cs). Each frame it accumulates `timer += Time.deltaTime` and fires tick events at fixed intervals:

| Interval | What fires |
|----------|-----------|
| 1 second | `AnimalController.TickUpdate()`, `PlantController.TickUpdate()`, `ResearchSystem.TickUpdate()` |
| 10 seconds | `WorkOrderManager.Reconcile()` — safety net re-scan (see SPEC-ai.md) |
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

## Weather & rain effects

`WeatherSystem` (Assets/Model/WeatherSystem.cs) advances state per in-game hour from `World.Update`. Rain probabilities: Clear → Rain 4%, Rain → Clear 12%. Lighting hooks (sun/ambient multipliers) are polled by `SunController` each frame.

When it is raining, `WeatherSystem.OnHourElapsed` runs rain effects via `WaterController`:

| Effect | Amount | Target |
|--------|--------|--------|
| Puddle top-up (`RainReplenish`) | +2 fixed-point water units | every partially-filled, non-full, non-solid tile |
| Tank rain-catch (`RainFillTanks`) | +100 fen (1 liang) water | every sky-exposed liquid-storage building whose filter allows water |

**Sky exposure**: `World.IsExposedAbove(x, y)` is the shared primitive. Returns true if no solid tile and no `solidTop` structure layer exists on any tile above `(x, y)`. Reuse this for future plant systems (rain-watered crops, sun-dependent growth).

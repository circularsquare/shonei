# Shonei — Save / Load / Reset & Time System

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

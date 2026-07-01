# Shonei — Save / Load / Reset & Time System

## Save / Load / Reset

World state is serialized to JSON via Newtonsoft.Json and stored in `Application.persistentDataPath/saves/<account>/<slot>.json` (editor: `<project>/SaveData/<account>/`). The entry point is `SaveSystem` (MonoBehaviour singleton). **Local saves are per-account**: `<account>` is `Session.StorageScope` — the logged-in username, or `.guest` when logged out — so accounts sharing a machine don't see each other's local saves. All slot I/O routes through `SaveStore` (the single chokepoint for the account-scoped path). When logged in, each save also mirrors to the account's cloud store (async, local stays authoritative) — see SPEC-trading "Account-owned cloud saves".

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

**Phase 7 is split across two frames** (full frame-by-frame mechanics in §Startup ordering): animals spawn in frame 1, `Animal.Start` finalises them in frame 2, then `PostLoadInit` runs. Anything that needs animals fully ready (cross-animal aggregates, colony stats) belongs in `PostLoadInit` — not directly in `ApplySaveData`.

### Front-end menu scene

The game boots into `Menu.unity` (build scene 0), **not** `Main`. `MenuController`
shows a login/register form (account auth — see SPEC-trading "Accounts & login"),
then a main menu (Continue / New Game / Load / Log Out / Quit) that `SceneManager.LoadScene("Main")`.
The logged-in identity lives in the static `Session`, which survives the scene
load. "New Game" sets `WorldController.bootNewGame` (a consumed-once static) to
force generation; "Continue" loads the freshest save (see below); the Load screen's
`bootSlot` names an explicit slot. The in-game save menu has a "main menu" button
(`SaveMenuPanel.OnClickMainMenu`) that returns to `Menu.unity`.

**Continue resolution & cloud prefetch.** When the menu appears (logged in) it warms a
cached cloud listing — `SaveSync.WarmCloudList()`, state `None/Fetching/Ready/Failed`,
exposed via `CloudState`/`CachedCloud` + `OnCloudListChanged`. Continue is enabled only
when there's a *known* save (a local slot, or a confirmed loadable cloud entry); it stays
disabled while the list is still fetching. At click time Continue compares the newest local
save's mtime against the freshest cloud save and loads the winner (downloading the cloud
copy to disk first if it wins). **Load-bearing:** a *failed* cloud fetch is NOT the same as
"no saves" — if the fetch failed and there's no local save, Continue keeps the player on the
menu rather than silently generating a new world (the original cloud-save bug conflated the
two). New-world-on-Continue happens only when we positively know there is nothing anywhere.

**Play always starts in Menu (editor).** `SceneMenu.cs` (`[InitializeOnLoad]`) sets
`EditorSceneManager.playModeStartScene` to `Menu.unity`, so pressing Play from any open
scene routes through the real login/Continue flow instead of booting Main into a save-less
generated world. Toggle off via `Tools/Scene/Start Play in Menu` to iterate on Main directly
(EditorPrefs-persisted). `Tools/Scene` also has Open Menu / Open Main shortcuts.
Opening `Main.unity` directly still works for inspection; only *Play* is rerouted.

### Startup ordering (frame by frame)

(Within `Main` — `Menu` is a lightweight scene with no World.) All three paths
(Initial / Reset / Load) follow the same two-frame handoff:

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
- On the new-world path only, `GenerateDefault` ends by pausing time and (if `World.settlementName` is still empty) opening `SettlementNamePopup` over the paused world. Confirming sets the name; skipping leaves it null → `SettlementDisplayName` falls back to `"new town"`. The name persists in `WorldSaveData.settlementName` (Phase 1) and surfaces in the happiness header + autosave slot names. The load path restores it in `ApplySaveData` and never re-prompts.

**Frame 2** — coroutines resume:
- **`Animal.Start()`** — initializes hunger/sleep/happiness; applies `pendingSaveData` if on load path
- **`DefaultJobSetup`** — assigns jobs, calls `World.ProduceAtTile` (standability and animals both ready)
- **`PostLoadInit`** — calls `AnimalController.Load()` → `SlowUpdate()`, `UpdateColonyStats()`; `WaterController.UpdateSurfaceMask()`; and `FlowerController.OnWorldReady()` (builds the decoration layer — restores the saved flower layout stashed by Phase 9, or scatters fresh on gen/reset). The flower build lives here *because* this is the one hook every world-creation path runs — putting it only in the boot coroutine left in-session reset/new-world worlds flowerless.

**Key rule**: use `PostLoadInit` for any initialization that must run on **every** world-creation path (initial gen, reset, load) — it's the single common hook. Reset-side clearing of such state goes in `ResetSystemState` (e.g. `FlowerController.ResetState`), the symmetric "tear down" hook. It also runs after animals are fully ready, so cross-animal aggregates belong here too.

> **Anti-pattern**: do NOT use the `if (world == null)` guard in `AnimalController.TickUpdate` to detect a fresh world — unreliable on Reset/Load since `world` is never reset to null.

`pendingSaveData`: `LoadAnimal()` sets this before `Animal.Start()` runs. `Start()` checks it and applies saved state if present; otherwise initializes fresh. Marked `[System.NonSerialized]` to prevent Unity from replacing null with a default instance.

---

## Save Slot UI

- `SaveMenuPanel` (`Assets/UI/SaveMenuPanel.cs`) — singleton. `Toggle()` opens and calls `RefreshSlotList()`, which scans `.json` files in `SaveDir` and instantiates a `SaveSlotEntry` per slot. "New Save" creates a uniquely-named slot and auto-focuses the rename field; "Reset" goes through `ConfirmationPopup` before calling `SaveSystem.Reset()`.
- `SaveSlotEntry` (`Assets/UI/SaveSlotEntry.cs`) — per-row component on `Assets/Resources/Prefabs/SaveSlot.prefab`. Handles inline rename (validates non-empty + no collision), Save/Load buttons, and a Delete button that routes through `ConfirmationPopup`.
- `SaveSystem` slot API: `GetSaveSlots`, `SlotExists`, `RenameSlot`, `DeleteSlot`, `GetAnimalCount` (deserialises just enough to read `animals.Length` for the mice-count label).
- `ConfirmationPopup` (`Assets/UI/ConfirmationPopup.cs`) — reusable singleton modal. **Keep inactive in scene** — `Show` finds it via `FindObjectOfType<ConfirmationPopup>(true)` so the inactive version is reachable. Sets itself last sibling on show; `Blocker` child absorbs background clicks. `Show(msg, onConfirm, confirmLabel?)`.

### Autosave

Interval is `SettingsManager.autosaveIntervalMinutes` (PlayerPref, default 5; **0 = off**), chosen from the `OptionsPanel` "autosave" dropdown (off / 1 / 5 / 10 / 30 min). `SaveSystem.Update` runs an **unscaled**-time timer (so the interval is wall-clock, not game-speed) and re-reads the interval live. The timer is **frozen while the game is paused (`Time.timeScale == 0`) or a load is in progress (`LoadingScreen.IsActive`)** — paused means nothing's changing, and saving mid-load could persist a half-built world. It freezes (doesn't reset), so a brief pause resumes where it left off. On fire, `WriteRotatingAutosave` keeps **at most 3** autosaves: it deletes the oldest while ≥3 autosave slots exist (`GetSaveSlots` is newest-first, so the oldest is last), then writes a fresh `auto <settlement> (N)` slot via `Save(slot, setCurrent: false)`. `N` is a per-town counter from `NextAutosaveNumber` (highest existing `(N)` for that town + 1, so numbers stay monotonic across rotation rather than reused). The reserved `"auto"` prefix stays **first** because both this rotation (`IsAutosaveSlot`) and the server's `pruneAutosaves`/`isAutosaveSlot` detect autosaves by it — matched as a **whole word** (`"auto"` or `"auto …"`, so a manual save like `automaton` isn't swept), plus a legacy clause for the old `autosave …` names so they still rotate out. The save date is **not** in the name (the load list shows it from the file mtime); the settlement name (`World.SettlementDisplayName`, pre-sanitized to exclude parens so the trailing `(N)` parses unambiguously) is embedded for readability. The `setCurrent:false` overload leaves the player's active `currentSlot` untouched so the background write never hijacks their named slot. Manual saves must avoid the `auto ` prefix (it's rotation-managed). `Save` is synchronous and stalls the frame on large worlds, so `AutosaveRoutine` activates the `SavingOverlay` (a plain centered UI element under the main canvas, referenced by `SaveSystem.savingOverlay`) and yields a frame to let it paint **before** the freeze, then hides it. Any save (manual or auto) resets the interval timer; it holds at 0 while there's no world loaded or autosave is off.

---

## Time System

The main game clock lives in `World.Tick(float dt)` (World.cs). `World.Update()` is a thin wrapper that calls `Tick(Time.deltaTime)`. Each call accumulates `timer += dt` and fires tick events at fixed intervals:

| Interval | What fires |
|----------|-----------|
| 1 second | `AnimalController.TickUpdate()`, `PlantController.TickUpdate()`, `ResearchSystem.TickUpdate()` |
| 10 seconds | `WorkOrderManager.Reconcile()` — safety net re-scan (see SPEC-ai.md) |
| 0.2 seconds | `InventoryController.TickUpdate()` (item display refresh), `InfoPanel.UpdateInfo()` |
| in-game hour (20 s) / day (480 s) | `StatsTracker.OnSampleTick()` samples pull-stats each hour; `OnDayElapsed()` finalizes each daily stat's day on the day boundary — see SPEC-stats.md |

All game logic is intended to be tick-driven. Movement and fall physics are the only things that run per-frame (in `Animal.Update()` → `AnimalStateManager.UpdateMovement(deltaTime)`), because smooth sub-tile animation requires it.

`AnimalController` follows the same pattern: `AnimalController.Update()` wraps `AnimalController.Tick(float dt)`, so per-animal staggered ticks are also testable from a fixed-step driver.

**Why Tick(float dt) is public**: tests and the snapshot harness can call `World.Tick(1/60f)` repeatedly to advance the simulation deterministically without depending on Unity's frame loop. Production keeps using `Time.deltaTime`, so timeScale and pause continue to work without special handling.

### Time scale

`TimeController.cs` wraps `Time.timeScale`. Setting it to `0` pauses all ticks and movement; `2` doubles everything.

**Loading pauses the sim.** `LoadingScreen.Begin` calls `TimeController.Pause()`, so the world is frozen for the whole boot load. `End` deliberately does *not* resume — the post-load speed is path-dependent: new worlds stay paused (`GenerateDefault`) for the settlement popup / press-space-to-start, while loaded worlds resume to normal speed in `WorldController.Start` (right after the synchronous `Load()`). Pressing **Esc while the loading screen is up** restores normal speed and returns to `Menu` (an escape hatch for a slow/stuck load; no confirm — nothing's lost mid-load). Because `World.Update()` and `AnimalController.Update()` pass `Time.deltaTime` into their respective `Tick(dt)` methods, scaling is automatic — no special handling needed in tick consumers. `TradingClient.ReconnectLoop` uses `WaitForSecondsRealtime` so network reconnection is unaffected by time scale.

### RNG and reproducibility

All gameplay-affecting randomness — recipe picking, animal AI, weather rolls, mouse names, scribe choice — flows through `Rng` (Assets/Model/Rng.cs), a static facade over a single seedable `System.Random`. The world's seed is generated in `WorldController.GenerateDefault` (new world) or restored by `SaveSystem.ApplySaveData` (load), and persisted in `WorldSaveData.worldSeed`. Reload reproduces the original stream.

Each `Animal` carries its own `random` (System.Random) seeded at creation from `Rng.NextInt()`; that seed is persisted as `AnimalSaveData.rngSeed` so animal-level decisions reproduce on save/load. Cosmetic-only randomness (UnityEngine.Random) does not need to go through Rng.

Old saves with `worldSeed = 0` and `rngSeed = 0` deserialize cleanly — they simply gain reproducibility from that point forward.

### Key time constants

| Constant | Value | Used by |
|----------|-------|---------|
| `ticksInDay` | 480 | Day/night length, research rate (1 tick = 1 s → 8 min day) |
| `daysInYear` | 24 | Calendar |

### Scaling day length (checklist)

`ticksInDay` is the only day-length knob (1 tick = 1 real second). Scaling it by
factor **k** stretches every *day-anchored* quantity automatically, but *per-tick*
rates and *tick-count durations* do NOT scale — they must be adjusted by hand or
per-day balance drifts. When changing `ticksInDay` by k:

1. **Day-anchored — leave alone** (formula already contains `ticksInDay`/`daysInYear`):
   sun/seasons, weather OU steps (72/day), fuel burn (liang/day), item decay (per-year),
   maintenance (`DaysToBreak`), furnishing lifetime, market
   transit, starvation timer, birth chance, `MoistureSystem.TicksPerInGameHour` (derived).
   Editing these *double-scales* — a bug.
2. **Per-tick rates — divide by k** (raw per-second, no `ticksInDay` in the formula):
   `Eating.hungerRate` **and** its `defaultHungerRate` copy in AnimalController;
   `Eeping.tireRate/eepRate/outsideEepRate`; `Happiness.decayPerTick`; `Happiness.warmthDecayFactor10`
   and `Flywheel.DecayFactor` (k-th root — they're per-SlowUpdate/per-tick decay factors);
   `ResearchSystem.DecayRate/ScientistRate`; `SkillSet.XpPerWorkTick`; OverlayGrowth grow/death
   chances; Snow `AccumChancePerSecond`; WeatherSystem `humiditySmoothingRate`/`windSmoothingRate`
   (per-real-second smoothing — divide to hold rain fraction + wind feel);
   `CloudLayer.cloudEvolutionRate` (visual).
3. **Tick-count durations — multiply by k**: plantsDb `growthTime`/`harvestTime`/`fruitRotTicks`;
   recipesDb `workload` **and processor-recipe `duration`** (both seconds/labour-seconds now —
   untended ferments included, since `duration` is raw seconds not days); chat/leisure objective
   durations; `Animal.MaxWorkStintTicks`.
4. **Wall-clock — never touch** (`unscaledTime`/`WaitForSecondsRealtime`): autosave, network
   reconnect/poll, chat-row fade, render throttles, fps caps, audio fades.
5. **Verify**: recompile clean; playtest the hunger↔food loop, one-sleep-per-night, rain
   frequency (~0.8/day), and research/leveling pace per day.

Two gotchas: (a) a per-tick rate may live at **two sites** — a per-instance field plus a
hardcoded colony-stat copy — so grep the value, don't trust one hit; (b) `public` fields on
scene MonoBehaviours (`CloudLayer.cloudEvolutionRate`) are **serialized in the scene**, so the
C# default is masked — set it on the live component and save the scene, not just the code default.

Fall physics constants (`fallSecondsPerTile`, `fallGravity`) live in SPEC-systems.md §Item Falling.

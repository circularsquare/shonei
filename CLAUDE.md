# Shonei — Claude Instructions

## Project

meow!

This is a big project — a Dream Project. Let's be ambitious and build scalable solutions that will still be clean to work with years down the line. I have a lot of time to spend on this, and you're very capable.

## Working style

I like to use plan mode! If you're about to make a code change that has real chances of going wrong and we're not in plan mode, assume I just forgot to set it and enter plan mode yourself.

Please feel free to explain your thought process.

If you think my query is based on incorrect assumptions or doesn't make sense to you, feel free to ask for clarification.

Feel free to spawn subagents to work on tasks or to double-check your own work — a fresh perspective catches mistakes your own context has normalized. This is especially valuable for (a) tasks with lots of moving or abstract parts, where there's more surface area for misunderstanding, and (b) any task that calls for an *estimate* (how hard something will be, how much compute alternatives might take, how long a refactor will run) — independent estimates reduce anchoring on your first guess.

If you notice anything that could be reorganized to improve clarity or efficiency, please mention it.

**Don't use MCP scene-mutating tools without explicit per-task permission.** Read-only MCP (find_gameobjects, get_hierarchy, read_console, scene/component resources) is fine for inspection. But actual mutations — `execute_code` that modifies state, `manage_gameobject create/modify`, `manage_components add/set_property`, etc. — only when I specifically say "use MCP" or similar. Default to no, and when in doubt describe the manual editor steps. MCP work has been consistently producing followup cleanup; until that pattern changes, the default is hands-off.

## Code practices

Avoid failing silently. Instead, LogError. Or if an unexpected case occurs, at least Log.

Make stuff private if you think it should be private.

## Code style

Prioritize documentation and code clarity, in a way that future Claudes would find easy to understand.

For braces: open brace on first line of function declaration.

Comments:

- **Class-level**: `//` line-block comments. No `/// <summary>` XML anywhere.
- **Section dividers**: `// ── Section name ──────────────────` (em-dash, ~70 chars total).
- **Method comments**: plain `//` above. Comment the "why", not the "what". Skip obvious ones.
- **Field comments**: trailing `//` for one-liners; above `//` block for multi-line.
- **TODOs**: `// TODO:` (uppercase T, colon).
- **Avoid value-specific comments.** Don't write comments whose truth depends on specific parameter values (`pow(0.99, 10) ~ 0.9044`) — they go stale silently when the parameter changes. General order-of-magnitude framing ("decays slowly", "roughly 5% per tick") is fine.

## Resources

**Before modifying any code**, read `Assets/spec/SPEC.md` to orient yourself, then read the relevant sub-document for the system you're touching. Do not skip this step even for small changes — most pattern violations come from not reading the spec first.

**Before any MCP / Unity Editor work** (scene mutations, UI building, `execute_code`, etc.), read `Assets/spec/SPEC-mcp.md`. It covers what's safe vs risky (live API mutations are fine; direct YAML writes aren't), UI style conventions (font sizes 12/14/16, black text, wood frame, sprite reuse map), and common gotchas (Play mode reverts, codedom C# 6 limits, inactive lookups).

You can also reference log.txt and todo.txt for my thoughts on what has happened recently and what we should work on in the future. But don't edit these.

Design plans for non-trivial in-progress features live in `C:\Users\anita\.claude\projects\c--Users-anita-projects-shonei\plans\` (alongside memory). Check there when picking up unfinished work or when the user references a plan by name. Save new plans there when scoping out a multi-session feature.

## Folder conventions

- `Assets/Model/` — pure C# game logic. Large standalone systems get their own file (Animal, World, Structure, etc.).
- `Assets/Model/Structure/` — the `Structure` base class plus all its subclasses (`Building`, `Plant`, `Windmill`, `Quarry`, `PumpBuilding`, `Flywheel`, `MouseWheel`, `MarketBuilding`, `PowerShaft`, …) and tightly-coupled support types (`Blueprint`, `StructType`, `StructureVisuals`). New Building/Structure subclasses go here, NOT in Components.
- `Assets/Components/` — single-purpose MonoBehaviours only: UI widgets (`FillBar`, `ItemIcon`, `StorageSlotDisplay`, …) and building-attached visuals (`ClockHand`, `RotatingPart`, `PortStubVisuals`, …). If your class is a `Structure`/`Building` subclass it belongs in `Model/Structure/` instead, even if it's small.

## C# / Unity IDE warnings
The VSCode C# extension (OmniSharp/Roslyn) sometimes reports errors like "missing using directive" or "type not found" for types that are defined in other Unity-compiled assemblies (e.g. UnityEngine types, or classes in other .cs files without explicit namespaces). These are **false positives** — Unity's own compiler resolves them correctly when it builds. Do not add spurious `using` statements or restructure code to silence these IDE-only warnings.

## Core patterns (always follow these)

### Data-driven content
New items, buildings, recipes, jobs, plants, research nodes = **JSON changes only** (`Assets/Resources/*.json`). `Db.cs` loads everything at startup. No hardcoded game content in C#.

### WorkOrderManager for all work dispatch
All task assignment goes through `WorkOrderManager` — animals never scan the world for work. Work is pushed into WOM as prioritised `WorkOrder` objects. See SPEC-ai.md for the full dispatch sequence.

### Reserve before execute
Tasks reserve resources (item stacks, building slots) in `Initialize()`, **before** any objectives run. Use `Task.ReserveStack()` / `FetchAndReserve()` — `Cleanup()` auto-unreserves. Return `false` from `Initialize()` if reservation fails.

### Fen everywhere in code, liang in JSON
All quantities in code are **fen** (`int`), **100 fen = 1 liang**. JSON is authored in liang (`float`). Conversion: `(int)Math.Round(q * 100)` at `ItemNameQuantity → ItemQuantity` sites. Display via `ItemStack.FormatQ()`.

### Callbacks for rendering, not polling
Tiles fire callbacks on change. Controllers subscribe for rendering updates. Don't poll model state from `Update()`.

### GlobalInventory for world totals
Use `Produce()` to add items (updates global inv). Use `MoveItemTo()` to transfer between inventories (no double-counting). `AddItem()` is private — never call it externally.

### Group items are never physical
Group/parent items (e.g. "wood") are wildcards for recipe inputs and building costs. Only leaf items exist in inventories. `LockGroupCostsAfterDelivery()` locks blueprints to a specific leaf on first delivery.

### Task/Objective queue pattern
Tasks decompose into an ordered queue of Objectives: `Initialize()` → build queue + reserve → `Start()` → objectives run sequentially → `Complete()`/`Fail()` → `Cleanup()`.

### Structure creation rules
Two ways to create structures:
- **Gameplay**: `Blueprint.Complete()` → `StructController.Construct()`. Consumes blueprint inventory.
- **Load/worldgen**: `Structure.Create(st, x, y)` + `StructController.Place(s)`. No cost side-effects.
Both paths use `Structure.Create()` (shared factory in `Structure.cs`) for subclass dispatch. When adding a new Structure subclass, add its case there. Always call `Place()` after direct construction — without it the structure isn't tracked.

### Save system: update the checklist
When adding new saveable state, update the checklist comment at the top of `SaveSystem.cs`. Gather in `Gather*`, restore in `Restore*`/`ApplySaveData()`. Use `PostLoadInit` coroutine for anything that depends on animals being fully ready (frame 2+).

### Exclusive panels
`TradingPanel`, `RecipePanel`, `ResearchPanel`, and `GlobalHappinessPanel` are mutually exclusive via `UI.RegisterExclusive()` / `UI.OpenExclusive()`. New exclusive panels must follow this pattern.

## Assembly structure

Source code is split across four asmdefs:

- `Assets/Shonei.Runtime.asmdef` — all gameplay code (Model, Controller, Components, UI, Lighting). Auto-referenced. Pulls in TextMeshPro, URP Universal+Core, NativeWebSocket.
- `Assets/Editor/Shonei.Editor.asmdef` — editor-only utilities (sheet splitters, sprite postprocessors). References `Shonei.Runtime`.
- `Assets/Tests/Editor/Shonei.EditMode.Tests.asmdef` — EditMode tests. References `Shonei.Runtime` + `Shonei.Editor`.
- `Assets/Tests/PlayMode/Shonei.PlayMode.Tests.asmdef` — PlayMode tests. References `Shonei.Runtime`.

Adding a new top-level Assets folder for source? It'll fall into `Shonei.Runtime` automatically (the asmdef sits at Assets root). Adding a new editor utility? Goes under `Assets/Editor/` and into `Shonei.Editor` automatically. New first-party engine module dependency (e.g. URP feature)? Add it to `Shonei.Runtime`'s `references` array — and check `read_console` for missing-type errors after recompile.

## Testing

**EditMode tests** (`Assets/Tests/Editor/`) — one file per system (e.g. `ItemStackTests.cs`). Fast (ms each). Use for pure-logic invariants — fen/liang math, recipe scoring, inventory bookkeeping. Cannot use Unity lifecycle (`Start` doesn't fire); singletons must be wired via reflection helpers.

**PlayMode tests** (`Assets/Tests/PlayMode/`) — load `Main.unity`, run actual game lifecycle. Slower (seconds each). Use for integration / snapshot tests where Animal AI, scene-loaded controllers, or the real save/load path matter. `TickSmokeTest.cs` is the canonical example: load Main → wait 3 frames → drive `World.Tick(1/60f)` × N → assert state.

**Snapshot tests** (`Assets/Tests/PlayMode/SnapshotTests.cs` + `SnapshotRunner.cs`) — capture full world state as JSON, diff against a checked-in golden file. Catches regressions in *any* system that affects serialized state (worldgen, animal AI, tick dispatch, save format) without writing per-system assertions. Goldens live in `Assets/Tests/PlayMode/Scenarios/<name>.golden.json`. On mismatch, the actual is written to `Application.temporaryCachePath` for diffing.

To add a new snapshot scenario:
1. Add a `[UnityTest]` method to `SnapshotTests.cs` that calls `SnapshotRunner.RunDefaultWorld(unitySeed: <fixed>, ticks: <N>, name: <unique>)`.
2. Run it once — golden is written and the test reports Inconclusive. Review the golden file, commit if good.
3. Subsequent runs diff against the golden. To accept new state after intentional behavior changes, delete the golden and re-run.

The runner pauses `Time.timeScale`, sets `WorldController.skipAutoLoad` so the user's most-recent save isn't picked up, and nulls singleton statics to keep state clean across consecutive runs in the same Unity session. If you add a new singleton that surfaces a "two instances of X" error during snapshot tests, add its type to `NullStaticInstances` in `SnapshotRunner.cs`.

**Workflows via Unity MCP** (`mcp__unity__*`):
- `read_console` — check warnings/errors after script edits. Run this after **non-trivial** code changes before claiming done. **Skip for tiny low-risk edits** (one-line filters, string-literal tweaks, magic-number changes, removing a line) — `refresh_unity` + `read_console` round-trips take real time and add little value when there's no plausible compile risk. Default to skipping unless the edit could reasonably break compile (new method, type signature change, reflection/framework API, etc.).
- `run_tests` returns a job_id; poll with `get_test_job` (use `wait_timeout: 60` and `include_failed_tests: true`). Specify `mode: "EditMode"` or `mode: "PlayMode"`.
- **Never auto-invoke `run_tests`.** It triggers a Unity recompile / domain reload and can interrupt in-flight editor work. Suggest it ("this touched save code — want me to run the EditMode tests?") and wait for explicit user confirmation. Tests can't run while Unity is already in Play Mode — wait, or ask the user to exit.

**Headless CLI**: `Tools/run-tests.bat [EditMode|PlayMode|all]` runs tests without opening the editor. Useful for ad-hoc runs and future CI. Requires Unity to be closed (it locks `Library/`). Output: `TestResults/<platform>.xml` (gitignored). Override Unity path with `UNITY_PATH` env var.

**Adding tests**:
- One test class per system, named `SystemNameTests.cs`. Use existing files as the style reference.
- **Keep them lean.** A bug fix gets ONE test that would have caught it. A new feature gets a small handful covering the contract — not an exhaustive matrix. Tests are read-mostly: pad them and you pay the cost forever.
- Cover the *invariant* or *contract*, not every getter or trivial branch. Heavy `[TestCase]` parameterization beats many copy-paste `[Test]` methods.
- For protected-set static singletons (`Db.itemByName`, `RecipePanel.instance`, etc.), use the `SetSingletonInstance` / `SetStaticProp` reflection helpers in existing files — copy the pattern, don't reinvent.
- EditMode tests for methods that touch `World.instance.timer` or require a live `Animal`/`Inventory`: skip with a clearly-marked `// Deferred` comment block, OR write them as PlayMode tests. Don't fight the dependency in unit-test setup.

**When a test fails**: diagnose the regression and fix the code. Don't change assertions to make them pass unless the test itself was wrong (rare; verify carefully).

## Anti-patterns (known past mistakes)

- **MCP scene/prefab writes**: Do NOT write `.unity`/`.prefab` files via MCP when user may have unsaved editor work — MCP reads stale on-disk state, not Unity's in-memory state. Describe manual steps instead.
- **`[Serializable]` on save data classes**: Don't add it — Newtonsoft.Json doesn't need it, and Unity's serializer will materialize default instances instead of null.
- **Craft order job check**: Do NOT use `structType.job` for craft eligibility — that's the *construction* job (e.g. "hauler" for a sawmill). Use `Array.Exists(a.job.recipes, r => r != null && r.tile == buildingName)`.
- **Stale WOM orders after world clear**: `WorkOrderManager.ClearAllOrders()` must be called at the start of `ClearWorld()`, before destroying any objects — otherwise `WorkOrder` references survive into the new session pointing at pre-load `ItemStack`/`Blueprint` objects.
- **Static collections that accumulate across scene reloads**: when adding a new `static List<>` / `static HashSet<>` / `static Dictionary<>` in a singleton (especially `Db.cs`), reset it in the singleton's constructor — not just declare it once. Otherwise scene reloads (PlayMode tests, future "new game" feature) double-populate, breaking determinism. See the reset block in `Db.cs:72-100` for the pattern.

## Session wrap-up checklist

When the user says anything like "let's wrap up", "running low on context", "let's finish this session", or similar — run through this checklist before ending:

1. **Update specs**: Review all changes made this session. Update the relevant `Assets/spec/SPEC-*.md` files so they reflect what was built or changed.
2. **Flag future work**: Call out anything worth revisiting — messy code, incomplete features, things that work but could be cleaner, potential refactors that would make the system easier to build on.
3. **Suggest reorgs**: If you noticed anything during the session that could be reorganized for clarity or extensibility, mention it even if it wasn't part of the task.
4. **Surface test coverage**: if changes touched non-trivial logic (model, AI, save, power, recipe scoring, etc.), point it out and *suggest* running `mcp__unity__run_tests` — but do not invoke it. Tests run only on explicit user request. If Unity needs to stay open, mention `Tools/run-tests.bat` as the headless alternative.

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

For Unity GameObjects, lean towards telling me what to do in the editor rather than creating them and setting properties in code.

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

## Resources

**Before modifying any code**, read `Assets/spec/SPEC.md` to orient yourself, then read the relevant sub-document for the system you're touching. Do not skip this step even for small changes — most pattern violations come from not reading the spec first.

You can also reference log.txt and todo.txt for my thoughts on what has happened recently and what we should work on in the future. But don't edit these.

Design plans for non-trivial in-progress features live in `C:\Users\anita\.claude\projects\c--Users-anita-projects-shonei\plans\` (alongside memory). Check there when picking up unfinished work or when the user references a plan by name. Save new plans there when scoping out a multi-session feature.

## Folder conventions

- `Assets/Model/` — pure C# game logic. Large standalone systems get their own file (Animal, World, Structure, etc.).
- `Assets/Components/` — small, tightly-scoped subclasses and single-purpose MonoBehaviours (e.g. PumpBuilding, ClockHand). If a class is fewer than ~30 lines and exists purely to override one method or add one behaviour, put it here rather than cluttering Model or Controller.

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

## Anti-patterns (known past mistakes)

- **MCP scene/prefab writes**: Do NOT write `.unity`/`.prefab` files via MCP when user may have unsaved editor work — MCP reads stale on-disk state, not Unity's in-memory state. Describe manual steps instead.
- **`[Serializable]` on save data classes**: Don't add it — Newtonsoft.Json doesn't need it, and Unity's serializer will materialize default instances instead of null.
- **Craft order job check**: Do NOT use `structType.job` for craft eligibility — that's the *construction* job (e.g. "hauler" for a sawmill). Use `Array.Exists(a.job.recipes, r => r != null && r.tile == buildingName)`.
- **Stale WOM orders after world clear**: `WorkOrderManager.ClearAllOrders()` must be called at the start of `ClearWorld()`, before destroying any objects — otherwise `WorkOrder` references survive into the new session pointing at pre-load `ItemStack`/`Blueprint` objects.

## Session wrap-up checklist

When the user says anything like "let's wrap up", "running low on context", "let's finish this session", or similar — run through this checklist before ending:

1. **Update specs**: Review all changes made this session. Update the relevant `Assets/spec/SPEC-*.md` files so they reflect what was built or changed.
2. **Flag future work**: Call out anything worth revisiting — messy code, incomplete features, things that work but could be cleaner, potential refactors that would make the system easier to build on.
3. **Suggest reorgs**: If you noticed anything during the session that could be reorganized for clarity or extensibility, mention it even if it wasn't part of the task.

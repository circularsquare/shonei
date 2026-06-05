# Shonei — Claude Instructions

## Project

meow!

This is a big project — a Dream Project. Let's be ambitious and build scalable solutions that will still be clean to work with years down the line. I have a lot of time to spend on this, and you're very capable.

## Working style

Please feel free to explain your thought process.

If you think my query is based on incorrect assumptions or doesn't make sense to you, feel free to ask for clarification.

**Bias toward acting on a clear best option.** Reserve `AskUserQuestion` for genuine forks where you can't tell which way I'd lean. If you already think one option is clearly better and expect I'd agree, just take it and tell me what you did — don't stop to ask. When it really is unclear, of course ask.

Spawn a subagent when (a) you're about to touch a subsystem not already in your context — have it read the relevant SPEC + named files and report current-state-vs-spec, (b) you need to verify a specific claim (file:line, API signature, memory entry) before acting on it — brief it with "quote the exact line", since past audit subagents have hallucinated, or (c) you're estimating effort or comparing approaches — your first guess anchors you.

Brief well: pose a question, name the files to read, ask for a short structured report. Vague briefs produce vague work.

If you notice anything that could be reorganized to improve clarity or efficiency, please mention it.

**Self-verify before asking.** If a value, state, or behaviour can be checked with the tools you have (read the file, grep, MCP read-only resources like `find_gameobjects` / `get_hierarchy` / scene+component resources, `read_console`, `execute_code` for runtime inspection, a quick test, or WebSearch for anything likely covered by docs/forums), do that instead of asking me to context-switch into the editor or look something up. I'm usually juggling other agents, so "can you check X?" is much more expensive than it looks. Only ask when verification genuinely isn't possible from your side (e.g. subjective visual judgement, info that only exists in my head).

**Verify mechanism, not symptoms.** Performance metrics (FPS, SetPass, srpHealth, GC) are noisy and shift with camera pan / scene state — treat as directional, not as proof a change worked. Before scaling a fix or layering more changes on top, confirm the underlying mechanism actually engaged via a direct invariant check (`execute_code` to query state, `atlas.spriteCount > 0`, a unit test, etc.). Aggregate metric moving the right direction isn't the same as the thing you just did working.

**MCP scene work: live API is OK for simple stuff, ask for extensive.** Read-only MCP (find_gameobjects, get_hierarchy, read_console, scene/component resources) is always fine for inspection. Simple live-API mutations — assigning a shader/sprite/material to a SerializeField on an existing component, toggling a bool, single-property tweaks — just do them via MCP instead of writing out manual "select X, set property Y to Z" steps. For **extensive** scene work (building UI hierarchies, configuring anchors/layouts, multi-component setup, anything that touched the trouble areas in past sessions) **ask first** — past sessions consistently left cleanup tasks from broken layouts and anchor fights. Direct `.unity` / `.prefab` YAML writes are still risky and can clobber unsaved editor state; require explicit save confirmation first and prefer manual editor steps for those.

## Writing to SPEC, CLAUDE.md, or memory

These load every session — bloat dilutes the rules that matter. Before adding a line, check: is it **non-obvious**, **load-bearing** (removing it would cause a wrong call), and **stable** (true in 3 months)? If not all three, don't write it.

When editing an existing section, prune redundant/stale lines in the same edit. Prefer one tight sentence over a paragraph; lead with the rule, not the narrative.

## Code practices

Avoid failing silently. Instead, LogError. Or if an unexpected case occurs, at least Log.

Make stuff private if you think it should be private.

## Code style

Prioritize documentation and code clarity, in a way that future Claudes would find easy to understand.

**Survey before adding.** When introducing a new entry into a class with similar existing entries (UI row Refresh methods, factory dispatch cases, panel rows, save data fields, etc.), survey the existing shape *before* writing your version. If the existing entries already vary in signature, that's a smell — propose unifying them, don't add a third variant. Additive specialization compounds: every new specialized entry makes the next one feel justified.

**Player-facing text must be extremely concise.** Tooltips, labels, alerts, panel headers, button labels — only essential information, no extra context. Players don't want to read. Prefer data first ("9/9 mice have a home"), drop articles and connective words. Sentence fragments / single-line info pockets (tooltips, labels) take no trailing period; full multi-sentence messages do. The exception is tutorial / help pages — those don't exist yet but when they do, longer is fine *only there*.

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

**Before any MCP / Unity Editor work** (scene mutations, UI building, `execute_code`, etc.), read `Assets/spec/SPEC-mcp.md`. It covers what's safe vs risky (live API mutations are fine; direct YAML writes aren't), UI style conventions (font size 16pt, black text, wood frame, sprite reuse map), and common gotchas (Play mode reverts, codedom C# 6 limits, inactive lookups).

You can also reference log.txt and todo.txt for my thoughts on what has happened recently and what we should work on in the future. But don't edit these.

Design plans for non-trivial in-progress features live in `C:\Users\anita\.claude\projects\c--Users-anita-projects-shonei\plans\` (alongside memory). Check there when picking up unfinished work or when the user references a plan by name. Save new plans there when scoping out a multi-session feature.

## Folder conventions

- `Assets/Model/` — pure C# game logic. Large standalone systems get their own file (Animal, World, Structure, etc.).
- `Assets/Model/Structure/` — the `Structure` base class plus all its subclasses (`Building`, `Plant`, `Windmill`, `Quarry`, `PumpBuilding`, `Flywheel`, `MouseWheel`, `MarketBuilding`, `PowerShaft`, …) and tightly-coupled support types (`Blueprint`, `StructType`, `StructureVisuals`). New Building/Structure subclasses go here, NOT in Components.
- `Assets/Components/` — single-purpose MonoBehaviours only: UI widgets (`FillBar`, `ItemIcon`, `StorageSlotDisplay`, …) and building-attached visuals (`ClockHand`, `RotatingPart`, `PortStubVisuals`, …). If your class is a `Structure`/`Building` subclass it belongs in `Model/Structure/` instead, even if it's small.

## C# / Unity IDE warnings
The VSCode C# extension (OmniSharp/Roslyn) sometimes reports errors like "missing using directive" or "type not found" for types that are defined in other Unity-compiled assemblies (e.g. UnityEngine types, or classes in other .cs files without explicit namespaces). These are **false positives** — Unity's own compiler resolves them correctly when it builds. Do not add spurious `using` statements or restructure code to silence these IDE-only warnings.

## Unity / URP / shader claims

Model training on Unity specifics is unreliable. When you're about to **act** on a Unity API, URP internal, shader/MPB lifecycle, or package behavior claim — verify via `mcp__unity__unity_docs` or WebSearch and cite inline, OR say "unverified" and ask. A wrong shader/URP change is expensive; citation is cheap.

This applies to acting, not chatting. Don't gate every sentence on a doc lookup — gate the ones that drive an edit.

**Version-sensitive APIs:** when a feature's behavior depends on Unity version (atlas v1/v2, addressables, render pipeline versions, async APIs), check `Application.unityVersion` *first* — one line via `execute_code`. "Newer/recommended" advice from training data is often wrong for this project's version.

**Silent failures:** Unity asset pipelines commonly fail with no error — atlas-not-packed, addressable-not-built, sprite-not-bound, importer-skipped. "No red console message" is not evidence of success. Confirm with a direct query after the action (e.g. `atlas.spriteCount`, `atlas.GetSprite(name) != null`, asset path resolves to expected type).

## Core patterns

Two invariants apply to almost any change — get them wrong and it's a silent data bug:

- **Data-driven content**: new items / buildings / recipes / jobs / plants / research nodes = **JSON only** (`Assets/Resources/*.json`), loaded by `Db.cs` at startup. No hardcoded game content in C#.
- **Fen in code, liang in JSON**: code quantities are **fen** (`int`, 100 fen = 1 liang); JSON is authored in liang (`float`). Convert with `(int)Math.Round(q * 100)` at the `ItemNameQuantity → ItemQuantity` boundary; display via `ItemStack.FormatQ()`.

The subsystem-specific patterns (work dispatch via `WorkOrderManager`, reserve-before-execute, callbacks-not-polling, GlobalInventory, group-vs-leaf items, the Task/Objective queue, structure creation, the save checklist, exclusive panels) live in the relevant SPEC sub-doc — read it before touching that system, per **Resources** above.

## Testing

Many impactful, easy-to-miss bugs aren't catchable by isolated tests — they surface in playtesting (AI feel, emergent interactions, visual/timing issues). Tests still help in plenty of cases (pure-logic invariants, save/load, regressions), but don't treat "run the tests" as a mandatory per-change step. Suggest them when a change plausibly broke something testable; otherwise lean on playtesting.

**Never auto-invoke `run_tests`** (`mcp__unity__*`) — it triggers a recompile / domain reload that interrupts in-flight editor work, and can't run while Unity is in Play Mode. Suggest it and wait for explicit confirmation.

Also: run `read_console` after **non-trivial** code edits to catch compile errors before claiming done — but skip it for tiny low-risk edits (one-line filters, string tweaks, magic-number changes), where the `refresh_unity` round-trip isn't worth it.

See `Assets/spec/SPEC-testing.md` for test types, the snapshot workflow, headless `run-tests.bat`, and conventions for adding tests.

## Anti-patterns (known past mistakes)

- **MCP scene/prefab writes**: Don't write `.unity`/`.prefab` via MCP when there may be unsaved editor work — MCP reads stale on-disk state, not Unity's in-memory state. Describe manual steps instead.
- **`[Serializable]` on save data classes**: Don't add it — Newtonsoft doesn't need it, and Unity's serializer materializes default instances instead of null.
- **Craft order job check**: Don't use `structType.job` for craft eligibility — that's the *construction* job (e.g. "hauler" for a sawmill). Use `Array.Exists(a.job.recipes, r => r != null && r.tile == buildingName)`.
- **Stale WOM orders after world clear**: Call `WorkOrderManager.ClearAllOrders()` at the *start* of `ClearWorld()`, before destroying objects — else `WorkOrder` refs survive into the new session pointing at pre-load `ItemStack`/`Blueprint` objects.
- **Static collections across scene reloads**: a new `static List`/`HashSet`/`Dictionary` in a singleton (esp. `Db.cs`) must be reset in the constructor, not just declared — otherwise scene reloads double-populate and break determinism. See the reset block in `Db.cs`'s constructor.
- **Custom-UV sprite shaders break NormalsCapture's alpha mask** (ghost silhouettes at sunset). Fix + full diagnosis: SPEC-rendering.md §NormalsCapturePass (pattern in `CloudLayer.cs` / `BackgroundLayer.cs`).

## Session wrap-up checklist

When the user says anything like "let's wrap up", "running low on context", "let's finish this session", or similar — run through this checklist before ending:

1. **Update specs**: Review all changes made this session. Update the relevant `Assets/spec/SPEC-*.md` files so they reflect what was built or changed.
2. **Flag future work**: Call out anything worth revisiting — messy code, incomplete features, things that work but could be cleaner, potential refactors that would make the system easier to build on.
3. **Suggest reorgs**: If you noticed anything during the session that could be reorganized for clarity or extensibility, mention it even if it wasn't part of the task.
4. **Surface test coverage**: if changes touched non-trivial logic (model, AI, save, power, recipe scoring, etc.), point it out and *suggest* running `mcp__unity__run_tests` — but do not invoke it. Tests run only on explicit user request. If Unity needs to stay open, mention `Tools/run-tests.bat` as the headless alternative.

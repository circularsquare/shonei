# Shonei — Claude Instructions

## Project spec

meow!

This is a big project. A Dream Project. Lets be ambitious and try to make scaleable solutions that will be clean to work with years down the line, as I have a lot of time to spend on this and you are very capable. 

Please feel free to explain your thought process in your responses.

Avoid failing silently. Instead, LogError. Or if an unexpected case occurs, at least Log.

Prioritize documentation and code clarity, in a way that future claude's would find easy to understand.

Make stuff private if you think it should be private. 

If you notice anything that could be reorganized to improve clarity or efficiency, please mention a suggestion to me.

If you think my query is based on incorrect assumptions or doesn't make sense to you, feel free to ask for clarification.

You can reference `Assets/spec/SPEC.md` — it's an index with architecture overview and links to sub-documents. Read it first to orient yourself, then pull the relevant sub-file for deeper detail:

- `Assets/spec/SPEC-data.md` — JSON database schemas (buildings, items, recipes, jobs, plants, tiles, research)
- `Assets/spec/SPEC-lifecycle.md` — save/load/reset, startup ordering, time system
- `Assets/spec/SPEC-ai.md` — animal AI, task system, WorkOrderManager
- `Assets/spec/SPEC-systems.md` — navigation, inventory, item falling, equip slots, fen/liang units
- `Assets/spec/SPEC-rendering.md` — depth layers, lighting pipeline, normal maps
- `Assets/spec/SPEC-trading.md` — WebSocket protocol, TradingClient, TradingPanel, market logistics
- `Assets/spec/SPEC-research.md` — research points, nodes, key classes

You can also reference log.txt and todo.txt for my thoughts on what has happened recently and what we should work on in the future. But don't edit these. 

For unity gameobjects, please lean towards telling me what to do in editor rather than creating them and setting properties in code.

For style, have open brace on first line of function declaration.

## Folder conventions

- `Assets/Model/` — pure C# game logic. Large standalone systems get their own file (Animal, World, Structure, etc.).
- `Assets/Components/` — small, tightly-scoped subclasses and single-purpose MonoBehaviours (e.g. PumpBuilding, ClockHand). If a class is fewer than ~30 lines and exists purely to override one method or add one behaviour, put it here rather than cluttering Model or Controller.

## C# / Unity IDE warnings
The VSCode C# extension (OmniSharp/Roslyn) sometimes reports errors like "missing using directive" or "type not found" for types that are defined in other Unity-compiled assemblies (e.g. UnityEngine types, or classes in other .cs files without explicit namespaces). These are **false positives** — Unity's own compiler resolves them correctly when it builds. Do not add spurious `using` statements or restructure code to silence these IDE-only warnings.


# Shonei — Research System

Scientists working in **laboratory** buildings generate research progress. Progress decays over time, so ongoing work is needed to maintain unlocks.

## Terminology

The individual unlockable things are called **technologies** (player- and user-facing noun). The *action* of working towards them is **research** (verb). The system, panel, task, and code identifiers are all still named "Research" — only the per-node noun changed. User and Claude may use "research" and "technology" interchangeably when referring to a single node; prefer "technology" in new player-facing copy.

## Progress & decay

Each technology has a progress value (0 to 2x cost). Every tick, all nodes with progress > 0 lose `DecayRate` (0.01) progress. Scientists add `workEfficiency * ScientistRate (0.05)` per tick.

Passive progress sources (maintain-only — caps at 2x cost and cannot unlock a locked tech from scratch):
- **Crafting**: recipes with a `research` field grant `researchPoints` to the named tech on each completed cycle. Hooked in `AnimalStateManager.ExecuteCraftTask`. Intended use: recipe-unlock techs gain from the recipe they unlock.
- **Construction**: each time a tech-gated building finishes construction via `Blueprint.Complete`, the gating tech gets a flat `ConstructionGain` (1) progress. Routed through the `buildingToTechNode` reverse index built in `LoadNodes`. Only fires on the gameplay path — world load / worldgen go through `Structure.Create` directly and do not grant research.
- **Maintenance**: completing a `MaintenanceTask` grants `ConstructionGain × repairAmount` to the same tech. Calibrated so a full 0→1 repair matches a fresh build (e.g. a 40 % repair = 0.4 progress), implemented via the `scale` parameter on `AddConstructionProgress`.

- **Unlock**: progress >= cost AND all prerequisites currently unlocked.
- **Forget**: progress < 0.75 x cost (hysteresis prevents flickering). Fires `OnTechForgotten` event + `Debug.Log`. Surfaced to the player via `EventFeed` (see SPEC-eventfeed).
- **Reinforcement**: progress can exceed cost up to 2x cost, providing a buffer against decay.

## Study system

Each technology has a per-player **study** toggle (`ResearchSystem.studiedIds`). When enabled, scientists will research and maintain that technology.

**Scientist priority when picking up a ResearchTask** (via `PickStudyTarget()`):
1. Any studied tech below cost with prereqs met → pick the one with the **oldest unlock timestamp** (LIFO forgetting — early techs are presumed more load-bearing). Techs that have never been unlocked are treated as newest (lowest priority).
2. If no studied tech is below cost: pick the studied tech with the **lowest progress %** relative to 2×cost (spreads reinforcement evenly).
3. If nothing to do: return -1 (scientist idles through the work loop).

Unlock timestamps are recorded in `unlockTimestamps` (monotonic counter, incremented on each unlock). If a tech is forgotten and re-unlocked, its timestamp is updated — it becomes lower priority than continuously-held techs.

There is no "active research" concept — study is the only control. Multiple scientists can work on the same tech simultaneously (no exclusive claims).

## Technologies (`researchDb.json`)

Each technology can grant multiple unlocks of mixed types. See SPEC-data.md for the full field schema.

```json
{ "id": 1, "name": "Excavation", "prereqs": [], "cost": 5,
  "unlocks": [ { "type": "building", "target": "dirt pit" } ] }
```

Unlock entry `type` is one of `"building"`, `"recipe"`, or `"job"`. For recipes, `target` is the recipe id as a string.

## Recipe gating

Gated recipes are filtered out at every pick site (`Animal.PickRecipe*`, `AnimalStateManager` mid-craft check that fails the task on forget, `RecipePanel.Rebuild`). Use `ResearchSystem.IsRecipeUnlocked(recipeId)` at any new pick site. Reverse index `recipeToTechNode` is built in `LoadNodes`.

## Job gating

Jobs flagged `defaultLocked: true` in `jobsDb.json` are hidden until a tech with `{"type":"job","target":"<name>"}` is unlocked. `IsJobUnlocked(name)` returns true when ungated or gating tech is currently unlocked. Reverse index `jobToTechNode` built in `LoadNodes`.

On unlock: `AnimalController.UnlockJob(name)` adds the jobs-panel row (idempotent). On forget: `LockJob(name)` reassigns any worker of that job back to `"none"` and removes the row.

## Key classes

| Class | Role |
|---|---|
| `ResearchSystem` | Singleton model. Holds progress, studiedIds, unlockTimestamps, unlockedIds. Ticked from `World.Update`. |
| `ResearchSystem.PickStudyTarget()` | Returns nodeId for scientist to work on, or -1. Called from WOM factory. |
| `ResearchSystem.AddScientistProgress(workEff, targetId)` | Adds progress to targetId. |
| `ResearchSystem.OnTechForgotten` | Static event fired when a tech is forgotten. |
| `ResearchPanel` | Full-screen UI. Icon grid. Cards show icon + name + progress bar. Card click toggles study. |
| `ResearchDisplay` | Prefab component. Progress bars (green + blue as one continuous bar), threshold marker. |

## Card visual states

Card background tint (`ResearchDisplay.RefreshProgress`) — 2×2 grid of (unlocked × studied):
- **Green** — unlocked AND studied (maintained).
- **Yellow** — unlocked AND not studied (warning — will decay).
- **Teal** — not unlocked AND studied (working towards unlock).
- **Transparent** — not unlocked AND not studied (no engagement).

Cost text colour:
- **Green** — a scientist is currently running a `ResearchTask` targeting this tech (see `ResearchSystem.IsActivelyResearched`). Not just "studied" — only while real progress is being added.
- **Black** — prereqs met, not being actively worked.
- **Grey** — prereqs not met.
| `ResearchTask` | Task with `studyTargetId`. Navigates to lab, works in 10-tick loops. |

## Save data

`ResearchSaveData`: `Dictionary<int,float> progress`, `int[] unlockedIds`, `int[] studiedIds`, `Dictionary<int,int> unlockTimestamps`, `int unlockCounter`.

Legacy migration: old saves with `maintainIds` / `activeResearchId` are merged into `studiedIds`. Missing `unlockTimestamps` are derived from `unlockedIds` array order.

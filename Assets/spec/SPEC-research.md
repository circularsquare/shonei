# Shonei — Research System

Scientists working in **laboratory** buildings generate research progress. Progress decays over time, so ongoing work is needed to maintain unlocks.

## Terminology

The individual unlockable things are called **technologies** (player- and user-facing noun). The *action* of working towards them is **research** (verb). The system, panel, task, and code identifiers are all still named "Research" — only the per-node noun changed. User and Claude may use "research" and "technology" interchangeably when referring to a single node; prefer "technology" in new player-facing copy.

## Progress & decay

Each technology has a progress value (0 to 2x cost). Every tick, all nodes with progress > 0 lose `DecayRate` (0.02) progress. Scientists add `workEfficiency * ScientistRate (0.1)` per tick. Passive progress comes from certain crafting recipes (`recipe.research` field).

- **Unlock**: progress >= cost AND all prerequisites currently unlocked.
- **Forget**: progress < 0.8 x cost (hysteresis prevents flickering).
- **Reinforcement**: progress can exceed cost up to 2x cost (shown in blue bar), providing a buffer against decay.

## Maintain system

Each technology has a per-player **maintain** toggle (`ResearchSystem.maintainIds`). When enabled, scientists prioritise keeping that technology above the unlock threshold before working on new research.

**Scientist priority when picking up a ResearchTask:**
1. If the **active research** is maintained AND below cost: work on it (no exclusive claim — multiple scientists can help).
2. Otherwise, find any non-active maintained node below cost that isn't already claimed by another scientist. Claim it exclusively.
3. If nothing needs maintenance: work on the active research as normal.

Claims are stored in `maintenanceClaims` (nodeId -> Animal). Released on task Cleanup (complete, fail, or animal death). The maintenance target is decided at task creation time in the WOM factory and stored as `ResearchTask.maintenanceTargetId`.

Setting a research as active auto-enables maintain for it.

## Technologies (`researchDb.json`)

Each technology can grant multiple unlocks of mixed types. See SPEC-data.md for the full field schema.

```json
{ "id": 1, "name": "Excavation", "prereqs": [], "cost": 5,
  "unlocks": [ { "type": "building", "target": "dirt pit" } ] }
```

Unlock entry `type` is one of `"building"`, `"recipe"`, `"job"`, or `"misc"`. For recipes, `target` is the recipe id as a string. `"misc"` currently has no effect handler — add a case to `ResearchSystem.ApplyEffect`/`RevertEffect` when introducing one.

## Recipe gating

Recipes referenced by a tech's unlocks are filtered out of:
- `Animal.PickRecipeRandom` / `PickRecipe` / `PickRecipeForBuilding`
- `AnimalStateManager` mid-craft check (fails the task if the tech gets forgotten mid-craft)
- `RecipePanel.Rebuild` (hidden from the UI until unlocked)

Ungated recipes are always craftable. The reverse index `recipeToTechNode` is built once in `LoadNodes`; use `ResearchSystem.IsRecipeUnlocked(recipeId)` anywhere a new recipe-pick site is added. `RecipePanel` rebuilds on `OnEnable`, so the exclusive-panel swap (research → recipe) naturally refreshes the list — no explicit callback needed.

## Job gating

Jobs flagged `defaultLocked: true` in `jobsDb.json` are hidden from the jobs panel until a tech with a matching `{"type":"job","target":"<name>"}` entry is unlocked. The reverse index `jobToTechNode` is built once in `LoadNodes`; `IsJobUnlocked(name)` returns true when the job is ungated or its gating tech is currently unlocked.

On **unlock** (`ApplyEffect`): `AnimalController.UnlockJob(name)` adds a row to the jobs panel. Idempotent — safe to call before the panel is built (lazy `AddJobCounts` queries research state on first run).

On **forget** (`RevertEffect`): `AnimalController.LockJob(name)` reassigns every animal currently working that job back to `"none"` and destroys the row. Players don't need to manually unassign — losing the tech automatically frees the workers.

## Key classes

| Class | Role |
|---|---|
| `ResearchSystem` | Singleton model. Holds progress, maintainIds, claims, unlockedIds. Ticked from `World.Update`. |
| `ResearchSystem.ClaimMaintenanceTarget(animal)` | Returns nodeId to maintain, or -1. Called from WOM factory. |
| `ResearchSystem.AddScientistProgress(workEff, targetId)` | Adds progress to targetId (or activeResearchId if -1). |
| `ResearchPanel` | Full-screen UI. Icon grid. Cards show sprite + cost + maintain toggle. Closes on world click. |
| `ResearchDisplay` | Prefab component. Progress bars, set-active button, maintain toggle button. |
| `ResearchTask` | Task with `maintenanceTargetId`. Navigates to lab, works in 10-tick loops. Releases claim on Cleanup. |

## Save data

`ResearchSaveData`: `Dictionary<int,float> progress`, `int activeResearchId`, `int[] unlockedIds`, `int[] maintainIds`.

# Shonei — Research System

Scientists working in **laboratory** buildings generate research progress. Progress decays over time, so ongoing work is needed to maintain unlocks.

## Progress & decay

Each research node has a progress value (0 to 2x cost). Every tick, all nodes with progress > 0 lose `DecayRate` (0.02) progress. Scientists add `workEfficiency * ScientistRate (0.1) * researchMultiplier` per tick. Passive progress comes from certain crafting recipes (`recipe.research` field).

- **Unlock**: progress >= cost AND all prerequisites currently unlocked.
- **Forget**: progress < 0.8 x cost (hysteresis prevents flickering).
- **Reinforcement**: progress can exceed cost up to 2x cost (shown in blue bar), providing a buffer against decay.

## Maintain system

Each node has a per-player **maintain** toggle (`ResearchSystem.maintainIds`). When enabled, scientists prioritise keeping that node above the unlock threshold before working on new research.

**Scientist priority when picking up a ResearchTask:**
1. If the **active research** is maintained AND below cost: work on it (no exclusive claim — multiple scientists can help).
2. Otherwise, find any non-active maintained node below cost that isn't already claimed by another scientist. Claim it exclusively.
3. If nothing needs maintenance: work on the active research as normal.

Claims are stored in `maintenanceClaims` (nodeId -> Animal). Released on task Cleanup (complete, fail, or animal death). The maintenance target is decided at task creation time in the WOM factory and stored as `ResearchTask.maintenanceTargetId`.

Setting a research as active auto-enables maintain for it.

## Research nodes (`researchDb.json`)

```json
{ "id": 1, "name": "Excavation", "type": "building", "unlocks": "dirt pit", "prereqs": [], "cost": 5 }
```

Types: `"building"`, `"recipe"`, `"misc"`. Prerequisites are node `id` integers.

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

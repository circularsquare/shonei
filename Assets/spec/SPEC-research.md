# Shonei — Research System

Scientists working in **laboratory** buildings generate research points over time. Points can be spent to unlock new buildings, recipes, or misc upgrades.

## Points mechanic

Every `ticksInDay/12` seconds of game time, the system samples how many scientist mice are actively working in a lab (in the `Working` state). That sample (`scientists × 10`) is stored in a 15-entry circular buffer. The player's **available research points** = `max(buffer) − totalSpent`. This gives a stable, peak-based value that doesn't swing when a mouse briefly stops to eat.

## Research nodes (`researchDb.json`)

```json
{ "id": 1, "name": "Excavation", "type": "building", "unlocks": "soil pit", "prereqs": [], "cost": 5 }
{ "id": 2, "name": "Quarry", "type": "building", "unlocks": "quarry", "prereqs": [1], "cost": 5 }
{ "id": 3, "name": "Improved Research", "type": "misc", "unlocks": "research_efficiency", "prereqs": [], "cost": 5 }
```

Types: `"building"`, `"recipe"`, `"misc"`. Prerequisites are node `id` integers. Unlocking permanently adds `cost` to `totalSpent`.

## Key classes

| Class | Role |
|---|---|
| `ResearchSystem` | Singleton model. Holds buffer, `totalSpent`, `unlockedIds`, `researchEfficiencyMultiplier`. Ticked from `World.Update`. |
| `ResearchSystem.ApplyEffect(node)` | Single dispatch point for all research effects. Called on unlock and on load via `ReapplyAllEffects()`. |
| `ResearchPanel` | Full-screen UI. Icon grid (`GridLayoutGroup`). Cards show sprite + cost. Hover → tooltip. Closes on world click. |
| `ResearchDisplay` | Component on the ResearchDisplay prefab. Receives `Setup(node, rs, onUnlock)` to populate icon, cost, button. |
| `TooltipSystem` | Singleton on Canvas. `Show(title, body)` / `Hide()`. Follows mouse. |
| `Tooltippable` | Component on any UI element; fires tooltip on pointer enter/exit. |
| `ResearchTask` | Task for scientist job. Navigates to lab, reserves it, works in 10-tick loops. |

## Save data

`ResearchSaveData`: `float[] pointHistory`, `int historyIndex/tickCounter`, `float totalSpent`, `int[] unlockedIds`.

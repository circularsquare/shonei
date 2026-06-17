# SPEC — Onboarding (PlayerTask system)

A linear, one-at-a-time list of starting goals shown to the player on the **Tasks
card** (bottom-right wood-frame HUD). Guides a newcomer through the core loop
(flag harvests → storage → housing → sawmill/jobs → lab → research). Distinct from
the mouse-AI `Task`/`Objective` system — a PlayerTask is player-facing guidance with
**no effect on the simulation**.

## Files

- `Assets/Controller/PlayerTaskController.cs` — `PlayerTask` (id + title + `Progress()`
  probe), `TaskProgress` struct, and the controller: the ordered task list (`BuildTasks`),
  `currentIndex`, `Current`, `Advance()`, and the detection probes.
- `Assets/UI/PlayerTaskCard.cs` — the view. A scene object under the `UI` canvas.
  `Frame` (woodframe Image) is a **bottom-anchored** `VerticalLayoutGroup` + `ContentSizeFitter`
  holding a collapsible `Header` (sibling 0: dropdown arrow + "task" label, `CollapsibleHeader`)
  and a `Content` TMP (sibling 1: title + progress). Self-wires children by name in `Awake`
  (static `instance` + `header`). Clicking the header toggles `Content` off, so the Frame shrinks
  to just the header — which stays pinned at the bottom corner because the card grows *upward*
  from a bottom pivot. Clicking `Content` (not the header) still bubbles to `OnPointerClick` to
  skip the "complete!" celebration. The "task" word lives in the header, so `SetText` writes the
  body only (no prefix). Collapse state persists via `CollapsibleHeader.saveKey = "tasks"`
  (SaveSystem gathers/restores it alongside the inventory/jobs headers — see SPEC-lifecycle.md).

## Load-bearing decisions (don't break these)

- **Advance runs on UNSCALED time, in the card** — not on the scaled world tick. A fresh
  world pauses after worldgen (`TimeController.Pause`), so a scaled-tick advance freezes;
  the card polls `Current` on `Time.unscaledTime` (~0.2s) so onboarding progresses while
  paused. Do NOT move completion/advance back into `World.Tick`.
- **Poll only the current task.** The card evaluates one probe per tick (cheap); there are
  no change-events for these states, so polling is correct. Predicates are C# lambdas in
  `BuildTasks()` (not data — the conditions are arbitrary logic).
- **Controller is code-bootstrapped** via `PlayerTaskController.EnsureExists()` in
  `World.Awake` — it is NOT placed in the scene, so the card (which IS a scene object)
  reads it through the static `instance`, never a serialized ref.
- **Completion flow**: current task complete → card shows `<title>` + `complete!` (green)
  for 3s → `Advance()` steps `currentIndex`. The **final** task instead shows
  `tutorial tasks all complete!` for 30s (`FinaleCelebrateSeconds`, via
  `PlayerTaskController.OnLastTask`). Either celebration is dismissible early by clicking
  the card. Progress display is capped at target (`Mathf.Min`), so over-completing shows
  `3/3`, not `5/3`.
- **`GetByType` returns null (not empty)** when no instances of a structure exist yet —
  probes must null-guard or they NRE every tick and freeze the card.

## Save (see SPEC-lifecycle.md)

Keyed by the current task's stable **`id`**, not its list position — so inserting or reordering
tasks never desyncs a mid-onboarding save. `WorldSaveData.playerTaskId` (`string`), via
`PlayerTaskController.SaveId()` / `RestoreFromId()`. Gathered/restored/reset in `SaveSystem`:
- `SaveId()` → current task's `id`, or `CompletedSaveId` (`"(complete)"`) once all tasks are done.
- `RestoreFromId(id)` → resolves `id` → index; `CompletedSaveId` → onboarding done; **unknown id**
  (task removed since the save) → onboarding done (so the player isn't wedged on a vanished task).
- **`playerTaskId` null** but legacy `playerTaskIndex` present → resume by raw index (best-effort
  back-compat; *this* path can still desync — see the history note below).
- **both null** (pre-feature save) → onboarding treated as already done, so returning players
  aren't re-shown tasks.
- `ResetSystemState` → `currentIndex = 0` (fresh world starts onboarding).

## Adding / changing a task

Append a `new PlayerTask(id, title, () => new TaskProgress(current, target))` in
`BuildTasks()`. Title: concise, ASCII-only (m5x7 has no non-ASCII glyphs), `\n` allowed for
a second hint line. Detection: a cheap query or poll — reuse the existing probe helpers
(`CountStructures`, `CountJob`, `CountMice`, `HousingProgress`, `YieldsWood`,
`CountConfiguredCrates`). `id` is the stable save key, so inserting/reordering tasks is now
safe — but **keep ids unique and don't rename an existing task's `id`** (a rename reads as a
removed task → returning players on it are bumped to "onboarding done").

## Current arc (18 tasks)

press space to unpause → flag 2 trees → flag 3 wheat → build 3 crates → configure crates
(wood/wheat) → house all mice → build sawmill → assign woodworker → build drawer → stockpile
2 days of food → have 6 mice → build laboratory → assign scientist → research Tools → build
workshop → build digging pit (or quarry) → gather 3 stone → **craft stone tools**.

- **Step 1 must teach unpause**: a fresh world pauses after worldgen, and most steps need mice
  to *act* (build/haul), which only happens while running. `CountStructures` counts only
  *completed* structures, so without unpausing the player soft-locks on the first build step.
  Probe is `Time.timeScale > 0`.
- **Food stockpile** reuses `AnimalController.ComputeDaysOfFoodInStorage()` (don't re-derive) —
  Infinity (no mice) counts as satisfied.
- **Tools finale**: researching Tools unlocks the `workshop` (`defaultLocked`) + the stone-tools
  recipe, and the closing steps actually exercise them — build the workshop, get stone (digging
  pit yields it as a rare drop; the quarry is Mining-locked so the pit is the tutorial path),
  then craft. See SPEC-research.md for the gating mechanism.

### History: the index-key skip bug (fixed)

Onboarding *used* to be saved as a raw **int index** (`playerTaskIndex`). Because the list grows
during development, an older save resumed on whatever task now occupied that slot. Concretely:
`0.1.0` inserted `unpause` (front) + `food_stockpile` (before `six_mice`), shifting `six_mice`
from index 8 → 10. A pre-`0.1.0` save sitting at old index 11 reloaded onto new index 11
(`build_laboratory`) — **silently skipping `six_mice`**, which then showed as completed despite
the colony never having 6 mice. The probe was never the bug; the index key was.

Fixed by keying on the stable `id` (above). If you ever see a task report complete without its
condition being met — or a returning player land on the wrong step — suspect the save key first,
not the probe. Legacy index-keyed saves still take the best-effort `playerTaskIndex` path and can
exhibit the old skew once; they self-heal on the next save (written with `playerTaskId`).

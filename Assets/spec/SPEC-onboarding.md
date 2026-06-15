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

`WorldSaveData.playerTaskIndex` (`int?`). Gathered/restored/reset in `SaveSystem`:
- Restored value → resume mid-onboarding across reloads.
- **null** (pre-feature save) → onboarding treated as already done (`currentIndex` set past
  the end), so returning players aren't re-shown tasks.
- `ResetSystemState` → `currentIndex = 0` (fresh world starts onboarding).

## Adding / changing a task

Append a `new PlayerTask(id, title, () => new TaskProgress(current, target))` in
`BuildTasks()`. Title: concise, ASCII-only (m5x7 has no non-ASCII glyphs), `\n` allowed for
a second hint line. Detection: a cheap query or poll — reuse the existing probe helpers
(`CountStructures`, `CountJob`, `CountMice`, `HousingProgress`, `YieldsWood`,
`CountConfiguredCrates`). `id` is the stable save key — don't reorder semantics without
considering saved `playerTaskIndex`.

## Current arc (17 tasks)

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

Note: progress is saved as an **int index** (`playerTaskIndex`), not keyed by `id` — inserting
or reordering tasks shifts what a mid-onboarding save resumes on. Low-stakes for early-game
onboarding, but be aware when editing the list mid-development.

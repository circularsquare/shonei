# SPEC — Historic colony statistics

Per-day time-series metrics (food points produced/eaten per day, etc.) tracked in a
generic, reusable way and rendered as bar charts. Built so a new graphed metric is a
few lines, not a new system.

## Pieces

| File | Role |
|------|------|
| `Assets/Model/Stats/DailyStat.cs` | One tracked metric: a fixed-capacity ring buffer of finalized daily values + the in-progress day's accumulator. |
| `Assets/Model/Stats/StatsTracker.cs` | Plain-C# singleton (created in `World.Awake`, like `WeatherSystem`). Owns the stat set, the production/consumption firehoses, and save/load. |
| `Assets/Components/BarChartGraph.cs` | Generic Texture2D bar chart (single- or double-sided). Reusable by any panel. |

## DailyStat

Two aggregation modes decide how a day's records collapse into one value:
- **`Sum`** — daily value = total recorded that day (food produced/eaten).
- **`Average`** — daily value = mean of the day's samples (avg social satisfaction).

Fed in one of two ways:
- **Push** — game code calls `StatsTracker.instance?.Record("id", value)` (or a firehose, below) on an event.
- **Pull** — the stat carries a `sampler` lambda; `StatsTracker` invokes it on the hourly cadence. A pull stat needs no other wiring.

`GetSeries(maxDays, includeCurrentDay)` returns values oldest→newest for charting; the
in-progress day is appended last when `includeCurrentDay` so a fresh colony shows
something before its first day ends.

## Cadence (driven by `World.Tick`)

- **In-game hour** (`OnSampleTick`) — pulls every sampler-backed stat.
- **Day boundary** (`OnDayElapsed`) — finalizes every stat's in-progress day into history and resets the accumulator. All stats finalize together, so they stay in lockstep within a save.

## Adding a metric

1. Register it in `StatsTracker.RegisterDefaults`:
   - Event/total: `Add(new DailyStat("id", "label", DailyStat.Agg.Sum, HistoryCapacity));` then feed it (`Record`, or a firehose).
   - Sampled/average: `Add(new DailyStat("id", "label", DailyStat.Agg.Average, HistoryCapacity, sampler: () => …));` — done, it samples itself.
2. Save/load is automatic (keyed by id; see below). No `SaveSystem` edit needed.
3. To chart it, point a `BarChartGraph` at `GetSeries(...)` (see below).

**Firehoses**: `NoteProduced(item, fen)` / `NoteConsumed(item, fen)` / `NoteDecayed(item, fen)`
are generic per-item entry points (called from `Animal.Produce`, `Processor.Tap`,
`Foundry` (smelt/cast output), `Animal.HandleNeeds`, and `ItemStack.DecayAtRate`). They extract whatever item-derived stats
are tracked — currently food points (`fen/100 × item.foodValue`, matching the game's
nutrition math; non-edibles are skipped, so tool/clothing decay doesn't count). Add new
item-derived stats by extending these, not by scattering `Record` calls at every site.

**Current stats**: `food_produced` / `food_consumed` / `food_decayed` (firehose-fed),
`research_gained` (scientist) / `research_passive` (construction/craft/repair) /
`research_decayed` (all `Record`-pushed from `ResearchSystem` as the actual clamped delta
applied), and the sampled `avg_social`. The food chart stacks eaten+decayed on its down bar;
the research chart (ResearchPanel) stacks scientist+passive up, decay down.

## BarChartGraph

Two feed APIs:
- `SetData(up, down, slotCount, lastIsLive)` — single series per side (float arrays, `down`
  null = single-sided). Convenience sugar over `SetSeries` using the shared amber (up) /
  red (down) palette.
- `SetSeries(Segment[] up, Segment[] down, slotCount, lastIsLive)` — **stacked** segments per
  side. A `Segment` is `{float[] values, Color32 color, Color32 liveColor, string label}`;
  segments in a side stack within one bar (bar height = sum of its segments). Used by the
  food chart (down = eaten + decayed) and the research chart (up = scientist + passive).
  Reuse the public palette constants (`Amber/AmberLive`, `Red/RedLive`, `Slate/SlateLive`,
  `Blue/BlueLive`, `Green/GreenLive`) so charts stay visually consistent.

`up`/`down` values are oldest→newest. `slotCount` is the fixed column count — bars are a
fixed width and **fill in from the right** (newest rightmost), so one day of data is one bar
at the far right, not a stretched block.

- **Each segment is right-aligned independently**, so series of different lengths still line up by day (e.g. a metric added later has a shorter history and simply starts further right, sharing "today"). Do not align one series using the other's slot offset — that was the original bug.
- The shared scale is driven by the tallest **stacked total** across all slots (max of the up-sum and down-sum per slot), so up and down read on one scale and stacked bars don't clip.
- Double-sided draws a centered baseline, `up` above / `down` below.
- Per-column hover lists every non-zero segment as `label ±value` (up = +, down = −; values < 10 show tenths, ≥ 10 integer), so a stacked bar shows its signed breakdown.
- Per-column hover tooltips via the shared `TooltipSystem` (`SetLabels(unit, upLabel, downLabel)` configures the text). `raycastTarget` must be on.
- Renders into a point-filtered `Texture2D` (crisp pixel art), like `PriceGraph`. Awake/runtime only — no bars in edit mode.

The happiness panel's food chart is the live example — see SPEC-ui.md "Food chart".

## Save / load

Persisted as `WorldSaveData.stats` (`StatSaveData[]`), one entry per stat keyed by id,
holding finalized history **and** the in-progress day's `currentSum`/`currentCount` — so
a mid-day save (incl. autosave) doesn't lose the day's partial tally, and the next day
boundary finalizes it correctly on the restored `World.timer`. Matched by id on load:
unknown ids are ignored and registered stats with no saved entry start empty, so
adding/removing a stat is save-safe with no version bump. `StatsTracker.ClearAll()` resets
on fresh world / `LoadDefault`.

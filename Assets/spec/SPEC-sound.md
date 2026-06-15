# Sound System

Audio for a side-view colony sim with many autonomous mice. The guiding problem:
**dozens of agents each doing micro-actions would produce cacophony, and if
everything is audible nothing reads as important.** The whole design is organized
around *focus* — a priority hierarchy plus throttles that keep the mix legible.

## Current implementation

`SoundManager` (`Assets/Controller/SoundManager.cs`) — MonoBehaviour singleton in
`Main.unity`. Self-creates two `AudioSource`s in `Awake`:
- `sfxSource` — one-shots via `PlayOneShot`.
- `ambientSource` — `loop = true`, per-frame volume.

Clips are `Resources.Load<AudioClip>`-ed on first use and cached:
- `Resources/Audio/SFX/` — one-shots (`click.wav`, `blueprint_place`, …)
- `Resources/Audio/Ambient/` — loops (`rain.mp3`)

**Volume**: designer baselines (`sfxVolume`, `ambientVolume` inspector fields) ×
the user's `SettingsManager` sliders (master × per-bus), applied at point-of-use.
"Max user volume" plays a clip at exactly its designer baseline.

**API**: `SoundManager.instance.PlaySFX("clipName", volumeScale = 1f)` (2D,
non-spatial). `volumeScale` trims an individual clip below the SFX baseline for
clips that are inherently hotter than the rest (e.g. `trade_fill` plays at 0.5).

**Missing clips**: a SFX whose file doesn't exist logs **once** per clip name
(`warnedMissing` set), then stays quiet — so an unauthored slot doesn't spam the
console on every trigger. Drop the file in and it just works (loaded by name).

**Ambient loops**: two independent looping `AudioSource`s, each volume-driven per
frame off `WeatherSystem` and time-smoothed over 3s (`ambientRampSeconds`) so changes
fade instead of popping. They mix freely (windy AND raining). Both pause together via
`AudioSource.Pause()` when `Time.timeScale == 0` (handled once in `Update`) so they
resume mid-loop instead of re-attacking.
- **rain** (`ambientSource`) — `0.5 + 0.5·rain` of the `ambientVolume` baseline,
  gated off `rainAmount` (silent when not raining).
- **wind** (`windSource`) — driven by `|wind|` (direction-agnostic). **Silent below
  `windThreshold` (0.3)**, then scales from there to full `windVolume` at
  `windFullMagnitude` (0.9) — so it only speaks up on breezier stretches, not the
  ~±0.4 typical drift. Tune via `windThreshold` / `windFullMagnitude` / `windVolume`.

**Gameplay bindings**: model-side events that should make a sound but have no UI
moment subscribe in `Start()` (not Awake — subscription-order safety) and are static
events (survive `ResearchSystem` recreation on load). Mirrors EventFeed's binding
style. Current: `ResearchSystem.OnTechUnlocked` → `research_complete`.

---

## Design philosophy

### Priority hierarchy (loudest intent → quietest)

1. **Player-action feedback** — the player clicked/placed/cancelled. Always audible;
   inherently sparse (one per deliberate action), so never cacophonous.
2. **Event stingers** — milestones the player should notice (build done, research
   done, trade filled, mouse born/died). Routed through a central feed, rate-limited.
3. **Machine / process loops** — running structures (windmill, pump, wheel). The
   main "the base is alive" layer. Self-limiting: only *running* machines, only
   *near camera*.
4. **Ambient bed** — world tone (rain, day, night, wind, underground). One or two
   cross-faded loops, never competes for attention.
5. **Critter presence** — mice. The quietest, most heavily throttled layer. See below.

When two layers compete, the higher one wins the mix. Lower tiers duck or drop.

### Two throttles (what keeps it legible)

- **Camera-cull + zoom-gate.** The camera *is* the focus signal. Sounds for
  off-screen things don't play; micro-sounds (tier 3/5) attenuate as the camera
  zooms out toward the macro/management view and emerge when zoomed in on one corner.
  Zoom is available via `MatchCameraZoom` / `MouseController` scroll handling.
- **Rate-limits / voice pools.** Each sound *category* has a global cooldown and a
  cap on concurrent instances, so nine mice sawing become an occasional sawing
  texture, not nine overlapping samples.

### The mice question (tier 5)

**Mice make sound as *presence*, not action-confirmation.** No footsteps, no
per-haul/per-saw sounds — that path is the cacophony trap. Instead: occasional
squeaks tied to **emotional / need state** (data already exists — `Happiness`,
`Eating` starving, social/reading grants), globally rate-limited (e.g. at most one
squeak every few seconds across the whole colony, biased toward mice near the
camera). A soft content squeak now and then; a distinct distressed squeak when a
mouse is starving. Gives "the colony feels alive" without confirming individual
actions. Build this **last** — it needs the global rate-limiter to not be annoying.

---

## Tier 1 — player-action feedback (BUILT)

Wired and live. Canonical clip names (files in `Resources/Audio/SFX/`):

| Clip | Fires when | Call site |
|---|---|---|
| `click` | UI buttons; blueprint placed; cancel a blueprint; designate deconstruct / flag harvest; un-flag | `BuildPanel`, `MouseController`, various UI |
| `blueprint_reject` (×0.5) | placement rejected (3 fail paths) | `BuildPanel.PlaceBlueprint` |
| `research_select` | toggle-study a research node | `ResearchPanel.OnClickToggleStudy` |
| `research_complete` | a node passively completes | binding → `ResearchSystem.OnTechUnlocked` |
| `trade_fill` (×0.5) | a market order fills | `ChatLog.DisplayFill` |

The deliberate choice: **most player actions share one `click`** — placing,
cancelling, designating, harvesting. Only genuinely distinct *outcomes* get their
own clip (rejection = "no", research = a softer confirm/chime, a trade fill = coins).
Build-completion deliberately has **no** sound (would fire unprompted and add noise).

Notes:
- Drag-harvest plays **once per commit**, not per plant (`CommitHarvestDrag`).
- `research_complete` uses the gameplay path only — `UnlockAll()` (debug) doesn't
  fire `OnTechUnlocked`, so the debug "unlock all" button won't machine-gun.
- `trade_fill` is hotter than the other clips, so it plays at `volumeScale 0.5`.

---

## Roadmap — tiers 2–4 (NOT built)

### Tier 2 — event stingers

[EventFeed](SPEC-eventfeed.md)'s bindings table *is* the "things the player should
notice" list — the natural hook. Add a `SoundManager` binding (like
`OnTechUnlocked`) per event, or a small `SoundBindings` once the list grows past ~5.
Candidates: trade fill (done), research forgotten (`OnTechForgotten`), market
errors, mouse born, mouse starved (`Animal` already tracks death). Keep each
deduped/cooldowned in one place so a burst of fills doesn't overlap.

### Tier 3 — machine / process loops (highest "alive" payoff)

Spatialized loops on **active** structures; gain scaled by operational state ×
distance × zoom. Self-limiting (only running machines near camera). Candidates:
windmill whoosh, pump glug, mouse-wheel patter, flywheel/powershaft hum, quarry chip.
Tie each loop's gain to the structure's running state so idle buildings are silent.

**This tier needs new infrastructure** that `PlaySFX` (single 2D source) lacks —
scope it deliberately:
- **Spatial sources + camera-cull**: pooled `AudioSource`s with 3D/2D pan by world
  position; cull/attenuate by distance from the rendering camera.
- **Per-category cooldown + voice cap**: a small registry keyed by category
  (machine, critter, …) with `maxConcurrent` and `minInterval`.
- **Zoom-as-mixer**: a global micro-sound gain curve driven by camera zoom; tier 3/5
  multiply into it.

### Tier 4 — ambient bed (rain + wind built)

Rain and wind loops are live (see Current implementation). Still to add, each a
further `AudioSource` following the same per-source pattern: day vs. night beds
(cross-faded by time of day) and an **underground** bed that fades in as the camera
descends below the surface (caves / digging should *sound* different from the
surface).

---

## Adding sounds

- **New SFX**: drop a `.wav`/`.mp3`/`.ogg` in `Resources/Audio/SFX/` named after the
  clip, call `PlaySFX("name")`. Loaded by name — extension doesn't matter to the call.
- **New ambient loop**: add an `AudioSource` and follow the rain pattern in
  `UpdateRainAmbient()` (target curve → time-smooth → set volume → play/stop).
- **New gameplay→SFX binding** (model event, no UI moment): add a static event on the
  model class (mirror `OnTechUnlocked`), subscribe in `SoundManager.Start()`,
  unsubscribe in `OnDestroy`.
- **Build tiers 3–5 only after the spatial/voice-pool/zoom infrastructure exists** —
  bolting per-machine or per-mouse one-shots onto the 2D `PlaySFX` path is the
  cacophony trap this whole design exists to avoid.

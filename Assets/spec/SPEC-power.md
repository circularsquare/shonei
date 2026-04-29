# Shonei — Mechanical Power

A scalar "power" flows along chains of shaft tiles from registered producers to
registered consumers. Power is a substrate for transit (elevators, future trains)
and for direct workstation acceleration.

## Model

- **Unit**: a single float `power`. No rpm, no torque. 1 power ≈ "one mouse on a wheel".
- **Topology**: a `PowerNetwork` is a connected component of shaft tiles plus the
  producers / consumers that attach via ports.
- **Allocation**: per-tick (1 in-game second), each network sums producer outputs
  and walks consumers in **rotated** registration order (start index advances by 1
  each tick globally), granting full demand or none. The rotation prevents
  first-placed consumers from always winning on a starved network — over N ticks
  every consumer takes a turn at the front of the queue. No fractional fulfilment
  in v1. Storage units (flywheels) are queried for discharge headroom up-front;
  consumers that exceed raw supply pull from storage if available. Any leftover
  after consumers charges storage, prioritizing the *emptiest* unit first;
  discharges similarly drain from the *fullest*. Both use `ChargeFraction`
  (charge / capacity) as the sort key, so storage units of mixed sizes equalize
  fairly. Storage decays each tick before allocation runs.

## Building types

| Building            | Role     | Footprint | Notes |
|---------------------|----------|-----------|-------|
| `power shaft`       | transmission | 1×1   | depth 4 (own slot, renders behind buildings); rotatable (R) — rotation 0/2 = horizontal, 1/3 = vertical |
| `power shaft turn`  | transmission | 1×1   | depth 4; turning shaft, axis always Both. Rotatable (R) for which corner; flippable (F) |
| `power shaft 4`     | transmission | 1×1   | depth 4; 4-way junction, axis always Both. Connectivity-identical to `turn` — the difference is purely visual (sprite shows shaft stubs on all four sides). |
| `wheel`             | producer     | 2×2   | workstation, "runner" job; 1.0 power while a runner is *in WorkObjective at the wheel* (not just dispatched — the wheel stays still and silent during the walk-in). Declares `workSpotX: 0.5, workSpotY: 0.25, workPose: "walk"` so the runner stands centred between the bottom tiles slightly above ground and plays the walk animation while producing — see SPEC-systems.md §Workspot waypoints. |
| `windmill`          | producer     | 2×4   | passive; output = `Mathf.Abs(WeatherSystem.wind) × MaxOutput`; needs open sky above the top row, re-checked each tick |
| `flywheel`          | storage      | 2×2   | charges from network surplus, discharges into deficits, exponential decay each tick |
| `elevator`          | consumer     | 1×N (3..10 via shapes) | variable-height transit. Implements `IPowerConsumer` directly; ports `(-1, 0, H)` and `(nx, 0, H)` at base. Demand `1.0` *only during the actual lift* (Riding state); idle / fetch-empty-cabin / unload all report `0`. All gates (cost branch in `Graph.GetEdgeInfo`, Idle→Trip start, Riding advance) use the inclusive `IsPowerAvailable` check — "would the network supply nominal demand if asked?" — rather than the strict `IsBuildingPowered` to avoid mid-trip freezes during normal allocator-rotation gaps. See SPEC-systems.md §Transit for the navigation side. |

Existing pump and press declare `powerBoost: 3.0` in JSON, opting them in as
consumers — operator's work-tick rate triples while the building is on a powered
network. **Demand is gated on active crafting**: a placed pump only reports
`Demand = 1.0` while a mouse is currently in WorkObjective at it. Idle / unmanned
buildings report `0`, so they don't drain the network or its flywheels.

## Connectivity rules

Two shaft tiles share a network iff they're orthogonally adjacent **and** their
axes are compatible:

- horizontal shaft ↔ horizontal shaft, along x
- vertical shaft ↔ vertical shaft, along y
- turning shaft (`Both`) connects on either axis

A `power shaft` is horizontal or vertical depending on its rotation:
- `rotation` 0 or 2 → horizontal axis
- `rotation` 1 or 3 → vertical axis

`power shaft turn` and `power shaft 4` are always axis `Both` regardless of
rotation. For `turn` the rotation chooses which corner the bend visually faces;
`power shaft 4` is rotationally symmetric, so it isn't `rotatable: true`.

Producers and consumers attach via `PowerPort(dx, dy, axis)` — a relative offset
from the building anchor and the axis the shaft tile at that offset must carry.
Mirroring flips X offsets the same way `Structure.workTile` does.

| Building     | Port(s) (anchor-relative) |
|--------------|---------------------------|
| Wheel        | `(-1, 0, Horizontal)` and `(nx, 0, Horizontal)` — both bottom-row sides. Routing is symmetric so the F flip is purely cosmetic. |
| Windmill     | Four options for routing flexibility: `(-1, 0, Horizontal)` and `(nx, 0, Horizontal)` (both base sides), `(0, -1, Vertical)` and `(1, -1, Vertical)` (under each base tile). |
| Flywheel     | Full perimeter, `Axis.Both` per tile — any adjacent shaft on any side connects. |
| Pump / press | Auto-wrapper (`BuildingPowerConsumer`) provides full perimeter `Axis.Both` — any side, any axis. The SPEC table previously said `(0, 0, Both)`; the actual wrapper layout is the perimeter. |

## Architecture

### `Assets/Model/PowerSystem.cs`

Singleton, created in `World.Awake()`, ticked every in-game second from
`World.Update()` alongside `MaintenanceSystem.Tick()`.

State:

- `shafts: HashSet<Structure>` — registered transmission tiles
- `producers: HashSet<IPowerProducer>` — registered producers
- `consumers: List<IPowerConsumer>` — registered consumers (List, not Set, so
  registration order is stable; allocation walks this list with a per-tick
  rotated start index for fairness, see `allocationCounter` below)
- `networks: Dictionary<int, PowerNetwork>` — rebuilt on `topologyDirty`
- `powered: HashSet<IPowerConsumer>` — recomputed each `Allocate()`; read by
  `IsBuildingPowered(Building)`

Topology rebuild: BFS over shafts using axis-compatibility, then attach producers
and consumers via their ports. O(power-relevant tiles + ports), much smaller
than the world graph.

`RebuildFromWorld()` clears registries and re-walks `StructController.GetStructures()`,
mirroring `MaintenanceSystem.RebuildFromWorld()`. Called from SaveSystem Phase 6.

### Building integration

`StructType.powerBoost: float` (default `1.0`). When a built instance has
`powerBoost > 1`, `Building.OnPlaced()` auto-creates a `BuildingPowerConsumer`
wrapper and registers it. The wrapper exposes a port on every tile around the
building's footprint (`top + bottom + left + right`, axis `Both`) so a shaft
adjacent to any side connects.

**Two consumer-registration paths exist, and you must pick exactly one per
building type:**

1. **Auto-wrapper path** — set `powerBoost > 1` in JSON, no subclass. The
   wrapper handles registration, ports, and the boost lookup. Used by pump
   and press.
2. **Subclass path** — implement `PowerSystem.IPowerConsumer` directly on a
   `Building` subclass and register from `OnPlaced` / unregister from
   `Destroy`. Use this when the consumer needs custom ports (a layout other
   than full perimeter), runtime state, or computed demand. Leave
   `powerBoost = 1` to suppress the auto-wrapper.

Mixing both on the same type would register two consumer entries for one
building — `Building.OnPlaced` skips auto-wrapping when `this is IPowerConsumer`,
so the subclass path takes precedence and the JSON flag is silently ignored.

### Storage (flywheel)

Storage is a third role alongside producer and consumer, exposed via
`PowerSystem.IPowerStorage`. The interface lets PowerSystem orchestrate
charge/discharge instead of letting the storage drive its own producer or
consumer values (which would create a circular dependency on net state).

Each tick, in `Allocate()`:

1. Sum non-storage producer outputs into `supply`.
2. Compute aggregate `MaxDischarge` across the network's storage units.
3. Walk consumers in **rotated** registration order — start index =
   `allocationCounter % consumers.Count`, advancing by 1 each tick globally.
   Each consumer draws from raw supply first; if short, pulls from the storage
   discharge pool. Allocation stays binary (full demand or none). The rotation
   ensures fairness on starved networks (no permanent winners by placement order).
4. Apply discharge greedily, drawing from storages sorted by `ChargeFraction`
   *descending* — fullest first, capped per-unit at `MaxDischarge`. Equalizes
   fill levels across the network's storages over time.
5. Surplus after consumers charges storage greedily, sorted by `ChargeFraction`
   *ascending* — emptiest first, capped per-unit at `MaxIntake`. Same equalization
   logic in reverse.

Decay runs in `Tick()` *before* allocation via `IPowerStorage.StorageTick()`.
Each unit owns its decay rule; the flywheel applies an exponential
`charge *= DecayFactor` (currently `0.97`, ≈ 23-tick half-life).

**Flywheel** (`Assets/Model/Structure/Flywheel.cs`) is the only storage type in v1:

| Constant     | Value | Meaning |
|--------------|-------|---------|
| `Capacity`   | 50.0  | Maximum stored energy. |
| `MaxRate`    | 3.0   | Per-tick cap on charge or discharge magnitude (symmetric). Sized so one flywheel can cover up to 3 nominal-demand consumers during a wind stall. |
| `DecayFactor`| 0.97  | Exponential bleed per tick (≈ 23-tick half-life ≈ 2.3 in-game hours). |

Charge persists via `StructureSaveData.flywheelCharge`. Without it, every
flywheel would reset to empty on load — players would lose any surplus
buffered during the session.

Future storage types (battery banks, capacitors) implement `IPowerStorage`
the same way; only the constants and decay rule differ.

### Power-boost gate

`AnimalStateManager.HandleWorking` (CraftTask branch only):

```csharp
if (wsBuilding.structType.powerBoost > 1f
        && PowerSystem.instance.IsBuildingPowered(wsBuilding)) {
    workEfficiency *= wsBuilding.structType.powerBoost;
}
```

Not gated for HarvestTask, ConstructTask, MaintenanceTask, or ResearchTask —
power only accelerates workstation crafting in v1.

## Save / load

**Nothing persisted.** All power state is derived:

- topology = function of placed shafts/producers/consumers
- "engaged" mouse wheel = function of any mouse currently in WorkObjective at the wheel (`Building.HasActiveCrafter()`)
- pump/press demand = function of any mouse currently in WorkObjective at the building (same predicate)
- windmill output = function of `WeatherSystem.wind`

`PowerSystem.RebuildFromWorld()` runs in SaveSystem **Phase 6** (observer phase)
right after `MaintenanceSystem.RebuildFromWorld()`. The next 1-second tick
recomputes networks and allocations.

## Placement constraints

- **Shafts** live in their own depth-4 slot (added to coexist with buildings,
  ladders, foreground decor, etc.). Visually they render at sortingOrder 5 —
  behind buildings/platforms/foreground but in front of roads — so they read as
  wall-mounted plumbing. Multiple shafts on a single tile are still rejected by
  the per-depth `t.structs[depth]` check in `StructPlacement`.
- **Windmill** declares `mustBeOpenSkyAbove: true` per top-row tile in its
  `tileRequirements` JSON. Each requirement runs `World.IsExposedAbove(t.x, t.y)`
  (no solid ground or solidTop structures above the requirement tile). The check
  also runs each tick inside `Windmill.HasOpenSky()`, so building a roof over a
  placed windmill kills its output immediately.

## InfoPanel display

`StructureInfoView.AppendPowerInfo` renders one of:

- shaft: `power: net N`
- producer: `power: net N  out: X.X`
- consumer (active or idle on a healthy net): `power: net N  status: powered (consuming X.X)`
  — `X.X` is `CurrentDemand`. `0.0` means idle (no crafter) but the network *would*
  satisfy nominal demand, so the player sees "your wiring is fine" instead of a
  spurious "unpowered" between rounds.
- consumer on a net that can't supply nominal demand: `power: net N  status: unpowered`
- port doesn't reach a shaft: `power: disconnected`

## Animation

Each power building attaches one or more visual components via the
`Structure.AttachAnimations()` virtual hook. Sprite frames (where used) are authored
as a single PNG per building set to "Multiple" mode in the Unity importer and sliced
in the Sprite Editor; `Resources.LoadAll<Sprite>` returns them in slice order. The
static fallback in `StructType.LoadSprite()` picks `LoadAll[0]` when
`Resources.Load<Sprite>` returns null on a multi-sliced sheet, so the first frame
renders correctly before the animator takes over.

| Building            | Driver                       | `isActive`                                            | speed                                       | base FPS |
|---------------------|------------------------------|-------------------------------------------------------|---------------------------------------------|----------|
| shaft / shaft turn  | `FrameAnimator`              | `PowerSystem.IsShaftActivelyTransmitting(this)`       | 1                                           | 6        |
| wheel               | `FrameAnimator`              | `MouseWheel.IsCurrentlyActive`                        | 1                                           | 8        |
| windmill (wheel)    | `RotatingPart` (transform)   | `Windmill.IsCurrentlyActive`                          | `\|wind\| × 180°/s`, clockwise                | n/a      |
| flywheel (wheel)    | `RotatingPart` (transform)   | `Flywheel.IsCurrentlyActive`                          | `(charge / Capacity) × 360°/s`, clockwise   | n/a      |

`PowerNetwork.flowing` is set during `Allocate()` whenever any consumer is powered
or storage absorbs surplus this tick. Shaft animators read it via
`IsShaftActivelyTransmitting()`. `FrameAnimator` holds the current frame when
`isActive` is false (no neutral pose required) and resumes from there when
reactivated. The windmill and flywheel rotating-wheel children use
`Components/RotatingPart.cs` instead — `speedSource` is a `Func<float>` returning a
scalar in roughly `[-1, 1]`, `degPerSecAtMaxSpeed` scales it, and `directionSign`
fixes the spin direction. Convention: rotating power parts always spin clockwise
(`directionSign = -1` in Unity 2D), and feed `RotatingPart` an unsigned magnitude
so wind direction / charge sign don't flip the visual. Rotation simply pauses when
`isActive` is false. Per-building wheel sprites: `windmill_wheel.png`,
`flywheel_wheel.png` — square, centred-pivot so rotation doesn't translate the
visual. Per-frame normal maps are not implemented — all frames share the single
static `_n` companion.

### Port-stub visualization

Several producers/consumers historically baked a "shaft poking out the side"
visual into their base sprite at a port offset (windmill's left axle, wheel's side
strut). When no shaft is wired up, that pixel art reads as an axle protruding into
thin air. `PortStubVisuals` (`Components/PortStubVisuals.cs`) replaces the baked
pixels with conditional child SpriteRenderers — one per port — that toggle on/off
based on whether `PowerSystem.HasCompatibleShaftAt(tx, ty, axis)` finds a matching
shaft tile.

Wiring contract:

- Power building's `AttachAnimations()` calls `AttachPortStubs(Ports)` on
  `Structure`. The helper instantiates `PortStubVisuals`, which spawns one child SR
  per port, sized to one tile, sorted at `parent.sortingOrder − 1` so it renders
  *behind* the building.
- Sprite per port: `port_shaft_h.png` for `Axis.Horizontal`, `port_shaft_v.png`
  for `Axis.Vertical`. For `Axis.Both` ports, the side is inferred from port
  position: ports outside the footprint on X (left/right) use the horizontal
  sprite; ports outside on Y (top/bottom) use the vertical sprite. Corner ports
  (outside on both axes) and inside ports are skipped. Missing sprite → silent
  skip, so a building can opt out by simply not authoring its stub asset.
- Sprite art convention: `port_shaft_h.png` is authored extending LEFTWARD (axle
  exits the right edge of the sprite); `port_shaft_v.png` extends DOWNWARD (axle
  exits the top edge). `PortStubVisuals` `flipX`/`flipY`s automatically for
  ports on the right or top of the building so the axle extends outward in
  every case.
- Refresh cadence: `PortStubVisuals.Start` does an initial pass; thereafter it
  subscribes to `PowerSystem.onTopologyRebuilt` (fired at the end of each
  `RebuildTopology`). `HasCompatibleShaftAt` lazily rebuilds if `topologyDirty`,
  so initial placement (which dirties the topology) sees correct stub state on
  the same frame.
- Mirroring: `PortStubVisuals.Refresh` flips X offsets the same way
  `PowerSystem.FindAttachedNetwork` does (`mirrored ? nx − 1 − dx : dx`). Stub
  position and `flipX` track the mirror state.

Save/load: nothing persisted — stub visibility is purely a function of world
state. `PowerSystem.RebuildFromWorld` (Phase 6) marks topology dirty; the next
`HasCompatibleShaftAt` call (or Tick) rebuilds and fires `onTopologyRebuilt`,
which refreshes all stubs.

## Extension points (deferred work)

- **Flywheel** (storage): implements `IPowerProducer + IPowerConsumer` with an
  internal charge state. Allocation order needs adjustment so flywheels charge
  from surplus and discharge into deficits — straightforward extension to
  `Allocate()`.
- **Fractional satisfaction**: replace binary "full demand or none" with
  proportional allocation when supply < demand.
- **Power-network overlay**: toggleable debug viz colouring shafts by network id.

## Tunables

| Constant | Value | Where |
|----------|-------|-------|
| `MouseWheel.Output` | 1.0 | `MouseWheel.cs` |
| `Windmill.MaxOutput` | 3.0 | `Windmill.cs` |
| `Windmill.StallThreshold` | 0.05 | `Windmill.cs` (below this `|wind|`, output is 0) |
| `WeatherSystem` wind noise | ±0.15 / hour | `WeatherSystem.cs` (Uniform shock per OU step; stationary `\|wind\|` σ ≈ 0.43, mean ≈ 0.35 → average windmill output ≈ 1.0 power/tick) |
| `BuildingPowerConsumer.Demand` | 1.0 | `BuildingPowerConsumer.cs` (reported only while `Building.HasActiveCrafter()`; idle / unmanned = 0) |
| pump/press `powerBoost` | 3.0 | `buildingsDb.json` |
| Mouse-wheel recipe `workload` | 5 ticks | `recipesDb.json` (id 50) |

## Anti-patterns

- **Don't poll the world graph from PowerSystem.Tick.** Topology rebuild only
  runs on registration changes (set by `topologyDirty`). Per-tick work is just
  the allocation walk over already-built networks.
- **Don't gate the WOM Craft order on power.** Pump/press fall back to 1×
  efficiency when unpowered — they should still craft, just slower. Power is a
  multiplier, not a precondition.
- **Don't try to stack two shafts on a single tile.** Shafts have their own
  depth slot (4), so they coexist freely with buildings/ladders/torches/roads,
  but only one shaft per tile. Use a turning or 4-way shaft if you need to
  branch.
- **Don't add `powerBoost > 1` to a building that already implements
  `IPowerConsumer` directly.** `Building.OnPlaced` skips auto-registration in
  that case, but it's still confusing — pick one path per building type.
- **Don't gate power semantics on `WorkOrder.res.reserved`.** That fires at
  *dispatch* (when an animal claims the order), before the GoObjective walk.
  The wheel needs to stay still and silent during the walk-in. Use
  `Building.HasActiveCrafter()` so production / demand track the runner
  actually being on the workspot in WorkObjective.

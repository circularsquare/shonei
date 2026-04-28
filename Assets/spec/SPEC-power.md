# Shonei — Mechanical Power

A scalar "power" flows along chains of shaft tiles from registered producers to
registered consumers. Power is a substrate for transit (elevators, future trains)
and for direct workstation acceleration.

## Model

- **Unit**: a single float `power`. No rpm, no torque. 1 power ≈ "one mouse on a wheel".
- **Topology**: a `PowerNetwork` is a connected component of shaft tiles plus the
  producers / consumers that attach via ports.
- **Allocation**: per-tick (1 in-game second), each network sums producer outputs
  and walks consumers in registration order, granting full demand or none. No
  fractional fulfilment in v1. Storage units (flywheels) are queried for discharge
  headroom up-front; consumers that exceed raw supply pull from storage if available.
  Any leftover after consumers charges storage in registration order. Storage decays
  each tick before allocation runs.

## Building types

| Building            | Role     | Footprint | Notes |
|---------------------|----------|-----------|-------|
| `power shaft`       | transmission | 1×1   | depth 2; rotatable (R) — rotation 0/2 = horizontal, 1/3 = vertical |
| `power shaft turn`  | transmission | 1×1   | depth 2; turning shaft, axis always Both. Rotatable (R) for which corner; flippable (F) |
| `power shaft 4`     | transmission | 1×1   | depth 2; 4-way junction, axis always Both. Connectivity-identical to `turn` — the difference is purely visual (sprite shows shaft stubs on all four sides). |
| `wheel`             | producer     | 2×2   | workstation, "runner" job; 1.0 power while occupied. Declares `workSpotX: 0.5, workSpotY: 0.25, workPose: "walk"` so the runner stands centred between the bottom tiles slightly above ground and plays the walk animation while producing — see SPEC-systems.md §Workspot waypoints. |
| `windmill`          | producer     | 2×4   | passive; output = `Mathf.Abs(WeatherSystem.wind) × MaxOutput`; needs open sky above the top row, re-checked each tick |
| `flywheel`          | storage      | 2×2   | charges from network surplus, discharges into deficits, exponential decay each tick |

Existing pump and press declare `powerBoost: 3.0` in JSON, opting them in as
consumers — operator's work-tick rate triples while the building is on a powered
network.

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
| Wheel        | `(-1, 0, Horizontal)` — one tile left of the wheel's bottom-left, where the sprite shows the axle output. Mirroring (F) flips the port to the right side. |
| Windmill     | Three options for routing flexibility: `(-1, 0, Horizontal)` (left side at base), `(0, -1, Vertical)` and `(1, -1, Vertical)` (under each base tile). |
| Pump / press | `(0, 0, Both)` — anchor tile (default for any `powerBoost > 1` building). |

## Architecture

### `Assets/Model/PowerSystem.cs`

Singleton, created in `World.Awake()`, ticked every in-game second from
`World.Update()` alongside `MaintenanceSystem.Tick()`.

State:

- `shafts: HashSet<Structure>` — registered transmission tiles
- `producers: HashSet<IPowerProducer>` — registered producers
- `consumers: List<IPowerConsumer>` — registered consumers (List, not Set, so
  registration order is stable for deterministic allocation)
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
3. Walk consumers in registration order. Each draws from raw supply first;
   if short, pulls from the storage discharge pool. Allocation stays binary
   (full demand or none).
4. Apply discharge proportionally across storage units that contributed.
5. Surplus after consumers charges storage in registration order, each
   capped at `MaxIntake`.

Decay runs in `Tick()` *before* allocation via `IPowerStorage.StorageTick()`.
Each unit owns its decay rule; the flywheel applies an exponential
`charge *= DecayFactor` (currently `0.97`, ≈ 23-tick half-life).

**Flywheel** (`Assets/Components/Flywheel.cs`) is the only storage type in v1:

| Constant     | Value | Meaning |
|--------------|-------|---------|
| `Capacity`   | 50.0  | Maximum stored energy. |
| `MaxRate`    | 2.0   | Per-tick cap on charge or discharge magnitude. |
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
- "engaged" mouse wheel = function of WOM Craft order's `res.reserved`
- windmill output = function of `WeatherSystem.wind`

`PowerSystem.RebuildFromWorld()` runs in SaveSystem **Phase 6** (observer phase)
right after `MaintenanceSystem.RebuildFromWorld()`. The next 1-second tick
recomputes networks and allocations.

## Placement constraints

- **Shafts** are depth 2 (foreground). They mutually exclude with stairs /
  ladders / torches per the existing `t.structs[depth]` check in
  `StructPlacement` and the `Structure` constructor. Power runs need their own
  dedicated tile channel — that's an intentional layout constraint.
- **Windmill** declares `mustBeOpenSkyAbove: true` per top-row tile in its
  `tileRequirements` JSON. Each requirement runs `World.IsExposedAbove(t.x, t.y)`
  (no solid ground or solidTop structures above the requirement tile). The check
  also runs each tick inside `Windmill.HasOpenSky()`, so building a roof over a
  placed windmill kills its output immediately.

## InfoPanel display

`StructureInfoView.AppendPowerInfo` renders one of:

- shaft: `power: net N`
- producer: `power: net N  out: X.X`
- consumer: `power: net N  status: powered/unpowered`
- port doesn't reach a shaft: `power: disconnected`

## Animation

Each power building attaches a `FrameAnimator` (`Components/FrameAnimator.cs`) via
the `Structure.AttachAnimations()` virtual hook. Sprite frames are authored as a
single PNG per building set to "Multiple" mode in the Unity importer and sliced in
the Sprite Editor; `Resources.LoadAll<Sprite>` returns them in slice order. The
static fallback in `StructType.LoadSprite()` picks `LoadAll[0]` when
`Resources.Load<Sprite>` returns null on a multi-sliced sheet, so the first frame
renders correctly before the animator takes over.

| Building            | `isActive`                                            | speed multiplier         | base FPS |
|---------------------|-------------------------------------------------------|--------------------------|----------|
| shaft / shaft turn  | `PowerSystem.IsShaftActivelyTransmitting(this)`       | 1                        | 6        |
| wheel               | `MouseWheel.IsCurrentlyActive`                        | 1                        | 8        |
| windmill            | `Windmill.IsCurrentlyActive`                          | `\|WeatherSystem.wind\|` | 6        |
| flywheel            | `Flywheel.IsCurrentlyActive`                          | `charge / Capacity`      | 10       |

`PowerNetwork.flowing` is set during `Allocate()` whenever any consumer is powered
or storage absorbs surplus this tick. Shaft animators read it via
`IsShaftActivelyTransmitting()`. `FrameAnimator` holds the current frame when
`isActive` is false (no neutral pose required) and resumes from there when
reactivated. Per-frame normal maps are not implemented — all frames share the
single static `_n` companion.

## Extension points (deferred work)

- **Per-frame normals for windmill**: shaft / wheel / flywheel keep their static
  `_n` (silhouette doesn't really change between frames). The windmill's blade
  shape changes per frame, so a sliced `_n` sheet would improve its lighting —
  left as a future pass.
- **Flywheel** (storage): implements `IPowerProducer + IPowerConsumer` with an
  internal charge state. Allocation order needs adjustment so flywheels charge
  from surplus and discharge into deficits — straightforward extension to
  `Allocate()`.
- **Elevator + transit**: per todo.txt, "general transit object with stops,
  capacity-constrained, wait-time-based nav cost". Will use `IPowerConsumer`
  with a non-default port layout. Likely also needs queue/wait state on Animal.
- **Fractional satisfaction**: replace binary "full demand or none" with
  proportional allocation when supply < demand.
- **Power-network overlay**: toggleable debug viz colouring shafts by network id.

## Tunables

| Constant | Value | Where |
|----------|-------|-------|
| `MouseWheel.Output` | 1.0 | `MouseWheel.cs` |
| `Windmill.MaxOutput` | 3.0 | `Windmill.cs` |
| `Windmill.StallThreshold` | 0.05 | `Windmill.cs` (below this `|wind|`, output is 0) |
| `BuildingPowerConsumer.Demand` | 1.0 | `BuildingPowerConsumer.cs` |
| pump/press `powerBoost` | 3.0 | `buildingsDb.json` |
| Mouse-wheel recipe `workload` | 5 ticks | `recipesDb.json` (id 50) |

## Anti-patterns

- **Don't poll the world graph from PowerSystem.Tick.** Topology rebuild only
  runs on registration changes (set by `topologyDirty`). Per-tick work is just
  the allocation walk over already-built networks.
- **Don't gate the WOM Craft order on power.** Pump/press fall back to 1×
  efficiency when unpowered — they should still craft, just slower. Power is a
  multiplier, not a precondition.
- **Don't put a shaft on the same tile as a stairs / ladder / torch.** Both
  occupy depth 2; the placer rejects. Route around or use a turning shaft.
- **Don't add `powerBoost > 1` to a building that already implements
  `IPowerConsumer` directly.** `Building.OnPlaced` skips auto-registration in
  that case, but it's still confusing — pick one path per building type.

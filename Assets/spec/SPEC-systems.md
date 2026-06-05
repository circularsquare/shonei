# Shonei — Navigation, Inventory & Units

## Navigation

- **Algorithm**: A* with Euclidean heuristic. Edge costs vary by traversal type (see below).
- **Locomotion**: `speed = GetTravelSpeedMultiplier(animal) * edgeLength / edgeCost`. Edge info comes from `Graph.GetRawEdgeInfo()` (excludes road cost — road bonus is tile-based via `ModifierSystem`). A* pathfinding still uses `GetEdgeInfo()` with road-reduced costs so paths prefer roads.
- **Standability**: tile is standable if tile below is solid, has a platform/building, or has a ladder.
  - *Tall-body exception*: a tile that itself contains a `solidTop` structure also occupying the tile below (i.e. the SAME structure body extends through both) is non-standable — see §Variable-shape structures.
  - *Per-tile override*: a structure can declare specific local tiles as standable internal floors via `Structure.HasInternalFloorAt(localDx, localDy)` (default `false`). `Elevator` uses this to make its bottom and top stops walkable inside the chassis even though the multi-tile body would otherwise block them.
  - *Future improvement*: data-driven per-tile / per-shape config replacing the binary `solidTop`.
- **Vertical movement**: ladders produce direct node-to-node vertical edges (cost 2.0). Cliff climbing and stairs use **waypoint chains** (see below).
- **Road speed boost**: road bonus is per-tile — only the tile the mouse is currently standing on contributes its `pathCostReduction` (doubled to match old two-endpoint feel). No bonus from adjacent road tiles.
- **Floor item slowdown**: tiles with floor items reduce movement speed by 25% (×0.75).
- **Crowding slowdown**: tiles with multiple mice reduce movement speed by 25% (×0.75, flat regardless of count). All speed modifiers are multiplicative.
- **Tile occupancy tracking**: `AnimalController` maintains a `Dictionary<Tile, int>` for O(1) crowding queries. Animals register/unregister via `UpdateCurrentTile()` after position changes.
- **Helper queries**: `FindPathToBuilding`, `FindPathItemStack`, `FindPathToStorage`, `FindPathToLeisureSeat`, `FindPathToDropTarget`

### Connected-components reachability cache

`Graph` maintains a component ID on every `Node` (`node.componentId: int`, -1 = impassable). `Graph.RebuildComponents()` runs a BFS flood-fill over all standable nodes and assigns integer IDs **renumbered from scratch each call**; waypoint nodes get IDs transitively via neighbor edges. `Graph.SameComponent(a, b)` is an O(1) check.

- **Off-grid waypoints must be reset to be re-flooded.** BFS uses `componentId >= 0` as its visited marker, so every off-grid waypoint must be reset to -1 first or it freezes at a stale id (and, since numbers are reassigned each rebuild, silently strands whatever it gates — e.g. a mouse inside a building). Stair/cliff waypoints are reset via their dicts. **Structure-owned waypoints (building interiors, workspot `workNode`, rope-bridge chains) must register via `Graph.RegisterWaypoint` on creation and `UnregisterWaypoint` on teardown** — they have no other handle. Any new off-grid waypoint type must do the same.
- **Rebuild triggers**: `StructController.Construct()` (end of method, after all `UpdateNeighbors` calls) and `Graph.AddNeighborsInitial()` (startup/load). Cost ≈ 0.1–0.2 ms for a 100×50 map.
- **Usage as pre-filter**: `Graph.Navigate()` itself checks `SameComponent` before running A*, so all pathfinding automatically rejects unreachable targets in O(1). Individual search loops no longer need their own `SameComponent` calls.
- **`Nav.CanReachBuilding(StructType, r)`**: checks whether any building of a given type is in the same component — used by `PickRecipe`/`PickRecipeRandom` instead of a full A* scan.

### Edge dispatch (`EdgePolicy`)

Every edge whose cost or traversal differs from "plain horizontal walk" is governed by an `EdgePolicy` ([Assets/Model/EdgePolicy.cs](../Model/EdgePolicy.cs)). A single resolver, `Graph.ResolveEdgePolicy(from, to)`, returns the policy for any edge — or `null` for plain horizontal, which falls through to `ComputeEdge`'s default branch (road bonus + water modifiers). Both `GetEdgeInfo` (A*-facing, road bonus included) and `GetRawEdgeInfo` (locomotion-facing, road bonus excluded) collapse into the same `ComputeEdge(from, to, useRoadBonus)` helper.

Two dispatch sources:
- **Per-instance**: both endpoints carry the same policy reference (`from.edgePolicy == to.edgePolicy`). Today only `Elevator` uses this (its `ElevatorEdgePolicy` wrapper, set on both stop tile-nodes in the constructor). Future transit types (trains, trams) will follow the same pattern with their own wrapper subclasses.
- **Geometric**: ladder, cliff vertical / stair diagonal middle legs, and cliff/stair/workspot approach edges resolve to four singleton constants (`LadderPolicy.Instance`, `CliffPolicy.Instance`, `StairPolicy.Instance`, `WaypointApproachPolicy.Instance`) based on the `isWaypoint` flag and `dx`/`dy` between endpoints.

`EdgePolicy`'s contract: `GetEdgeInfo` (cost, length), `PreventFall` (default `true` — every special edge suppresses falls), `SuspendsLerp` (default `false` — true only for transits where an external system drives position), `OnApproach(animal, from, to)` (called every Nav.Move while this edge is the next step; idempotent), `OnPathCommit(animal)` / `OnPathRelease(animal)` (called once when a path scan adds/drops this edge — used by transits for tentative queue-depth reservations).

`Nav.MoveCore` reads the policy, applies `preventFall = policy.PreventFall`, calls `OnApproach`, and bails the lerp if `SuspendsLerp`. The `ridingElevator != null` early-return stays at the top of `MoveCore` (it's animal state, not edge state — and it must run before any policy lookup so a one-frame race during `Elevator.Destroy` where `edgePolicy == null` while `ridingElevator` is still set stays safe).

### Waypoint system (stairs and cliff climbs)

Both stairs and one-block cliff climbs are represented as **waypoint chains** — intermediate `Node` objects with fractional world positions, not backed by tiles. They are stored in `stairWaypoints` and `cliffWaypoints` dictionaries and rebuilt whenever nearby tiles change.

**Cliff climb** (one solid block beside a standable tile): `base → wp1 → wp2 → cliffTop`
- `wp1` at `(base.x + dir×0.25, base.y)` — 0.25 tiles from base
- `wp1 → wp2` vertical (governed by `CliffPolicy`)
- `wp2` at `(base.x + dir×0.25, base.y+1)` — 0.75 tiles from cliff top

**Stair** (stair tile): `left → entry → exit → right`, where entry/exit are 0.5-tile offsets from their endpoints; the diagonal entry→exit step is governed by `StairPolicy`.

`preventFall` in `Nav.Move()` is set from `policy.PreventFall` — true for every waypoint, ladder, and transit edge. Direct non-standable tile traversal no longer occurs — all such paths go through waypoints.

**Loaded-mouse footing.** Save data persists only an animal's raw `(x, y)` — no path/edge/nav state — so a mouse saved mid-traversal reloads off-grid. `AnimalController.SnapOntoGraph` (from `LoadAnimal`) snaps it onto the nearest standable node via `Graph.FindNearestStandableNode`: nearby standable tile nodes, plus a bounded BFS along any stair/cliff/bridge waypoint chain to its standable endpoint (cliff base/top, stair entry/exit, nearer bridge post). Without it the mouse drifts diagonally through the air on its first move (the lerp starts from the off-grid point) and can idle mid-air. Mice inside a building interior are skipped — they sit on non-standable dirt by design.

**No idling on rungs.** An idle mouse on *ladder-only footing* — standable solely because of a ladder/side-ladder, no ground beneath (`Graph.IsLadderOnlyFooting`, i.e. `GetStandability(x, y, allowLadder:false)` is false) — paths to the nearest stable tile rather than loitering (`AnimalStateManager.HandleIdle`). Covers the post-job-switch and post-load cases. Stairs and ground-level ladders are stable footing and exempt.

### Workspot waypoints (off-grid worker pose)

A third use of the waypoint system: structures that want their worker to stand at a fractional position rather than on an integer tile centre. Authored via `StructType.workSpotX/workSpotY` (see SPEC-data.md). The wheel uses this so the runner sits centred between the 2×2 footprint columns, slightly above the ground.

Mechanism:
- `Structure.workNode: Node` is set in the constructor. If a workSpot is declared, a fresh waypoint Node is created at `(anchor + (mirrored)workSpotX, workSpotY)` and edged bidirectionally to the bottom-row tile-node closest to `workSpotX` (tie → lower x). Otherwise `workNode = workTile.node` (today's behaviour, no graph change).
- Path-targeting code (`Nav.FindPathToStruct`, `Nav.CanReachBuilding`, `CraftTask`) routes to `s.workNode` instead of `s.workTile.node`. `WorkOrderManager.RegisterWorkstation`'s distance heuristic uses `workNode.wx/wy`.
- `Animal.target` is a `Node` (not `Tile`); the arrival snap in `AnimalStateManager.UpdateMovement` reads `target.wx/wy`. Works for both tile-backed and waypoint endpoints.
- Cleanup: `Structure.Destroy()` removes the waypoint's reciprocal edges and clears `workNode` so neighbour tile-nodes don't dangle.

**Critical load invariant**: waypoint registration runs in the `Structure` constructor — *not* `OnPlaced()`, which is gameplay-only. `Structure.Create()` runs the constructor on both gameplay and load paths. Standability isn't checked when picking the connecting tile because at load time the constructor runs in `SaveSystem` Phase 2 *before* `graph.Initialize()` (Phase 4) sets `node.standable`. The picked tile is implicitly standable post-placement (placement validates it), and Phase 4's `RebuildComponents` BFS picks up the workspot via its neighbour edge.

### Door + interior waypoints

The mechanism that gets mice from outside a building to inside it. Doors are pure graph topology — Task code never branches on "does this building have a door?", it just calls `nav.PathTo(building.interiorNodes[0])` and A* routes through the door automatically.

Buildings with `StructType.interiorTiles[]` register one off-grid `Node` per entry in their constructor (mirror of the workspot pattern, same load-invariant). Each interior node:
- Is positioned at the tile's centre (`worldX[i], worldY[i]`) — same world coords a mouse standing on the exterior tile-on-top would land at.
- Marks its tile with `Tile.interiorBuilding = this` (the owning `Building`). `Animal.insideBuilding` is a derived property reading `TileHere()?.interiorBuilding` — never cached, so it can't go stale when the animal is displaced by a fall / snap / load. A mouse is "inside" exactly while it stands on an interior tile.
- Is edged horizontally to interior-tile neighbours (Manhattan `|dx|=1, dy=0` adjacency only). Vertical access requires an explicit `ladders[]` entry — each ladder edges `(dx, dy)` up to `(dx, dy+1)`. Mice never climb through walls or ceilings unless an author declared it.

For each `doors[]` entry the constructor edges the interior node at the door's tile to the *existing* approach-tile graph node (the tile just outside the door, computed from `side`: left/right/top/bottom). A door is one bidirectional edge — no separate door node in v1 — so the door is purely a connection point, not a stopover.

Mirror handling lives entirely inside `Structure`: `dx` flips via `nx-1-dx`, `side: left ↔ right` swap, ladder dx flips, interior-tile dx flips. Callers read these fields raw.

Cleanup in `Structure.Destroy`: every interior node has its neighbour edges removed and its tile's `interiorBuilding` back-ref cleared — so `insideBuilding` self-corrects to null for any mouse left on those tiles. `Building.Destroy` nulls `homeBuilding` so future `FindHome` calls don't read a dangling ref; positional cleanup falls to the standard fall integration once the interior tiles return to empty air.

### Housing assignment (`Animal.homeBuilding` vs `homeTile`)

`Animal.homeBuilding` is the **authoritative reservation owner** — the `Building` whose `Reservable` holds this mouse's claim. `Animal.homeTile` is the path/approach tile (door's approach tile for doored housing, the building's anchor for any legacy doorless housing). The two are decoupled so multi-tile housing doesn't break: the home tile sits *outside* the footprint for doored buildings, where no `building` reference exists — using `homeTile.building` to mean "my home" would always be null.

`AtHome()` returns true when `insideBuilding == homeBuilding`. The legacy 1-tile-on-top path still works through a fallback that checks `TileHere() == homeBuilding.tile` for buildings with no `interiorNodes`. `HasHouse` checks `homeBuilding?.structType.isHousing` rather than any hardcoded structType name — adding a new housing tier is JSON-only.

`FindHome` (Animal.cs) iterates `StructController.GetStructures()` to find any reachable `isHousing` building with an available reservation slot, ranking by A* path cost to the building's first interior node (or `workNode` for legacy). Save/load persists `(homeBuildingX, homeBuildingY)` so reload restores the home reservation without re-running FindHome; `insideBuilding` is derived from the animal's position (see §Door + interior waypoints) and needs no persistence.

`EepTask` is door-agnostic: each resident paths to its own id-indexed interior tile, `home.interiorNodes[id % interiorNodes.Length]` (or `home.workNode` for doorless legacy housing), with no door-system knowledge. interiorNodes are edged together, so A* walks the mouse the whole way onto its tile — through the door and up an interior ladder if needed — and `EepObjective` does no repositioning (no end-of-path snap). The id index is stable, so a mouse always returns to the same spot. No persistent per-mouse slot reservation — collisions when two residents share `id % interiorNodes.Length` are accepted (purely cosmetic).

### Transit (elevators)

Capacity-1 vertical lifts that A* sees as a single graph edge. Implementation lives in [Elevator.cs](../Model/Structure/Elevator.cs) + [ElevatorEdgePolicy.cs](../Model/ElevatorEdgePolicy.cs); only the load-bearing contracts are documented here.

**Topology.** The two stop tiles (anchor and `y+ny-1`) are the footprint endpoints, made standable via `Structure.HasInternalFloorAt`. Middle column tiles stay non-standable. The constructor edges the two stops together with a shared `ElevatorEdgePolicy` reference — the "transit edge" — and `Graph.IsNeighbor` preserves it across `UpdateNeighbors` filtering despite non-adjacency.

**Edge cost.** `EstimatedTransitCost = travelTicks + queueDepth × avgTrip` (rolling 20-sample buffer, optimistic cold-start). Returns `+∞` when the network can't supply `MaxTickDemand` — A* drops it from the candidate set. `EdgePolicy.OnPathCommit` / `OnPathRelease` book queue-depth reservations on `pendingAnimals` so simultaneous planners see realistic wait.

**Power semantics.**
- Demand is **proportional to this tick's motion**: `CurrentDemand = PowerPerTile × min(PlatformSpeed, |targetY − currentY|)`. Both empty-cabin fetch and riding draw; Idle / Unloading draw 0. Total power for a trip = `0.5 × tilesTravelled` exactly.
- All gates (cost branch, Idle→Trip start, per-tick advance) use the **inclusive** `IsPowerAvailable` (raw supply + storage discharge ≥ `MaxTickDemand`), NOT the strict `IsBuildingPowered`. The strict check caused mid-trip platform freezes during normal allocator-rotation gaps; the inclusive check accepts a one-tick "advance without strict allocation" in pathological setups as a trade-off.

**Boarding-tile `preventFall = true` is load-bearing** even though the boarding tile is itself standable. There's a one-frame seam between `Elevator.Tick` setting `ridingElevator` and `Nav.Move` re-asserting preventFall via the riding branch; `ElevatorPlatform.Update` may drag the passenger into the non-standable chassis interior before `Animal.Update` runs for that mouse, and `UpdateMovement`'s fall check would read stale-false on the first riding frame without the policy's preventFall.

**Save/load.** `currentY` and the two history buffers (`recentTripTicks`, `recentEndToEndTicks`) persist via `StructureSaveData`. Dispatch state, queue, and pending reservations do NOT — they reset to Idle/empty on load (animals lose their tasks across save boundaries anyway).

### Rope bridges

Walkable rope curve strung between two endpoint posts. Placed by clicking two tiles — the first click drops a `BridgePost` at one end, the second click drops its partner and links them via a side-car `RopeBridge` entity that owns the nav waypoint chain and visual rope.

**Architecture: linked posts + side-car entity.**
- Each `BridgePost` is a normal 1×1 depth-2 `Structure` for every purpose except its lifecycle entanglement with the partner — placement, supply, construction, save data, decay, selection, and rendering all flow through the standard structure paths.
- `RopeBridge` is NOT a `Structure`. It's a plain side-car class holding the catenary's nav waypoint chain (N `Node`s edged into the `Graph`) and the sprite-chain visual. State that spans the gap lives here; state per-post lives on the posts.
- Each post stores its partner's coords (`partnerX, partnerY`); the bridge holds back-refs to both posts. Mining one post calls `RopeBridge.OnPostDestroyed`, which tears down the waypoint chain + visuals AND destroys the surviving post (a lone stake in the ground is meaningless).

**Placement validation** (`StructPlacement.CanPlaceTwoPoint`):
- `minDx ≤ |xA − xB| ≤ maxDx`, `|yA − yB| ≤ maxDy`. Defaults 3/20/5; see `StructType.minDx` etc.
- Both posts pass `CanPlaceHere` (standable, empty at depth 2, no blueprint).
- Every integer tile along the catenary's claim (one per x-column from `min(xA, xB)` to `max(xA, xB)`) must be non-solid and empty at depth 2. The claim function (`Catenary.ClaimedTiles`) is the SAME one the live bridge uses — placement validation never disagrees with what gets occupied.
- Failed clicks `Debug.LogWarning` the reason (`[bridge] reject: …`) so the player can diagnose without source diving.

**Catenary math** (`Catenary` static helper, pure):
- `y(t) = lerp(yLo, yHi, t) - sagFraction * |Δx| * sin(π * t)`, sampled monotonically left→right.
- `sin`-arch approximates true `cosh` catenary — visually identical at this scale, cheaper.
- Sag uses `|Δx|` only, NOT euclidean length. Drawing from euclidean length lets steep bridges dip below their lower endpoint — the rope would visually pass through the ground.
- Two-typed API: `YAt`, `WaypointPositions`, `HorizontalDelta` are float (used by both tile-coord placement and world-coord visuals); `ClaimedTiles` stays int (operates on the tile grid).

**Post sprite + mirror geometry.**
- `bridgeplank.png` / `bridgerope.png` / `bridgeropev.png` (optional vertical variant) live in `Sprites/Buildings/` with auto-generated `_n` normal maps — they all participate in the lighting pipeline.
- The post sprite (`ropebridgepost.png`) is asymmetric: pole on the LEFT half of the un-mirrored sprite. `Blueprint.Complete` flips the LEFT post (smaller x) mirrored=true so its pole sits on the tile's right side facing the bridge; the RIGHT post stays unmirrored. Mirror is geometry-driven for two-click bps and overrides whatever F-key state was on `BuildPanel.mirrored`.

**Rope attachment geometry.** `RopeBridge.AttachmentPoints` returns the world-coord endpoints used by every layer (visual + nav). Both endpoints are pulled 0.125 PAST the tile edge into the post so the rope visually grips the pole rather than floating off the corner:
- Left attachment: `(xL + AttachmentInset, yL − 0.5)` where `AttachmentInset = 0.375f`
- Right attachment: `(xR − AttachmentInset, yR − 0.5)`

**Nav waypoint chain** (built in `RopeBridge` ctor). Layout: `leftTile ↔ leftCorner ↔ wp[0] ↔ … ↔ wp[N−1] ↔ rightCorner ↔ rightTile`.
- **Anchor-corner waypoints** (`leftCorner` / `rightCorner`) sit at the tile edge above the rope start (`x = wxL or wxR`; `y = post.y`, which equals `catenary_y + WalkAboveRope` at the rope endpoint). Without them the chain would step diagonally from post tile-centre straight into the first interior catenary sample — mice walked through the post silhouette before the rope descent. Corner waypoints force horizontal-then-descend.
- **Interior waypoints**: `Catenary.WaypointPositions(wxL, wyL, wxR, wyR, sagFraction)` → roughly `2 × worldDx − 1`. Each has `wy = catenary_y + WalkAboveRope` (= `0.5f`) so the mouse stands on top of the rope, not through it. At the endpoint x's, this offset arithmetic resolves to `post.y` exactly, joining the corners seamlessly.
- All bridge-owned waypoints carry the **per-instance** `BridgePolicy`. **Per-instance, not singleton**: `Graph.IsNeighbor` and `Graph.ResolveEdgePolicy` use shared-reference equality (in `Navigation.cs`) — a singleton would falsely glue unrelated bridges' waypoints together across `UpdateNeighbors` rebuilds.
- Approach edges (corner ↔ post tile-node) do NOT carry the policy — they resolve to `WaypointApproachPolicy.Instance` via the waypoint-flag fallback in `ResolveEdgePolicy`. Same euclidean cost; lower coupling.
- `BridgePolicy.PreventFall = true` so a mouse mid-chain doesn't fall when the integer tile below the waypoint isn't standable.

**Load-time waypoint ordering.** `BridgePost` constructors run in Phase 2; `OnPlaced` is gameplay-only, so bridges don't materialise during load via the live path. Instead, `RopeBridge.PairAllAfterLoad()` runs between Phase 3 and Phase 4 (`SaveSystem.cs`, just before `graph.Initialize()`) so the waypoint chain enters the initial `RebuildComponents` sweep. Without this, mice can't path across a saved bridge until something else perturbs the graph.

**Save format.** Bridges are NOT persisted as a separate top-level list. Each `BridgePost`'s `StructureSaveData` carries nullable `partnerX, partnerY`; `RopeBridge.PairAllAfterLoad` rebuilds the side-car entity from the matched pair. Mid-construction blueprints persist their second endpoint via nullable `BlueprintSaveData.x2/y2`. The per-post mirror flag round-trips through the existing `StructureSaveData.mirrored` field, so loaded posts keep the geometry-correct asymmetry.

**Sprite-chain visual** (`RopeBridge.BuildVisual`). Replaces an earlier LineRenderer implementation — LineRenderers don't carry normal maps and render at sub-pixel widths, both wrong for pixel art. All three layers below are regular `SpriteRenderer`s through `SpriteMaterialUtil.AddSpriteRenderer`, picking up lighting + auto-normal-map for free. `GameObject[]` arrays for each layer are cached on the bridge so future wind physics can move individual planks without rebuilding.
- **Walking line**: planks at every `PlankSpacing = 0.5f` world units, upright (no tangent rotation — the user found the rotated version visually busy). Endpoint planks (indices 0 and last) are **skipped** so a plank doesn't sit under each pole. Rope segments fill the gaps between consecutive samples, rotated to the local tangent and x-scaled so the rope sprite's native width covers the gap.
- **Handrope**: rope segments only (no planks), at the same x samples shifted by `HandropeHeight = 0.625f`.
- **Connectors**: vertical rope sprite at every `ConnectorEveryNPlanks = 3` planks, y-scaled to `HandropeHeight`. Uses dedicated `bridgeropev.png` when present, falls back to `bridgerope.png` rotated 90°.

**Sort-order layering**: planks 39 > horizontal ropes 38 > vertical connectors 37. Posts at depth-2 default (~40) so they punch through the rope at attachments. Mice at animal sort buckets (~50) render in front of planks — they visually walk on top.

**Construction flow**:
- `Blueprint` for two-click placements carries nullable `x2/y2` and claims BOTH post tiles in its ctor (one Blueprint instance referenced from both tiles' `structs[depth]` slot). Visual: one ghost spawned at the anchor + a partner ghost at `(x2, y2)` mirrored opposite. Frame overlay duplicated for the partner tile via `SpawnFrameSr` helper.
- Cost scales linearly with `|Δx|`: total = `ncosts × |Δx|`, authored per-tile-of-span.
- On `Complete()`, the blueprint calls `StructController.Construct` twice — once per post — with the partner's coords swapped and the geometry-driven mirror flag. The second post's `OnPlaced` finds the first BridgePost already in place and spins up the `RopeBridge`.
- `Blueprint.IsSuspended` was extended to also check the partner tile's standability when `IsTwoClick` — without that, mining the support out from under the partner mid-construction silently completes onto a non-standable tile.
- `Blueprint.FootprintTiles` and `Blueprint.CenterApproachTiles` are virtual enumerations consulted by `Nav.PathToOrAdjacentBlueprint`. Two-click bps yield both post tiles, so haulers route to whichever post is closer.

**First-endpoint UX** (`MouseController.UpdateFirstEndpointGhost`). After the first click, a translucent ghost post sits at the chosen tile until the second click commits. Cursor x relative to the firstEndpoint flips the ghost's `flipX` so it shows the same mirror the built post will have:
- `cursor.x > firstEndpoint.x` → firstEndpoint becomes the LEFT post → mirrored=true
- `cursor.x < firstEndpoint.x` → firstEndpoint becomes the RIGHT post → mirrored=false

Failed second clicks clear `BuildPanel.firstEndpoint` so the ghost goes away; `CanPlaceTwoPoint` already logged the reason.

**Known v1 limitations / phase-3 polish:**
- No wind physics yet; `RopeBridge.BuildVisual` is called once. Future wind work would tick segment positions on the cached plank/rope GameObject arrays.
- Connector spacing isn't perfectly anchored — it lands on every Nth plank rather than at uniform world-x positions, so the gap pattern shifts slightly with `PlankSpacing`. Looks fine; calling it out for future tweakers.

---

## Inventory System

Six inventory types:

| Type | Slots | Stack Size | Decay Rate | Notes |
|------|-------|-----------|-----------|-------|
| Animal | 5 | 1000 fen | none | General-purpose carry inventory |
| Storage | varies | varies | normal | `allowed` dict restricts item types |
| Floor | 4 | 1000 fen | 5× normal | Created/destroyed dynamically; up to 4 item types can share a tile |
| Equip | 1 | varies | normal | Animal equip slots (food, tool, clothing, book). Items may also opt into use-based wear via `equipDecayRate` — ticked once per `HandleWorking` call, on top of passive `decayRate`. See §Equip decay below. |
| Market | varies | varies | none | Market building only; set via `SetMarket()` on a Storage inv |
| Fuel | 1 | varies | none | Internal building resource (torch wood, furnace coal). No sprite, no tile. See below. |

### Inventory ownership

- **`tile.inv`** is always a Floor inventory or null. Never Storage/Market.
- **Storage/Market inventories** live on `building.storage`. Created in Building constructor, auto-registered with `InventoryController.byType` via the Inventory constructor. Not placed on `tile.inv`.
- **`FindPathToInv`** iterates `InventoryController.byType[type]` — not tiles — so pathfinding works for all inventory types regardless of tile ownership.
- **`FindPathToStorage`** returns `(Path, Inventory)` — callers destructure to get both the navigation target and the storage inventory directly.
- **`FindPathItemStack`** returns `(Path, ItemStack)` using the inventory from `FindPathToInv`, not `path.tile.inv`.

### Fuel Inventory (`InvType.Reservoir`)

Created by `Building` constructor when `structType.hasFuelInv = true`. Fields:
- Not tied to a tile position (x/y = building anchor, used for nav targeting only)
- No sprite, no decay
- All item types accepted (no `allowed` filter at inv level)
- Not searchable as a haul source (`GetItemToHaul`, `HasItemToHaul`, `GetStorageForItem`, and `FindPathItemStack` all exclude Fuel invs)
- Registered with InventoryController for GlobalInventory tracking

WOM registers a standing `SupplyBuilding` (priority 3) order when a fuel building is placed (via `Building.OnPlaced()`). `isActive` suppresses the order when `building.reservoir.NeedsSupply()` is false. Haulers fulfill it via `SupplyFuelTask` + `DeliverToInventoryObjective`. All runtime state lives in the `Reservoir` component (`building.reservoir`), not on Building directly.

JSON fields on StructType:
- `hasFuelInv: bool` — opt-in
- `fuelItemName: string` — group or leaf item (e.g. `"wood"`)
- `fuelCapacity: float` — max fen (liang in JSON, × 100 in `OnDeserialized`)
- `fuelBurnRate: float` — liang/day consumed; `Reservoir.Burn()` converts to fen/sec using `World.ticksInDay`

`LightSource` holds a back-ref to its owning `Building` (`ls.building`, set in `Building.cs` when the light is attached). While `building.disabled` is true, `LightSource.Update()` skips `Reservoir.Burn()` and sets `isLit=false` — so disabling a torch immediately stops fuel consumption and emission. Refuel hauls are also gated off via the WOM `isActive` check, so a disabled fuel building neither receives nor consumes fuel.

`isBuilding: true` on StructType makes StructController use the `Building` class for any depth (e.g. foreground torches at depth 2). `tile.building` (= `structs[0] as Building`) is still depth-0 specific; fuel buildings at other depths are accessed directly via task/WOM references.

- Items decay over time (Floor fastest; Animal/Market never; Equip at normal rate)
- **Rain-soaked floor invs** decay at 2× their already-fast Floor rate. `WeatherSystem.OnHourElapsed` sweeps every tile while raining (rain only — gated on `isRaining && temperature ≥ snowThresholdC`); any tile carrying a Floor inv that is `World.IsExposedAbove` gets its `Inventory.wetUntil` refreshed to `World.timer + ticksInDay/4`. `Inventory.Decay` doubles its multiplier while `wetUntil > World.timer`. The wet timer doesn't dry early — once a pile has been soaked, it stays at 2× decay for the full quarter-day even if a roof goes up before then. `wetUntil` is persisted on `InventorySaveData`.
- **Discrete items** (`Item.discrete = true`, e.g. tools, stools): stored/moved in whole-**unit** multiples. A unit is `Item.unitFen` fen — `unitWeight` liang (JSON-authored, default 1) × 100. Tools weigh 1 liang/unit (`unitFen` 100); a stool weighs 3 (`unitFen` 300), so a fixed-fen slot holds proportionally fewer — weight and bulk are deliberately one number.
  - **Capacity**: `ItemStack.EffectiveCapacity` floors a stack's raw `stackSize` down to a whole-unit multiple (trailing remainder is dead space); `AddItem` / `FreeSpace` / `HasSpaceForItem` all use it.
  - **Decay/display**: decay removes whole units only; display shows the unit count (`fen / unitFen`). Depositing a non-unit-multiple quantity logs a warning.
  - **Authoring**: recipe/cost JSON authors discrete quantities as a **unit count**, not liang (see Fen/Liang below). `Db` warns at load if a discrete item's `unitFen` exceeds the largest storage stack (it would be un-storable).
- `allowed` dict filters what item types a storage accepts (all allowed by default for other types)
- `Reservable` (capacity-based) prevents multiple animals targeting same resource. Has two fields: `capacity` (hard max from JSON) and `effectiveCapacity` (player-adjustable; defaults to `capacity`); `Available()` gates on `effectiveCapacity`. **Not** created for workstation buildings — WOM Craft orders own their reservation directly.
- `Produce()` adds to inventory and global inventory simultaneously; `MoveItemTo()` moves between inventories without touching global inventory
- `AddItem()` is private — always use `Produce` or `MoveItemTo` externally
- **Group-item wildcard**: `Quantity`, `ContainsAvailableItem`, `GetItemStack`, `AvailableQuantity`, and `MoveItemTo` all expand group items to their leaf descendants (`MatchesItem` helper). Passing `"wood"` to any of these matches oak/maple/pine transparently. `AddItem` and `GlobalInventory.AddItem` reject group items with a `LogError` — only leaf items may physically exist in inventories. `MatchesItem(candidate, query)` is `public static` — use it externally when matching a leaf iq.item against a group cost.item (e.g. in `DeliverToBlueprintObjective`). `MoveItemTo` uses the private `GetLeafStack` (not `GetItemStack`) for group resolution: `GetLeafStack` does not require `Available()` (the caller already holds the reservation) and picks the leaf type with the highest combined quantity, then the smallest individual stack of that type.
- **`InventoryController.byType`**: `Dictionary<InvType, List<Inventory>>` maintained alongside the flat `inventories` list. Use for type-filtered lookups (e.g. iterate only Storage invs) instead of tile scans. All add/remove/type-change paths go through `AddInventory`, `RemoveInventory`, `MoveInventoryType`.
- **`ValidateGlobalInventory()`**: sums all registered inventory stacks and compares against `GlobalInventory.itemAmounts`; called at end of save load. `LogError`s any mismatch.

### Item Falling

When a tile or building change reduces standability, items on the tile above that is no longer standable fall straight down to the nearest standable tile.

- **Trigger**: `StructController.Construct()` and `Structure.Destroy()` both call `World.FallIfUnstandable(x, y+1)` after updating the nav graph. This covers tile mining, building placement, and building removal.
- **`World.FallIfUnstandable(x, y)`**: no-op if the tile has no items or is still standable; otherwise calls `FallItems`.
- **`World.FallItems(tile)`**: scans downward from `tile.y−1` for the first standable tile (`landing`). Moves all stacks via `MoveItemTo` (no GlobalInventory double-count). Fires `World.OnItemFall` to trigger the fall animation (subscribed in `WorldController`). Any items that can't fit in the landing inventory are subtracted from GlobalInventory and logged as a warning before the source inventory is destroyed. Same applies if no landing tile exists at all (e.g. items at y=0).
- **Mixing**: `PutOnFloor` / `ProduceAtTile` prevent different item types from being placed on the same floor tile normally. `FallItems` bypasses this deliberately — a floor tile can hold up to 4 types after a fall.
- **GlobalInventory**: `MoveItemTo` is used (not `Produce`) so no double-counting occurs. Lost items are explicitly removed from GlobalInventory before destruction.
- **Fall physics constants** (`World.cs`): `fallSecondsPerTile = 0.4f` (time to fall one tile), `fallGravity = 2 / fallSecondsPerTile²` (12.5 tiles/s²). Both item animation (t² ease-in over `fallSecondsPerTile × dist` seconds) and mouse falling (velocity accumulation) are derived from these constants. Animation spawned in `WorldController.ItemFallAnimCoroutine`.

### Equip Slots

Each animal has three `InvType.Equip` inventory instances (1 stack each, registered with InventoryController for GlobalInventory tracking, no sprite, decay at normal rate):

| Slot | Field | Capacity | Purpose |
|------|-------|----------|---------|
| Food | `foodSlotInv` | 500 fen (5 liang) | Carries food for eating |
| Tool | `toolSlotInv` | 1000 fen | Equipped tool (work speed bonus) |
| Clothing | `clothingSlotInv` | 200 fen | Equipped clothing (temperature comfort bonus) |
| Book | `bookSlotInv` | 100 fen (1 liang) | Carries a book during research / leisure reading. Class-restricted: only accepts `ItemClass.Book`. See SPEC-books.md for the borrow/return flow. |

**Clothing system**: `Db.clothingItems` lists all items whose parent chain includes `"clothing"`. `FindClothing()` in `ChooseTask()` equips one clothing item into `clothingSlotInv` when idle (after tool equip, before work orders). `Happiness.UpdateComfortRange()` adjusts `comfortTempLow`/`comfortTempHigh` by ±3°C when any clothing is equipped, and additionally widens `comfortTempLow` by up to 5°C from the fireplace warmth buff. Clothing items are discrete (like tools) and decay at normal rate in equip slots.

**Clothing overlay**: equipped clothing renders as a child `SpriteRenderer` ("ClothingOverlay") on the Animal prefab, assigned to `AnimationController.clothingRenderer`. Sprites loaded by item name from `Resources/Sprites/Animals/Clothing/{itemName}/` (`idle`, `walk`, `eep` — walk and eep fall back to idle if missing). `AnimationController.UpdateClothingOverlay()` swaps sprite on state change; `LateUpdate` syncs `flipX`. Adding a new clothing visual = add sprites to a new folder, no code changes.

**Food acquisition flow:**
1. Animal gets hungry → `FindFood()` checks `foodSlotInv` for room
2. If room exists, creates `ObtainTask(food, amount, foodSlotInv)` — item goes to slot, not main inventory
3. `HandleNeeds()` eats from `foodSlotInv`: full meals (≥100 fen) restore `foodValue` and grant full satisfaction; partial meals (remaining fen) scale both nutrition and satisfaction proportionally

**Key methods on `Animal`:**
- `Unequip(slotInv)` — moves slot contents back to main inventory (leftover stays in slot if inv full)

**`ObtainTask` / `FetchObjective`** both accept an optional `Inventory targetInv` to route pickup into an equip slot instead of main inventory.

**Partial fills:** Equip slot fetches (`targetInv != null`) accept whatever quantity was available on the source tile and complete without retrying across multiple tiles. This is intentional — food/tools are useful at any amount, and the mouse will re-fetch next time it's hungry/unequipped. Crafting fetches (`targetInv == null`) do retry across tiles until the full amount is collected.

### Blueprint inventory

`Blueprint` has its own `Inventory inv` (`InvType.Blueprint`, not registered with InventoryController — no decay, no tick overhead). Materials are delivered into it via `MoveItemTo` from the animal's inventory. On `Complete()`, `inv.Produce(item, -qty)` is called for each cost item to decrement GlobalInventory (the items were already counted in GlobalInventory when originally harvested). On cancel (`BuildPanel.Remove`), materials are returned to the floor via `MoveItemTo`.

One slot per cost item (`stackSize = cost.quantity`). Because a slot can only hold one leaf type, `LockGroupCostsAfterDelivery()` is called after the first delivery of each group cost: it updates `blueprint.costs[i].item` from the group (e.g. "wood") to the specific leaf delivered (e.g. "pine"). Subsequent `SupplyBlueprintTask` initializations read the locked type and fetch only that leaf, avoiding slot conflicts.

Stacks are bound to their cost slot via `Inventory.slotConstraints[i] = costs[i].item` (set in the `Blueprint` ctor). `Inventory.AddItem` consults this when adding (positive quantity, non-`force`) and skips any stack whose constraint doesn't match the incoming item via `MatchesItem` — so a small-quantity cost item delivered first can't squat in a slot sized for a different, larger cost. The constraint stays as the original (often group) item even after `LockGroupCostsAfterDelivery` swaps `costs[i].item` to a leaf — the locked leaf still matches the group constraint, so no update is needed. Subtraction (`quantity < 0`) and `force: true` paths bypass the filter so misrouted items can still be removed and overflow returns can land anywhere.

`SaveSystem.RestoreBlueprint` routes each saved stack to the matching cost slot (by `Inventory.MatchesItem(item, bp.costs[j].item)`) rather than trusting the saved stack index. This heals saves written before slot-constraint routing existed, where order-dependent delivery could leave items in the wrong stack.

`SupplyBlueprintTask.Initialize()` commits to a specific leaf *before* pathfinding via `PickSupplyLeaf`: it walks the group's leaf tree and picks the leaf with the highest `GlobalInventory` quantity. This prevents collecting a mix of leaf types that would lock the blueprint to whichever happened to be delivered first — potentially a scarce one (e.g. 2 oak when 20 pine is available). `DeliverToBlueprintObjective` uses `Inventory.MatchesItem(iq.item, cost.item)` for the cost-slot lookup so a leaf `iq.item` correctly matches an unlocked group `cost.item`.

### Equip decay

Items in Equip slots wear via **two parallel paths** that both add into the stack's `decayCounter`:

1. **Passive `decayRate`** — ticked every standard inventory decay cycle (Equip `InvType` multiplier is `1×`, same as Storage). Use this for *time-based* wear: things that lose value just by sitting in/around the animal. **Clothing** uses this — a shirt frays from being worn day in day out, regardless of what the mouse is doing.
2. **`equipDecayRate`** — ticked once per `HandleWorking` call by `AnimalStateManager.ApplyEquipDecay`. Same per-year units as `decayRate` and shares the same accumulator. Use this for *use-based* wear: things that wear because the work itself wears them. **Tools** use this — an idle mouse holding a copper axe doesn't dull the axe; an active mouse does.

Both contributions sum into one wear pool, so a tool item could in principle set both fields if the design called for it.

`ApplyEquipDecay` calls `EquipDecay(1f)` on every Equip slot (tool, clothing, food, book) — items with `equipDecayRate == 0` are no-ops, so opt-in is purely data-driven via `itemsDb.json`. Likewise, items with `decayRate == 0` skip the passive path.

Current values (per-year units):
- **Tools** (equipDecayRate, work-based): stone 7.5, copper 5.0, bronze 4.0. At ~50% mouse-working time this works out to roughly 6 / 10 / 12 in-game days per tool. Passive `decayRate: 0.05` is storage-like — gear loses only a trickle when idle.
- **Clothing** (decayRate, 24/7-while-equipped): 4.0 at the group level. The original 2.0 was bumped to 4.0 because the use-based-tools framing made the previous wear feel too gentle.
- **Food and books** don't currently set `equipDecayRate` — food has its own consumption mechanic (eating), books haven't been given a wear concept yet.

**When to use which**: ask "does this wear out from time, or from being involved in work?" Time → `decayRate`. Work → `equipDecayRate`. Hybrid → both.

---

## Water System (added 2026-03-19, rendering overhauled 2026-03-20)

`Assets/Controller/WaterController.cs` — singleton MonoBehaviour; must be in the scene.

**Data**: `Tile.water` (`ushort`, 0–160). `WaterController.WaterMax = 160` = fully filled tile. The 10× internal scale (instead of 0–16) eliminates the integer truncation dead zone in the spread formula (`diff/2 == 0` when `diff == 1`), which would otherwise leave water visibly stuck in a staircase. The dead zone shrinks to 1/10 of a visual unit — sub-pixel. Only non-solid tiles hold water.

**Simulation**: `TickUpdate()` called every 0.2 s from `World.Update()`. Four passes, bottom-to-top:
1. **Fall** — pour water straight down (`flow = min(tile.water, WaterMax - below.water)`).
2. **Spread** — equalize with one horizontal neighbor (`flow = (tile.water - neighbor.water) / 2`). Direction alternates left/right each tick to avoid directional bias.
3. **Look-ahead equalization** — fixes diff-1 slopes that Pass 2 can't resolve (truncates to 0). When a tile is exactly 1 unit below its sweep-direction neighbor, scans further for a tile at +2 or higher and pulls 1 unit from it.
4. **Look-ahead drain for residual** (dual of Pass 3) — sub-pixel residual (`water < ResidualBandMax = 5`, i.e. tiles that render as 0 px) picks a random direction via `Rng` and scans across plateau tiles at equal water for a strictly-lower target, pushing 1 unit there. Mirrors Pass 3's plateau-walking structure; the difference is push-from-residual vs pull-to-low-tile, and random direction vs sweep direction (alternation would just shuffle residual back and forth across symmetric shelves). Random walks abandoned residual off open edges so it doesn't get re-grown into a visible pool by the next rain (`RainReplenish` gates on `water > 0`, not on the render threshold). Sealed flat shelves still trap residual but it stays invisible.

Volume is conserved exactly (integer math, explicit transfers).

**Rendering**: GPU shader pipeline — zero per-frame CPU work.
- `Assets/Lighting/Water.shader` (`Water/WaterSurface`) — URP 2D unlit sprite shader. Reads a 1-byte-per-pixel R8 surface mask, returns: transparent (0) / shimmer lerp (0.5) / surface highlight (1.0). Per-pixel shimmer uses `_Time.y` (frame-rate driven on GPU).
- Surface mask texture (`TextureFormat.R8`, 1600×800 for 100×50 world): rebuilt on the CPU every 0.2 s (sim tick only). Encodes: `0`=no water, `127`=interior water, `255`=surface pixel (any of 8 orthogonal+diagonal neighbours is open air — non-solid, non-water). Water touching solid walls is NOT highlighted.
- World-spanning `WaterSprite` GameObject: 1×1 white pixel sprite at PPU=1, scaled to `(nx, ny)` Unity units, placed at `(−0.5, −0.5)`. sortingOrder=2. Must be on the **`Water`** Unity layer, excluded from `LightFeature` litLayers and SkyCamera culling mask.

**Pump draining**: `PumpBuilding` (`Assets/Model/Structure/PumpBuilding.cs`) is a depth-0 Building subclass for the pump (id 140, nx=2). It overrides `ConditionsMet()` to suppress the WOM Craft order when the source tile has no water. After each completed craft round, `AnimalStateManager` calls `pump.DrainForCraft()`, which subtracts `WaterDrainPerRound` units from the tile at `(x+1, y-1)` (directly below the pump head). Drain only happens when a mouse is actively pumping — not on a passive timer. `WaterDrainPerRound` is a private const; see the file for the current value.

**Mouse speed**: Water on either endpoint of a horizontal nav edge doubles the A* edge cost (→ 0.5× speed). Applied in `Graph.GetEdgeInfo()`.

**World gen**: `WorldController.GenerateDefault()` seeds `water=WaterMax` at y=9 for x=[0,3] and x=[30,40], first clearing those tiles to empty.

**Save/load**: `WorldSaveData.waterLevels` — flat `byte[]`, index `y * nx + x`. Omitted (null) if all-dry. Restored in `SaveSystem.ApplySaveData()` before tile types are applied.

**ClearWorld**: `WaterController.ClearWater()` zeros all `tile.water`, clears the surface mask texture, and calls `UpdateSurfaceMask()`.

## Soil Moisture (added 2026-04-22)

Distinct from liquid `tile.water` — moisture represents damp **soil**. Lives on `Tile.moisture` as a `byte` (0–100 percent) and is only meaningful on **solid** tiles (dirt/stone). Air tiles keep their moisture at 0 by convention; all moisture sweeps skip non-solid tiles. Drives plant growth gating; see SPEC-data.md `plantsDb.json` for the per-plant comfort-range fields.

**Per in-game second** (1 s real-time, dispatched from the 1 s block in `World.Update` — both run before `PlantController.TickUpdate` so plants' `Grow()` sees freshly-updated soil):
- **Rain uptake** (`MoistureSystem.RainUptakePerSecond()`, soil whose immediate tile-above isn't a ceiling): gains `round(rainAmount × MoistureRainGainPerHour / TicksPerInGameHour)` (currently 10 at full rain — i.e. 100/h spread over 10 one-second slices). "Ceiling" = tile directly above is solid ground OR carries any `solidTop` structure; see `MoistureSystem.CapsSoilFromAbove`. This is *not* a full sky-trace — a detached overhang higher up doesn't block rain, only the tile immediately at y+1 matters. Simpler + avoids the asymmetry where a single ceiling layer would slip through a sky-trace (its tile type is non-solid; its `solidTop` struct flag would be missed).
- **Water-neighbour seep** (`MoistureSystem.SeepPerSecond()`): each solid tile with moisture headroom picks its wettest 4-orthogonal water neighbour and absorbs up to `MoistureSeepMoisturePerSec` (currently 10) moisture from it. Yield is `MoistureSeepGainPerWater` (currently 20) moisture per 1 unit of water drained — i.e. each unit of water in the source goes twice as far as it does in rain/evaporation accounting.

  *Amortisation*: because `tile.water` is integer, the source can't lose half a unit per tick directly; the cost is amortised via a per-soil-tile debt accumulator (`_seepDebt`) that holds absorbed-but-unpaid moisture and cashes out 1 water from the source whenever it exceeds `GainPerWater`. If the source runs dry, absorption caps so leftover debt stays below one whole water unit (no free moisture).

  Net effect: dry soil still saturates from a wet neighbour in ~10 s real-time (rate unchanged), but the source water drains at roughly half the previous rate — a 1-tile pond feeding 4 solid neighbours empties in ~80 s under load. Pump-irrigated farms stay wet only while the pump keeps refilling water.
- **Overlay growth + health state** (`OverlayGrowthSystem.Tick()`): ticks live decoration on overlay-bearing tiles (today: grass on dirt). Walks every overlay-bearing tile (no global cold early-exit — the death paths must fire below the growth gate). **Depth gate**: tiles deeper than `MaxDepthBelowSurface` (default 6) below the original surface skip both stages — keeps grass from spreading down a mineshaft into the underground decoration zone (mushrooms/moss in `FlowerController`). Shared with `WorldGen.PopulateOverlays` so seed-time and runtime use the same cutoff. **Fully-buried tiles** (cardinal mask `0xF`, all four neighbours solid) skip both stages: buried grass is treated as preserved in place — insulated from surface weather, frozen at whatever state it carried when it got buried. Re-exposing the tile (mining a neighbour) makes the next Tick pick it up normally. Two stages per non-buried tile:
  1. **Health-state transition** (skipped on bare tiles, since there's no decoration to wilt). One Rng roll per tick — death rolls at `DeathChancePerSecond` (≈ 10 s steady-state expected); recovery rolls at the slower `GrowChancePerSecondPerSide` (~½ in-game day):
     - `temp < -1°C` → Dead (roll, from Live OR Dying; overrides Dying gate)
     - `temp < 2°C` OR `moisture == 0` → Dying (roll, only from Live — Dead doesn't downgrade to Dying)
     - Dying or Dead, `temp > 5°C` AND `moisture > 40` → Live (roll, scales with fresh-grass growth so a Dead patch takes about as long to revive as bare dirt takes to sprout)
     - Sudden deep freeze rolls Live → Dead direct; gradual cooling can walk Live → Dying → Dead.
  2. **Per-side growth** (Live tiles only, gated on `moisture > 40` AND `temp > 5°C`): for each non-grassy side that's exposed and non-flooded, rolls `GrowChancePerSecondPerSide ≈ 1/120` (~½ in-game day expected wait per side). Only L/R/U sides — never D (no underside grass).

  Writes `tile.overlayMask` and `tile.overlayState` via property setters so `WorldController.OnTileOverlayChanged` redraws automatically. The renderer appends `_dying` / `_dead` to the atlas name based on state (`grass` → `grass_dying` → `grass_dead`); atlas geometry is identical across variants. Reproducible via `Rng` (deterministic gameplay RNG). See SPEC-rendering "Tile overlays" for the data side.

**Per in-game hour** (10 s real-time, via `MoistureSystem.HourlyUpdate()`, called from `WeatherSystem.OnHourElapsed()`). Single snapshot-and-sweep so no step biases by sweep direction, followed by a plant-iteration pass:
- **Soil-to-soil diffusion** (all solid tiles): pull `round(diff × MoistureDiffusionPerHour)` toward the wettest solid neighbour's snapshot value, where `diff = maxNeighbour − cur`. One-way (never lowers). Currently `MoistureDiffusionPerHour = 0.05` (5%/h). Approximates capillary spread — a water-adjacent stone wall's moisture slowly propagates inward, a rained-on surface row slowly wets the column below.
- **Evaporation** (same "not capped" gate as rain): `−MoistureEvaporationPerHour` (currently 1). Not temperature-scaled. Clamped ≥ 0. Capped soil (under buildings / stone / platforms) holds baseline without drying, so cave farms / deep nurseries / covered growhouses stay viable without irrigation.
- **Plant passive draw**: each live plant pulls `round(plantType.moistureDrawPerHour)` from the soil tile directly below (clamped ≥ 0; no penalty when undersupplied — only the advancement cost gates growth). Default 2; overridable per `plantsDb.json` entry via the `moistureDrawPerHour` field.

**Worldgen seed**: `WorldGen.SeedMoisture` sets every solid tile to `StartingMoisture = 50` at world generation so plants can grow from turn 1. Surface soil then drifts from this baseline under rain/decay; underground holds unless a water neighbour bumps it higher.

## Plant Growth

Plants advance through discrete growth stages, stored as `growthStage` on `Plant`. Ticked once per in-game second by `PlantController.TickUpdate → Plant.Grow(1)`.

**Gates on every tick**:
1. **Comfort**: `plantType.IsComfortableAt(soilTile, weather)` — ambient temp AND the moisture of the tile directly below the plant must fall within the JSON-authored `[tempMin, tempMax]` / `[moistureMin, moistureMax]` ranges. Out-of-range returns early, freezing both age and stage.
2. **Stage advancement** (only when the tick would push `growthStage` higher): costs `2 × plantType.moistureDrawPerHour` from the soil tile below. Can't afford → freeze.
3. **Height extension** (only when advancement lands in a new height band): every new tile above must be non-solid with `structs[0] == null`. Any blocker → freeze.

**Height mechanic** (multi-tile plants with `maxHeight > 1`):
- Max stage = `4 × maxHeight − 1`. One height tile per 4 growth stages.
- `height = 1 + growthStage / 4`. Derived, not persisted — rebuilt on load by `Plant.RebuildExtensionTiles()`.
- When a stage crossing triggers height increase, Plant claims the tile at `y + h` via `tile.structs[0] = this` and spawns a child `GameObject` + `SpriteRenderer` for rendering. Placement code (`StructPlacement.CanPlaceHere`, `StructController.Construct`) sees this and blocks new *depth-0* placements there. Other depths (shafts, roads, foreground decorations) are allowed to coexist with the plant — visual clipping inside the trunk is accepted as a trade-off for the freedom.
- Rendering: the topmost occupied tile shows `g{stage % 4}`, every tile below shows `g4` (stalk continuation). Bamboo requires `g0..g4` in `Sprites/Plants/Split/bamboo/`.
- Harvest yield scales linearly with `height` at harvest time. The harvest work order is gated on `Plant.IsDoneGrowing()` — a multi-tile plant is auto-harvested only once it reaches its full attainable height (max stage, or frozen-blocked at a band top), so the height-scaled yield is actually delivered rather than lost to an early stage-3 cut. Harvest releases all extension tiles and resets `age = 0`, `growthStage = 0`.
- `Plant.Destroy()` releases all extension tiles.

**`Mature()` shortcut** (worldgen): sets age + stage directly to max, calls `RebuildExtensionTiles()` which claims as many upper tiles as the geometry allows. Skips the moisture advancement cost (fresh soil isn't guaranteed wet yet) and silently tops-out below `maxHeight` if the world above the anchor is blocked.

**Plant growth gate**: a plant occupies an air tile; `Plant.Grow()` reads moisture from the **solid tile directly below** and calls `plantType.IsComfortableAt(soilTile, weather)`. Returns early (skips the age increment) if `WeatherSystem.temperature` or `soilTile.moisture` is outside the plant's authored `[tempMin,tempMax]` / `[moistureMin,moistureMax]`. Null bounds = "no limit" on that side, so a plant with no ranges grows unconditionally (back-compat for content authored before this system). If there is no tile below (world bottom edge), the moisture check is skipped — not failed.

**Save/load**: `WorldSaveData.moistureLevels` — flat `byte[]`, index `y * nx + x`. Omitted (null) when every tile is 0, mirroring `waterLevels`. Restored in Phase 1 of `ApplySaveData` alongside water.

**ClearWorld**: `MoistureSystem.Clear()` zeros `tile.moisture` on every tile. Called from `WorldController.ClearWorld()` right after `WaterController.ClearWater()`.

**InfoPanel display**: `TileInfoView` shows `moisture: N/100` on any solid tile. `StructureInfoView` (for a Plant) shows `temp: now°C  comfort: lo–hi°C` and `moisture: now/100  comfort: lo–hi`, with the current moisture read from the soil tile below — same source the growth gate uses. Null comfort bounds render as `—`.

### Plant slowdown estimation

When balancing a plant's `[moistureMin, moistureMax]` comfort window against `growthTime`, model soil moisture as a four-phase cycle (dry drain → dry floor → rain ramp → rain cap) driven by the rain Markov chain in `WeatherSystem`. Compute `happyFraction` = hours-inside-comfort-window per cycle / total cycle hours; effective grow time ≈ `growthTime / happyFraction`.

**Key takeaway**: an `mHi < 100` cap forfeits the entire "rain cap" phase as unhappy, which dominates the slowdown. As of 2026-04-28 every default plant sets `moistureMax = 100` for this reason; only `moistureMin` is varied to differentiate species. Trees `[10, 100]` come out ~0.7× speed; a `[20, 80]` plant ~0.32× speed.

Caveats: steady-state only (first cycle on a fresh world is worse — `StartingMoisture = 50` and weather starts clear); temperature gate is independent and multiplies on top; the stage-crossing cost (`2 × moistureDrawPerHour`) is effectively free vs. the comfort gate as long as `mLo ≥ 2 × moistureDrawPerHour`.

## Fermentation processors

A `Processor` is an optional `Building` component (sibling to `Workstation` / `Reservoir`, in `Processor.cs`) — a **passive timed converter**. Created when `StructType.hasProcessor` is set; the conversion it runs — inputs, outputs, `processDays`, temperature ramp, Working-state tint — is a `ProcessorRecipe` loaded from `processorRecipesDb.json` and linked to the building by name (see SPEC-data.md §`processorRecipesDb.json`). The `Processor` holds its recipe by reference, so a future "select a process" feature only has to reassign it. The brewery is the first user (rice + water + yeast → rice wine); the component is generic, so vinegar / soy-sauce / compost buildings can reuse it by adding a building plus a processor recipe — no new code.

It differs from the worker-driven `CraftTask` paradigm: no worker is present during the wait, inputs are consumed at load time, and progress advances on world ticks regardless of who is nearby.

**Two internal inventories** (neither is the building's `storage` — the brewery has no `storage`):
- `inputBuffer` — `InvType.Reservoir`, so it accepts mixed item classes together (rice/water/yeast), never decays, and is never a haul source.
- `output` — `InvType.Storage` (liquid), so the finished goods are a normal haul source.

**Lifecycle** (`Processor.State`): `Empty → Filling → Working → Ready → Tapped → Empty`.
- `Empty` — a WOM `FillProcessor` order is open (`isActive`).
- `Filling` — a cook is delivering inputs (`FillProcessorTask`); the fill order self-suppresses.
- `Working` — inputs locked; `progress` accrues each tick via `Processor.Tick`, scaled by ambient temperature (`Rate()` — linear ramp between `processTempMin`/`processTempIdeal`, or constant 1.0 if unconfigured). On `progress ≥ processDays` → `Ready`.
- `Ready` — a WOM `TapProcessor` order is open. `TapProcessorTask` → `Processor.Tap()` drains the inputs, produces the outputs, → `Tapped`.
- `Tapped` — `output` holds the finished goods (haulable). `Tick` flips back to `Empty` once `output` is drained.

**Ticking**: `StructController.TickUpdate()` calls `Processor.Tick` for every building with a processor, alongside furnishing decay (no dedicated controller — fewer reload-state footguns).

**WOM**: `RegisterFillProcessor` / `RegisterTapProcessor` (priority 3, cook-gated) are registered from `Building.OnPlaced` and re-registered by `WorkOrderManager.ScanOrders` on load. `RemoveProcessorOrders` pairs with deconstruct. `Building.Destroy` drops both inventories' contents to the floor and destroys them.

**Save/load**: `StructureSaveData.processorState` / `processorProgress` / `processorInputData` / `processorOutputData` round-trip the full state. State/progress restore directly (no re-fired transitions); `ScanOrders` re-registers orders and any haul-out for a tank restored mid-`Tapped`.

## Variable-shape structures

Some StructTypes can be placed in multiple footprint variants — e.g. the platform can be 1, 2, or 3 tiles tall. The player cycles between variants with **Q** (-1) / **E** (+1) during build placement; `Esc` cancels. Variants are declared in `buildingsDb.json` via the `shapes` field (see SPEC-data.md).

**Data model**:
- `StructType.shapes: Shape[]?` — null = single fixed shape (legacy behaviour). When set, each entry is a `{nx, ny}` pair. `shapes[0]` is the *authored* baseline (`ncosts` is sized for it).
- `StructType.GetShape(int)` returns the chosen entry clamped to the array, or a synthetic shape mirroring base `nx`/`ny` when `shapes` is null. Use this everywhere instead of branching on `HasShapes` directly.
- `Structure.shapeIndex` and `Blueprint.shapeIndex` carry the player's choice through placement → save → load. `BuildPanel.shapeIndex` is reset to 0 when the StructType selection changes.

**Cost scaling**: Blueprint ctor multiplies each `cost.quantity` by `shape.TileCount / shapes[0].TileCount` (rounded). Platform shapes `[1×1, 1×2, 1×3]` → 1×, 2×, 3× the wood per height step.

**Multi-tile claim**: every tile in the visual footprint claims the structure / blueprint at its depth — `shape.nx × shape.ny` for shape-aware types, `structType.nx × structType.ny` for legacy multi-tile (windmill 2×4, wheel 2×2, flywheel 2×2). This keeps tile→structure lookup (selection, collision, `tile.building`) symmetric with what's rendered: clicking any tile of a 2×4 windmill resolves to the windmill, and `StructPlacement.CanPlaceHere` correctly rejects new structures stacked into a windmill's upper rows. `Mathf.Max(1, st.ny)` guards against StructTypes that omit `ny` (default 0).

**Sprite composition** (vertical extension only, `nx=1, ny>1`): `StructureVisuals.LoadShapeSprite` resolves per-tile sprites — `_b` (anchor), `_m` (middle), `_t` (top). 1-tall shapes use the base `{name}.png` sprite directly so the existing 1×1 platform render is unchanged. Anchor SR renders the bottom tile; child SRs are spawned at local `(0, dy)` for `dy=1..ny-1` (mirrors `Plant.ClaimExtensionTile`). Missing variant sprites log once and fall back to the base sprite. Center-pivot sprites are assumed (matches the existing platform.png convention) — child SRs at integer dy align correctly.

Lookup order for the suffixed sprite (first match wins): (1) slice named `{stem}_<suffix>` inside `{name}_s.png`, (2) slice named `{stem}_<suffix>` inside `{name}.png`, (3) standalone file `{name}_<suffix>.png`. Path 1 lets `platform_s.png` (sliced sheet) coexist with the legacy 1×1 `platform.png`. Path 2 is for structures with no 1-tile form (e.g. `elevator.png`). Path 3 is the legacy per-file convention — works unchanged for buildings that haven't been consolidated. Use `Assets → Slice Vertical Building Sheet` (see SPEC-rendering.md *Sprite normal maps*) to set up a sliced sheet; it also sets the merged-normals flag so per-tile boundaries don't get false bevels.

**Standability rule** (`Navigation.GetStandability`): a tile is *not* standable if it contains a `solidTop` structure that ALSO occupies the tile directly below. This treats a tall platform's body as an obstacle (mice can't stand inside the column) while preserving existing behaviour for separate stacked 1×1 platforms (different `Structure` instances → rule doesn't fire). The very top of a tall column remains standable as before. Applied at depths 0 and 1 (buildings + platforms).

**Build preview**: `MouseController` composes the cursor-following ghost from per-tile preview SRs (pooled across builds) so a height-3 platform appears as `_b` + `_m` + `_t` before placement, matching what will be built.

**Placement**: `StructPlacement.CanPlaceHere` iterates the full visual footprint (matching the multi-tile claim above) when checking for blocking structs / blueprints / plants at the chosen depth. Standability/support is anchored to the bottom row only — only the base of the column needs to rest on something solid.

**Save/load**: `shapeIndex` is persisted on both `StructureSaveData` and `BlueprintSaveData`; defaults to 0 for old saves and non-shape types.

**Construction reach for multi-tile blueprints**: `Nav.PathToOrAdjacentBlueprint(bp)` extends the usual "stand on the centerTile or any of its 8 neighbours" pattern to consider neighbours of EVERY footprint tile. A hauler standing on a cliff that's level with the *top* of a 2-tall platform can now supply/construct it, even though the bottom (centerTile) is unreachable (cliff too tall to descend, water moat below). `ConstructTask` and `SupplyBlueprintTask` both route through this helper.

## preservesTile buildings (holes-in-banks)

A structure built into a solid tile (today only burrow, with `requiredTileName: "dirt"`) can opt out of the default "convert footprint to empty" mining behaviour by setting `preservesTile: true` in JSON. The footprint tiles keep their original type — grass continues, snow accumulates, water still blocked, support unchanged — and the structure renders in front of them as if it were a carved hole.

**Effects of the flag**:
- `StructController.Construct` skips both the `tile.type = empty` loop AND the 8-neighbour diagonal sweep (solidity didn't change, so cliff/stair edges around the diagonals don't need refreshing).
- `Blueprint.Complete` still captures yields: for `preservesTile`, it walks every footprint tile and accumulates `tile.type.products` into `pendingOutput` (burrow → 3× dirt's products = 30 dirt). Without this branch the legacy `minesTile` capture would miss `requiredTileName`-only structures entirely.
- `Blueprint.Deconstruct` converts the footprint to `empty` after `Destroy` runs — semantically "digging away the roof" — via the shared `World.SetFootprintTileType(x, y, w, h, target)` helper (also used by any future post-load self-heal). The helper handles graph sweep + items-fall + nav rebuild.
- `StructPlacement.CanPlaceHere` rejects an `"empty"` (mine) blueprint placed on any tile occupied by a `preservesTile` structure. Without this, players could mine the dirt out from under their own burrow and silently break grass/standability/snow invariants.
- See SPEC-rendering.md *Tile overlays* for the rim/grass suppression that goes with this (`bodyEdgeSuppressMask` set on door tiles).
- See SPEC-ai.md *Doored buildings & path start* for the navigation contract that makes preservesTile interiors reachable.

The flag is generic — any future "hole into a bank" building can opt in by pairing it with `requiredTileName` or `requiresSolidTilePlacement`. Schema is documented in SPEC-data.md.

## Side-ladders (`ladder_side`)

A side-ladder hangs on a wall in the air tile beside it (no floor needed). `dir = mirrored ? +1 : -1` puts the wall on the opposite side from the sprite's lean.

**Mounting rule** (`StructPlacement.GetPlacementFailReason`, `ladder_side` branch): the wall tile must be either a natural/built **solid tile**, OR a **building whose sprite actually has body on the face the ladder rests against**. It is rejected against a plant, and against a building's visually-empty footprint tile (e.g. a windmill's blade tiles, which are claimed in `structs[0]` but draw nothing).

- "Has body on that edge" is decided by `StructType.SideEdgeSolid`, which reads per-footprint-tile left/right edge **bitmasks** baked from the building sprite's alpha. Baked offline by the **`Tools > Bake Building Edge Masks`** editor command ([BuildingEdgeMaskBaker.cs](../Editor/BuildingEdgeMaskBaker.cs)) into `Resources/buildingEdgeMasks.json`, loaded at `Db` startup. Probing at gameplay/load time was ~15–25 ms (over budget); baking drops it to ~0. **Re-run the bake after editing any building sprite** — the masks are a stale-able artifact, not auto-refreshed.
- The edge test samples the **outer 2 pixel columns** on a side (≥50% of the 16 rows opaque), so a frame inset by 1px (e.g. the workshop) still counts as solid. Buildings the baker can't map cleanly (animated sheets, shape variants, sprite size ≠ `nx·16 × ny·16`) get no entry → `SideEdgeSolid` returns permissive (treat every edge as solid) so legit mounts are never wrongly blocked.

**Mining a wall drops its side-ladder** (`Blueprint.DestroyDependentSideLadders`, fired from `Complete` when `minesTile`): when a tile is mined to empty (the `"empty"` action or a `requiresSolidTilePlacement` building like the mineshaft), any side-ladder that was leaning on the now-removed wall is instantly `Destroy()`-ed. Its half-cost wood is appended to the blueprint's `pendingOutput`, so it flows to the **mining mouse** via `animal.Produce` — which already falls back to a floor drop, then vanish-with-log, if the mouse can't hold it. Only side-ladders are handled (regular ladders sit in air, not on a mined wall).

## Weather & Temperature

`Assets/Model/WeatherSystem.cs` — singleton, created by `World.Awake()`. Ticked every frame by `World.Update()`.

**Temperature** is a global ambient value in Celsius, driven by two additive sine waves:
- **Yearly**: peaks midsummer (day 9/24), troughs midwinter. Amplitude ±12.5°C around 13.5°C mean.
- **Daily**: peaks at 2pm, amplitude ±4°C.
- Formula: `T = 13.5 + 12.5·sin(yearly) + 4·sin(daily)`
- Range: ~−3°C (midwinter night) to ~30°C (midsummer afternoon).

**Seasons** (time 0 = first day of spring, `daysInYear = 24`): Spring 0–5, Summer 6–11, Fall 12–17, Winter 18–23. `GetSeason()` returns the name, `GetDayOfYear()` returns the fractional day.

**Temperature comfort** (on `Happiness`): each animal has `comfortTempLow` (default 10°C) and `comfortTempHigh` (25°C).
- In range → +2 happiness, 100% efficiency.
- Outside range → `2 − deviation/5` happiness (smooth falloff from +2, crosses zero at 10°C deviation); efficiency = `max(0.7, 1.0 − deviation × 0.04)`.
- Clothing expands the comfort range: `UpdateComfortRange()` shifts both bounds by ±3°C when any clothing item is equipped (7–28°C with a ramie shirt).
- Fireplace warmth buff: leisuring at a fireplace grants a `warmth` value (0–5) that widens `comfortTempLow` by up to 5°C. Decays slowly over ~2 days (`×0.94` per SlowUpdate).

**Rain/wind**: `WeatherSystem` advances rain state per in-game hour from `World.Update`. Rain probabilities: Clear → Rain 4%, Rain → Clear 12%. Lighting hooks (sun/ambient multipliers) are polled by `SunController` each frame. While raining, `OnHourElapsed` runs:

| Effect | Amount | Target |
|--------|--------|--------|
| Puddle top-up (`WaterController.RainReplenish`) | +2 fixed-point water units | every partially-filled, non-full, non-solid tile |
| Tank rain-catch (`RainFillTanks`) | +100 fen (1 liang) water | every sky-exposed liquid-storage building whose filter allows water |

**Snow vs rain**: a temperature gate inside `WeatherSystem` splits "isRaining" into two channels. Above `snowThresholdC = 2°C` the active channel is `rainAmount`; below, it's `snowAmount`. Both lerp on the same `MoveTowards` step. `RainReplenish` / `RainFillTanks` skip while snowing (snow doesn't fill puddles or tanks today; melt-driven water is a future feature). Light multipliers (`GetSunMultiplier`, `GetAmbientMultiplier`) use `max(rainAmount, snowAmount)` so an overcast snow scene dims identically to an overcast rain scene.

**Snow accumulation**: `SnowAccumulationSystem` ticks once per in-game second from `World.Tick`. Per tile rules: if `tile.snow == false`, when `temperature < 0°C` and `WeatherSystem.snowAmount > 0`, roll `(1/10) × snowAmount` per second to flip `tile.snow = true`. Eligibility: `tile.type.solid` and `World.IsExposedAbove(x, y)`. Roads/buildings are not gated — sortingOrder layering in the renderer handles the visuals (snow draws above roads as a wintry road-cover; buildings draw above snow on their anchor tile, hiding it). **Grass preservation**: accumulation snapshots the live `overlayMask` and `overlayState` into `tile.preSnowOverlayMask`/`State` and clears the live mask so snow renders cleanly on top; `OverlayGrowthSystem` skips snowed tiles entirely so the snapshot doesn't drift. On melt, the snapshot is restored verbatim — same grass returns. Melt rolls when `tile.snow == true`: chance per second = `clamp01((temp − 0°C) / 20°C)`, so 0% at 0°C, 100% at and above 20°C, linear ramp between (e.g. 25% at 5°C, 50% at 10°C). Mining a snowy tile clears `tile.snow` and the grass snapshot automatically (the `Tile.type` setter, mirror of the grass `_overlayMask` reset). Save format: nullable `bool? snow`, `byte? preSnowOverlayMask`, `byte? preSnowOverlayState` on `TileSaveData` — absent on snow-free tiles or when there was no grass to preserve, keeping golden diffs minimal.

**Sky exposure**: `World.IsExposedAbove(x, y)` is the shared primitive. Returns true if no solid tile and no `solidTop`-or-`blocksRain` structure layer exists on any tile above `(x, y)`. Reused by rain-catch, windmills, the moisture rain-uptake gate, and snow accumulation.

**`blocksRain` vs. `solidTop`**: two separate flags on `StructType`. `solidTop` means "walkable on top" and *also* blocks rain (a roofed-over tile is by definition sheltered). `blocksRain` is the rain-shelter half on its own — for structures like tarps that should shadow the tiles below from rain *without* being walkable. Either flag is sufficient to make `IsExposedAbove` and `MoistureSystem.CapsSoilFromAbove` report a tile as sheltered.

---

## Reservation Systems

`Reservable` (`Reservable.cs`) is the shared primitive — a capacity counter with `Reserve()`/`Unreserve()`/`Available()`. `Reserve` has two overloads: `Reserve(string by)` for callers without a task context (home, WOM orders) and `Reserve(Task by)` which additionally stores a task reference so `ExpireIfStale` can suppress expiry while the owning task is still the animal's active task (see "Staleness expiry" below). It appears in three conceptually different roles:

### Structure-level capacity ("can I go here?")

| Mechanism | Created by | Used by | What it gates |
|-----------|------------|---------|---------------|
| `Structure.res` | Structure constructor (`capacity > 0`, not workstation, not leisure) | `Animal.FindHome()` | House sleeping slots |
| `Structure.seatRes[]` | Structure constructor (leisure buildings only) | `LeisureTask` | Per-seat leisure access — each work tile gets its own `Reservable(1)` so two mice sit on different seats |
| `WorkOrder.res` (craft) | `RegisterWorkstation()` | `ChooseOrder()` / `Task.Cleanup()` | Workstation worker slots (player-adjustable via `workerLimit`) |

Workstations don't use `Structure.res` — the WOM Craft order's `res` is the sole reservation tracker. Leisure buildings don't use `Structure.res` — they use `seatRes[]` instead. Houses use `Structure.res`.

### WOM dispatch gating ("can I take this work?")

`WorkOrder.res` defaults to `Reservable(1)`. Harvest, research, and fuel-supply orders set capacity from the building/plant. `ChooseOrder()` reserves on dispatch; `Task.Cleanup()` unreserves. Orders stay in the queue permanently — reservation state determines availability.

### Item reservation

| Mechanism | Where | What it gates |
|-----------|-------|---------------|
| `ItemStack.resAmount` | Per-stack int counter (source) | Prevents two tasks from fetching the same items. Reserved via `Task.ReserveStack()` / `FetchAndReserve()`. Stale reservations expire via `Inventory.TickUpdate()` — see "Staleness expiry" below. |
| `ItemStack.resSpace` | Per-stack int counter (destination) | Prevents two tasks from delivering to the same space. Reserved via `Task.ReserveSpace(inv, item, amount)`. `FreeSpace(item)` returns `stackSize - quantity - resSpace`. All space-checking methods (`GetStorageForItem`, `GetMergeSpace`, `HasSpaceForItem`) account for it. Empty stacks track `resSpaceItem` to prevent conflicting item claims. Stale reservations expire via `Inventory.TickUpdate()` — see "Staleness expiry" below. |

### Staleness expiry

Both `Reservable` and `ItemStack` have a safety-net `ExpireIfStale(maxAge)` that clears reservations held longer than `maxAge` in-game seconds (timestamps use `World.instance.timer`, so they scale with `timeScale` and don't advance while paused). The guard is **AND-gated** on the owning task being inactive: a reservation whose `reservedByTask` (on Reservable) or `resTask`/`resSpaceTask` (on ItemStack) is still the animal's `task` is never expired, regardless of age. This prevents false-positive expiry on legitimately long-running tasks (e.g. `ReadBookTask` with fetch + walk + read + return). If the owning task was registered via a string-only `Reserve` overload (home assignment), or no task context exists, the guard falls through and the time-only path fires.

Called from `StructController.TickUpdate` (every 120 × 0.2s = 24s, threshold 60s) for leisure seats, and from `Inventory.TickUpdate` (per-tick, threshold 60s) for item stacks.

### Save/load invariant

Reservations are **never persisted**. On load, `ItemStack.resAmount`/`resSpace` and every `Reservable.reserved` start at 0 (fresh construction + explicit `= 0` in `SaveSystem.Restore*`). Non-resumable tasks are implicitly aborted at save — safe because their reservations vanish with the recreated world. Resumable tasks (`HaulToMarketTask`, `HaulFromMarketTask`) must re-make every reservation in their `InitializeResume()`. Any new resumable task type must do the same.

---

## When a structure isn't running

Multiple orthogonal mechanisms can cause a Building / Blueprint / Plant to skip work or be skipped over. They split cleanly into **player intent** (toggles set via UI) and **world state** (runtime conditions). The universal WOM-order gate is `!disabled && ConditionsMet()` on both Building and Blueprint (Blueprint mirrors the method by convention — it is *not* a Structure subclass).

### Player-toggled

| Mechanism | Owner | What it suppresses | Notes |
|-----------|-------|--------------------|-------|
| `Building.disabled` | `Building` | All WOM orders for this building (craft + research + supply hauls) | Order stays in queue, `isActive` returns false. Also gates LightSource burn/emission. |
| `Blueprint.disabled` | `Blueprint` | Supply + construct orders for this blueprint | Order is *removed* on `SetDisabled(true)` and re-registered on re-enable (via `RegisterOrdersIfUnsuspended`). Asymmetric with Building — see "Disabled-enforcement asymmetry" below. |
| `Blueprint.cancelled` | `Blueprint` | All orders, terminal | Set when blueprint is being torn down; not user-reversible. |
| `Workstation.workerLimit` | `Building.workstation` | Reduces effective craft capacity (set to 0 = no workers assigned) | Read by WOM at order registration as `effectiveCapacity`. Only affects craft, not supply/research. |
| `Plant.harvestFlagged` | `Plant` | Harvest order existence | Order only exists while flagged. `SetHarvestFlagged(false)` removes the order; `true` registers it. |

### Runtime-gated (world state)

| Mechanism | Owner | What it suppresses | Notes |
|-----------|-------|--------------------|-------|
| `Structure.ConditionsMet()` | `Structure` (virtual, default `true`) | Building craft order via `isActive` lambda | Override for runtime preconditions. Currently overridden only by `PumpBuilding` (requires water below pump head). |
| `Blueprint.ConditionsMet()` | `Blueprint` | Supply + construct orders via `isActive` lambda | Returns `!IsSuspended()`. Same call-site shape as Structure but no inheritance. |
| `Blueprint.IsSuspended()` | `Blueprint` | Order *registration* (constructor + `RegisterOrdersIfUnsuspended`); also drives UI tint | True when tile requirements fail or blueprint sits on unbuilt support. Default rule: every tile in the bottom row of the footprint must be standable. **`StructType.edgeSupported`**: only the leftmost and rightmost bottom-row tiles need to be standable — the middle is free to hang (used by tarps). The reason behind `Blueprint.ConditionsMet()`. |
| `Building.reservoir.HasFuel()` | `Building` (when `reservoir != null`) | Building skipped by animal AI work-finding, water routing, and light emission | Not a WOM gate — these are direct checks at use sites (`Animal.cs`, `WaterController.cs`, `LightSource.cs`). |
| `LightSource.IsInActiveWindow()` | `LightSource` | Fuel burn + light emission outside `activeStartHour..activeEndHour` | StructType-driven schedule, e.g. torches lit only at night. |
| `uses >= depleteAt` | `Workstation` (`uses`) + `StructType` (`depleteAt`) | Triggers building destruction at craft completion | Not technically a "skip" — the building gets removed. Checked in `AnimalStateManager` after each craft round. |
| `Structure.IsBroken` | `Structure` (`condition < 0.5`) | Craft / research / fuel supply orders; decoration happiness; fountain decorative water; clock hand rotation; leisure seats; house sleep; road speed bonus; light burn + emission | Driven by the Maintenance System (see below). Gating sites mirror `disabled` — WOM `isActive` lambdas, plus direct checks in `Animal.cs`, `Navigation.cs`, `ModifierSystem.cs`, `LightSource.cs`, `WaterController.cs`, and the `ClockHand.isActive` closure wired by `Clock.AttachAnimations`. |

### Disabled-enforcement asymmetry

Building and Blueprint enforce `disabled` differently:
- **Building**: order *kept* in queue; `isActive = () => !building.disabled && building.ConditionsMet()` returns false when disabled, so the dispatch loop skips it.
- **Blueprint**: order *removed* entirely on `SetDisabled(true)` (via `RemoveForBlueprint`); re-registered on re-enable.

The asymmetry exists because building craft orders carry `res` (worker-seat reservations) that are awkward to tear down and rebuild. Blueprints have no comparable per-seat state, so remove/re-register is cleaner. Reconciling this is a deferred goal — see the broader reservation-unification work.

---

## Maintenance System

Structures slowly deteriorate over time and must be repaired by a dedicated **Mender** job. Purely-structural nav pieces (platform, stairs, ladder) are exempt so a neglected map doesn't cut mice off from parts of the world.

### Condition model

Every `Structure` carries a `condition` float in `[0, 1]` (1 = pristine, 0 = fully broken). Three thresholds govern behaviour:

| Constant | Value | Meaning |
|----------|-------|---------|
| `BreakThreshold` | `0.5` | Below this → `IsBroken` → function gated off + grey tint. |
| `RegisterThreshold` | `0.75` | Below this → `WantsMaintenance` → WOM Maintenance order is active. |
| `MaxRepairPerTask` | `0.40` | A single mender visit can restore at most +40 % condition. Fully-broken (0 → 1) therefore requires 3 visits. |
| `RepairWorkPerTick` | `0.05` | Base condition gained per work tick (≈ 8 ticks for a full-cap repair). |
| `RepairCostFraction` | `0.25` | A full 0→1 repair costs ¼ × `StructType.ncosts`. A single visit scales by `repairAmount` (e.g. 40 % repair = 10 % of build cost for each cost item). |
| `DaysToBreak` | `30` | In-game days from 1.0 down to `BreakThreshold` (0.5). Full 1.0 → 0.0 takes ~60 days. Decay per tick = `(1 - BreakThreshold) / (DaysToBreak × World.ticksInDay)`. |

**Opt-out**: `StructType.noMaintenance = true` (JSON flag) exempts a type entirely. Plants and zero-cost structures are also auto-exempt (`NeedsMaintenance` is false). The three nav types (platform, stairs, ladder) and market carry the flag in `buildingsDb.json`.

**Predicates on Structure**:
- `NeedsMaintenance` — opt-in gate: non-plant, has build cost, not `noMaintenance`.
- `IsBroken` — `NeedsMaintenance && condition < 0.5`.
- `WantsMaintenance` — `NeedsMaintenance && condition < 0.75`.

### Decay ticker (`MaintenanceSystem`)

`MaintenanceSystem` is a singleton instantiated by `World.Awake` and ticked once per in-game second from `World.Update` (same cadence as `WeatherSystem`). Each tick:

1. Iterate `StructController.GetStructures()`.
2. For any `NeedsMaintenance` structure, decrement `condition` by the per-tick decay rate (clamped at 0).
3. Track threshold crossings so edge callbacks fire exactly once:
   - **Downward across `RegisterThreshold`**: `WorkOrderManager.RegisterMaintenance(s)` — Maintenance order enters the queue.
   - **Downward across `BreakThreshold`**: `OnBroken(s)` — calls `s.RefreshTint()` (grey tint). WOM gates suppress craft/research/supply automatically via `IsBroken` in their `isActive` lambdas; no removal needed.
   - **Upward across `BreakThreshold`**: `OnRepaired(s)` — restores normal tint. Order's `isActive = () => s.WantsMaintenance` suppresses it automatically once condition ≥ 0.75 (no removal, no churn on every tick).

The Maintenance WOM order is **not removed** when condition climbs back into the "fine" band — it just becomes inactive. Removal only happens when the structure is destroyed (`WorkOrderManager.RemoveMaintenanceOrders` is called from `Structure.Destroy()`).

### Mender job

`mender` is a dedicated crafter-type job with `defaultSkill: construction`. No recipes — menders respond only to Maintenance orders (plus survival). Construction skill accelerates the work-tick rate.

### MaintenanceTask flow

`MaintenanceTask` lives in `Assets/Model/Tasks/MaintenanceTask.cs` and follows the same Initialize/Objective pattern as other supply-then-work tasks. At start it snapshots `startCondition` and `repairAmount = min(MaxRepairPerTask, 1 - condition)`.

1. **Job gate**: `animal.job.name == "mender"`. 
2. **Cost computation**: for each `ItemQuantity` in `structType.costs`, `needed = ceil(cost.quantity × RepairCostFraction × repairAmount)`.
3. **Pathfind**: `Nav.FindPathTo(target.workTile)`; aborts if unreachable.
4. **Leaf resolution**: `Task.PickSupplyLeaf(group)` picks the highest-stock leaf per group cost item (single-leaf commit; no mixed-leaf delivery like blueprints). Shared with `SupplyBlueprintTask`.
5. **Fetch chain**: one `FetchObjective` per cost item with reservations held by the task.
6. **GoObjective** → **MaintenanceObjective** — ticks condition up by `RepairWorkPerTick × workEfficiency` per tick, grants Construction XP, stops at `startCondition + repairAmount` or `1.0`.
7. **Completion**: consume fetched materials from mender inventory; call `MaintenanceSystem.OnRepaired(target)` + `target.RefreshTint()`.

**Nearest-below-75-%** target selection is emergent — `WorkOrderManager.ChooseOrder` distance-sorts within a priority tier, and `isActive = () => s.WantsMaintenance` narrows the candidate pool to qualifying structures. No bespoke selection code.

### Visual

`Structure.RefreshTint()` swaps `sr.sharedMaterial` between two materials:
- **Healthy** — the renderer's original material (captured on first `RefreshTint` call into `Structure.defaultMat`). This is URP 2D's Sprite-Lit-Default, which carries the `LightMode = Universal2D` tag that the `NormalsCapturePass` filter matches on. We capture and restore by reference rather than `Shader.Find("Sprites/Default")` because the latter is the *legacy* CG sprite shader and would silently drop the renderer out of the lighting pipeline (no ambient, no sun).
- **Broken** — `Resources/Materials/CrackedSprite.mat`, which drives `Assets/Resources/Shaders/CrackedSprite.shader`. The shader has both `Universal2D` and `UniversalForward` passes so broken buildings continue to participate in NormalsCapture and LightComposite. It composites a tileable world-space crack texture on top of the base sprite's RGB, alpha-masked by the base sprite's own alpha (cracks never appear in transparent gaps). Full-structure lighting (sun, torches, fireplaces, ambient) continues to apply on top via the normal composite path.

Called on threshold crossings (`OnBroken` / `OnRepaired`) and on every structure at load (`SaveSystem` Phase 6). Deconstruct tints run via `sr.color` and compose multiplicatively with either material — broken + deconstructing renders correctly.

### Per-building broken effects

Beyond the universal cracked-material tint, specific building types have additional visual/functional responses to `IsBroken`. All are implemented as polling checks (not edge callbacks) at the site that already controls the behaviour:

| Building | Broken effect | Poll site |
|----------|---------------|-----------|
| Workstations | Craft orders halt | WOM `isActive` lambda |
| Laboratory | Research orders halt | WOM `isActive` lambda |
| Torch / fireplace | Light + fuel burn stop | `LightSource.UpdateLitState()` |
| Fountain | Decorative water overlay hidden; decoration happiness lost | `WaterController.UpdateSurfaceMask()`, `Animal.ScanForNearbyDecorations()` |
| Clock | Hand freezes; catches up on repair (rotation derived from current time, not accumulated) | `Clock.AttachAnimations` (`ClockHand.isActive` closure) |
| House | Animals abandon and find new homes | `Animal.FindHome()` |
| Road | Speed bonus drops to 0 | `Structure.EffectivePathCostReduction` |

**Exempt from decay** (`noMaintenance: true`): platform, stairs, ladder, market.

### Save/load

- `StructureSaveData.condition` (float) persists per-structure. Old saves missing the field deserialize to 0.0 which `RestoreStructure` treats as "default to 1.0" so pre-maintenance saves don't load every structure as broken.
- Maintenance WOM orders are **not** persisted. `WorkOrderManager.Reconcile` registers them from world state at load via `ScanOrders`, same mechanism as every other order type.
- `MaintenanceSystem.RebuildFromWorld()` runs in `SaveSystem` Phase 6 (after Reconcile) to rebuild the internal `registered`/`broken` sets from restored condition values. Tint refresh for every structure follows in the same block.

### Audit (`Ctrl+D`)

`ScanOrders` includes bi-directional Maintenance coverage:
- **Direction 1**: every structure with `WantsMaintenance` must have a Maintenance order registered.
- **Direction 2**: every Maintenance order must reference a live structure that still `NeedsMaintenance`.

---

## Unit System — Fen / Liang

All item quantities are stored as **fen** (integers), where **100 fen = 1 liang**. Display uses `ItemStack.FormatQ(int fen, Item item = null)` — for a discrete item shows the whole-unit count (`fen / item.unitFen`); otherwise renders liang, dropping trailing zeros. Overload `FormatQ(ItemQuantity iq)` passes `iq.item` automatically.

- **JSON data** is authored in liang for normal items (can be decimal, e.g. `0.5`), but as a whole **unit count** for discrete items. The field type is `float` (`ItemNameQuantity.quantity`).
- **Conversion** to fen for all authored `ItemNameQuantity` values goes through one chokepoint — the `ItemQuantity(ItemNameQuantity)` constructor (used by Db.cs, StructType.cs, Tile.cs, Plant.cs). It applies `LiangToFen` for normal items but `count × unitFen` for discrete items, so a recipe says `{ "stool": 1 }` for one stool. User-typed input uses `ItemStack.TryParseQ(string, Item)` — same discrete-vs-liang split, plus overflow/validation.
- **Stack sizes**: animal inv = 5 × 1000 fen; floor/default = 1000 fen; storage = `storageStackSize * 100` (converted in `StructType.OnDeserialized`).
- Old saves are **incompatible** (quantities were in the old unit). Fresh start required after this change.

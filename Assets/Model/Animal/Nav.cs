using System.Collections.Generic;
using UnityEngine;
using System;

// Per-animal navigation facade: owns the active path, drives per-frame movement along
// it, and exposes FindPath*/Path* helpers for tasks to locate targets/paths. Graph
// infrastructure (node data, A*) lives in Navigation.cs — this class only consumes it.
public class Nav {
    public Animal a;
    public World world;

    public Path path { get; private set; }
    private int pathIndex = 0;
    private Node prevNode = null;
    private Node nextNode = null;
    // true while traversing a vertical or stair edge — suppresses Falling state dispatch.
    // Set internally as path progresses; AnimalStateManager reads it during fall gating.
    public bool preventFall { get; private set; } = false;
    // Accumulated downward speed during Falling state. Mutated externally by
    // AnimalStateManager.UpdateMovement, which owns the fall physics integration.
    public float fallVelocity = 0f;
    // Non-null while the animal is loaded onto an elevator's platform. Move() returns false
    // (no lerp, position driven by elevator); preventFall stays true; OnTransitComplete clears
    // it when the elevator arrives at the destination stop. AbortRide does the same on cancel.
    public Elevator ridingElevator;

    // Elevators we've placed a tentative (plan-time) reservation on for the current path.
    // Populated in Navigate by scanning the path for transit edges; drained in EndNavigation.
    // Each entry corresponds to one AddTentativeReservation call; cleanup must call
    // RemoveTentativeReservation symmetrically. Holds stale refs harmlessly if the elevator
    // is destroyed mid-path — RemoveTentativeReservation on a dead instance is a no-op.
    private readonly List<Elevator> pendingElevators = new List<Elevator>();

    // True iff Move() is currently translating the animal's position via lerp. False when:
    //   - path is null / fully consumed
    //   - parked at an elevator boarding tile waiting for a ride (transit edge ahead)
    //   - loaded onto the elevator platform (ridingElevator != null)
    // AnimalState stays "Moving" through all of those — the task is still in progress —
    // but the animal isn't actually walking, so AnimationController uses this to pick the
    // idle animation instead of the walk cycle in those cases.
    public bool IsLocomoting { get; private set; } = false;


    public Nav (Animal a){
        this.a = a;
        this.world = a.world;
        path = null;
    }

    // Commits the animal to walking this path. The only entry point for starting
    // navigation — all task/objective code routes through here.
    public bool Navigate(Path p){
        if (p == null){Debug.LogError("Navigate called with null path"); return false;}
        // Drain any tentative reservations from a previous path so we don't double-count
        // ourselves on elevators we're abandoning.
        EndNavigation();
        path = p;
        pathIndex = 0;
        prevNode = path.nodes[0];
        nextNode = path.length != 0 ? path.nodes[1] : prevNode;
        // Endpoint may be a tile-backed Node or an off-grid waypoint (e.g. wheel workspot).
        // AnimalStateManager.UpdateMovement reads target.wx/wy on arrival, which works for both.
        a.target = path.end;
        // Tentative reservations: scan the path for transit edges and tell each elevator
        // we're inbound. EstimatedTransitCost picks these up immediately so simultaneous
        // planners see realistic queue depth.
        for (int i = 0; i < path.length; i++) {
            Node n0 = path.nodes[i];
            Node n1 = path.nodes[i + 1];
            if (n0.payload != null && ReferenceEquals(n0.payload, n1.payload) && n0.payload is Elevator e) {
                e.AddTentativeReservation(a);
                pendingElevators.Add(e);
            }
        }
        RefreshLocomotion();
        return true;
    }
    private void EndNavigation(){
        // Symmetric cleanup of tentative reservations. Idempotent: AddTentativeReservation
        // is no-op if the animal already graduated to `reserved`, and RemoveTentativeReservation
        // is a HashSet.Remove that no-ops on missing entries.
        foreach (Elevator e in pendingElevators) e?.RemoveTentativeReservation(a);
        pendingElevators.Clear();
        path = null; pathIndex = 0; prevNode = null; nextNode = null; preventFall = false;
        RefreshLocomotion();
    }

    // Recomputes IsLocomoting from current state and notifies the animator on changes so
    // the walk/idle anim swaps when the mouse parks at an elevator stop or boards. Called
    // at the end of every Move() and at navigation lifecycle boundaries (Navigate, EndNav,
    // OnTransitComplete, AbortRide). Idempotent — only fires UpdateState on actual flips.
    void RefreshLocomotion() {
        bool now = ComputeLocomoting();
        if (now == IsLocomoting) return;
        IsLocomoting = now;
        a.animationController?.UpdateState();
    }
    bool ComputeLocomoting() {
        if (path == null || pathIndex >= path.length) return false;
        if (ridingElevator != null) return false;
        // Parked at a transit-edge boarding tile, waiting for a ride.
        if (prevNode != null && nextNode != null
                && prevNode.payload != null
                && ReferenceEquals(prevNode.payload, nextNode.payload)
                && prevNode.payload is Elevator) return false;
        return true;
    }
    public void Fall(){
        if (a.task != null) {
            Debug.Log(a.aName + " falling! interrupting task " + a.task.ToString());
            a.task.Fail();
            a.task = null;
        }
        EndNavigation();
        fallVelocity = 0f;
        a.state = Animal.AnimalState.Falling;
    }

    public bool Move(float deltaTime){ // called by animal every frame!! returns whether you're done
        // Wrap the real logic so we can refresh IsLocomoting and notify the animator on
        // every transition (path commit, transit-edge entry, riding handoff, completion).
        bool result = MoveCore(deltaTime);
        RefreshLocomotion();
        return result;
    }

    bool MoveCore(float deltaTime){
        if (path == null || pathIndex >= path.length){return true;}  // no path... return true, give up
        // Loaded onto an elevator: position is driven by Elevator.Tick. Hold here until
        // OnTransitComplete clears ridingElevator (elevator delivers us to the dest stop)
        // or AbortRide bails on a power loss / wait timeout.
        if (ridingElevator != null) {
            preventFall = true;
            return false;
        }
        // If the next tile became solid mid-path (e.g. building placed), abort early.
        // Skip waypoints — they're virtual intermediate points and are never standable.
        if (!nextNode.isWaypoint && !nextNode.standable) {
            Debug.Log($"{a.aName} path blocked at ({(int)nextNode.wx},{(int)nextNode.wy}), aborting task");
            a.task?.Fail();
            EndNavigation();
            a.state = Animal.AnimalState.Idle;
            return true;
        }
        if (SquareDistance(a.x, nextNode.wx, a.y, nextNode.wy) < 0.001f){
            if (pathIndex + 1 >= path.length){
                EndNavigation();
                return true;
            } else {
                pathIndex++;
                prevNode = nextNode;
                nextNode = path.nodes[pathIndex + 1];
            }
        }
        // Transit edge: prev and next are both stop tile-nodes of the same elevator. Don't
        // lerp through the air — request a ride and wait at the boarding tile. The elevator
        // will load us when our turn comes; ridingElevator gates the early return above.
        if (prevNode.payload != null && ReferenceEquals(prevNode.payload, nextNode.payload)
                && prevNode.payload is Elevator ev) {
            if (!ev.HasReservation(a)) ev.RequestRide(a, prevNode, nextNode);
            preventFall = true;
            return false;
        }
        // Suppress falling on waypoint edges (cliff/stair) and vertical edges (ladders).
        preventFall = prevNode.isWaypoint || nextNode.isWaypoint
                   || Mathf.Abs(nextNode.wy - prevNode.wy) > 0.1f;
        var (edgeCost, edgeLen) = Graph.instance.GetRawEdgeInfo(prevNode, nextNode);
        float speed = ModifierSystem.GetTravelSpeedMultiplier(a);
        Vector2 newPos = Vector2.MoveTowards(
            new Vector2(a.x, a.y), new Vector2(nextNode.wx, nextNode.wy),
            speed * edgeLen / edgeCost * deltaTime);
        a.x = newPos.x; a.y = newPos.y;
        a.go.transform.position = new Vector3(a.x, a.y, a.z);

        // Use the edge direction rather than the live position delta, so we don't
        // flicker facing when the animal lands exactly on a waypoint mid-frame.
        float facingDx = nextNode.wx - prevNode.wx;
        if (Mathf.Abs(facingDx) > 0.01f) a.facingRight = facingDx > 0;
        return false;
    }

    // Called by Elevator.Tick when the platform finishes the trip and unloads. Snaps the
    // animal to the destination stop's coordinates so the next Move() distance check fires
    // and pathIndex advances past the transit edge naturally.
    public void OnTransitComplete(Node arrivalStop) {
        if (ridingElevator == null) return;
        ridingElevator = null;
        if (arrivalStop != null) {
            a.x = arrivalStop.wx;
            a.y = arrivalStop.wy;
            if (a.go != null)
                a.go.transform.position = new Vector3(a.x, a.y, a.z);
        }
        preventFall = false;
        RefreshLocomotion();
    }

    // Called when a ride is cancelled mid-loop (elevator destroyed, future: power lost,
    // wait timeout). Clears riding state without snapping position; the caller is expected
    // to also fail the animal's task so the path doesn't dangle.
    public void AbortRide() {
        ridingElevator = null;
        preventFall = false;
        RefreshLocomotion();
    }


    // ── Tile-box scan primitive ──────────────────────────────────────────────
    // Returns tiles in the (2r+1)² box centered at (cx,cy), sorted ascending by
    // Chebyshev distance from the center. Off-map (null) tiles are skipped.
    // Use this for "scan outward, first match wins" logic — e.g. FindPathTo, or
    // any task-local spatial search around an anchor (ReadBookTask anchors on a
    // shelf tile, not the animal). Static + takes World explicitly so non-animal
    // callers can use it without a Nav instance.
    public static List<Tile> TilesAroundByDistance(World world, int cx, int cy, int r){
        var candidates = new List<(int cheb, Tile tile)>();
        for (int dx = -r; dx <= r; dx++) {
            for (int dy = -r; dy <= r; dy++) {
                Tile tile = world.GetTileAt(cx + dx, cy + dy);
                if (tile == null) continue;
                candidates.Add((Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)), tile));
            }
        }
        candidates.Sort((p, q) => p.cheb.CompareTo(q.cheb));
        var result = new List<Tile>(candidates.Count);
        foreach (var (_, tile) in candidates) result.Add(tile);
        return result;
    }

    // ── Candidate-list first-fit primitive ───────────────────────────────────
    // Counterpart to TilesAroundByDistance for callers whose candidates come from a
    // pre-built list (inventories, structures) rather than a spatial box. Collects
    // candidates within Chebyshev r of the animal, applies the filter, sorts ascending
    // by Chebyshev, then pathfinds in order and returns the FIRST whose path fits
    // within r × FindRadiusTolerance. Callers provide position accessors and a nodeFn
    // (nullable — if it returns null, that candidate is skipped: lets callers short-
    // circuit unpathable structures without extra plumbing). Used by FindPathToInv,
    // FindPathToStruct, FindPathToHarvestable.
    private (Path path, T candidate) FindPathToCandidate<T>(
        IEnumerable<T> candidates,
        Func<T, int> xFn, Func<T, int> yFn,
        Func<T, Node> nodeFn,
        Func<T, bool> filter,
        int r) {
        float maxCost = r * Task.FindRadiusTolerance;
        Node myNode = a.TileHere().node;
        var scored = new List<(int cheb, T item)>();
        foreach (T c in candidates) {
            int cheb = Mathf.Max(Mathf.Abs(xFn(c) - (int)a.x), Mathf.Abs(yFn(c) - (int)a.y));
            if (cheb > r) continue;
            if (filter != null && !filter(c)) continue;
            scored.Add((cheb, c));
        }
        scored.Sort((x, y) => x.cheb.CompareTo(y.cheb));
        foreach (var (_, item) in scored) {
            Node target = nodeFn(item);
            if (target == null) continue;
            Path p = world.graph.Navigate(myNode, target);
            if (p != null && p.cost <= maxCost) return (p, item);
        }
        return (null, default);
    }

    // ── Generic path finder ──────────────────────────────────────────────────
    // FindPathTo(condition, r) returns a Path to the nearest tile matching `condition`,
    // or null. Candidates iterate by Chebyshev order (crow-flies lower bound on walk
    // cost) and the first whose A* cost fits within r × FindRadiusTolerance wins.
    // Not guaranteed minimum-cost across all candidates — but the nearest crow-flies
    // match is almost always the shortest walk, and we avoid pathfinding the rest of
    // the box.
    //
    // Every FindPath* method below gates candidates the same way, so any returned path
    // is a "reasonable journey" relative to the caller's search radius.
    public Path FindPathTo(Func<Tile, bool> condition, int r = Task.MediumFindRadius){
        float maxCost = r * Task.FindRadiusTolerance;
        Node myNode = a.TileHere().node;
        foreach (Tile tile in TilesAroundByDistance(world, (int)a.x, (int)a.y, r)) {
            if (!condition(tile)) continue;
            Path p = world.graph.Navigate(myNode, tile.node);
            if (p != null && p.cost <= maxCost) return p;
        }
        return null;
    }

    // ── Specific path finders ────────────────────────────────────────────────

    // minSpace: minimum free capacity (fen) the storage must offer for `item`.
    // Default 1 matches legacy "any space counts" behaviour. Callers that plan to deliver
    // a meaningful batch (e.g. merchants with MinMarketHaul) should pass a higher floor so
    // we skip near-full storages and route to one that can actually hold the trip.
    public (Path path, Inventory inv) FindPathToStorage(Item item, int r = Task.MediumFindRadius, int minSpace = 1) {
        return FindPathToInv(new[] { Inventory.InvType.Storage },
            inv => inv.GetStorageForItem(item) >= minSpace, r); }

    // Like FindPathToStorage, but picks the storage with the MOST free space for `item`
    // among reachable candidates. Use for batch deliveries where fit matters more than
    // proximity — e.g. merchant return trips where a cramped-but-close storage can't hold
    // the whole payload. Still gated by `minSpace` and the radius cap.
    public (Path path, Inventory inv) FindPathToStorageMostSpace(Item item, int r = Task.MediumFindRadius, int minSpace = 1) {
        var ic = InventoryController.instance;
        float maxCost = r * Task.FindRadiusTolerance;
        Node myNode = a.TileHere().node;
        var candidates = new List<(int space, Inventory inv, Tile tile)>();
        if (ic.byType.TryGetValue(Inventory.InvType.Storage, out var list)) {
            foreach (Inventory inv in list) {
                int cheb = Mathf.Max(Mathf.Abs(inv.x - (int)a.x), Mathf.Abs(inv.y - (int)a.y));
                if (cheb > r) continue;
                int space = inv.GetStorageForItem(item);
                if (space < minSpace) continue;
                Tile t = world.GetTileAt(inv.x, inv.y);
                if (t == null) continue;
                candidates.Add((space, inv, t));
            }
        }
        // Descending by free space — best-fit first, with reachability as the tiebreaker.
        candidates.Sort((x, y) => y.space.CompareTo(x.space));
        foreach (var (_, inv, t) in candidates) {
            Path p = world.graph.Navigate(myNode, t.node);
            if (p != null && p.cost <= maxCost) return (p, inv);
        }
        return (null, null);
    }
    // Drop searches are intentionally tight — a mouse finishing a task shouldn't walk far just to put
    // items down. No tolerance multiplier applied here; the r cap is absolute.
    public Path FindPathToDrop(Item item, int animalQuantity, int r = 10){
        return FindPathTo(t => {
            if (t.inv == null) return true; // empty tile: full stack worth of space
            int space = t.inv.GetMergeSpace(item);
            return space > 0 && (space >= Task.MinHaulQuantity || space >= animalQuantity);
        }, r); }
    // Returns the best drop target: storage/liquid inv if within storageBonusTiles of nearest floor tile, else floor.
    // targetInv is null when dropping on floor.
    public (Path path, Inventory targetInv) FindPathToDropTarget(Item item, int animalQuantity, int storageBonusTiles = 10) {
        Path floorPath = FindPathToDrop(item, animalQuantity);
        float floorCost = floorPath != null ? floorPath.cost : float.MaxValue;
        var (storagePath, storageInv) = FindPathToInv(new[] { Inventory.InvType.Storage },
            inv => inv.GetStorageForItem(item) > 0, r: Task.MediumFindRadius);
        // Storage preferred if its cost minus the bonus is still <= floor cost
        if (storageInv != null && storagePath.cost - storageBonusTiles <= floorCost)
            return (storagePath, storageInv);
        if (floorPath != null)
            return (floorPath, null);
        return (storagePath, storageInv); // no floor reachable: fall back to any storage
    }
    private (Path path, Inventory inv) FindPathToInv(Inventory.InvType[] types, Func<Inventory, bool> filter, int r) {
        var ic = InventoryController.instance;
        return FindPathToCandidate(
            EnumerateInvs(ic, types),
            inv => inv.x, inv => inv.y,
            inv => world.GetTileAt(inv.x, inv.y)?.node,
            filter, r);
    }

    private static IEnumerable<Inventory> EnumerateInvs(InventoryController ic, Inventory.InvType[] types) {
        foreach (var type in types) {
            if (!ic.byType.TryGetValue(type, out var list)) continue;
            foreach (Inventory inv in list) yield return inv;
        }
    }
    private Path FindPathToStruct(StructType st, Func<Structure, bool> filter = null, int r = Task.MediumFindRadius) {
        var list = StructController.instance.GetByType(st);
        if (list == null) return null;
        var (p, _) = FindPathToCandidate<Structure>(
            list,
            s => s.x, s => s.y,
            s => s.workNode,    // off-grid waypoint when StructType declares workSpot, else workTile.node
            filter, r);
        return p;
    }
    public Path FindPathToBuilding(StructType structType, int r = Task.MediumFindRadius) {
        return FindPathToStruct(structType, s => s.res.Available() && !s.IsBroken, r);
    }

    // Finds the nearest reachable available seat on any leisure building matching `buildingFilter`
    // (typically a leisureNeed check + Building.CanHostLeisureNow). Uses the same Chebyshev-sort +
    // first-fit discipline as FindPathToStruct — not guaranteed minimum A* cost, but avoids
    // pathfinding every seat of every building. Returns (null, null, -1) on no match.
    // The caller reserves seatRes[seatIndex] on the returned Building.
    public (Path path, Building building, int seatIndex) FindPathToLeisureSeat(
        Func<Building, bool> buildingFilter, int r = Task.MediumFindRadius) {
        var (p, seat) = FindPathToCandidate<(Building b, int i)>(
            EnumerateAvailableLeisureSeats(buildingFilter),
            s => s.b.WorkTileAt(s.i)?.x ?? 0,
            s => s.b.WorkTileAt(s.i)?.y ?? 0,
            s => s.b.WorkTileAt(s.i)?.node,
            null, r);
        return (p, seat.b, seat.b != null ? seat.i : -1);
    }

    private static IEnumerable<(Building b, int i)> EnumerateAvailableLeisureSeats(Func<Building, bool> filter) {
        var sc = StructController.instance;
        if (sc == null) yield break;
        foreach (Building b in sc.GetLeisureBuildings()) {
            if (!filter(b)) continue;
            if (b.seatRes == null) continue;
            for (int i = 0; i < b.seatRes.Length; i++)
                if (b.seatRes[i].Available()) yield return (b, i);
        }
    }
    public Path FindMarketPath() {
        // Any standable tile on the x=0 column acts as the portal to the off-screen market.
        // Searching the whole column means a wall at the market's exact tile won't block merchants.
        return FindPathTo(t => t.x == 0 && t.node.standable, r: Task.MarketFindRadius);
    }
    public (Path, ItemStack) FindPathItemStack(Item item, int r = Task.MediumFindRadius){
        var (path, foundInv) = FindPathToInv(
            new[] { Inventory.InvType.Floor, Inventory.InvType.Storage },
            inv => inv.ContainsAvailableItem(item), r);
        if (path == null || foundInv == null) return (null, null);
        return (path, foundInv.GetItemStack(item));
    }

    // WOM always provides the exact source stack (targeted mode only).
    public HaulInfo FindFloorConsolidation(ItemStack sourceStack, int r = Task.MediumFindRadius) {
        if (sourceStack.item == null || sourceStack.quantity == 0) return null;
        Item item = sourceStack.item;
        Tile sourceTile = world.GetTileAt(sourceStack.inv.x, sourceStack.inv.y);
        if (sourceTile == null) return null;
        if (FindPathToStorage(item).path != null) return null; // storage exists — should haul instead

        // Find a dest floor tile: same item, room to receive, more quantity than source
        Path destPath = FindPathToInv(new[] { Inventory.InvType.Floor },
            inv => world.GetTileAt(inv.x, inv.y) != sourceTile
                && inv.HasSpaceForItem(item)
                && inv.Quantity(item) > sourceTile.inv.Quantity(item), r).path;
        if (destPath == null) return null;

        Tile destTile = destPath.tile;
        int qty = Math.Min(sourceTile.inv.AvailableQuantity(item), destTile.inv.GetMergeSpace(item));
        if (qty <= 0) return null;
        if (qty < Task.MinHaulQuantity && qty < sourceTile.inv.Quantity(item)) return null;
        return new HaulInfo(item, qty, sourceTile, destTile, sourceStack);
    }


    // ── Path to specific known tile ──────────────────────────────────────────

    public Path PathTo(Tile tile){
        if (tile == null || a.TileHere() == null){Debug.LogError("path to or from null tile?");}
        return world.graph.Navigate(a.TileHere().node, tile.node);
    }
    // Node overload — used when the path target is a workspot waypoint (off-grid) rather
    // than an integer tile. Same A* under the hood; the Node may be tile-backed or a waypoint.
    public Path PathTo(Node node){
        if (node == null || a.TileHere() == null){Debug.LogError("path to or from null node?");}
        return world.graph.Navigate(a.TileHere().node, node);
    }
    public Path PathToOrAdjacent(Tile target) {
        Path directPath = PathTo(target);
        if (directPath != null) { return directPath; }
        return PathStrictlyAdjacent(target);
    }
    // Like PathToOrAdjacent but never navigates onto the target tile itself.
    // Use for solid-tile blueprints where the tile is currently passable but will become solid.
    public Path PathStrictlyAdjacent(Tile target) {
        Tile[] adjacents = target.GetAdjacents(); // 0-3 orthogonal, 4-7 diagonal
        Path shortestPath = null;
        float shortestCost = float.MaxValue;
        // Prefer orthogonal neighbours
        for (int i = 0; i < 4; i++) {
            Tile adjacent = adjacents[i];
            if (adjacent != null && adjacent.node.standable) {
                Path candidatePath = PathTo(adjacent);
                if (candidatePath != null && candidatePath.cost < shortestCost) {
                    shortestPath = candidatePath;
                    shortestCost = candidatePath.cost;
                }
            }
        }
        if (shortestPath != null) return shortestPath;
        // Fall back to diagonal
        for (int i = 4; i < 8; i++) {
            Tile adjacent = adjacents[i];
            if (adjacent != null && adjacent.node.standable) {
                Path candidatePath = PathTo(adjacent);
                if (candidatePath != null && candidatePath.cost < shortestCost) {
                    shortestPath = candidatePath;
                    shortestCost = candidatePath.cost;
                }
            }
        }
        return shortestPath;
    }

    // Like PathToOrAdjacent, but accepts ANY footprint tile of a multi-tile blueprint as the
    // construction interaction point — not just the centerTile. A hauler standing next to the
    // top of a 2-tall platform can still supply/construct it, even when the bottom tile is
    // unreachable (cliff too tall to descend, water moat below, etc.).
    //
    // Order of preference:
    //   1. PathTo(centerTile)                — stand on the platform's primary interaction tile
    //   2. orthogonal neighbours of any footprint tile
    //   3. diagonal neighbours of any footprint tile
    // Footprint tiles themselves are excluded from the neighbour candidate sets (they're either
    // the centerTile already tried, or non-standable structure interior).
    public Path PathToOrAdjacentBlueprint(Blueprint bp) {
        Path direct = PathTo(bp.centerTile);
        if (direct != null) return direct;

        Shape shape = bp.Shape;
        bool shapeAware = bp.structType.HasShapes;
        int fnx = shapeAware ? shape.nx : bp.structType.nx;
        int fny = shapeAware ? shape.ny : Mathf.Max(1, bp.structType.ny);

        var footprint = new HashSet<Tile>();
        for (int dy = 0; dy < fny; dy++)
            for (int dx = 0; dx < fnx; dx++) {
                Tile t = world.GetTileAt(bp.x + dx, bp.y + dy);
                if (t != null) footprint.Add(t);
            }

        var orthCandidates = new HashSet<Tile>();
        var diagCandidates = new HashSet<Tile>();
        foreach (Tile ft in footprint) {
            Tile[] adjs = ft.GetAdjacents();
            for (int i = 0; i < 4; i++) if (adjs[i] != null) orthCandidates.Add(adjs[i]);
            for (int i = 4; i < 8; i++) if (adjs[i] != null) diagCandidates.Add(adjs[i]);
        }
        // A tile that's orthogonal of one footprint tile and diagonal of another is treated
        // as orthogonal (the better category wins).
        orthCandidates.ExceptWith(footprint);
        diagCandidates.ExceptWith(footprint);
        diagCandidates.ExceptWith(orthCandidates);

        Path best = ShortestStandablePath(orthCandidates);
        return best ?? ShortestStandablePath(diagCandidates);
    }

    private Path ShortestStandablePath(HashSet<Tile> candidates) {
        Path best = null;
        float bestCost = float.MaxValue;
        foreach (Tile c in candidates) {
            if (!c.node.standable) continue;
            Path p = PathTo(c);
            if (p != null && p.cost < bestCost) { best = p; bestCost = p.cost; }
        }
        return best;
    }



    // ── Utils ────────────────────────────────────────────────────────────────
    private static float SquareDistance(float x1, float x2, float y1, float y2){return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);}

    // Pure component check — no A*. Returns true if any building of this type is in the same
    // connected component as the animal. Use this instead of FindPathToBuilding() != null
    // when you only need reachability, not the actual path.
    public bool CanReachBuilding(StructType structType, int r = Task.MediumFindRadius) {
        var list = StructController.instance.GetByType(structType);
        if (list == null) return false;
        Node myNode = a.TileHere()?.node;
        if (myNode == null) return false;
        foreach (Structure s in list) {
            if (Mathf.Max(Mathf.Abs(s.x - (int)a.x), Mathf.Abs(s.y - (int)a.y)) > r) continue;
            if (!s.res.Available()) continue;
            if (s.workNode == null) continue;
            if (world.graph.SameComponent(myNode, s.workNode)) return true;
        }
        return false;
    }

    // True if p is non-null and its cost fits within r × Task.FindRadiusTolerance.
    // Use after PathTo / PathToOrAdjacent / PathStrictlyAdjacent in WOM tasks where the
    // target is provided directly (no Find* loop to inherit the gate from).
    public bool WithinRadius(Path p, int r) {
        return p != null && p.cost <= r * Task.FindRadiusTolerance;
    }
}

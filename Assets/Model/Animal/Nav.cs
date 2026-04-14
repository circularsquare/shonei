using System.Collections.Generic;
using UnityEngine;
using System;

public class Nav {
    public Animal a;
    public World world;

    public Path path;
    private int pathIndex = 0;
    private Node prevNode = null;
    private Node nextNode = null;
    public bool preventFall = false; // true while traversing a vertical or stair edge
    public float fallVelocity = 0f;  // accumulated downward speed during Falling state


    public Nav (Animal a){
        this.a = a;
        this.world = a.world;
        path = null;
    }

    // sets target tile and sets path, starting navigation.
    public bool NavigateTo(Tile t, Path iPath = null){  // this should be the only way you set target!
        if (t == null && iPath == null){Debug.LogError("navigating without destination or path!"); return false;}
        if (iPath != null){path = iPath; t = iPath.tile;} // if fed a path, just use it.
        else {path = world.graph.Navigate(a.TileHere().node, t.node);} // otherwise find a path
        if (path == null){ return false; } // if still no path exists, leave

        pathIndex = 0; prevNode = path.nodes[0];        // set prevNode, pathindex
        if (path.length != 0) {nextNode = path.nodes[1];} else{nextNode = prevNode;} // set nextNode
        a.target = t;   // set target, starting navigation
        return true;
    }
    public bool Navigate(Path p){return NavigateTo(p.tile, p);}
    public void EndNavigation(){ path = null; pathIndex = 0; prevNode = null; nextNode = null; preventFall = false; }
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
        if (path == null || pathIndex >= path.length){return true;}  // no path... return true, give up
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
        // Suppress falling while deliberately on a non-standable path segment:
        //   - Waypoint edges: cliff/stair traversal through fractional world positions.
        //   - Vertical edges: ladder climbing/descending.
        // The old "!prevNode.standable" case (horizontal cliff-exit from intermediate tile)
        // is gone — cliff traversal now uses waypoints which are already covered above.
        preventFall = prevNode.isWaypoint || nextNode.isWaypoint
                   || Mathf.Abs(nextNode.wy - prevNode.wy) > 0.1f;
        var (edgeCost, edgeLen) = Graph.instance.GetRawEdgeInfo(prevNode, nextNode);
        float speed = ModifierSystem.instance.GetTravelSpeedMultiplier(a);
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


    // ── Naming convention ────────────────────────────────────────────────────
    //    Find(...)                -> returns Tile. No pathfinding. Can find unreachable tiles.
    //    FindPathTo(...)          -> returns Path. Animal goes TO the matching tile.
    //    FindPathAdjacentTo(...)  -> returns (Tile, Path). Animal stands NEXT TO matching tile.
    //
    // Every FindPath* method gates candidates by path cost: paths whose cost exceeds
    // r × Task.FindRadiusTolerance are ignored, so the returned path is always a
    // "reasonable journey" for the caller's search radius.
    public Tile Find(Func <Tile, bool> condition, int r = Task.MediumFindRadius){
        Tile closestTile = null;
        float closestDistance = float.MaxValue;
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(a.x + x, a.y + y);
                if (tile != null && condition(tile)) {
                    float distance = SquareDistance((float)tile.x, a.x, (float)tile.y, a.y);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestTile = tile;
                    }
                }
            }
        }
        return closestTile;
    }

    public Path FindPathTo(Func<Tile, bool> condition, int r = Task.MediumFindRadius){
        // Sort matching tiles by Chebyshev (crow-flies), then pathfind in order and return the
        // FIRST candidate whose path fits within budget. Not guaranteed minimum-cost across all
        // candidates — but in practice the nearest crow-flies candidate is almost always also
        // the shortest walk, and we avoid pathfinding the rest of the box.
        float maxCost = r * Task.FindRadiusTolerance;
        Node myNode = a.TileHere().node;
        var candidates = new List<(int cheb, Tile tile)>();
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(a.x + x, a.y + y);
                if (tile != null && condition(tile)) {
                    candidates.Add((Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)), tile));
                }
            }
        }
        candidates.Sort((p, q) => p.cheb.CompareTo(q.cheb));
        foreach (var (_, tile) in candidates) {
            Path p = world.graph.Navigate(myNode, tile.node);
            if (p != null && p.cost <= maxCost) return p;
        }
        return null;
    }

    // Returns (matchingTile, pathToStandingPosition).
    // Use when the target tile might not be standable (e.g. blueprints for walls/stairs).
    public (Tile, Path) FindPathAdjacentTo(Func<Tile, bool> condition, int r = Task.MediumFindRadius){
        // First-fit by Chebyshev order. See FindPathTo for rationale.
        float maxCost = r * Task.FindRadiusTolerance;
        var candidates = new List<(int cheb, Tile tile)>();
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(a.x + x, a.y + y);
                if (tile != null && condition(tile)) {
                    candidates.Add((Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)), tile));
                }
            }
        }
        candidates.Sort((p, q) => p.cheb.CompareTo(q.cheb));
        foreach (var (_, tile) in candidates) {
            Path cPath = PathToOrAdjacent(tile);
            if (cPath != null && cPath.cost <= maxCost) return (tile, cPath);
        }
        return (null, null);
    }

    // ── Specific path finders ────────────────────────────────────────────────
    //    FindPathToX           = animal walks to the tile
    //    FindPathAdjacentToX   = animal walks next to the tile

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
        float maxCost = r * Task.FindRadiusTolerance;
        Node myNode = a.TileHere().node;
        var candidates = new List<(int cheb, Inventory inv, Tile tile)>();
        foreach (var type in types) {
            if (!ic.byType.TryGetValue(type, out var list)) continue;
            foreach (Inventory inv in list) {
                int cheb = Mathf.Max(Mathf.Abs(inv.x - (int)a.x), Mathf.Abs(inv.y - (int)a.y));
                if (cheb > r) continue;
                if (!filter(inv)) continue;
                Tile t = world.GetTileAt(inv.x, inv.y);
                if (t == null) continue;
                candidates.Add((cheb, inv, t));
            }
        }
        candidates.Sort((x, y) => x.cheb.CompareTo(y.cheb));
        foreach (var (_, inv, t) in candidates) {
            Path p = world.graph.Navigate(myNode, t.node);
            if (p != null && p.cost <= maxCost) return (p, inv);
        }
        return (null, null);
    }
    private Path FindPathToStruct(StructType st, Func<Structure, bool> filter = null, int r = Task.MediumFindRadius) {
        var list = StructController.instance.GetByType(st);
        if (list == null) return null;
        float maxCost = r * Task.FindRadiusTolerance;
        Node myNode = a.TileHere().node;
        var candidates = new List<(int cheb, Structure s)>();
        foreach (Structure s in list) {
            int cheb = Mathf.Max(Mathf.Abs(s.x - (int)a.x), Mathf.Abs(s.y - (int)a.y));
            if (cheb > r) continue;
            if (filter != null && !filter(s)) continue;
            candidates.Add((cheb, s));
        }
        candidates.Sort((x, y) => x.cheb.CompareTo(y.cheb));
        foreach (var (_, s) in candidates) {
            Path p = world.graph.Navigate(myNode, s.workTile.node);
            if (p != null && p.cost <= maxCost) return p;
        }
        return null;
    }
    public Path FindPathToBuilding(StructType structType, int r = Task.MediumFindRadius) {
        return FindPathToStruct(structType, s => s.res.Available(), r);
    }
    public Path FindMarketPath() {
        // Any standable tile on the x=0 column acts as the portal to the off-screen market.
        // Searching the whole column means a wall at the market's exact tile won't block merchants.
        return FindPathTo(t => t.x == 0 && t.node.standable, r: Task.MarketFindRadius);
    }
    public Path FindPathToHarvestable(Job job, int r = Task.MediumFindRadius) {
        float maxCost = r * Task.FindRadiusTolerance;
        Node myNode = a.TileHere().node;
        var candidates = new List<(int cheb, Structure s)>();
        foreach (var st in Db.structTypeByName.Values) {
            if (!st.isPlant || st.job != job) continue;
            var list = StructController.instance.GetByType(st);
            if (list == null) continue;
            foreach (Structure s in list) {
                int cheb = Mathf.Max(Mathf.Abs(s.x - (int)a.x), Mathf.Abs(s.y - (int)a.y));
                if (cheb > r) continue;
                if (!(s is Plant p) || !p.harvestable || !s.res.Available()) continue;
                candidates.Add((cheb, s));
            }
        }
        candidates.Sort((x, y) => x.cheb.CompareTo(y.cheb));
        foreach (var (_, s) in candidates) {
            Path pa = world.graph.Navigate(myNode, s.workTile.node);
            if (pa != null && pa.cost <= maxCost) return pa;
        }
        return null;
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



    // ── Utils ────────────────────────────────────────────────────────────────
    public float SquareDistance(float x1, float x2, float y1, float y2){return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);}
    public bool CanReach(Tile t) {
        if (t?.node == null) return false;
        Node myNode = a.TileHere()?.node;
        if (myNode == null) return false;
        return world.graph.SameComponent(myNode, t.node);
    }

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
            if (world.graph.SameComponent(myNode, s.workTile.node)) return true;
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

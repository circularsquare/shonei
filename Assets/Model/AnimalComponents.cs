using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class Nav {
    public Animal a; // have this be more generic? idk
    public World world;
    
    public Path path; 
    private int pathIndex = 0;
    private Node prevNode = null;
    private Node nextNode = null;


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
    public void EndNavigation(){ path = null; pathIndex = 0; prevNode = null; nextNode = null;} // should stop you from moving
    public void Fall(){ // navigates to tileBelow, even though theres no path cuz unstandable
        if (!a.TileHere().node.standable) {
            if (a.task != null && a.task is not FallTask) {
                Debug.Log(a.aName + " falling! interrupting current task "  + a.task.ToString());
                a.task.Fail();
            }
            a.task = new FallTask(a);
            a.task.Start();
        }
    }

    public bool Move(float deltaTime){ // called by animal every frame!! returns whether you're done
        if (path == null || pathIndex >= path.length){return true;}  // no path... return true, give up
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
        Vector2 newPos = Vector2.MoveTowards(
            new Vector2(a.x, a.y), new Vector2(nextNode.wx, nextNode.wy),
            a.maxSpeed * deltaTime);
        a.x = newPos.x; a.y = newPos.y;
        a.go.transform.position = new Vector3(a.x, a.y, 0);

        a.isMovingRight = (nextNode.wx - a.x > 0);
        return false;
    }


    // =========================================================
    //  Naming convention:
    //    Find(...)            -> returns Tile. No pathfinding. Can find unreachable tiles.
    //    FindPathTo(...)      -> returns Path. Animal goes TO the matching tile.
    //    FindPathAdjacentTo(...)  -> returns (Tile, Path). Animal stands NEXT TO matching tile.
    // =========================================================
    public Tile Find(Func <Tile, bool> condition, int r = 40, bool persistent = false){
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
        if (persistent && closestTile == null && r < 60){ 
            Debug.Log("no tile found. expanding radius to " + (r + 5));
            return (Find(condition, r + 4, persistent));
        }
        return closestTile;
    }

    public Path FindPathTo(Func<Tile, bool> condition, int r = 40, bool persistent = false){
        Path closestPath = null;
        float closestDistance = float.MaxValue;
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(a.x + x, a.y + y);
                if (tile != null && condition(tile)) {
                    Path cPath = world.graph.Navigate(a.TileHere().node, tile.node);
                    if (cPath == null) { continue; }
                    float distance = cPath.cost;
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestPath = cPath;
                    }
                }
            }
        } 
        if (persistent && closestPath == null && r < 20){ 
            Debug.Log("no tile found for " + a.name + ", expanding radius to " + (r + 5));
            return (FindPathTo(condition, r + 4, persistent));
        }
        return closestPath;
    }
    
    // Returns (matchingTile, pathToStandingPosition).
    // Use when the target tile might not be standable (e.g. blueprints for walls/stairs).
    public (Tile, Path) FindPathAdjacentTo(Func<Tile, bool> condition, int r = 40){
        (Tile, Path) best = (null, null);
        float bestCost = float.MaxValue;
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(a.x + x, a.y + y);
                if (tile != null && condition(tile)) {
                    Path cPath = PathToOrAdjacent(tile);
                    if (cPath != null && cPath.cost < bestCost) {
                        bestCost = cPath.cost;
                        best = (tile, cPath);
                    }
                }
            }
        }
        return best;
    }

    // =========================================================
    //  SPECIFIC PATH FINDERS 
    //  "FindPathToX"  = animal walks to the tile
    //  "FindPathAdjacentToX" = animal walks next to the tile
    // =========================================================

    public Path FindPathToItem(Item item, int r = 40){ 
        return FindPathTo(t => t.ContainsAvailableItem(item), r); }
    public Tile FindItem(Item item, int r = 40){
        return Find(t => t.ContainsAvailableItem(item), r); }
    public Path FindPathToStorage(Item item, int r = 40){ 
        return FindPathTo(t => t.HasStorageForItem(item), r); }
    public Path FindPathToDrop(Item item, int r = 3){ 
        return FindPathTo(t => t.HasSpaceForItem(item), r, true); }
    public Path FindPathToBuilding(StructType structType, int r = 40){
        return FindPathTo(t => t.building != null && t.building.structType == structType &&
            t.building.res.Available(), r);
    }

    public Path FindPathToHarvestable(Job job, int r = 40){
        return FindPathTo(t => t.building != null && t.building is Plant 
        && (t.building as Plant).harvestable
        && t.building.res.Available()
        && t.building.structType.job == job, r);
    }
    public (Path, ItemStack) FindPathItemStack(Item item, int r = 40){
        Path path = FindPathTo(t => t.ContainsAvailableItem(item), r);
        if (path == null) return (null, null);
        ItemStack stack = path.tile.inv.GetItemStack(item); // or however you access it
        return (path, stack);
    }

    // --- Blueprints (use adjacent, since blueprint tile may not be standable) ---
    public (Tile, Path) FindPathAdjacentToBlueprint(Job job, Blueprint.BlueprintState bpState, int r = 40){
        var candidates = new List<(Tile tile, Path path, Blueprint bp)>();
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(a.x + x, a.y + y);
                if (tile == null) continue;
                Blueprint bp = tile.GetMatchingBlueprint(b =>
                    b.structType.job == job && b.state == bpState && b.res.Available());
                if (bp == null) continue;
                Path path = PathToOrAdjacent(tile);
                if (path != null) candidates.Add((tile, path, bp));
            }
        }
        if (candidates.Count == 0) return (null, null);
        candidates.Sort((a, b) => {
            int pc = b.bp.priority.CompareTo(a.bp.priority); // higher priority first
            return pc != 0 ? pc : a.path.cost.CompareTo(b.path.cost); // tiebreak: closer first
        });
        return (candidates[0].tile, candidates[0].path);
    }
    public HaulInfo FindFloorConsolidation(int r = 50) {
        // Collect all floor haul-eligible tiles sorted by path cost, try each as a source
        var candidates = new List<Path>();
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(a.x + x, a.y + y);
                if (tile != null && tile.inv != null &&
                    tile.inv.invType == Inventory.InvType.Floor &&
                    tile.inv.GetItemToHaul() != null) {
                    Path path = world.graph.Navigate(a.TileHere().node, tile.node);
                    if (path != null) candidates.Add(path);
                }
            }
        }
        candidates.Sort((p1, p2) => p1.cost.CompareTo(p2.cost));

        foreach (Path sourcePath in candidates) {
            ItemStack sourceStack = sourcePath.tile.inv.GetItemToHaul();
            if (sourceStack == null) continue;
            Item item = sourceStack.item;
            if (item == null) continue;
            Tile sourceTile = sourcePath.tile;

            // Item has storage available — should be hauled, not consolidated
            if (FindPathToStorage(item) != null) continue;

            // Find another floor tile with same item that has a partial stack with room
            Path destPath = FindPathTo(t =>
                t != sourceTile &&
                t.inv != null &&
                t.inv.invType == Inventory.InvType.Floor &&
                t.inv.HasSpaceForItem(item), r);
            if (destPath == null) continue;

            // Always move smaller stack into bigger stack
            Tile destTile = destPath.tile;
            if (sourceTile.inv.Quantity(item) > destTile.inv.Quantity(item)) {
                (sourceTile, destTile) = (destTile, sourceTile);
                sourceStack = sourceTile.inv.GetItemToHaul();
                if (sourceStack == null) continue;
            }

            int qty = Math.Min(sourceTile.inv.AvailableQuantity(item), destTile.inv.GetMergeSpace(item));
            if (qty <= 0) continue;
            // Skip tiny moves unless we'd be clearing the entire smaller stack
            if (qty < 90 && qty < sourceTile.inv.Quantity(item)) continue;

            return new HaulInfo(item, qty, sourceTile, destTile, sourceStack);
        }
        return null;
    }

    public HaulInfo FindAnyItemToHaul(int r = 50){
        // Collect all haul-eligible tiles sorted by path cost, try each until one has storage
        var candidates = new List<Path>();
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(a.x + x, a.y + y);
                if (tile != null && tile.HasItemToHaul(null)) {
                    Path path = world.graph.Navigate(a.TileHere().node, tile.node);
                    if (path != null) candidates.Add(path);
                }
            }
        }
        candidates.Sort((p1, p2) => p1.cost.CompareTo(p2.cost));
        foreach (Path itemPath in candidates) {
            ItemStack itemStack = itemPath.tile.inv.GetItemToHaul();
            if (itemStack == null) continue;
            Item item = itemStack.item;
            if (item == null) continue;
            Path storagePath = FindPathToStorage(item, r);
            if (storagePath == null) continue;
            int quantity = Math.Min(
                itemPath.tile.inv.Quantity(item),
                storagePath.tile.GetStorageForItem(item)
            );
            return new HaulInfo(item, quantity, itemPath.tile, storagePath.tile, itemStack);
        }
        return null;
    }


    // =========================================================
    //  PATH TO SPECIFIC KNOWN TILE
    // =========================================================

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



    // ========= utils =============
    public float SquareDistance(float x1, float x2, float y1, float y2){return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);}
    public bool IsReachable(Tile t){ // TODO: implement! can you reach this tile from where you are?
        return t.node.standable;
    }


}


public class Eating {
    public float maxFood = 100f;
    public float food = 90f;
    public float hungerRate = 0.5f;
    public float timeSinceLastAte = 9999f;

    public Eating(){ }

    public float Fullness(){ return food / maxFood; }
    public bool Hungry(){ return food / maxFood < 0.5f; }
    public bool AteRecently(){ return timeSinceLastAte < 300f; } // within 5 min

    public float Efficiency(){
        if (Fullness() > 0.5f){
            return 1f;
        } else {
            return Fullness() * 2f * 0.8f + 0.2f; // 20% at worst.
        }
    }
    public void Eat(float nFood){
        food += nFood;
        timeSinceLastAte = 0f;
    }
    public void SlowUpdate(float t = 10f){
        timeSinceLastAte += t;
    }
    public void Update(float t = 1f){
        food -= hungerRate * t;
        if (food < 0f){food = 0f;}
    }
}

public class Happiness {
    public bool house;
    public float score;

    const float recentThreshold = 120f;
    const float soonThreshold = 30f;
    const float maxTime = recentThreshold * 1.5f;
    public float timeSinceAteWheat = maxTime;
    public float timeSinceAteFruit = maxTime;

    public Happiness(){}

    public void NoteAte(Item food, float fraction = 1f) {
        if (food.name == "wheat")      timeSinceAteWheat = Mathf.Max(0f, timeSinceAteWheat - fraction * recentThreshold);
        else if (food.name == "apple") timeSinceAteFruit = Mathf.Max(0f, timeSinceAteFruit - fraction * recentThreshold);
        // add more mappings here as new foods are added
    }

    // True if eating this food would satisfy a currently-unhappy category
    public bool WouldHelp(Item food) {
        if (food.name == "wheat") return timeSinceAteWheat >= recentThreshold - soonThreshold;
        if (food.name == "apple") return timeSinceAteFruit >= recentThreshold - soonThreshold;
        return false;
    }

    public void SlowUpdate(Animal a){
        timeSinceAteWheat = Mathf.Min(timeSinceAteWheat + 10f, maxTime);
        timeSinceAteFruit = Mathf.Min(timeSinceAteFruit + 10f, maxTime);
        bool wheat = timeSinceAteWheat < recentThreshold;
        bool fruit = timeSinceAteFruit < recentThreshold;
        house = a.HasHouse;
        score = (wheat ? 1f : 0f) + (fruit ? 1f : 0f) + (house ? 1f : 0f);
    }

    public override string ToString(){
        bool wheat = timeSinceAteWheat < recentThreshold;
        bool fruit = timeSinceAteFruit < recentThreshold;
        return $"wheat: {(wheat?1:0)}/1, fruit: {(fruit?1:0)}/1, housing: {(house?1:0)}/1  ({score:0.0})";
    }
}

public class Eeping {
    public float maxEep = 100f;
    public float eep = 90f;
    public static float tireRate = 0.3f;
    public static float eepRate = 4f;
    public static float outsideEepRate = 2f;

    public Eeping(){}
    public bool Eepy(){
        return eep / maxEep < 0.5f;
    }
    public float Efficiency(){
        if (eep / maxEep > 0.5f){
            return 1f;
        } else {
            return eep / maxEep * 2f * 0.8f + 0.2f; // 20% at worst.
        }
    }
    public float Eepness(){ return eep / maxEep; }
    public void Eep(float t, bool atHome){
        if (atHome){ eep += t * eepRate; }
        else { eep += t * outsideEepRate; }
    }
    public void Update(float t = 1f){
        eep -= tireRate * t;
        if (eep < 0f){eep = 0f;}
    }

}



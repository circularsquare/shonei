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
    public bool Fall(){ // navigates to tileBelow, even though theres no path cuz unstandable
        List<Node> pathNodes = new List<Node>();
        pathNodes.Add(a.TileHere().node);
        pathNodes.Add(world.GetTileAt(a.x, a.y - 1).node);
        Path fallPath = new Path(pathNodes);
        NavigateTo(fallPath.tile, fallPath);
        a.state = Animal.AnimalState.Walking;
        return true;
    }

    public bool Move(float deltaTime){ // called by animal every frame!! returns whether you're done
        if (path == null || pathIndex >= path.length){return true;}  // no path... return true, give up
        if (SquareDistance(a.x, nextNode.x, a.y, nextNode.y) < 0.001f){
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
            new Vector2(a.x, a.y), new Vector2(nextNode.x, nextNode.y), 
            a.maxSpeed * deltaTime);

        a.x = newPos.x; a.y = newPos.y;
        a.go.transform.position = new Vector3(a.x, a.y, 0);

        a.isMovingRight = (nextNode.x - a.x > 0);
        return false;
    }


    public Path FindItem(Item item, int r = 50){ return FindPath(t => t.ContainsItem(item), r); }
    public Path FindItemToHaul(Item item, int r = 50){ return FindPath(t => t.HasItemToHaul(item), r); }
    public Path FindStorage(Item item, int r = 50){ return FindPath(t => t.HasStorageForItem(item), r); }
    public Path FindPlaceToDrop(Item item, int r = 3){ return FindPath(t => t.HasSpaceForItem(item), r, true); }
    public Path FindBuilding(BuildingType buildingType, int r = 50){
        return FindPath(t => t.building != null && t.building.buildingType == buildingType && 
            t.building.capacity - t.building.reserved > 0, r);
    }
    public Path FindBuilding(StructType structType, int r = 50){ return FindBuilding(structType as BuildingType, r);}
    public Path FindStructure(StructType structType, int r = 50){

        Debug.LogError("FindStructure not implemented!"); return null;}
    public Path FindWorkTile(TileType tileType, int r = 50){
        return FindPath(t => t.type == tileType && (t.building != null) && (t.building.capacity - t.building.reserved > 0) && 
            !(t.building != null && t.building is Plant), r);
    }
    public Path FindWorkTile(string tileTypeStr, int r = 30){ return FindWorkTile(Db.tileTypeByName[tileTypeStr], r); }
    public Path FindReceivingBlueprint(Job job, int r = 50){return FindPath(t => t.blueprint != null 
        && t.blueprint.structType.job == job 
        && t.blueprint.state == Blueprint.BlueprintState.Receiving, r);}
    public Path FindPathConstructingBlueprint(Job job, int r = 50){return FindPath(t => t.blueprint != null 
        && t.blueprint.structType.job == job 
        && t.blueprint.state == Blueprint.BlueprintState.Constructing, r);}
    public Tile FindConstructingBlueprint(Job job, int r = 50){return Find(t => t.blueprint != null 
        && t.blueprint.structType.job == job 
        && t.blueprint.state == Blueprint.BlueprintState.Constructing, r);}
    public Path FindHarvestable(Job job, int r = 40){
        return FindPath(t => t.building != null && t.building is Plant 
        && (t.building as Plant).harvestable
        && (t.building.buildingType.job == job), r); // something about jobs here?
    }
    public Path FindPathTo(Tile tile){
        return (world.graph.Navigate(a.TileHere().node, tile.node));
    }
    public Path FindPath(Func<Tile, bool> condition, int r, bool persistent = false){
        Path closestPath = null;
        float closestDistance = float.MaxValue;
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(a.x + x, a.y + y);
                if (tile != null && condition(tile)) {
                    // wasn't this reversed for a while but it seemed to work? vvv
                    Path cPath = world.graph.Navigate(a.TileHere().node, tile.node);
                    if (cPath == null) { continue; }
                    float distance = cPath.cost; // try cost later.
                    //float distance = SquareDistance((float)tile.x, a.x, (float)tile.y, a.y);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestPath = cPath;
                    }
                }
            }
        } // should check in a wider radius if none found...
        if (persistent && closestPath == null && r < 20){ 
            Debug.Log("no tile found. expanding radius to " + (r + 3));
            return (FindPath(condition, r + 4, persistent));
        }
        return closestPath;
    }
    
    public Tile Find(Func <Tile, bool> condition, int r, bool persistent = false){
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
        } // should check in a wider radius if none found...
        if (persistent && closestTile == null && r < 60){ 
            Debug.Log("no tile found. expanding radius to " + (r + 3));
            return (Find(condition, r + 4, persistent));
        }
        return closestTile;
    }

    public Path FindAnyItemToHaul(int r = 50){ 
        float closestDistance = float.MaxValue;
        Path closestItemPath = null;
        Tile closestStorage = null;
        Item closestItem = null;
        Path itemPath = FindPath(t => t.HasItemToHaul(null), r);
        if (itemPath != null){
            Item item = itemPath.tile.inv.GetItemToHaul();
            if (item != null){
                Path storagePath = FindStorage(item, r=50);
                if (storagePath != null){
                    float distance = itemPath.length;
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestItemPath = itemPath;
                        closestStorage = storagePath.tile;
                        closestItem = item;
                    }
                }
            }
        }
        if (closestItemPath != null){
            Navigate(closestItemPath);
            a.storageTile = closestStorage;
            a.desiredItem = closestItem;
            a.desiredItemQuantity = Math.Min(closestItemPath.tile.inv.Quantity(closestItem),
                closestStorage.GetStorageForItem(closestItem)); // don't take more than u can store
            return closestItemPath;
        } else {
            a.Refresh();
            return null;
        }
    }


    public Path FindAdjacentTile(Tile target) {
            // Check all adjacent tiles
        Vector2Int[] adjacentOffsets = new[] {
            new Vector2Int(0, 1),
            new Vector2Int(1, 0),
            new Vector2Int(0, -1),
            new Vector2Int(-1, 0) };
        foreach (var offset in adjacentOffsets) {
            Tile adjacentTile = world.GetTileAt(
                target.x + offset.x, 
                target.y + offset.y );  
            if (adjacentTile != null && IsReachable(adjacentTile)) {
                return world.graph.Navigate(a.TileHere().node, adjacentTile.node);
            }
        }        
        return null;
    }

    public bool IsReachable(Tile t){ // TODO: implement! can you reach this tile from where you are?
        return t.node.standable;
    }


    // ========= utils =============
    public float SquareDistance(float x1, float x2, float y1, float y2){return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);}


}


public class Eating {
    public float maxFood = 100f;
    public float food = 90f;
    public float hungerRate = 0.5f;

    public Eating(){ }
    
    public float Fullness(){ return food / maxFood; }
    public bool Hungry(){ return food / maxFood < 0.5f; }

    public float Efficiency(){
        if (Fullness() > 0.5f){
            return 1f;
        } else {
            return Fullness() * 2f * 0.8f + 0.2f; // 20% at worst.
        }
    }
    public void Eat(float nFood){
        food += nFood;
    }
    public void Update(float t = 1f){
        food -= hungerRate * t;
        if (food < 0f){food = 0f;}
    }
}

public class Eeping {
    public float maxEep = 100f;
    public float eep = 90f;
    public static float tireRate = 0.3f;
    public static float eepRate = 5f;
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



using UnityEngine;
using System;
using System.Collections.Generic;

public class AnimalNavigation {
    private Animal animal;
    private World world;
    private Path path;
    private int pathIndex = 0;
    private Node prevNode = null;
    private Node nextNode = null;

    public AnimalNavigation(Animal animal) {
        this.animal = animal;
        this.world = animal.world;
        path = null;
    }

    // Navigation methods
    public bool NavigateTo(Tile t, Path iPath = null) {
        if (t == null && iPath == null) {
            Debug.LogError("navigating without destination or path!");
            return false;
        }
        if (iPath != null) {
            path = iPath;
            t = iPath.tile;
        } else {
            path = world.graph.Navigate(animal.TileHere().node, t.node);
        }
        if (path == null) { return false; }

        pathIndex = 0;
        prevNode = path.nodes[0];
        if (path.length != 0) {
            nextNode = path.nodes[1];
        } else {
            nextNode = prevNode;
        }
        animal.target = t;
        return true;
    }

    public bool Navigate(Path p) {
        return NavigateTo(p.tile, p);
    }

    public void EndNavigation() {
        path = null;
        pathIndex = 0;
        prevNode = null;
        nextNode = null;
    }

    public bool Fall() {
        List<Node> pathNodes = new List<Node>();
        pathNodes.Add(animal.TileHere().node);
        pathNodes.Add(world.GetTileAt(animal.x, animal.y - 1).node);
        Path fallPath = new Path(pathNodes);
        NavigateTo(fallPath.tile, fallPath);
        animal.state = Animal.AnimalState.Walking;
        return true;
    }

    public bool Move(float deltaTime) {
        if (path == null || pathIndex >= path.length) {
            return true;
        }
        if (SquareDistance(animal.x, nextNode.x, animal.y, nextNode.y) < 0.001f) {
            if (pathIndex + 1 >= path.length) {
                EndNavigation();
                return true;
            } else {
                pathIndex++;
                prevNode = nextNode;
                nextNode = path.nodes[pathIndex + 1];
            }
        }

        Vector2 newPos = Vector2.MoveTowards(
            new Vector2(animal.x, animal.y),
            new Vector2(nextNode.x, nextNode.y),
            animal.maxSpeed * deltaTime);

        animal.x = newPos.x;
        animal.y = newPos.y;
        animal.go.transform.position = new Vector3(animal.x, animal.y, 0);

        animal.isMovingRight = (nextNode.x - animal.x > 0);
        return false;
    }

    // Path finding methods
    public Path FindItem(Item item, int r = 50) {
        return FindPath(t => t.ContainsItem(item), r);
    }

    public Path FindItemToHaul(Item item, int r = 50) {
        return FindPath(t => t.HasItemToHaul(item), r);
    }

    public Path FindStorage(Item item, int r = 50) {
        return FindPath(t => t.HasStorageForItem(item), r);
    }

    public Path FindPlaceToDrop(Item item, int r = 3) {
        return FindPath(t => t.HasSpaceForItem(item), r, true);
    }

    public Path FindBuilding(BuildingType buildingType, int r = 50) {
        return FindPath(t => t.building != null && t.building.buildingType == buildingType &&
            t.building.capacity - t.building.reserved > 0, r);
    }

    public Path FindBuilding(StructType structType, int r = 50) {
        return FindBuilding(structType as BuildingType, r);
    }

    public Path FindWorkTile(TileType tileType, int r = 50) {
        return FindPath(t => t.type == tileType && (t.building != null) && (t.building.capacity - t.building.reserved > 0) &&
            !(t.building != null && t.building is Plant), r);
    }

    public Path FindWorkTile(string tileTypeStr, int r = 30) {
        return FindWorkTile(Db.tileTypeByName[tileTypeStr], r);
    }

    public Path FindReceivingBlueprint(Job job, int r = 50) {
        return FindPath(t => t.blueprint != null
            && t.blueprint.structType.job == job
            && t.blueprint.state == Blueprint.BlueprintState.Receiving, r);
    }

    public Path FindPathConstructingBlueprint(Job job, int r = 50) {
        return FindPath(t => t.blueprint != null
            && t.blueprint.structType.job == job
            && t.blueprint.state == Blueprint.BlueprintState.Constructing, r);
    }

    public Tile FindConstructingBlueprint(Job job, int r = 50) {
        return Find(t => t.blueprint != null
            && t.blueprint.structType.job == job
            && t.blueprint.state == Blueprint.BlueprintState.Constructing, r);
    }

    public Path FindHarvestable(Job job, int r = 40) {
        return FindPath(t => t.building != null && t.building is Plant
            && (t.building as Plant).harvestable
            && (t.building.buildingType.job == job), r);
    }

    public Path FindPathTo(Tile tile) {
        return world.graph.Navigate(animal.TileHere().node, tile.node);
    }

    public Path FindPath(Func<Tile, bool> condition, int r, bool persistent = false) {
        Path closestPath = null;
        float closestDistance = float.MaxValue;
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(animal.x + x, animal.y + y);
                if (tile != null && condition(tile)) {
                    Path cPath = world.graph.Navigate(animal.TileHere().node, tile.node);
                    if (cPath == null) { continue; }
                    float distance = cPath.cost;
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestPath = cPath;
                    }
                }
            }
        }
        if (persistent && closestPath == null && r < 20) {
            Debug.Log("no tile found for " + animal.name + ", expanding radius to " + (r + 3));
            return FindPath(condition, r + 4, persistent);
        }
        return closestPath;
    }

    public Tile Find(Func<Tile, bool> condition, int r, bool persistent = false) {
        Tile closestTile = null;
        float closestDistance = float.MaxValue;
        for (int x = -r; x <= r; x++) {
            for (int y = -r; y <= r; y++) {
                Tile tile = world.GetTileAt(animal.x + x, animal.y + y);
                if (tile != null && condition(tile)) {
                    float distance = SquareDistance((float)tile.x, animal.x, (float)tile.y, animal.y);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                        closestTile = tile;
                    }
                }
            }
        }
        if (persistent && closestTile == null && r < 60) {
            Debug.Log("no tile found. expanding radius to " + (r + 3));
            return Find(condition, r + 4, persistent);
        }
        return closestTile;
    }

    public Path FindAnyItemToHaul(int r = 50) {
        float closestDistance = float.MaxValue;
        Path closestItemPath = null;
        Tile closestStorage = null;
        Item closestItem = null;
        Path itemPath = FindPath(t => t.HasItemToHaul(null), r);
        if (itemPath != null) {
            Item item = itemPath.tile.inv.GetItemToHaul();
            if (item != null) {
                Path storagePath = FindStorage(item, r = 50);
                if (storagePath != null) {
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
        if (closestItemPath != null) {
            Navigate(closestItemPath);
            animal.storageTile = closestStorage;
            animal.desiredItem = closestItem;
            animal.desiredItemQuantity = Math.Min(closestItemPath.tile.inv.Quantity(closestItem),
                closestStorage.GetStorageForItem(closestItem));
            return closestItemPath;
        } else {
            animal.Refresh();
            return null;
        }
    }

    public bool IsReachable(Tile t) {
        return t.node.standable;
    }

    private float SquareDistance(float x1, float x2, float y1, float y2) {
        return (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);
    }
} 
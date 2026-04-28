using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class GoObjective : Objective {
    // Either a tile-backed destination (most callers) or an off-grid waypoint Node
    // (workspot-aware buildings like the wheel). Exactly one is non-null.
    private Tile destinationTile;
    private Node destinationNode;
    public GoObjective(Task task, Tile destination) : base(task) {
        this.destinationTile = destination;
    }
    public GoObjective(Task task, Node destination) : base(task) {
        this.destinationNode = destination;
    }
    public override void Start(){
        Path path;
        if (destinationNode != null) {
            path = animal.nav.PathTo(destinationNode);
        } else {
            if (animal.TileHere() == destinationTile) {Complete(); return;}
            path = animal.nav.PathTo(destinationTile);
        }
        if (path != null){
            animal.nav.Navigate(path);
            animal.state = Animal.AnimalState.Moving;
        } else {Fail();}
    }
    public override void OnArrival(){ Complete(); }
    public override string GetObjectiveName() {
        if (destinationTile != null) return $"Go ({destinationTile.x}, {destinationTile.y})";
        if (destinationNode != null) return $"Go ({destinationNode.wx:F2}, {destinationNode.wy:F2})";
        return "Go";
    }
}

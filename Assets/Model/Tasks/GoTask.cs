using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class GoTask : Task {
    public Tile tile;
    public GoTask (Animal animal, Tile tile) : base(animal){ this.tile = tile;}
    public override bool Initialize(){
        if (animal.nav.PathTo(tile) == null) return false;
        objectives.AddLast(new GoObjective(this, tile)); return true;
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class DropTask : Task {
    public DropTask(Animal animal) : base(animal){}
    public override bool Initialize(){
        List<Item> itemsToDrop = animal.inv.GetItemsList();
        if (itemsToDrop.Count == 0) { return false; }
        foreach (Item item in itemsToDrop) {
            objectives.AddLast(new DropObjective(this, item)); }
        return true;
    }
}

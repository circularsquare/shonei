using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Dumps every item currently in the animal's inventory. Run by ChooseTask as the very
// first survival step — clears stale carry-over before any new work is picked.
//
// Queue: Drop(item₁) → Drop(item₂) → … (one per distinct item).
// Reserves: Nothing.
// IsWork = false.
public class DropTask : Task {
    public override bool IsWork => false;
    public DropTask(Animal animal) : base(animal){}
    public override bool Initialize(){
        List<Item> itemsToDrop = animal.inv.GetItemsList();
        if (itemsToDrop.Count == 0) { return false; }
        foreach (Item item in itemsToDrop) {
            objectives.AddLast(new DropObjective(this, item)); }
        return true;
    }
}

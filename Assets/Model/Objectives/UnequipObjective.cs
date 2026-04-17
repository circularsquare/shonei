using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Moves the contents of an equip slot back to the animal's main inventory and completes.
// Used as a step before returning a borrowed item (e.g. a book) to its storage location:
// callers typically queue this immediately before a DropObjective for the same item.
// No-op if the slot is empty.
public class UnequipObjective : Objective {
    private Inventory slot;
    public UnequipObjective(Task task, Inventory slot) : base(task) { this.slot = slot; }
    public override void Start() {
        animal.Unequip(slot);
        Complete();
    }
}

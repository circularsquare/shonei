using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class DropObjective : Objective {
    private Item item;
    private Tile destination;
    private Inventory targetInv; // null = drop on floor
    public DropObjective(Task task, Item item) : base(task) {
        this.item = item;
    }
    public override void Start(){
        if (animal.inv.Quantity(item) == 0) {Complete(); return;}
        int qty = animal.inv.Quantity(item);
        var (dropPath, storageInv) = animal.nav.FindPathToDropTarget(item, qty);
        if (dropPath != null){
            targetInv = storageInv;
            destination = dropPath.tile;
            // Reserve destination space (best-effort — don't fail if no space, floor drop is always possible)
            Inventory destInv = storageInv ?? dropPath.tile.EnsureFloorInventory();
            task.ReserveSpace(destInv, item, qty);
            animal.nav.Navigate(dropPath);
            animal.state = Animal.AnimalState.Moving;
        } else {
            // No reachable drop target (boxed in, all neighbours full, etc.) — warn and
            // back off for 3s so ChooseTask falls through to other branches instead of
            // respawning DropTask every tick. Warning (not error) because this is a
            // recoverable, expected-at-edge-of-colony condition.
            Debug.LogWarning($"{animal.aName} ({animal.job.name}) can't find a place to drop {item.name} at ({(int)animal.x},{(int)animal.y}) — retrying in 3s");
            animal.dropCooldownUntil = World.instance.timer + 3f;
            Fail();
        }
    }
    public override void OnArrival(){
        if (targetInv != null && targetInv.GetStorageForItem(item) > 0) {
            animal.inv.MoveItemTo(targetInv, item, animal.inv.Quantity(item));
        } else {
            animal.DropItem(item);
        }
        Complete();
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Finds the best drop target for a carried item (storage preferred, floor fallback),
// walks there, and unloads. Loops across targets until the item is fully offloaded —
// tops off the nearest storage, then spills the remainder to the next storage/floor
// (floor is the guaranteed sink), mirroring FetchObjective's cross-tile retry. Two
// back-off cases set a 3 s dropCooldown and stop so ChooseTask's drop pre-step doesn't
// spin every tick: no reachable target (Fail), and a visit that deposits 0 fen — a
// discrete item or full floor tile that re-searching would re-pick (Complete, not Fail,
// so chained/best-effort callers survive a stuck remainder).
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
        int before = animal.inv.Quantity(item);
        if (before == 0) { Complete(); return; } // nothing left (defensive)

        int moved;
        if (targetInv != null && targetInv.GetStorageForItem(item) > 0) {
            moved = animal.inv.MoveItemTo(targetInv, item, before);
        } else {
            moved = before - animal.DropItem(item); // DropItem returns the leftover it couldn't place
        }

        if (animal.inv.Quantity(item) == 0) { Complete(); return; } // fully offloaded

        // No progress this visit — a single target (a near-full crate the storage filter still let
        // through, or this tile's floor) couldn't take any. Re-searching would re-pick the same
        // target and loop forever, so give up on the remainder. Back off 3s (same as the no-path
        // branch in Start) so ChooseTask's drop pre-step doesn't re-spawn this every idle tick —
        // otherwise a permanently-stuck remainder busy-loops the planner and spams this warning.
        // Complete (not Fail) so chained/best-effort callers (Craft/Harvest outputs, book returns)
        // aren't torn down.
        if (moved == 0) {
            Debug.LogWarning($"{animal.aName} ({animal.job.name}) dropped 0 of {item.name} at " +
                             $"({(int)animal.x},{(int)animal.y}); {animal.inv.Quantity(item)} fen stuck — giving up");
            animal.dropCooldownUntil = World.instance.timer + 3f;
            Complete(); return;
        }

        // Storage topped off but more remains (or this was a partial floor drop) — find the next
        // target and continue until the item is fully gone. A filled storage now reports
        // GetStorageForItem == 0 and is skipped; floor is the guaranteed sink. Mirrors
        // FetchObjective's cross-tile retry.
        targetInv = null;
        destination = null;
        Start(); // recomputes target: Moving + Navigate, or boxed-in → 3s cooldown + Fail
    }
}

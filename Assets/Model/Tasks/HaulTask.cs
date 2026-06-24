using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// HaulTask is only for moving a specific floor stack to storage (always WOM-targeted).
public class HaulTask : Task {
    private readonly ItemStack targetStack;
    private ItemQuantity _iq;        // tracked for sub-threshold completion logging
    private int _intendedQuantity;   // what we planned to haul at Initialize (for shrinkage detection)
    public HaulTask(Animal animal, ItemStack targetStack) : base(animal){
        this.targetStack = targetStack;
    }
    public override bool Initialize() {
        if (targetStack.item == null || targetStack.quantity == 0) return false; // stale
        Item item = targetStack.item;
        Tile itemTile = World.instance.GetTileAt(targetStack.inv.x, targetStack.inv.y);
        if (itemTile == null) return false;
        // Gate the source leg too — unreachable or obscenely-winding pickup should skip the task.
        // Storage leg is already gated by FindPathToStorage's internal radius cap.
        if (!animal.nav.WithinWorkRange(animal.nav.PathTo(itemTile))) return false;
        // exclude the source inv so an eviction haul never targets its own inventory as the
        // destination (e.g. the foundry output is a Storage that allows its own bars + has spare room).
        var (storagePath, storageInv) = animal.nav.FindPathToStorage(item, exclude: targetStack.inv);
        if (storagePath == null) return false;
        int available = targetStack.quantity - targetStack.resAmount;
        int quantity = Math.Min(available, storageInv.GetStorageForItem(item));
        if (quantity <= 0) return false;
        // De minimis check before reserving: GetStorageForItem already accounts for resSpace,
        // so this is safe. Avoids leaking a space reservation that would linger until stale expiry.
        if (!MeetsHaulMinimum(quantity, available)) return false; // de minimis — no reservation made yet
        // Reserve destination space so other haulers don't target the same slots
        int spaceReserved = ReserveSpace(storageInv, item, quantity);
        if (spaceReserved <= 0) return false;
        quantity = Math.Min(quantity, spaceReserved);
        _iq = new ItemQuantity(item, quantity);
        _intendedQuantity = quantity;
        FetchAndReserve(_iq, itemTile, targetStack, quantity);
        objectives.AddLast(new GoObjective(this, storagePath.tile));
        objectives.AddLast(new DeliverToInventoryObjective(this, _iq, storageInv));
        return true;
    }
    public override void Complete() {
        // Flag only hauls that shrank mid-task (decay / reservation drain) — intentional
        // stack-clearing cleanups below MinHaulQuantity are expected and don't log.
        if (objectives.Count == 0 && _iq != null && !MeetsHaulMinimum(_iq.quantity, _intendedQuantity)) {
            Debug.Log($"{animal.aName} ({animal.job.name}) tiny Haul: delivered {_iq.quantity} fen of {_iq.item.name} (intended {_intendedQuantity} — shrunk mid-task)");
        }
        base.Complete();
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// ConsolidateTask is only for consolidating a specific floor stack when no storage is available (always WOM-targeted).
public class ConsolidateTask : Task {
    private readonly ItemStack stack;
    private ItemQuantity _iq;
    private int _intendedQuantity;
    public ConsolidateTask(Animal animal, ItemStack stack) : base(animal) {
        this.stack = stack;
    }
    public override bool Initialize() {
        HaulInfo h = animal.nav.FindFloorConsolidation(stack);
        if (h == null) return false;
        int available = h.itemStack.quantity - h.itemStack.resAmount;
        // Reserve first because spaceReserved caps the quantity we'll actually move.
        // If de minimis fails below, Start() → Cleanup() releases this reservation.
        int spaceReserved = ReserveSpace(h.destTile.inv, h.item, h.quantity);
        if (spaceReserved <= 0) return false;
        int quantity = Math.Min(h.quantity, spaceReserved);
        // Cleanup releases the space reservation we just made before bailing.
        if (quantity < MinHaulQuantity && quantity < available) return false; // de minimis
        _iq = new ItemQuantity(h.item, quantity);
        _intendedQuantity = quantity;
        FetchAndReserve(_iq, h.itemTile, h.itemStack, quantity);
        objectives.AddLast(new DeliverObjective(this, _iq, h.destTile));
        return true;
    }
    public override void Complete() {
        if (objectives.Count == 0 && _iq != null && _iq.quantity < MinHaulQuantity && _iq.quantity < _intendedQuantity) {
            Debug.Log($"{animal.aName} ({animal.job.name}) tiny Consolidate: moved {_iq.quantity} fen of {_iq.item.name} (intended {_intendedQuantity} — shrunk mid-task)");
        }
        base.Complete();
    }
}

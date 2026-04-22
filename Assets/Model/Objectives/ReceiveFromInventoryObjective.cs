using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Moves items from a source inventory directly into the animal's inventory.
// Always queued after a TravelingObjective — the animal is conceptually "at" the source
// (the market), so no navigation is needed. Symmetric counterpart to DeliverToInventoryObjective.
public class ReceiveFromInventoryObjective : Objective {
    private ItemQuantity iq;
    private Inventory sourceInv;
    public ReceiveFromInventoryObjective(Task task, ItemQuantity iq, Inventory sourceInv) : base(task) {
        this.iq = iq;
        this.sourceInv = sourceInv;
    }
    public override string GetObjectiveName() => $"ReceiveFrom({iq.item.name})";
    public override void Start() {
        if (sourceInv == null) { task.FailAtMarket(); return; }
        // Use Inventory.Quantity (raw physical sum across stacks) rather than GetItemStack — the latter
        // filters by Available() and returns null once resAmount == quantity, which happens every time
        // we reserved the whole stack in Initialize. MoveItemTo / AddItem(-) don't gate on reservations
        // at the execution layer, so we can draw down even a fully-reserved stack.
        int available = sourceInv.Quantity(iq.item);
        if (available <= 0) { Debug.Log($"{animal.aName} ReceiveFromInventory: no {iq.item.name} at source"); task.FailAtMarket(); return; }
        int toReceive = Math.Min(available, iq.quantity);
        int moved = sourceInv.MoveItemTo(animal.inv, iq.item, toReceive);
        if (moved <= 0) { Debug.Log($"{animal.aName} ReceiveFromInventory: couldn't move {iq.item.name} to animal inv"); task.FailAtMarket(); return; }
        // Partial pickup is fine — downstream DeliverToInventoryObjective caps delivery by what the
        // animal actually has; no need to mutate iq.quantity here.
        Complete();
    }
}

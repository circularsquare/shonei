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
        if (sourceInv == null) { Fail(); return; }
        ItemStack stack = sourceInv.GetItemStack(iq.item);
        int available = stack != null ? stack.quantity - stack.resAmount : 0;
        if (available <= 0) { Debug.Log($"{animal.aName} ReceiveFromInventory: no {iq.item.name} available in source"); Fail(); return; }
        int toReceive = Math.Min(available, iq.quantity);
        int moved = sourceInv.MoveItemTo(animal.inv, iq.item, toReceive);
        if (moved <= 0) { Debug.Log($"{animal.aName} ReceiveFromInventory: couldn't move {iq.item.name} to animal inv"); Fail(); return; }
        Complete();
    }
}

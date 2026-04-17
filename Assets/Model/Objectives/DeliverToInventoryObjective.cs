using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Generic delivery objective: moves items from the animal's inventory into any target inventory.
// Always queued after GoObjective so the animal is already in position when Start() runs.
public class DeliverToInventoryObjective : Objective {
    private ItemQuantity iq;
    // Target inventory exposed read-only so tasks can introspect their queue
    // (e.g. HaulToMarketTask distinguishes market-deliver vs home-deliver for phase detection).
    public Inventory TargetInv { get; }
    public DeliverToInventoryObjective(Task task, ItemQuantity iq, Inventory targetInv) : base(task) {
        this.iq = iq;
        this.TargetInv = targetInv;
    }
    public override string GetObjectiveName() { return $"DeliverTo({iq.item.name}>{TargetInv.displayName})"; }
    public override void Start(){
        if (TargetInv == null) { Fail(); return; }
        int have = animal.inv.Quantity(iq.item);
        if (have <= 0) { Debug.Log($"{animal.aName} DeliverToInventoryObjective: missing {iq.item.name}"); Fail(); return; }
        int toDeliver = Math.Min(have, iq.quantity);
        int moved = animal.inv.MoveItemTo(TargetInv, iq.item, toDeliver);
        if (moved < toDeliver) {
            // Diagnostic dump: destination-side partial fill means FreeSpace shrunk between reserve and
            // delivery despite the reservation system. Dump every stack's reservation state so we can
            // tell whether our resSpace was clamped, eaten by another task, or never set.
            var sb = new System.Text.StringBuilder();
            sb.Append($"{animal.aName} delivered {moved}/{toDeliver} {iq.item.name} to {TargetInv.displayName} — partial fill\n");
            sb.Append($"  task={task} iq.quantity={iq.quantity} animal.inv.Quantity={have}\n");
            sb.Append($"  TargetInv stacks ({TargetInv.invType} at ({TargetInv.x},{TargetInv.y})):\n");
            for (int i = 0; i < TargetInv.nStacks; i++) {
                var s = TargetInv.itemStacks[i];
                string resOwner = s.resSpaceTask == this.task ? "OURS" : (s.resSpaceTask?.ToString() ?? "null");
                sb.Append($"    [{i}] item={s.item?.name ?? "null"} qty={s.quantity}/{s.stackSize} resAmount={s.resAmount} resSpace={s.resSpace} resSpaceItem={s.resSpaceItem?.name ?? "null"} resSpaceTask={resOwner}\n");
            }
            Debug.LogWarning(sb.ToString());
        }
        Complete();
    }
}

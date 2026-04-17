using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class FetchObjective : Objective {
    private ItemQuantity iq;
    private Tile destination;
    private Tile sourceTile;
    private Inventory sourceInv; // which inventory to take from (storage or floor); set by Start() or caller
    private Inventory targetInv; // null = animal's main inventory; non-null = equip into that slot
    private bool softFetch; // if true: Complete (not Fail) when no path found or nothing taken; no cross-tile retry
    private Inventory Dest => targetInv ?? animal.inv;
    public FetchObjective(Task task, ItemQuantity iq, Tile sourceTile = null, Inventory targetInv = null, bool softFetch = false, Inventory sourceInv = null) : base(task) {
        this.iq = iq;
        this.sourceTile = sourceTile;
        this.sourceInv = sourceInv;
        this.targetInv = targetInv;
        this.softFetch = softFetch;
    }
    public override string GetObjectiveName() { return $"Fetch({iq.item.name})"; }
    public override void Start(){
        // Start() may be called more than once (non-soft): if the animal arrives and the stack is partially
        // exhausted, sourceTile/sourceInv are cleared and Start() re-runs to find a new source.
        if (Dest.Quantity(iq.item) >= iq.quantity){Complete(); return;}
        Path itemPath;
        if (sourceTile != null) {
            itemPath = animal.nav.PathTo(sourceTile);
        } else {
            ItemStack stack;
            (itemPath, stack) = animal.nav.FindPathItemStack(iq.item);
            if (itemPath != null) {
                task.ReserveStack(stack, iq.quantity);
                sourceTile = itemPath.tile;
                sourceInv = stack.inv;
            }
        }
        if (itemPath != null){
            destination = itemPath.tile;
            animal.nav.Navigate(itemPath);
            animal.state = Animal.AnimalState.Moving;
        } else {
            if (softFetch) { Complete(); return; }
            // If the animal already has a partial amount from a prior fetch attempt, deliver what it has
            // rather than failing and dropping everything — avoids a tight drop-and-re-fetch loop.
            // Cap iq.quantity to what we actually fetched so downstream deliver objectives don't log
            // spurious partial-fill mismatches (they compare moved against iq.quantity).
            if (Dest.Quantity(iq.item) > 0) {
                int have = Dest.Quantity(iq.item);
                Debug.Log($"{animal.aName} ({animal.job.name}) partial fetch: has {have}/{iq.quantity} {iq.item.name}, no more found — capping deliver target");
                iq.quantity = have;
                Complete(); return;
            }
            Fail(); Debug.Log($"{animal.aName} ({animal.job.name}) found no path to fetch {iq.item.name} at ({(int)animal.x},{(int)animal.y})");
        }
    }
    public override void OnArrival(){
        int needed = iq.quantity - Dest.Quantity(iq.item); // how much more we still need
        if (needed <= 0) { Complete(); return; }
        // Use tracked sourceInv (storage or floor); fall back to tile.inv for floor items
        Inventory src = sourceInv ?? animal.TileHere()?.inv;
        if (src == null) {
            if (softFetch) { Complete(); return; }
            Fail(); Debug.Log($"{animal.aName} FetchObjective: no source inv at ({(int)animal.x},{(int)animal.y})"); return;
        }
        // Source torn down between Initialize and arrival — usually decay silently drained the
        // reserved stack (ItemStack.AddItem zeros resAmount when the stack empties). Log what
        // we reserved, not the raw request, so "expected vs gone" is visible.
        if (src.destroyed) {
            int expected = task.ReservedAmountFromInv(src);
            Debug.LogWarning($"{animal.aName} FetchObjective: source {src.invType} '{src.displayName}' at ({src.x},{src.y}) destroyed before arrival — expected {expected} fen of {iq.item.name} (iq.quantity={iq.quantity}, needed={needed})");
            if (softFetch) { Complete(); return; }
            Fail(); return;
        }
        int amountTaken = src.MoveItemTo(Dest, iq.item, needed);
        // Clean up empty floor inventories (replicates TakeItem behavior)
        if (src.invType == Inventory.InvType.Floor && src.IsEmpty()) {
            Tile t = World.instance.GetTileAt(src.x, src.y);
            if (t != null) { src.Destroy(); t.inv = null; }
        }
        if (amountTaken == 0) {
            if (softFetch) { Complete(); return; }
            Fail(); Debug.Log($"{animal.aName} Couldn't fetch any {iq.item.name} (needed {needed} fen)"); return;
        }
        // softFetch: always complete after one tile visit (CraftTask.Complete handles retry logic)
        // targetInv != null means equip slot (food/tool) — partial fills are fine there, don't retry
        if (softFetch || Dest.Quantity(iq.item) >= iq.quantity || Dest.GetStorageForItem(iq.item) < 5 || targetInv != null) {
            Complete();
        } else {
            sourceTile = null; // source tile may be exhausted; search for another
            sourceInv = null;
            Start();
        }
    }
}

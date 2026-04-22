using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class HaulFromMarketTask : Task {
    // Persisted for save/load so a mid-transit merchant can reconstruct the
    // receive/deliver tail on load. Set by the normal Initialize.
    public ItemQuantity iq;
    public Tile storageTile;

    // True once the merchant has already fetched from market and is past the
    // ReceiveFromInventoryObjective — i.e. on the return leg carrying goods home.
    // Mirrors MerchantJourneyDisplay's leg-detection so behaviour stays consistent.
    public bool IsReturnLeg => !RemainingObjectives().Any(o => o is ReceiveFromInventoryObjective)
                            && !(currentObjective is ReceiveFromInventoryObjective);

    // See HaulToMarketTask for isResume rationale. Travel progress itself lives on
    // animal.workProgress; Animal.Start() restores it after task.Start() zeroes it.
    private readonly bool isResume;
    private readonly bool resumeReturnLeg;

    public HaulFromMarketTask(Animal animal) : base(animal) {}

    // Resume constructor. `returnLeg = true` means the merchant has already visited
    // the market and is heading home with goods; false means still outbound.
    public HaulFromMarketTask(Animal animal, ItemQuantity iq, Tile storageTile, bool returnLeg) : base(animal) {
        this.iq              = iq;
        this.storageTile     = storageTile;
        this.isResume        = true;
        this.resumeReturnLeg = returnLeg;
    }

    // Called when an at-market objective (Receive) aborts. The merchant's sprite is physically at
    // the town portal, but conceptually off-screen at the market — a plain Fail would snap them
    // idle at x=0 as if they never travelled. Instead, drop any pending work and walk back.
    // iq/storageTile are nulled so a mid-return save emits no task descriptor (we have nothing
    // to deliver); the loader falls through to ResumeTravelTask and finishes the remaining travel.
    public override void FailAtMarket() {
        Debug.Log($"{animal.aName} ({animal.job.name}) HaulFromMarket aborting at market — walking home");
        iq = null;
        storageTile = null;
        objectives.Clear();
        objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        StartNextObjective();
    }

    public override bool Initialize() {
        if (isResume) return InitializeResume();

        if (animal.eeping.Eepness() < 0.75f) return false; // see HaulToMarketTask for rationale
        if (MarketBuilding.instance == null) return false;
        Inventory marketInv = MarketBuilding.instance.storage;
        if (marketInv == null) return false;
        // Any reachable x=0 tile is a valid portal — not just the market building's own tile.
        Path marketPath = animal.nav.FindMarketPath();
        if (marketPath == null) return false;
        Tile marketTile = marketPath.tile;

        foreach (var kvp in marketInv.targets) {
            Item item = kvp.Key;
            if (item.IsGroup) continue; // targets on groups are ignored (see SPEC-trading: market targets are leaf-only)
            int excess = marketInv.Quantity(item) - kvp.Value;
            if (excess <= 0) continue;

            ItemStack stack = marketInv.GetItemStack(item);
            if (stack == null) continue;
            // Pick the storage with the most free space so the full excess lands in one drop-off —
            // a near-full nearest storage would cap spaceReserved below MinMarketHaul.
            var (storagePath, storageInv) = animal.nav.FindPathToStorageMostSpace(item, minSpace: MinMarketHaul(item));
            if (storagePath == null) continue;

            int qty = Math.Min(excess, stack.quantity - stack.resAmount);
            if (qty <= 0) continue;
            int spaceReserved = ReserveSpace(storageInv, item, qty);
            if (spaceReserved <= 0) continue;
            qty = Math.Min(qty, spaceReserved);
            if (qty < MinMarketHaul(item)) { UndoLastSpaceReservation(); continue; }
            this.iq          = new ItemQuantity(item, qty);
            this.storageTile = storagePath.tile;
            // Reserve the items in the market now so no other task double-counts them.
            ReserveStack(stack, qty);
            // Walk to portal → travel to market → receive goods → travel back → deliver to storage.
            objectives.AddLast(new GoObjective(this, marketTile));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new ReceiveFromInventoryObjective(this, iq, marketInv));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new GoObjective(this, storageTile));
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, storageInv));
            // transitTicks = per-leg; PrependFoodFetchForMarketJourney doubles internally for round trip.
            return PrependFoodFetchForMarketJourney(MarketTransitTicks);
        }
        return false;
    }

    // Rebuilds the task tail for a merchant loaded mid-transit. Two shapes:
    //   return leg:   [Travel(remaining) → Go(storage) → DeliverToInventory]
    //   outbound leg: [Travel(remaining) → ReceiveFromInventory → Travel(return) →
    //                   Go(storage) → DeliverToInventory]
    // The home storage inventory is resolved from the saved tile each load —
    // if the storage building or market has been demolished since save,
    // returns false so Animal.Start() falls back to ResumeTravelTask.
    private bool InitializeResume() {
        if (iq?.item == null || storageTile == null) return false;
        if (MarketBuilding.instance?.storage == null) return false;
        Inventory marketInv  = MarketBuilding.instance.storage;
        Inventory storageInv = storageTile.building?.storage;
        if (storageInv == null) return false;

        // Both legs still need to deliver into home storage — reserve space there now
        // so other haul tasks don't eat the slot while we're still travelling.
        ReserveSpace(storageInv, iq.item, iq.quantity);

        if (resumeReturnLeg) {
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new GoObjective(this, storageTile));
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, storageInv));
        } else {
            // Outbound — re-reserve the source stack eagerly so a competing merchant
            // can't drain the market while we're still travelling toward it.
            // Stack may have shrunk since save; ReceiveFromInventoryObjective tolerates
            // shortfall (takes min(available, iq.quantity), only Fails on empty).
            ItemStack stack = marketInv.GetItemStack(iq.item);
            if (stack != null) ReserveStack(stack, Math.Min(iq.quantity, stack.quantity - stack.resAmount));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new ReceiveFromInventoryObjective(this, iq, marketInv));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new GoObjective(this, storageTile));
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, storageInv));
        }
        return true;
    }
}

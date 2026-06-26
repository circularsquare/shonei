using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Merchant trip: walk to the off-screen market, receive excess goods (items above target),
// travel back, and deliver into home storage. Spawned from WOM HaulFromMarket orders
// (tier 4). Round-trip travel hides the merchant during TravelingObjective phases.
//
// Multi-item: one trip picks up several above-target item types at once, filling the
// merchant's cargo capacity. Each item may go to a different home storage, so the return
// tail is [Receive×N → Travel → (Go(storage) → Deliver)×N].
//
// Queue: [optional food Fetch] → Go(portal) → Travel → Receive×N → Travel → (Go→Deliver)×N.
// Reserves: each market source ItemStack, each home storage's space.
// Has a resume constructor for mid-transit save/load and a FailAtMarket exit that walks
// the merchant home instead of teleporting them idle to the portal.
public class HaulFromMarketTask : Task {
    // Goods being hauled home, one entry per item type, parallel to storageTiles
    // (iqs[i] is delivered into the storage at storageTiles[i]). Persisted for save/load
    // so a mid-transit merchant can reconstruct the receive/deliver tail. Set by Initialize.
    public List<ItemQuantity> iqs = new();
    public List<Tile> storageTiles = new();

    // True once the merchant has received all goods and is past every
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
    // iqs and storageTiles are parallel lists.
    public HaulFromMarketTask(Animal animal, List<ItemQuantity> iqs, List<Tile> storageTiles, bool returnLeg) : base(animal) {
        this.iqs             = iqs ?? new List<ItemQuantity>();
        this.storageTiles    = storageTiles ?? new List<Tile>();
        this.isResume        = true;
        this.resumeReturnLeg = returnLeg;
    }

    // Called when an at-market objective (Receive) aborts. The merchant's sprite is physically at
    // the town portal, but conceptually off-screen at the market — a plain Fail would snap them
    // idle at x=0 as if they never travelled. Instead, drop any pending work and walk back.
    // The lists are cleared so a mid-return save emits no task descriptor (we have nothing to
    // deliver); the loader falls through to ResumeTravelTask and finishes the remaining travel.
    public override void FailAtMarket() {
        Debug.Log($"{animal.aName} ({animal.job.name}) HaulFromMarket aborting at market — walking home");
        iqs.Clear();
        storageTiles.Clear();
        objectives.Clear();
        objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        StartNextObjective();
    }

    public override bool Initialize() {
        if (isResume) return InitializeResume();

        if (animal.eeping.Eepness() < MinMarketEepness) return false;
        if (MarketBuilding.instance == null) return false;
        Inventory marketInv = MarketBuilding.instance.storage;
        if (marketInv == null) return false;
        // Any reachable x=0 tile is a valid portal — not just the market building's own tile.
        Path marketPath = animal.nav.FindMarketPath();
        if (marketPath == null) return false;
        Tile marketTile = marketPath.tile;

        // Fill the cargo budget with as many above-target item types as fit; reserve each
        // source stack + home storage space (shared with the HaulToMarket piggyback).
        var picks = SelectMarketPickups(marketInv);
        if (picks.Count == 0) return false;
        foreach (var p in picks) {
            iqs.Add(p.iq);
            storageTiles.Add(p.tile);
        }

        // Walk to portal → travel → receive each item → travel back → deliver each to its storage.
        objectives.AddLast(new GoObjective(this, marketTile));
        objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        foreach (var p in picks)
            objectives.AddLast(new ReceiveFromInventoryObjective(this, p.iq, marketInv));
        objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        foreach (var p in picks) {
            objectives.AddLast(new GoObjective(this, p.tile));
            objectives.AddLast(new DeliverToInventoryObjective(this, p.iq, p.inv));
        }
        // transitTicks = per-leg; PrependFoodFetchForMarketJourney doubles internally for round
        // trip. Each home drop is an extra on-map walk, so budget WalkToPortalSeconds per pickup.
        return PrependFoodFetchForMarketJourney(MarketTransitTicks, WalkToPortalSeconds * picks.Count);
    }

    // Rebuilds the task tail for a merchant loaded mid-transit. Two shapes:
    //   return leg:   [Travel(remaining) → (Go(storage) → Deliver)×N]
    //   outbound leg: [Travel(remaining) → Receive×N → Travel(return) → (Go(storage) → Deliver)×N]
    // Each item's home storage is re-resolved from its saved tile. Graceful per-item
    // degradation: an item whose storage building was demolished since save is dropped from
    // the plan (its goods fall to idle-drop on the return) while the rest still deliver.
    // Returns false only if the market is gone or nothing is deliverable, in which case
    // Animal.Start() falls back to ResumeTravelTask.
    private bool InitializeResume() {
        if (iqs == null || iqs.Count == 0 || storageTiles == null) return false;
        if (MarketBuilding.instance?.storage == null) return false;
        Inventory marketInv = MarketBuilding.instance.storage;

        // Pair each item with a live storage inventory; skip items whose storage is gone.
        int n = Math.Min(iqs.Count, storageTiles.Count);
        var valid = new List<(ItemQuantity iq, Tile tile, Inventory inv)>();
        for (int i = 0; i < n; i++) {
            ItemQuantity iq = iqs[i];
            Tile tile = storageTiles[i];
            if (iq?.item == null || tile == null) continue;
            Inventory storageInv = tile.building?.storage;
            if (storageInv == null) continue; // storage demolished — drop this item's delivery
            valid.Add((iq, tile, storageInv));
        }
        if (valid.Count == 0) return false;

        // Both legs deliver home — reserve storage space now so other hauls don't eat the slot.
        foreach (var v in valid)
            ReserveSpace(v.inv, v.iq.item, v.iq.quantity);

        if (resumeReturnLeg) {
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            foreach (var v in valid) {
                objectives.AddLast(new GoObjective(this, v.tile));
                objectives.AddLast(new DeliverToInventoryObjective(this, v.iq, v.inv));
            }
        } else {
            // Outbound — re-reserve each source stack eagerly so a competing merchant can't
            // drain the market while we travel. Stacks may have shrunk; ReceiveFromInventory
            // tolerates shortfall (takes min(available, iq.quantity), only Fails on empty).
            foreach (var v in valid) {
                ItemStack stack = marketInv.GetItemStack(v.iq.item);
                if (stack != null) ReserveStack(stack, Math.Min(v.iq.quantity, stack.quantity - stack.resAmount));
            }
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            foreach (var v in valid)
                objectives.AddLast(new ReceiveFromInventoryObjective(this, v.iq, marketInv));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            foreach (var v in valid) {
                objectives.AddLast(new GoObjective(this, v.tile));
                objectives.AddLast(new DeliverToInventoryObjective(this, v.iq, v.inv));
            }
        }
        return true;
    }
}

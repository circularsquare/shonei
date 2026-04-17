using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Travel duration for market journeys: one quarter of a game day.
// The market is off-screen at the left world edge; merchants walk there and disappear
// while "travelling to/from market", then reappear when the trip is complete.
public class HaulToMarketTask : Task {
    // Persisted for save/load so a mid-transit merchant can reconstruct the delivery
    // tail on load (see resume constructor below). Set by the normal Initialize once
    // an item + quantity has been chosen.
    public ItemQuantity iq;

    // ── Opportunistic return-leg pickup (piggyback) ──────────────────────────
    // If the market simultaneously needs items delivered AND has excess to haul away,
    // a single merchant does both in one round trip: outbound delivery, then receive
    // excess at market, return with goods, deliver into home storage. The pickup is
    // planned at Initialize time and reserved up-front; if no viable pickup exists
    // the task behaves identically to the pre-piggyback HaulToMarketTask.
    // pickupIq / pickupStorageTile are null when no pickup is planned.
    public ItemQuantity pickupIq;
    public Tile pickupStorageTile;
    // Claimed HaulFromMarket WOM order — reserved in TryAppendPickup so a second
    // merchant doesn't race us, released in Cleanup override.
    private WorkOrderManager.WorkOrder pickupOrder;

    // True once the merchant has already delivered to market and is on the return leg.
    // Distinguishes market-deliver from the piggyback home-deliver via TargetInv, so
    // this stays correct even when the queue contains two DeliverToInventoryObjectives.
    public bool IsReturnLeg {
        get {
            Inventory marketInv = MarketBuilding.instance?.storage;
            if (marketInv == null) return true; // market demolished — no way back to a pre-deliver state
            if (currentObjective is DeliverToInventoryObjective cd && cd.TargetInv == marketInv) return false;
            foreach (var o in RemainingObjectives())
                if (o is DeliverToInventoryObjective d && d.TargetInv == marketInv) return false;
            return true;
        }
    }

    // True once the piggyback pickup has been received into the animal's inventory
    // (i.e. the ReceiveFromInventoryObjective has completed). Used by SaveSystem to
    // decide whether a mid-return-travel save should be persisted as a HaulFromMarket
    // descriptor (carrying goods home) or a plain HaulToMarket return descriptor.
    public bool PickupReceived {
        get {
            if (pickupIq == null) return false;
            if (currentObjective is ReceiveFromInventoryObjective) return false;
            foreach (var o in RemainingObjectives())
                if (o is ReceiveFromInventoryObjective) return false;
            return true;
        }
    }

    // Resume-mode flag — when true, Initialize skips gameplay gates (Eepness,
    // food fetch, market path, item search) because the task is being re-created
    // for a merchant already mid-journey. The merchant's travel progress lives
    // on animal.workProgress, restored by Animal.Start() after task.Start().
    private readonly bool isResume;
    private readonly bool resumeReturnLeg;

    public HaulToMarketTask(Animal animal) : base(animal) {}

    // Resume constructor. Called from Animal.Start() when a save records the
    // merchant was mid-transit on a HaulToMarket. `returnLeg = true` means the
    // merchant has already delivered and is on the second (homeward) TravelingObjective;
    // false means still outbound with goods in inventory.
    // Note: piggyback pickup state is never resumed — the pickup descriptor (when received)
    // is saved as a HaulFromMarket entry instead, which routes to HaulFromMarketTask's
    // resume constructor. See SaveSystem.GatherAnimal / SPEC-trading.md save-load mapping.
    public HaulToMarketTask(Animal animal, ItemQuantity iq, bool returnLeg) : base(animal) {
        this.iq              = iq;
        this.isResume        = true;
        this.resumeReturnLeg = returnLeg;
    }

    public override bool Initialize() {
        if (isResume) return InitializeResume();

        // Market trips have a stricter eep gate than the general night-sleep threshold —
        // a merchant who leaves close to the threshold could dip into efficiency-loss
        // territory mid-transit and arrive useless at the far side.
        if (animal.eeping.Eepness() < 0.75f) return false;
        if (MarketBuilding.instance == null) return false;
        Inventory marketInv = MarketBuilding.instance.storage;
        if (marketInv == null) return false;
        // Any reachable x=0 tile is a valid portal — not just the market building's own tile.
        Path marketPath = animal.nav.FindMarketPath();
        if (marketPath == null) return false;
        Tile marketTile = marketPath.tile;

        foreach (var kvp in marketInv.targets) {
            Item item = kvp.Key;
            // Targets on groups are ignored — UI hides them and model skips them.
            if (item.IsGroup) continue;
            int quantityNeeded = kvp.Value - marketInv.Quantity(item);
            if (quantityNeeded <= 0) continue;
            if (marketInv.allowed[item.id] == false) continue;

            var (itemPath, stack) = animal.nav.FindPathItemStack(item);
            if (itemPath == null || stack == null) continue;

            int firstAvail = stack.quantity - stack.resAmount;
            if (firstAvail <= 0) continue;
            // Reserve market space for the full amount needed so FetchObjective can aggregate
            // from multiple stacks in one trip, rather than one small trip per stack.
            int spaceReserved = ReserveSpace(marketInv, item, quantityNeeded);
            if (spaceReserved <= 0) continue;
            if (spaceReserved < MinMarketHaul(item)) { UndoLastSpaceReservation(); continue; }
            this.iq = new ItemQuantity(item, spaceReserved);
            // Pre-reserve only the nearest stack; FetchObjective reserves additional stacks
            // as it visits them until iq.quantity is gathered.
            FetchAndReserve(iq, itemPath.tile, stack, firstAvail);
            // Walk to portal → travel to market → deliver → [optional pickup tail] → travel back.
            // If TryAppendPickup succeeds it splices in [Receive → Travel → GoStorage → DeliverHome],
            // otherwise we just append the return travel and the merchant reappears at x=0 idle.
            objectives.AddLast(new GoObjective(this, marketTile));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, marketInv));
            float extraGround = 0f;
            if (TryAppendPickup(marketInv)) {
                // Piggyback adds a portal→storage walk at trip end; budget it in the food fetch.
                // Over-estimating slightly (WalkToPortalSeconds) keeps it simple and safe.
                extraGround = WalkToPortalSeconds;
            } else {
                objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            }
            // transitTicks = per-leg; PrependFoodFetchForMarketJourney doubles internally for round trip.
            return PrependFoodFetchForMarketJourney(MarketTransitTicks, extraGround);
        }
        return false;
    }

    // Attempts to extend the current delivery queue with an opportunistic pickup from
    // the same market visit. On success: reserves the market source stack + home storage
    // space, claims the HaulFromMarket WOM order, populates pickupIq/pickupStorageTile,
    // and appends [ReceiveFromInventory → Travel(return) → Go(storage) → DeliverHome]
    // to the objective queue. On failure: no mutations, caller should append its own
    // plain return TravelingObjective.
    private bool TryAppendPickup(Inventory marketInv) {
        if (marketInv.targets == null) return false;

        // We scan all market targets for an item with (excess > 0) where we can secure a
        // stack reservation, storage space, and reachable home storage. The WOM
        // HaulFromMarket order is claimed only as a nicety (so a parallel pure-pickup
        // merchant doesn't duplicate this excess) — its absence or prior reservation
        // does NOT block piggyback, because stack-level `resAmount` already prevents
        // double-booking of the physical items.
        Item   chosenItem        = null;
        int    chosenQty         = 0;
        ItemStack chosenStack    = null;
        Tile   chosenStorageTile = null;
        Inventory chosenStorage  = null;

        foreach (var kvp in marketInv.targets) {
            Item item = kvp.Key;
            if (item.IsGroup) continue; // targets on groups are ignored (see SPEC-trading: market targets are leaf-only)
            int excess = marketInv.Quantity(item) - kvp.Value;
            if (excess <= 0) continue;

            ItemStack stack = marketInv.GetItemStack(item);
            if (stack == null) continue;
            int avail = stack.quantity - stack.resAmount;
            if (avail <= 0) continue;

            // Pick the storage with the most free space so the full piggyback payload lands in one
            // drop-off — a near-full nearest storage would cap spaceReserved below MinMarketHaul.
            var (storagePath, storageInv) = animal.nav.FindPathToStorageMostSpace(item, minSpace: MinMarketHaul(item));
            if (storagePath == null) continue;

            int qty = Math.Min(excess, avail);
            if (qty < MinMarketHaul(item)) continue;

            chosenItem        = item;
            chosenQty         = qty;
            chosenStack       = stack;
            chosenStorageTile = storagePath.tile;
            chosenStorage     = storageInv;
            break;
        }

        if (chosenItem == null) return false;

        // Reserve storage space now (may be smaller than chosenQty if space tightened).
        int spaceReserved = ReserveSpace(chosenStorage, chosenItem, chosenQty);
        if (spaceReserved <= 0) return false;
        int finalQty = Math.Min(chosenQty, spaceReserved);
        if (finalQty < MinMarketHaul(chosenItem)) {
            UndoLastSpaceReservation();
            return false;
        }

        this.pickupIq          = new ItemQuantity(chosenItem, finalQty);
        this.pickupStorageTile = chosenStorageTile;
        ReserveStack(chosenStack, finalQty);

        // Opportunistically claim the HaulFromMarket WOM order if one exists and is free.
        // If the order is missing or already reserved by another merchant, we proceed anyway
        // — stack-level reservation is the real race guard. Cleanup only unreserves when we
        // did reserve (pickupOrder is set).
        WorkOrderManager wom = WorkOrderManager.instance;
        WorkOrderManager.WorkOrder order = wom?.FindMarketHaulFromOrder(marketInv);
        if (order != null && order.res.Available()) {
            order.res.Reserve(animal.aName);
            this.pickupOrder = order;
        }

        objectives.AddLast(new ReceiveFromInventoryObjective(this, pickupIq, marketInv));
        objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        objectives.AddLast(new GoObjective(this, pickupStorageTile));
        objectives.AddLast(new DeliverToInventoryObjective(this, pickupIq, chosenStorage));
        return true;
    }

    public override void Cleanup() {
        // Release the claimed HaulFromMarket order before base.Cleanup() clears objectives.
        pickupOrder?.res.Unreserve();
        pickupOrder = null;
        base.Cleanup();
    }

    // Rebuilds the tail of the task for a merchant loaded mid-transit. Two shapes:
    //   outbound: [Travel(remaining) → DeliverToInventory → Travel(return)]
    //   return:   [Travel(remaining)]                  (items already delivered)
    // Returns false only if the market has been demolished between save and load,
    // in which case Animal.Start() falls back to ResumeTravelTask.
    private bool InitializeResume() {
        if (iq?.item == null) return false;
        if (MarketBuilding.instance?.storage == null) return false;
        Inventory marketInv = MarketBuilding.instance.storage;

        if (resumeReturnLeg) {
            // Items already delivered pre-save — no space reservation, no delivery step.
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        } else {
            // Re-issue the destination space reservation. Save/load invariant: reservations
            // are never persisted — on load, ItemStacks are freshly constructed (resAmount/
            // resSpace = 0) and Reservables default to reserved=0. Non-resumable tasks are
            // implicitly aborted at save, which is safe because their reservations vanish
            // with the (recreated) world state. Only resumable tasks need to re-reserve here.
            // Any new resumable task type MUST re-make every reservation its normal
            // Initialize() made, or the task will run without backing reservations.
            ReserveSpace(marketInv, iq.item, iq.quantity);
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, marketInv));
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        }
        return true;
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Travel duration for market journeys: one quarter of a game day.
// The market is off-screen at the left world edge; merchants walk there and disappear
// while "travelling to/from market", then reappear when the trip is complete.
//
// Multi-item: a single trip delivers several below-target items at once (one Fetch +
// one market Deliver per item), filling the merchant's cargo capacity. On the return
// leg it opportunistically picks up several above-target items (the piggyback below).
public class HaulToMarketTask : Task {
    // Outbound goods, one entry per item type. Persisted for save/load so a mid-transit
    // merchant can reconstruct the delivery tail (see resume constructor). Populated by
    // the normal Initialize.
    public List<ItemQuantity> iqs = new();

    // ── Opportunistic return-leg pickup (piggyback) ──────────────────────────
    // If the market simultaneously needs items delivered AND has excess to haul away,
    // a single merchant does both in one round trip: outbound delivery, then receive
    // excess at market, return with goods, deliver into home storage. Pickups are
    // chosen at the market (AppendReturnPickups, via SelectMarketPickupsObjective) so they
    // reflect live state on arrival; if none are viable the task behaves identically to
    // the pre-piggyback HaulToMarketTask.
    // Parallel lists: pickupIqs[i] is delivered into the storage at pickupStorageTiles[i].
    public List<ItemQuantity> pickupIqs = new();
    public List<Tile> pickupStorageTiles = new();
    // Claimed HaulFromMarket WOM order — reserved in AppendReturnPickups so a second
    // merchant doesn't race us, released in Cleanup override.
    private WorkOrderManager.WorkOrder pickupOrder;

    // True once the merchant has already delivered to market and is on the return leg.
    // Distinguishes market-deliver from the piggyback home-deliver via TargetInv, so
    // this stays correct even when the queue contains many DeliverToInventoryObjectives.
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

    // True once every piggyback pickup has been received into the animal's inventory
    // (no ReceiveFromInventoryObjective remains). Used by SaveSystem to decide whether a
    // mid-return-travel save should be persisted as a HaulFromMarket descriptor (carrying
    // goods home) or a plain HaulToMarket return descriptor.
    public bool PickupReceived {
        get {
            if (pickupIqs.Count == 0) return false;
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
    // Note: piggyback pickup state is never resumed here — once pickups are received the
    // phase is saved as a HaulFromMarket descriptor, which routes to HaulFromMarketTask's
    // resume constructor. See SaveSystem.GatherAnimal / SPEC-trading.md save-load mapping.
    public HaulToMarketTask(Animal animal, List<ItemQuantity> iqs, bool returnLeg) : base(animal) {
        this.iqs             = iqs ?? new List<ItemQuantity>();
        this.isResume        = true;
        this.resumeReturnLeg = returnLeg;
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

        // Fill the cargo budget with as many below-target item types as fit. Each item
        // gets its own Fetch (gathered in town before the portal walk) and its own market
        // Deliver. Capacity is tracked in whole stacks via CargoBudget.
        var budget = new CargoBudget(animal);
        foreach (var kvp in marketInv.targets) {
            if (budget.Exhausted) break;
            Item item = kvp.Key;
            // Targets on groups are ignored — UI hides them and model skips them.
            if (item.IsGroup) continue;
            if (marketInv.allowed[item.id] == false) continue;
            int quantityNeeded = kvp.Value - marketInv.Quantity(item);
            if (quantityNeeded <= 0) continue;

            var (itemPath, stack) = animal.nav.FindPathItemStack(item);
            if (itemPath == null || stack == null) continue;
            int firstAvail = stack.quantity - stack.resAmount;
            if (firstAvail <= 0) continue;

            int want = Math.Min(quantityNeeded, budget.Cap);
            if (want < MinMarketHaul(item)) continue;

            // Reserve market space for the planned amount so FetchObjective can aggregate from
            // multiple source stacks in one trip. ReserveSpace is the only fail-then-undo call
            // here and nothing reserves space before the next iteration, so UndoLast is safe.
            int spaceReserved = ReserveSpace(marketInv, item, want);
            if (spaceReserved < MinMarketHaul(item)) { UndoLastSpaceReservation(); continue; }
            int haul = Math.Min(want, spaceReserved);

            var iq = new ItemQuantity(item, haul);
            iqs.Add(iq);
            // Pre-reserve only the nearest stack; FetchObjective reserves additional stacks
            // as it visits them until iq.quantity is gathered.
            FetchAndReserve(iq, itemPath.tile, stack, firstAvail);
            budget.Commit(haul);
        }
        if (iqs.Count == 0) return false;

        // Walk to portal → travel to market → deliver each item → [optional pickup tail] → travel back.
        objectives.AddLast(new GoObjective(this, marketTile));
        objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        foreach (var iq in iqs)
            objectives.AddLast(new DeliverToInventoryObjective(this, iq, marketInv));

        // Return-leg pickups are chosen at the market (SelectMarketPickupsObjective), not here, so the
        // manifest reflects live market state on arrival. That objective appends [Receive×N →
        // Travel(return) → (Go→DeliverHome)×N], or just the return Travel when nothing is viable.
        objectives.AddLast(new SelectMarketPickupsObjective(this));

        // transitTicks = per-leg; PrependFoodFetchForMarketJourney doubles internally for round trip.
        // Pickup count is unknown at departure (decided at the market), so provision a fixed buffer of
        // assumed home-delivery legs (see AssumedMarketPickupLegs).
        return PrependFoodFetchForMarketJourney(MarketTransitTicks, WalkToPortalSeconds * AssumedMarketPickupLegs);
    }

    // Called by SelectMarketPickupsObjective once the merchant is at the market and has delivered its
    // outbound goods. Fills the (now-empty-on-return) cargo with above-target excess against LIVE market
    // state, reserves each market source stack + home storage space (via SelectMarketPickups), claims the
    // HaulFromMarket WOM order once, and appends [Receive×N → Travel(return) → (Go → DeliverHome)×N]. If
    // nothing is viable it appends just the return Travel, so the merchant always has a way home.
    //
    // Min-haul is NOT enforced here (enforceMinHaul: false): the trip is already happening, so any
    // positive excess that fits is worth grabbing. Picks come back largest-excess-first, so if cargo
    // fills it's the trickles that get left behind, not the bulk hauls.
    public void AppendReturnPickups() {
        Inventory marketInv = MarketBuilding.instance?.storage;
        if (marketInv == null) {
            // Market demolished mid-trip — nothing to pick up; just walk home.
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            return;
        }

        var picks = SelectMarketPickups(marketInv, enforceMinHaul: false);
        foreach (var p in picks) {
            pickupIqs.Add(p.iq);
            pickupStorageTiles.Add(p.tile);
        }

        // Opportunistically claim the HaulFromMarket WOM order if one exists and is free. With selection
        // deferred to the market, the order stays unclaimed during outbound travel, so a second merchant
        // could be dispatched on a pure HaulFromMarket meanwhile — the per-stack reservation above is the
        // real race guard; this claim is just a courtesy to reduce duplicate trips. Released in Cleanup.
        WorkOrderManager wom = WorkOrderManager.instance;
        WorkOrderManager.WorkOrder order = wom?.FindMarketHaulFromOrder(marketInv);
        if (order != null && order.res.Available()) {
            order.res.Reserve(animal.aName);
            this.pickupOrder = order;
        }

        foreach (var p in picks)
            objectives.AddLast(new ReceiveFromInventoryObjective(this, p.iq, marketInv));
        objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        foreach (var p in picks) {
            objectives.AddLast(new GoObjective(this, p.tile));
            objectives.AddLast(new DeliverToInventoryObjective(this, p.iq, p.inv));
        }
    }

    public override void Cleanup() {
        // Release the claimed HaulFromMarket order before base.Cleanup() clears objectives.
        pickupOrder?.res.Unreserve();
        pickupOrder = null;
        base.Cleanup();
    }

    // Called when an at-market objective (a piggyback Receive, or a hypothetical market-deliver
    // failure) aborts. See HaulFromMarketTask.FailAtMarket for rationale — walk home rather than
    // teleporting idle to the portal. Clearing the item lists means a mid-return save emits no
    // task descriptor; on load the merchant finishes travel as a bare ResumeTravelTask. Any goods
    // still in the animal's inventory get dropped by normal idle behaviour once it reappears.
    public override void FailAtMarket() {
        Debug.Log($"{animal.aName} ({animal.job.name}) HaulToMarket aborting at market — walking home");
        iqs.Clear();
        pickupIqs.Clear();
        pickupStorageTiles.Clear();
        pickupOrder?.res.Unreserve();
        pickupOrder = null;
        objectives.Clear();
        objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        StartNextObjective();
    }

    // Rebuilds the tail of the task for a merchant loaded mid-transit (to-market direction only;
    // the return-with-pickup phase loads as a HaulFromMarketTask instead). Two shapes:
    //   outbound: [Travel(remaining) → Deliver×N → SelectMarketPickups]   (picks chosen at the market)
    //   return:   [Travel(remaining)]                  (items already delivered, no pickup taken)
    // Returns false only if the market has been demolished between save and load, or the item
    // list is empty, in which case Animal.Start() falls back to ResumeTravelTask.
    private bool InitializeResume() {
        if (iqs == null || iqs.Count == 0) return false;
        if (MarketBuilding.instance?.storage == null) return false;
        Inventory marketInv = MarketBuilding.instance.storage;

        if (resumeReturnLeg) {
            // Items already delivered pre-save — no space reservation, no delivery step.
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
        } else {
            // Re-issue the destination space reservation per item. Save/load invariant:
            // reservations are never persisted, so each resumable task must re-make every
            // reservation its normal Initialize() did, or it runs without backing reservations.
            foreach (var iq in iqs) {
                if (iq?.item == null) continue;
                ReserveSpace(marketInv, iq.item, iq.quantity);
            }
            objectives.AddLast(new TravelingObjective(this, MarketTransitTicks));
            foreach (var iq in iqs) {
                if (iq?.item == null) continue;
                objectives.AddLast(new DeliverToInventoryObjective(this, iq, marketInv));
            }
            // Pickups are chosen at the market, so a resumed outbound merchant still does the at-market
            // selection after delivering (it gets a fresh, live piggyback rather than the one it planned
            // before saving — which was never committed anyway). The objective adds the return travel.
            objectives.AddLast(new SelectMarketPickupsObjective(this));
        }
        return true;
    }
}

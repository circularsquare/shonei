using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Runs on a HaulToMarketTask once the merchant is at the market (parked at the portal mid-trip),
// AFTER its outbound goods have been delivered. It selects the return-leg piggyback pickups against
// the LIVE market state — so the manifest reflects what's actually above target on arrival, not what
// was when the merchant set out — then appends the receive/return/deliver tail and completes.
//
// Deferring selection to here (rather than HaulToMarketTask.Initialize, where the old TryAppendPickup
// ran at departure) is what makes "what to take back" up to date. The heavy lifting lives in
// HaulToMarketTask.AppendReturnPickups; this objective is just the queue hook that triggers it.
public class SelectMarketPickupsObjective : Objective {
    public SelectMarketPickupsObjective(Task task) : base(task) {}

    public override void Start() {
        // AppendReturnPickups always enqueues at least a return TravelingObjective, so Complete()
        // always has a next objective to advance to (the merchant never strands here at the portal).
        (task as HaulToMarketTask)?.AppendReturnPickups();
        Complete();
    }
}

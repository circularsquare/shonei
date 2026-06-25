using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

// A single slot in an Inventory — one item type, a fen quantity, and dual reservation
// counters: resAmount tracks "items claimed for pickup" (source-side); resSpace tracks
// "space claimed for incoming items" (destination-side). Tasks reserve via
// Task.ReserveStack / Task.ReserveSpace and release in Cleanup(). FormatQ() converts
// fen→liang for display.
public class ItemStack {
    public bool isComposite{get; set;}
    public Item item { get; set; }
    public int quantity { get; set; }
    public int decayCounter; // increments from 0 to maxdecaycount, when reaches it, it decays 1 item.
    public static int maxDecayCount = 1000000;  
    public int stackSize = 100;
    public Inventory inv;
    public int resAmount = 0;    // how much of this stack is reserved by pending tasks (source reservation)
    public float resTime = 0f;   // World.instance.timer value when last Reserve() was called (for staleness)
    public Task resTask;         // task that made the source reservation (for validity checking + logging)
    public int resSpace = 0;     // space reserved for incoming deliveries (destination reservation)
    public Item resSpaceItem;    // which item the space reservation is for (needed for empty stacks)
    public float resSpaceTime = 0f; // World.instance.timer value when last ReserveSpace() was called (for staleness)
    public Task resSpaceTask;    // task that made the space reservation (for validity checking + logging)

    // Formats a fen quantity for display. For a discrete item, shows the whole-unit count
    // (fen / unitFen). Otherwise renders liang (e.g. 250 → "2.5", 200 → "2", 5 → "0.05"); once
    // magnitude reaches 3 digits (≥99.5 liang) decimals are dropped to keep the UI compact.
    public static string FormatQ(int fen, Item item = null){
        if (item != null && item.discrete) return (fen / item.unitFen).ToString();
        if (fen % 100 == 0) return (fen / 100).ToString(); // exact integer, no decimals
        float val = fen / 100f;
        return Math.Abs(val) switch{
            >= 99.5f => val.ToString("0"),
            >= 0.96f => val.ToString("0.#"),
            _ => val.ToString("0.##")
        };
    }
    public static string FormatQ(ItemQuantity iq) => FormatQ(iq.quantity, iq.item);

    // Converts an author-facing liang value (from JSON) into internal fen.
    // All ItemNameQuantity → ItemQuantity conversion sites should use this to
    // preserve the "fen in code, liang in JSON" invariant (see SPEC-systems.md).
    // For user-typed input use TryParseQ instead — it handles overflow and validation.
    public static int LiangToFen(float liang) => (int)Math.Round(liang * 100);

    // Inverse of FormatQ — parses a user-typed quantity string into fen. For a discrete item the
    // input is a whole unit count (fen = count × unitFen); otherwise it is liang (fen = liang × 100).
    // Empty/whitespace → 0. Returns false for unparseable, negative, non-whole-count (discrete), or
    // overflowing input; callers should revert the display.
    public static bool TryParseQ(string qStr, Item item, out int fen) {
        fen = 0;
        if (string.IsNullOrWhiteSpace(qStr)) return true;
        if (!float.TryParse(qStr.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out float val))
            return false;
        if (val < 0) return false;
        bool discrete = item != null && item.discrete;
        if (discrete && Mathf.Abs(val - Mathf.Round(val)) > 0.0001f) return false;
        double fenD = discrete ? Math.Round(val) * item.unitFen : Math.Round(val * 100.0);
        if (fenD > int.MaxValue) return false;
        fen = (int)fenD;
        return true;
    }

    public ItemStack(Inventory inv, Item item, int quantity = 0, int stackSize = 100){
        this.item = item;
        this.quantity = quantity;
        this.stackSize = stackSize;
        this.inv = inv;
        decayCounter = 0;
    }

    // Usable capacity in fen. A discrete stack can only hold whole units, so the raw stackSize is
    // floored to the nearest unitFen multiple — a trailing remainder smaller than one unit is dead
    // space. Non-discrete items use the full stackSize.
    public int EffectiveCapacity => (item != null && item.discrete) ? stackSize - stackSize % item.unitFen : stackSize;

    public void Decay(float time = 1f) => DecayAtRate(item?.decayRate ?? 0f, time);

    // Wear applied while equipped on an animal that is currently working. Same
    // per-year unit as decayRate but only ticked from HandleWorking, so idle and
    // sleeping mice don't wear their tools/clothes. Shares decayCounter with the
    // passive Decay path — both contributions accumulate toward the same wear pool.
    public void EquipDecay(float time = 1f) => DecayAtRate(item?.equipDecayRate ?? 0f, time);

    void DecayAtRate(float rate, float time){
        if (item != null && quantity > 0 && rate != 0){
            float decayedQuantity = (float)quantity * (rate * time / (float)(World.ticksInDay*World.daysInYear));
            // ^ number near 0
            decayCounter += (int)(decayedQuantity * maxDecayCount);
            int amountToDecay = decayCounter / maxDecayCount;
            if (item.discrete) amountToDecay = (amountToDecay / item.unitFen) * item.unitFen; // only decay whole units
            if (amountToDecay > 0){
                // Capture pre-decay state so we can flag the rare case where decay empties a stack —
                // decay slows with quantity, so full zero-out means a small remnant sat around. Notable
                // especially if reserved — the reservation is silently dropped when the stack empties.
                Item before = item;
                int hadRes = resAmount;
                Animal animal = resTask?.animal;
                inv.Produce(item, -amountToDecay);
                decayCounter -= amountToDecay * maxDecayCount;
                // Track spoiled food for the colony food chart (no-op for non-edibles).
                StatsTracker.instance?.NoteDecayed(before, amountToDecay);
                if (this.item == null && inv?.invType == Inventory.InvType.Floor) {
                    string resNote = hadRes > 0 ? $" — silently dropped {hadRes} fen reservation (animal={animal?.aName})" : "";
                    Debug.LogWarning($"Decay emptied stack: {before.name} in {inv?.invType} at ({inv?.x},{inv?.y}){resNote}");
                }
            }
        }
    }
    // adjustReservation: when false, the else-branch auto-clamps of resAmount/resSpace are
    // skipped. Used by task-owned moves (Inventory.MoveItemTo with a `by` task), which release
    // exactly the acting task's reservation themselves (Task.ConsumeSource/SpaceReservation) and
    // would otherwise double-count it — the clamp releasing it once, Cleanup's Unreserve again.
    // The caller restores the invariant afterwards via Inventory.ClampReservationsToCapacity.
    public int? AddItem(Item item, int quantity, bool adjustReservation = true){
        if (this.item == null){ // adopt the incoming item only when stack is genuinely untagged
            this.item = item; }
        if (item != this.item){ // item slot occupied by different item. go next
            return null; }
        // Guard deposits only — a non-unit *removal* must never be silently discarded; only a
        // non-unit deposit is the error.
        if (item.discrete && quantity > 0 && quantity % item.unitFen != 0){
            Debug.LogWarning($"Discrete item '{item.name}': non-unit deposit {quantity} fen (unit={item.unitFen}) passed to AddItem");
            return 0; // discard the fractional item entirely
        }
        if (this.quantity + quantity > EffectiveCapacity){
            int sizeOver = this.quantity + quantity - EffectiveCapacity;
            this.quantity = EffectiveCapacity;
            resSpace = 0; // stack is full, no space to reserve
            resSpaceItem = null;
            return sizeOver; // overflow (3 if still have 3 to deposit)
        } else if (this.quantity + quantity <= 0){ // <= 0 because want to null out stack
            int sizeUnder = this.quantity + quantity - 0;
            this.quantity = 0;
            this.item = null;
            resAmount = 0;
            resSpace = 0;
            resSpaceItem = null;
            return sizeUnder; // underflow (-3 if still need 3 more)
        } else {
            this.quantity += quantity; // add to stack
            if (adjustReservation && resAmount > this.quantity) resAmount = this.quantity;
            // Clamp resSpace: someone else may have added items, reducing available space
            int maxResSpace = EffectiveCapacity - this.quantity;
            if (adjustReservation && resSpace > maxResSpace) resSpace = maxResSpace;
            return 0;
        }
    }
    public bool Empty(){ return (item == null || quantity == 0); }

    public bool ContainsItem(Item iitem){
        return (item == iitem && quantity > 0);
    }
    public bool HasSpaceForItem(Item iitem){
        return (item == iitem && quantity < EffectiveCapacity);
    }


    // ── Source reservations (resAmount) ─────────────────────────
    public bool Available() => resAmount < quantity;
    public int Reserve(int n, Task by = null) {
        int amount = Math.Min(n, quantity - resAmount);
        if (amount <= 0) return 0;
        resAmount += amount;
        resTime = World.instance != null ? World.instance.timer : 0f; // tolerate no-World (early init / model tests)
        resTask = by;
        return amount;
    }
    public bool Reserve(Task by = null) {
        if (!Available()) return false;
        resAmount++;
        resTime = World.instance != null ? World.instance.timer : 0f; // tolerate no-World (early init / model tests)
        resTask = by;
        return true;
    }
    public void Unreserve(int n = 1) {
        if (resAmount < n) { Debug.LogError($"ItemStack.Unreserve: underflow! item={item?.name ?? "null"} resAmount={FormatQ(resAmount, item)} n={FormatQ(n, item)}"); resAmount = 0; resTask = null; return; }
        resAmount -= n;
        if (resAmount == 0) resTask = null;
    }

    // ── Destination reservations (resSpace) ──────────────────
    // How much free space is available for `item`, accounting for resSpace. For a discrete forItem
    // the result is floored to a whole-unit multiple — callers must never be handed space they
    // cannot fill with whole units.
    public int FreeSpace(Item forItem) {
        if (item != null && item == forItem)
            return FloorToUnit(stackSize - quantity - resSpace, forItem);
        if (item == null || quantity == 0) {
            // Empty stack: available if unclaimed or claimed by the same item
            if (resSpaceItem == null || resSpaceItem == forItem)
                return FloorToUnit(stackSize - resSpace, forItem);
            return 0; // claimed by a different item
        }
        return 0; // occupied by a different item
    }
    // Physical free space (fen) currently usable by `query`, IGNORING reservations — unlike
    // FreeSpace, which subtracts resSpace for delivery planning. Used by the GlobalInventoryPanel
    // storage-capacity indicator, which reports the physical capacity the player has built, not the
    // in-flight-adjusted figure. An empty stack counts at full capacity; a stack already holding a
    // matching item counts its remaining space; a non-matching occupied stack counts 0. `query` may
    // be a group item (matches any leaf descendant).
    public int PhysicalFreeSpace(Item query) {
        if (item == null || quantity == 0)            // empty stack — floor to the query's unit if discrete
            return FloorToUnit(stackSize, query.IsGroup ? null : query);
        if (Inventory.MatchesItem(item, query))       // holds a matching item — floor to that leaf's unit
            return FloorToUnit(stackSize - quantity, item);
        return 0;                                      // occupied by a non-matching item
    }
    // Clamps a free-space figure to >=0, and for a discrete item down to a whole-unit (unitFen)
    // multiple. Floors the final figure — never EffectiveCapacity separately — so a non-unit
    // resSpace can't leak a fractional remainder through.
    private static int FloorToUnit(int free, Item forItem) {
        if (free <= 0) return 0;
        if (forItem != null && forItem.discrete) free -= free % forItem.unitFen;
        return free;
    }
    // Reserves up to `n` units of free space for incoming `item`. Returns amount reserved.
    public int ReserveSpace(Item forItem, int n, Task by = null) {
        int free = FreeSpace(forItem);
        int amount = Math.Min(n, free);
        if (amount <= 0) return 0;
        resSpace += amount;
        resSpaceTime = World.instance != null ? World.instance.timer : 0f; // tolerate no-World (early init / model tests)
        resSpaceTask = by;
        if (item == null && quantity == 0) resSpaceItem = forItem;
        return amount;
    }
    public void UnreserveSpace(int n) {
        if (resSpace < n) { Debug.LogError($"ItemStack.UnreserveSpace: underflow! resSpace={resSpace}, n={n}"); resSpace = 0; resSpaceItem = null; resSpaceTask = null; return; }
        resSpace -= n;
        if (resSpace == 0) { resSpaceItem = null; resSpaceTask = null; }
    }

    // ── Staleness expiry ─────────────────────────────────────
    // A reservation is only expired if BOTH conditions are met:
    //   1. Held longer than maxAge (time-based safety net)
    //   2. The task that made the reservation is no longer the animal's active task
    // This prevents false-positive expiry on legitimately long tasks (e.g. market journeys).
    private static bool TaskStillActive(Task t) => t != null && t.animal.task == t;

    public bool ExpireIfStale(float maxAge) {
        bool expired = false;
        if (resAmount > 0 && World.instance.timer - resTime > maxAge && !TaskStillActive(resTask)) {
            string itemName = item?.name ?? "null";
            string by = resTask != null ? $" by {resTask.animal.aName}" : "";
            Debug.LogWarning($"Cleared stale ItemStack source reservation{by}: item={itemName} resAmount={FormatQ(resAmount, item)} qty={FormatQ(quantity, item)} inv={inv.invType} ({inv.x},{inv.y}) held={World.instance.timer - resTime:F0}s");
            resAmount = 0;
            resTask = null;
            expired = true;
        }
        if (resSpace > 0 && World.instance.timer - resSpaceTime > maxAge && !TaskStillActive(resSpaceTask)) {
            string itemName = resSpaceItem?.name ?? item?.name ?? "null";
            string by = resSpaceTask != null ? $" by {resSpaceTask.animal.aName}" : "";
            Debug.LogWarning($"Cleared stale ItemStack space reservation{by}: item={itemName} resSpace={FormatQ(resSpace, resSpaceItem ?? item)} qty={FormatQ(quantity, item)} inv={inv.invType} ({inv.x},{inv.y}) held={World.instance.timer - resSpaceTime:F0}s");
            resSpace = 0;
            resSpaceItem = null;
            resSpaceTask = null;
            expired = true;
        }
        return expired;
    }

    public override string ToString(){
        // Reservation amounts (resAmount/resSpace) are dev internals — debug mode only.
        if (item != null){
            string resStr = DebugMode.Enabled && resAmount > 0 ? " (r" + FormatQ(resAmount, item) + ")" : "";
            string spcStr = DebugMode.Enabled && resSpace > 0 ? " (s" + FormatQ(resSpace, item) + ")" : "";
            return item.name + " x " + FormatQ(quantity, item) + resStr + spcStr + "\n";
        }
        if (DebugMode.Enabled && resSpace > 0 && resSpaceItem != null){
            return "(reserved for " + resSpaceItem.name + " s" + FormatQ(resSpace, resSpaceItem) + ")\n";
        }
        return "";
    }

}
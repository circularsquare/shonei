using System;
using UnityEngine;

// Per-building sub-component holding N named furnishing slots. Each slot is a 1-stack
// Inventory(Furnishing) plus a per-slot lifetime timer (in in-game days). Items are
// delivered by SupplyFurnishingTask (WOM standing order) and granted happiness to
// residents while installed. Decay is a fixed countdown, NOT routed through
// Inventory.Decay — the slot inventory uses InvType.Furnishing (0× decay multiplier)
// so the generic per-inv decay loop is a no-op for these.
//
// Lifetime ownership:
//   Building owns FurnishingSlots; FurnishingSlots owns the slot Inventory[]s and the
//   onSlotChanged callback. Building.Destroy() must call FurnishingSlots.Destroy().
//
// Eventing:
//   onSlotChanged(int slotIndex) fires on:
//     - NotifyInstalled (delivery complete) — called by SupplyFurnishingTask.Complete
//     - decay-out (TickDecay crosses 0)
//   Building's wired handler walks residents (by scanning AnimalController for
//   animals whose homeTile.building == this) and recomputes their furnishingScore.
public class FurnishingSlots {
    public string[] slotNames;           // from StructType.furnishingSlotNames; immutable
    public Inventory[] slotInvs;         // one Inventory(Furnishing), 1 stack, cap 1 per slot
    public float[] slotRemainingDays;    // per-slot lifetime timer; 0 when empty

    // Per-slot installed-item cache. Mirrors slotInvs[i].itemStacks[0].item but lets us
    // detect the non-null → null transition in TickDecay even when Inventory.Produce
    // nulls the stack item before our callback fires. Also used as the source of truth
    // for happiness recompute (Inventory queries would work but this is a single read).
    public Item[] slotItems;

    public Action<int> onSlotChanged;    // (slotIndex) install-complete or decay-out

    // Ctor takes (slotNames, position, name) directly rather than a Building reference so
    // EditMode tests can construct a FurnishingSlots without the full Building dependency.
    // The owning Building wires onSlotChanged → its own handler to do happiness recompute.
    public FurnishingSlots(string[] slotNames, int x, int y, string ownerName) {
        this.slotNames = slotNames ?? Array.Empty<string>();
        slotInvs = new Inventory[this.slotNames.Length];
        slotRemainingDays = new float[this.slotNames.Length];
        slotItems = new Item[this.slotNames.Length];
        for (int i = 0; i < this.slotNames.Length; i++) {
            // 1 stack, capacity 1 fen — a slot is "one furnishing or empty", not a quantity.
            slotInvs[i] = new Inventory(1, 1, Inventory.InvType.Furnishing, x, y);
            slotInvs[i].displayName = $"{ownerName}_furnish_{this.slotNames[i]}";
        }
    }

    public int SlotCount => slotNames.Length;
    public bool IsEmpty(int i) => slotItems[i] == null;
    public Item Get(int i) => slotItems[i];

    // Returns the index of the first empty slot whose name matches the item's furnishingSlot,
    // or -1 if no slot fits. Used by SupplyFurnishingTask.Initialize.
    public int FindEmptyMatchingSlot(Item item) {
        if (item == null || string.IsNullOrEmpty(item.furnishingSlot)) return -1;
        for (int i = 0; i < slotNames.Length; i++) {
            if (!IsEmpty(i)) continue;
            if (slotNames[i] == item.furnishingSlot) return i;
        }
        return -1;
    }

    // Returns the index of the first empty slot that has at least one matching item
    // currently in GlobalInventory (i.e. something a hauler could deliver). Returns -1
    // if no slot can be filled right now. Used by WOM isActive to gate the standing
    // SupplyFurnishing order without spawning useless tasks.
    public int FindAnyHaulableSlotIndex() {
        for (int i = 0; i < slotNames.Length; i++) {
            if (!IsEmpty(i)) continue;
            // Skip if a hauler is already mid-delivery to this slot — capacity is 1 and
            // resSpace already reserves the only fen, but Inventory.ReserveSpace returning
            // 0 for a fully-reserved empty stack is the canonical signal.
            if (slotInvs[i].itemStacks[0].resSpace > 0) continue;
            if (!Db.itemsByFurnishingSlot.TryGetValue(slotNames[i], out var candidates)) continue;
            foreach (Item candidate in candidates) {
                if (GlobalInventory.instance.Quantity(candidate) > 0) return i;
            }
        }
        return -1;
    }

    // Called by SupplyFurnishingTask.Complete after the item has been Move'd into the
    // slot's Inventory. Sets the lifetime timer and fires onSlotChanged so happiness
    // recomputes and the sprite overlay refreshes.
    public void NotifyInstalled(int slotIndex) {
        if (slotIndex < 0 || slotIndex >= slotNames.Length) return;
        Item installed = slotInvs[slotIndex].itemStacks[0].item;
        if (installed == null) {
            Debug.LogError($"FurnishingSlots.NotifyInstalled: slot {slotIndex} ('{slotNames[slotIndex]}') has no item — delivery must precede notify.");
            return;
        }
        slotItems[slotIndex] = installed;
        slotRemainingDays[slotIndex] = installed.furnishingLifetimeDays;
        onSlotChanged?.Invoke(slotIndex);
    }

    // Decrements per-slot lifetime by `secondsElapsed / World.ticksInDay` (= in-game days
    // elapsed). Empties any slot whose lifetime crosses ≤0 and fires onSlotChanged for it.
    // Called from StructController.TickUpdate (every 0.2s) — see [StructController.cs:182].
    public void TickDecay(float secondsElapsed) {
        for (int i = 0; i < slotNames.Length; i++) {
            if (IsEmpty(i)) continue;
            slotRemainingDays[i] -= secondsElapsed / (float)World.ticksInDay;
            if (slotRemainingDays[i] <= 0f) {
                Item item = slotItems[i];
                slotItems[i] = null;
                slotRemainingDays[i] = 0f;
                // Drain the slot inventory so GlobalInventory's totals stay accurate.
                int qty = slotInvs[i].itemStacks[0].quantity;
                if (qty > 0) slotInvs[i].Produce(item, -qty);
                onSlotChanged?.Invoke(i);
            }
        }
    }

    // Fires onSlotChanged for every currently-filled slot. Use after a silent restore
    // (SaveSystem.RestoreStructure populates slots directly without going through
    // NotifyInstalled, so visuals + happiness need a manual nudge).
    public void NotifyAllInstalled() {
        for (int i = 0; i < slotItems.Length; i++) {
            if (slotItems[i] != null) onSlotChanged?.Invoke(i);
        }
    }

    // Tears down every slot inventory. Called from Building.Destroy().
    public void Destroy() {
        for (int i = 0; i < slotInvs.Length; i++) {
            if (slotInvs[i] != null) {
                slotInvs[i].Destroy(reason: "furnishing slot destroyed");
                slotInvs[i] = null;
            }
        }
        onSlotChanged = null;
    }
}

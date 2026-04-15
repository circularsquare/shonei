#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

// Editor-only leak canary for the Task ↔ ItemStack reservation accounting.
//
// Invariant (one-directional): for every ItemStack,
//   stack.resAmount  ≤  Σ Task.ReservedStacks amounts pointing at this stack
//   stack.resSpace   ≤  Σ Task.ReservedSpaces amounts for (inv, item) on this stack
//
// We only flag actual > expected — that's the leak direction (stack claims more reservation
// than any live task tracks). The other direction (actual < expected) is *normal*: ItemStack
// auto-clamps resAmount/resSpace inside AddItem when quantity changes (items get taken,
// stack fills up, etc.), without notifying the holding Task. Task.reservedStacks is "what I
// originally asked for", stack.resAmount is "what's actually still reserved" — they can drift
// downward and Cleanup handles it via Math.Min.
//
// Real leak signatures this catches:
//   - Cleanup forgot to call base.Cleanup() → reservedStacks not released → stack stays high.
//   - An animal/task destroyed without Fail() → same.
//   - Code outside Task.ReserveStack calling stack.Reserve() directly.
//
// Cost: O(animals + Σ stacks). Hooked from World.Update() on a 30-second cadence.
// See SPEC-systems.md § Reservation Systems for the full mechanism landscape.
public static class ItemResChecker {

    // Runs one pass. LogErrors on every leak; never mutates state.
    public static void Check() {
        var ac = AnimalController.instance;
        var ic = InventoryController.instance;
        if (ac == null || ic == null) return; // pre-init — nothing to check yet

        // ── Build expected totals from live tasks ─────────────────────
        var expectedRes   = new Dictionary<ItemStack, int>();
        var expectedSpace = new Dictionary<(Inventory, Item), int>();

        for (int i = 0; i < ac.na; i++) {
            Animal a = ac.animals[i];
            Task t = a?.task;
            if (t == null) continue;
            foreach (var (stack, amount) in t.ReservedStacks) {
                expectedRes.TryGetValue(stack, out int prev);
                expectedRes[stack] = prev + amount;
            }
            foreach (var (inv, item, amount) in t.ReservedSpaces) {
                var key = (inv, item);
                expectedSpace.TryGetValue(key, out int prev);
                expectedSpace[key] = prev + amount;
            }
        }

        // ── Source-side leak: stack.resAmount > Σ live-task claims ────
        foreach (Inventory inv in ic.inventories) {
            if (inv == null || inv.itemStacks == null) continue;
            foreach (ItemStack s in inv.itemStacks) {
                if (s == null || s.resAmount <= 0) continue;
                expectedRes.TryGetValue(s, out int expected);
                if (s.resAmount > expected) {
                    string itemName = s.item?.name ?? "null";
                    string by = s.resTask != null ? $" lastResTask={s.resTask.animal?.aName}/{s.resTask}" : "";
                    Debug.LogError($"ItemResChecker[resAmount LEAK]: {inv.invType}({inv.x},{inv.y}) item={itemName} reserved={s.resAmount} claimed={expected} leaked={s.resAmount - expected}{by}");
                }
            }
        }

        // ── Destination-side leak: aggregated resSpace per (inv,item) > Σ live-task claims ──
        // Aggregate across all stacks in each inventory so a partial-fill spread across
        // multiple stacks reads as a single (inv, item) total.
        var actualSpace = new Dictionary<(Inventory, Item), int>();
        foreach (Inventory inv in ic.inventories) {
            if (inv == null || inv.itemStacks == null) continue;
            foreach (ItemStack s in inv.itemStacks) {
                if (s == null || s.resSpace <= 0) continue;
                Item item = s.item ?? s.resSpaceItem;
                if (item == null) continue;
                var key = (inv, item);
                actualSpace.TryGetValue(key, out int prev);
                actualSpace[key] = prev + s.resSpace;
            }
        }
        foreach (var kv in actualSpace) {
            expectedSpace.TryGetValue(kv.Key, out int expected);
            if (kv.Value > expected) {
                Inventory inv = kv.Key.Item1;
                Debug.LogError($"ItemResChecker[resSpace LEAK]: {inv.invType}({inv.x},{inv.y}) item={kv.Key.Item2.name} reserved={kv.Value} claimed={expected} leaked={kv.Value - expected}");
            }
        }
    }
}
#endif

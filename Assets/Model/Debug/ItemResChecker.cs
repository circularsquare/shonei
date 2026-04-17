#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

// Editor-only leak canary for the Task ↔ ItemStack reservation accounting.
//
// Invariant (one-directional): for every ItemStack,
//   stack.resAmount  ≤  Σ Task.ReservedStacks amounts pointing at this stack
//   stack.resSpace   ≤  Σ Task.ReservedSpaces amounts pointing at this stack
//
// We only flag actual > expected — that's the leak direction (stack claims more reservation
// than any live task tracks). The other direction (actual < expected) is *normal*: ItemStack
// auto-clamps resAmount/resSpace inside AddItem when quantity changes (items get taken,
// stack fills up, etc.), without notifying the holding Task. Task.reservedStacks/reservedSpaces
// is "what I originally asked for", stack.resAmount/resSpace is "what's actually still
// reserved" — they can drift downward and Cleanup handles it via Math.Min.
//
// Real leak signatures this catches:
//   - Cleanup forgot to call base.Cleanup() → reservations not released → stack stays high.
//   - An animal/task destroyed without Fail() → same.
//   - Code outside Task.ReserveStack/ReserveSpace calling stack.Reserve()/ReserveSpace() directly.
//
// Cost: O(animals + Σ stacks). Hooked from World.Update() on a 30-second cadence.
// See SPEC-systems.md § Reservation Systems for the full mechanism landscape.
public static class ItemResChecker {

    // Runs one pass. LogErrors on every leak; never mutates state.
    public static void Check() {
        var ac = AnimalController.instance;
        var ic = InventoryController.instance;
        if (ac == null || ic == null) return; // pre-init — nothing to check yet

        // ── Build expected totals from live tasks (both keyed by ItemStack identity) ──
        var expectedRes   = new Dictionary<ItemStack, int>();
        var expectedSpace = new Dictionary<ItemStack, int>();

        for (int i = 0; i < ac.na; i++) {
            Animal a = ac.animals[i];
            Task t = a?.task;
            if (t == null) continue;
            foreach (var (stack, amount) in t.ReservedStacks) {
                expectedRes.TryGetValue(stack, out int prev);
                expectedRes[stack] = prev + amount;
            }
            foreach (var (stack, amount) in t.ReservedSpaces) {
                expectedSpace.TryGetValue(stack, out int prev);
                expectedSpace[stack] = prev + amount;
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

        // ── Destination-side leak: stack.resSpace > Σ live-task claims for that stack ──
        foreach (Inventory inv in ic.inventories) {
            if (inv == null || inv.itemStacks == null) continue;
            foreach (ItemStack s in inv.itemStacks) {
                if (s == null || s.resSpace <= 0) continue;
                expectedSpace.TryGetValue(s, out int expected);
                if (s.resSpace > expected) {
                    string itemName = s.item?.name ?? s.resSpaceItem?.name ?? "null";
                    Debug.LogError($"ItemResChecker[resSpace LEAK]: {inv.invType}({inv.x},{inv.y}) item={itemName} reserved={s.resSpace} claimed={expected} leaked={s.resSpace - expected}");
                }
            }
        }
    }
}
#endif

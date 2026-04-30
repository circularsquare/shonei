#if UNITY_EDITOR
using NUnit.Framework;

// EditMode tests for ItemResChecker — the editor-only leak canary that compares
// stack.resAmount/resSpace against the sum of live Task reservation claims.
//
// ── What's testable in EditMode ───────────────────────────────────────
// The pre-init guard: Check() must early-return without throwing or logging when
// AnimalController.instance and InventoryController.instance haven't been wired up
// (i.e. no scene loaded). This is the only branch reachable without spinning up
// World/Animal/Inventory infrastructure.
//
// ── Deferred to integration tests ─────────────────────────────────────
// The interesting branches — building expectedRes/expectedSpace from live tasks,
// detecting source-side and destination-side leaks, formatting the LogError messages
// — all require:
//   - AnimalController.instance with populated `animals` and Tasks holding reservations
//   - InventoryController.instance with populated `inventories` and ItemStacks
//   - A Task fixture that can populate ReservedStacks / ReservedSpaces
// These are the same dependencies blocking ItemStack.Reserve / Decay tests, and will
// be picked up together when we have the World/Inventory/Task test fixture (Tier 2).
//
// Wrapped in UNITY_EDITOR because the SUT is — without the guard, the test class
// references a type that doesn't exist in player builds.
[TestFixture]
public class ItemResCheckerTests {

    // ── Pre-init guard ─────────────────────────────────────────────────
    [Test]
    public void Check_NoControllers_DoesNotThrow(){
        // EditMode tests run with no scene loaded → both controllers are null →
        // Check() should hit the early-return on line 31 of ItemResChecker.cs.
        // If somebody removes that guard, every Check() pass during normal editor
        // startup will NRE; this test catches that regression.
        Assert.DoesNotThrow(() => ItemResChecker.Check());
    }

    [Test]
    public void Check_NoControllers_DoesNotLog(){
        // The early-return must be silent — no LogErrors, no spurious leak warnings.
        // If anything below the guard runs without controllers, we'd see an NRE log
        // or a misleading leak message; LogAssert.NoUnexpectedReceived() asserts the
        // pass produced zero log output.
        ItemResChecker.Check();
        UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void Check_RepeatedCalls_NoControllers_StillIdempotent(){
        // The checker is meant to run on a 30-second cadence from World.Update —
        // even before init it'll be invoked many times. Multiple calls in the
        // pre-init state must remain harmless.
        Assert.DoesNotThrow(() => {
            for (int i = 0; i < 5; i++) ItemResChecker.Check();
        });
        UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
    }
}
#endif

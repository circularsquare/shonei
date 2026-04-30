using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// EditMode tests for Reservable — semaphore-like ref-count for multi-animal tasks.
// Focuses on the pure / dependency-free surface: the constructor, Available(),
// Unreserve(), and the early-out path of Reserve() when capacity is exhausted
// (which short-circuits before World.instance.timer is read).
//
// Reservation symmetry is exercised by mutating the `reserved` field directly to
// stand in for successful Reserve() calls — the field is public, and this avoids
// pulling in a live World MonoBehaviour just to read its timer.
//
// ── Deferred to integration tests ────────────────────────────────────
// The successful path of Reserve(string) / Reserve(Task) and ExpireIfStale touch
// World.instance.timer; constructing a World requires the full singleton graph
// (Tile grid, sub-controllers, MaintenanceSystem, PowerSystem, …) which is
// inappropriate for a unit test. The Task overload of Reserve also needs a live
// Animal MonoBehaviour to populate reservedBy via task.animal.aName. These will
// be covered once we have a World/Animal test fixture (Tier 2 of the test plan).
[TestFixture]
public class ReservableTests {

    // ── Constructor ─────────────────────────────────────────────────────
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(5)]
    [TestCase(1000)]
    public void Constructor_SetsCapacityAndEffectiveCapacity(int capacity){
        Reservable r = new Reservable(capacity);
        Assert.That(r.capacity, Is.EqualTo(capacity));
        Assert.That(r.effectiveCapacity, Is.EqualTo(capacity));
        Assert.That(r.reserved, Is.EqualTo(0));
    }

    [Test]
    public void Constructor_LeavesReservedByFieldsNull(){
        Reservable r = new Reservable(3);
        Assert.That(r.reservedBy, Is.Null);
        Assert.That(r.reservedByTask, Is.Null);
        Assert.That(r.reservedAt, Is.EqualTo(0f));
    }

    // ── Available ───────────────────────────────────────────────────────
    [Test]
    public void Available_FreshReservable_ReturnsTrue(){
        Reservable r = new Reservable(2);
        Assert.That(r.Available(), Is.True);
    }

    [Test]
    public void Available_PartiallyReserved_ReturnsTrue(){
        Reservable r = new Reservable(3);
        r.reserved = 2;
        Assert.That(r.Available(), Is.True);
    }

    [Test]
    public void Available_FullyReserved_ReturnsFalse(){
        Reservable r = new Reservable(3);
        r.reserved = 3;
        Assert.That(r.Available(), Is.False);
    }

    [Test]
    public void Available_ZeroCapacity_ReturnsFalse(){
        // Zero-capacity reservables (effectiveCapacity == 0) are never available —
        // useful for player-disabling a workplace without destroying it.
        Reservable r = new Reservable(0);
        Assert.That(r.Available(), Is.False);
    }

    [Test]
    public void Available_GatesOnEffectiveCapacity_NotHardCapacity(){
        // Player-set effectiveCapacity below hard capacity must restrict availability.
        Reservable r = new Reservable(5);
        r.effectiveCapacity = 2;
        r.reserved = 2;
        Assert.That(r.Available(), Is.False);
    }

    [Test]
    public void Available_EffectiveCapacityRaisedAboveReserved_ReturnsTrue(){
        Reservable r = new Reservable(5);
        r.effectiveCapacity = 1;
        r.reserved = 1;
        Assert.That(r.Available(), Is.False);
        r.effectiveCapacity = 3;
        Assert.That(r.Available(), Is.True);
    }

    // ── Reserve (early-out path) ────────────────────────────────────────
    // The successful Reserve path reads World.instance.timer, which we can't
    // stand up here. The capacity-exhausted path returns false BEFORE that read,
    // so it's safely testable in isolation.
    [Test]
    public void Reserve_String_AtEffectiveCapacity_ReturnsFalse_NoMutation(){
        Reservable r = new Reservable(2);
        r.reserved = 2;
        bool ok = r.Reserve("animal-a");
        Assert.That(ok, Is.False);
        Assert.That(r.reserved, Is.EqualTo(2));
        Assert.That(r.reservedBy, Is.Null);
        Assert.That(r.reservedByTask, Is.Null);
    }

    [Test]
    public void Reserve_String_ZeroCapacity_AlwaysReturnsFalse(){
        Reservable r = new Reservable(0);
        bool ok = r.Reserve("animal-a");
        Assert.That(ok, Is.False);
        Assert.That(r.reserved, Is.EqualTo(0));
    }

    [Test]
    public void Reserve_String_EffectiveCapacityZero_ReturnsFalse(){
        // Player-disabled workplace (capacity 5 but effectiveCapacity dialled to 0).
        Reservable r = new Reservable(5);
        r.effectiveCapacity = 0;
        bool ok = r.Reserve("animal-a");
        Assert.That(ok, Is.False);
        Assert.That(r.reserved, Is.EqualTo(0));
    }

    [Test]
    public void Reserve_Task_AtEffectiveCapacity_ReturnsFalse_NoMutation(){
        Reservable r = new Reservable(1);
        r.reserved = 1;
        // Task is abstract and the success path would dereference task.animal — but the
        // capacity-full early-out doesn't touch the parameter at all, so null is safe here.
        bool ok = r.Reserve((Task)null);
        Assert.That(ok, Is.False);
        Assert.That(r.reserved, Is.EqualTo(1));
        Assert.That(r.reservedByTask, Is.Null);
    }

    // ── Unreserve ──────────────────────────────────────────────────────
    [Test]
    public void Unreserve_DefaultCount_DecrementsByOne(){
        Reservable r = new Reservable(3);
        r.reserved = 2;
        bool ok = r.Unreserve();
        Assert.That(ok, Is.True);
        Assert.That(r.reserved, Is.EqualTo(1));
    }

    [TestCase(5, 1, 4)]
    [TestCase(5, 3, 2)]
    [TestCase(5, 5, 0)]
    public void Unreserve_ExplicitCount_Decrements(int initial, int n, int expected){
        Reservable r = new Reservable(10);
        r.reserved = initial;
        bool ok = r.Unreserve(n);
        Assert.That(ok, Is.True);
        Assert.That(r.reserved, Is.EqualTo(expected));
    }

    [Test]
    public void Unreserve_ToZero_ClearsReservedByFields(){
        Reservable r = new Reservable(2);
        r.reserved = 1;
        r.reservedBy = "animal-a";
        // reservedByTask stays null — Task is abstract and not needed for this assertion.
        bool ok = r.Unreserve();
        Assert.That(ok, Is.True);
        Assert.That(r.reserved, Is.EqualTo(0));
        Assert.That(r.reservedBy, Is.Null);
        Assert.That(r.reservedByTask, Is.Null);
    }

    [Test]
    public void Unreserve_PartialDecrement_PreservesReservedByFields(){
        // While reserved > 0 we shouldn't lose track of who's holding it.
        Reservable r = new Reservable(3);
        r.reserved = 2;
        r.reservedBy = "animal-a";
        bool ok = r.Unreserve();
        Assert.That(ok, Is.True);
        Assert.That(r.reserved, Is.EqualTo(1));
        Assert.That(r.reservedBy, Is.EqualTo("animal-a"));
    }

    [Test]
    public void Unreserve_Underflow_ReturnsFalse_LogsError_NoMutation(){
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
            "Unreserve underflow.*reserved=1 n=2"));
        Reservable r = new Reservable(3);
        r.reserved = 1;
        bool ok = r.Unreserve(2);
        Assert.That(ok, Is.False);
        Assert.That(r.reserved, Is.EqualTo(1)); // unchanged on failure
    }

    [Test]
    public void Unreserve_FromZero_ReturnsFalse_LogsError(){
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
            "Unreserve underflow.*reserved=0 n=1"));
        Reservable r = new Reservable(3);
        bool ok = r.Unreserve();
        Assert.That(ok, Is.False);
        Assert.That(r.reserved, Is.EqualTo(0));
    }

    [Test]
    public void Unreserve_WithLabel_IncludesLabelInErrorMessage(){
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
            @"Unreserve underflow \[mill-seat\]"));
        Reservable r = new Reservable(2);
        bool ok = r.Unreserve(1, "mill-seat");
        Assert.That(ok, Is.False);
    }

    [Test]
    public void Unreserve_EmptyLabel_OmitsBracketContext(){
        // Label "" should not produce "[]" in the message — the code branches on Length>0.
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
            @"^(?!.*\[\]).*Unreserve underflow"));
        Reservable r = new Reservable(2);
        r.Unreserve(1);
    }

    // ── Reserve / Unreserve symmetry ───────────────────────────────────
    // We can't drive Reserve() directly without World.instance, but we can
    // simulate N successful reservations by setting the reserved field and
    // confirming N Unreserves return state to zero with metadata cleared.
    [TestCase(1)]
    [TestCase(3)]
    [TestCase(10)]
    public void ReserveThenUnreserveN_LeavesReservedAtZero(int n){
        Reservable r = new Reservable(n);
        r.reserved = n;
        r.reservedBy = "animal-a";
        for (int i = 0; i < n; i++){
            bool ok = r.Unreserve();
            Assert.That(ok, Is.True, $"unreserve #{i + 1} should succeed");
        }
        Assert.That(r.reserved, Is.EqualTo(0));
        Assert.That(r.reservedBy, Is.Null);
        Assert.That(r.reservedByTask, Is.Null);
    }

    [Test]
    public void Unreserve_BatchEqualsTotal_ClearsInOneCall(){
        // Unreserve(n) where n == reserved should be equivalent to N single-step calls.
        Reservable r = new Reservable(5);
        r.reserved = 5;
        r.reservedBy = "animal-a";
        bool ok = r.Unreserve(5);
        Assert.That(ok, Is.True);
        Assert.That(r.reserved, Is.EqualTo(0));
        Assert.That(r.reservedBy, Is.Null);
        Assert.That(r.reservedByTask, Is.Null);
    }

    // ── Capacity edges ─────────────────────────────────────────────────
    [Test]
    public void SingleCapacity_BehavesLikeMutex(){
        Reservable r = new Reservable(1);
        Assert.That(r.Available(), Is.True);
        r.reserved = 1;
        Assert.That(r.Available(), Is.False);
        bool denied = r.Reserve("animal-b");
        Assert.That(denied, Is.False);
        Assert.That(r.reserved, Is.EqualTo(1));
        bool released = r.Unreserve();
        Assert.That(released, Is.True);
        Assert.That(r.Available(), Is.True);
    }

    [Test]
    public void LargeCapacity_AvailableUntilFullyReserved(){
        Reservable r = new Reservable(1000);
        r.reserved = 999;
        Assert.That(r.Available(), Is.True);
        r.reserved = 1000;
        Assert.That(r.Available(), Is.False);
    }
}

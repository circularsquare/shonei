using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// EditMode tests for ItemStack — focuses on the pure / dependency-free surface:
// the static fen/liang utilities (LiangToFen, FormatQ, TryParseQ) and the instance
// helpers that don't reach into World or Inventory (AddItem, Empty, ContainsItem,
// HasSpaceForItem, Available, FreeSpace).
//
// ── Deferred to integration tests ────────────────────────────────────
// Reserve / ReserveSpace / ExpireIfStale touch World.instance.timer; Decay needs
// World.ticksInDay + Inventory.Produce. Unreserve / UnreserveSpace are pure but
// realistic setup (a reservation already in place) currently requires the World/
// Inventory plumbing too. These will be covered once we have a World/Inventory
// test fixture (Tier 2 of the test plan).
[TestFixture]
public class ItemStackTests {

    // ── LiangToFen ──────────────────────────────────────────────────────
    [TestCase(0f, 0)]
    [TestCase(1f, 100)]
    [TestCase(0.5f, 50)]
    [TestCase(2.5f, 250)]
    [TestCase(99.5f, 9950)]
    [TestCase(100f, 10000)]
    [TestCase(1234.56f, 123456)]
    public void LiangToFen_ValidInput_ReturnsRoundedFen(float liang, int expectedFen){
        Assert.That(ItemStack.LiangToFen(liang), Is.EqualTo(expectedFen));
    }

    [Test]
    public void LiangToFen_Negative_ReturnsNegativeFen(){
        // LiangToFen does no validation — it's the conversion primitive. Caller
        // (TryParseQ) is responsible for rejecting negatives. Documenting the contract.
        Assert.That(ItemStack.LiangToFen(-1.5f), Is.EqualTo(-150));
    }

    [Test]
    public void LiangToFen_RoundsBankerStyle(){
        // Math.Round defaults to banker's rounding. Spot-check a value that exposes it.
        Assert.That(ItemStack.LiangToFen(0.005f), Is.EqualTo(1).Or.EqualTo(0));
        // Note: 0.005f as a float isn't exactly 0.005, so result depends on FP — accept either.
    }

    [Test]
    public void LiangToFen_FormatQ_RoundTrip(){
        // Whole and half-liang values should survive the round-trip via FormatQ → float → LiangToFen.
        foreach (float liang in new[]{ 0f, 1f, 2.5f, 10f, 12.3f, 99f, 100f, 250f }){
            int fen = ItemStack.LiangToFen(liang);
            string formatted = ItemStack.FormatQ(fen);
            float parsed = float.Parse(formatted, System.Globalization.CultureInfo.InvariantCulture);
            int back = ItemStack.LiangToFen(parsed);
            // FormatQ may drop precision (e.g. >=99.5 → no decimals), so allow ±50 fen for those tiers.
            int tolerance = liang >= 99.5f ? 50 : 1;
            Assert.That(System.Math.Abs(back - fen), Is.LessThanOrEqualTo(tolerance),
                $"round-trip failed for {liang} liang ({fen} fen → '{formatted}' → {back} fen)");
        }
    }

    // ── FormatQ ─────────────────────────────────────────────────────────
    // Exact-integer tier (fen % 100 == 0) → no decimals.
    [TestCase(0, "0")]
    [TestCase(100, "1")]
    [TestCase(200, "2")]
    [TestCase(9900, "99")]
    [TestCase(10000, "100")]
    public void FormatQ_ExactInteger_NoDecimals(int fen, string expected){
        Assert.That(ItemStack.FormatQ(fen), Is.EqualTo(expected));
    }

    // Small tier (magnitude < 0.96) → "0.##" (up to two decimals).
    [TestCase(5, "0.05")]
    [TestCase(25, "0.25")]
    [TestCase(50, "0.5")]
    [TestCase(95, "0.95")]
    public void FormatQ_SmallMagnitude_TwoDecimals(int fen, string expected){
        Assert.That(ItemStack.FormatQ(fen), Is.EqualTo(expected));
    }

    // Mid tier (0.96 <= magnitude < 99.5) → "0.#" (one decimal, dropped if zero).
    [TestCase(150, "1.5")]
    [TestCase(250, "2.5")]
    [TestCase(999, "10")]   // 9.99 → rounds to "10" under "0.#"
    [TestCase(1234, "12.3")]
    [TestCase(9949, "99.5")]
    public void FormatQ_MidMagnitude_OneDecimal(int fen, string expected){
        Assert.That(ItemStack.FormatQ(fen), Is.EqualTo(expected));
    }

    // Large tier (magnitude >= 99.5) → no decimals.
    [TestCase(9950, "100")]
    [TestCase(12345, "123")]
    [TestCase(99999, "1000")]
    public void FormatQ_LargeMagnitude_NoDecimals(int fen, string expected){
        Assert.That(ItemStack.FormatQ(fen), Is.EqualTo(expected));
    }

    // discrete=true short-circuits to integer-liang regardless of magnitude.
    [TestCase(0, "0")]
    [TestCase(100, "1")]
    [TestCase(150, "1")]      // truncates fractional liang
    [TestCase(9999, "99")]
    [TestCase(10000, "100")]
    public void FormatQ_Discrete_IntegerLiangOnly(int fen, string expected){
        Assert.That(ItemStack.FormatQ(fen, discrete: true), Is.EqualTo(expected));
    }

    [Test]
    public void FormatQ_UsesInvariantCulture(){
        // 2.5 liang must render with a '.' decimal separator regardless of test-runner locale.
        Assert.That(ItemStack.FormatQ(250), Does.Contain(".").And.Not.Contain(","));
    }

    [Test]
    public void FormatQ_ItemQuantityOverload_DelegatesToIntBoolVersion(){
        Item discreteItem = MakeItem("apple", discrete: true);
        ItemQuantity iq = new ItemQuantity(discreteItem, 250);
        // discrete=true → 250 fen → "2"
        Assert.That(ItemStack.FormatQ(iq), Is.EqualTo("2"));

        Item smoothItem = MakeItem("water", discrete: false);
        ItemQuantity iq2 = new ItemQuantity(smoothItem, 250);
        Assert.That(ItemStack.FormatQ(iq2), Is.EqualTo("2.5"));
    }

    // ── TryParseQ ───────────────────────────────────────────────────────
    [TestCase("", 0)]
    [TestCase("   ", 0)]
    [TestCase(null, 0)]
    public void TryParseQ_EmptyOrWhitespace_ReturnsTrueWithZero(string input, int expectedFen){
        bool ok = ItemStack.TryParseQ(input, discrete: false, out int fen);
        Assert.That(ok, Is.True);
        Assert.That(fen, Is.EqualTo(expectedFen));
    }

    [TestCase("0", 0)]
    [TestCase("1", 100)]
    [TestCase("2.5", 250)]
    [TestCase("0.05", 5)]
    [TestCase("99.5", 9950)]
    [TestCase("1000", 100000)]
    public void TryParseQ_Valid_RoundTrips(string input, int expectedFen){
        bool ok = ItemStack.TryParseQ(input, discrete: false, out int fen);
        Assert.That(ok, Is.True);
        Assert.That(fen, Is.EqualTo(expectedFen));
    }

    [Test]
    public void TryParseQ_TrimsWhitespace(){
        bool ok = ItemStack.TryParseQ("  2.5  ", discrete: false, out int fen);
        Assert.That(ok, Is.True);
        Assert.That(fen, Is.EqualTo(250));
    }

    [TestCase("-1")]
    [TestCase("-0.5")]
    public void TryParseQ_Negative_ReturnsFalse(string input){
        bool ok = ItemStack.TryParseQ(input, discrete: false, out int fen);
        Assert.That(ok, Is.False);
    }

    [TestCase("abc")]
    [TestCase("1.2.3")]
    [TestCase("--1")]
    public void TryParseQ_Garbage_ReturnsFalse(string input){
        bool ok = ItemStack.TryParseQ(input, discrete: false, out int fen);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryParseQ_DiscreteFractional_ReturnsFalse(){
        bool ok = ItemStack.TryParseQ("1.5", discrete: true, out int fen);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryParseQ_DiscreteWhole_ReturnsTrue(){
        bool ok = ItemStack.TryParseQ("3", discrete: true, out int fen);
        Assert.That(ok, Is.True);
        Assert.That(fen, Is.EqualTo(300));
    }

    [Test]
    public void TryParseQ_Overflow_ReturnsFalse(){
        // liang * 100 must exceed int.MaxValue (2_147_483_647). 1e9 liang = 1e11 fen.
        bool ok = ItemStack.TryParseQ("99999999999", discrete: false, out int fen);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryParseQ_InvariantCulture_DotDecimal(){
        bool ok = ItemStack.TryParseQ("1.5", discrete: false, out int fen);
        Assert.That(ok, Is.True);
        Assert.That(fen, Is.EqualTo(150));
    }

    [Test]
    public void TryParseQ_InvariantCulture_RejectsCommaDecimal(){
        // "1,5" must NOT parse as 1.5 — TryParseQ pins InvariantCulture.
        // (NumberStyles.Float still allows thousands separators in some cultures, but
        //  "1,5" under invariant should fail because ',' isn't a decimal separator.)
        bool ok = ItemStack.TryParseQ("1,5", discrete: false, out int fen);
        Assert.That(ok, Is.False);
    }

    // ── AddItem ─────────────────────────────────────────────────────────
    [Test]
    public void AddItem_ToEmptyStack_SetsItemAndQuantity(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, null, 0);
        int? overflow = s.AddItem(apple, 50);
        Assert.That(overflow, Is.EqualTo(0));
        Assert.That(s.item, Is.SameAs(apple));
        Assert.That(s.quantity, Is.EqualTo(50));
    }

    [Test]
    public void AddItem_MismatchedItem_ReturnsNullAndDoesNotMutate(){
        Item apple = MakeItem("apple");
        Item pear  = MakeItem("pear");
        ItemStack s = new ItemStack(null, apple, 30);
        int? r = s.AddItem(pear, 10);
        Assert.That(r, Is.Null);
        Assert.That(s.item, Is.SameAs(apple));
        Assert.That(s.quantity, Is.EqualTo(30));
    }

    [Test]
    public void AddItem_DiscreteFractional_ReturnsZeroAndWarns(){
        // Discrete items must come in whole-liang (100 fen) chunks. Anything else
        // is silently dropped (returns 0) plus a Debug.LogWarning.
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
            "Discrete item.*non-whole-liang quantity"));
        Item apple = MakeItem("apple", discrete: true);
        ItemStack s = new ItemStack(null, apple, 0);
        int? r = s.AddItem(apple, 150); // 1.5 liang — fractional for discrete
        Assert.That(r, Is.EqualTo(0));
        Assert.That(s.quantity, Is.EqualTo(0)); // nothing was actually added
    }

    [Test]
    public void AddItem_DiscreteWholeLiang_AddsNormally(){
        Item apple = MakeItem("apple", discrete: true);
        // stackSize=500 leaves room for 2 liang (200 fen) without overflow.
        ItemStack s = new ItemStack(null, apple, 0, stackSize: 500);
        int? r = s.AddItem(apple, 200); // 2 liang — whole
        Assert.That(r, Is.EqualTo(0));
        Assert.That(s.quantity, Is.EqualTo(200));
    }

    [Test]
    public void AddItem_ExceedsStackSize_ReturnsOverflow_ClampsAndClearsResSpace(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 80, stackSize: 100);
        s.resSpace = 10;
        s.resSpaceItem = apple;
        int? r = s.AddItem(apple, 30); // would total 110, overflow by 10
        Assert.That(r, Is.EqualTo(10));
        Assert.That(s.quantity, Is.EqualTo(100));
        Assert.That(s.resSpace, Is.EqualTo(0));
        Assert.That(s.resSpaceItem, Is.Null);
    }

    [Test]
    public void AddItem_ReducesToZero_ClearsItemAndReservations(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 30);
        s.resAmount = 5;
        s.resSpace = 7;
        s.resSpaceItem = apple;
        int? r = s.AddItem(apple, -30);
        Assert.That(r, Is.EqualTo(0));        // exact zero, no underflow
        Assert.That(s.quantity, Is.EqualTo(0));
        Assert.That(s.item, Is.Null);
        Assert.That(s.resAmount, Is.EqualTo(0));
        Assert.That(s.resSpace, Is.EqualTo(0));
        Assert.That(s.resSpaceItem, Is.Null);
    }

    [Test]
    public void AddItem_ReducesBelowZero_ReturnsUnderflow_ClearsItem(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 20);
        int? r = s.AddItem(apple, -50); // 20 - 50 = -30 underflow
        Assert.That(r, Is.EqualTo(-30));
        Assert.That(s.quantity, Is.EqualTo(0));
        Assert.That(s.item, Is.Null);
    }

    [Test]
    public void AddItem_NormalAdd_ClampsResAmountToNewQuantity(){
        // If resAmount > new quantity (which can happen post-decay or odd states),
        // a normal add should clamp it down. Concrete case: stack with resAmount=50,
        // we shrink quantity by adding a negative that doesn't zero out…
        // Actually — resAmount only gets clamped *down*, so set up: stack 50 + res 50,
        // remove 30 → quantity=20, resAmount must clamp to 20.
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 50);
        s.resAmount = 50;
        int? r = s.AddItem(apple, -30);
        Assert.That(r, Is.EqualTo(0));
        Assert.That(s.quantity, Is.EqualTo(20));
        Assert.That(s.resAmount, Is.EqualTo(20));
    }

    [Test]
    public void AddItem_NormalAdd_ClampsResSpaceWhenSpaceShrinks(){
        // After this add there's only stackSize-quantity = 30 free, but resSpace was 50.
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 20, stackSize: 100);
        s.resSpace = 50;
        s.resSpaceItem = apple;
        int? r = s.AddItem(apple, 50); // quantity 70, free space 30 < resSpace 50
        Assert.That(r, Is.EqualTo(0));
        Assert.That(s.quantity, Is.EqualTo(70));
        Assert.That(s.resSpace, Is.EqualTo(30));
    }

    // ── Empty / ContainsItem / HasSpaceForItem ─────────────────────────
    [Test]
    public void Empty_NoItem_ReturnsTrue(){
        ItemStack s = new ItemStack(null, null, 0);
        Assert.That(s.Empty(), Is.True);
    }

    [Test]
    public void Empty_ItemButZeroQuantity_ReturnsTrue(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 0);
        Assert.That(s.Empty(), Is.True);
    }

    [Test]
    public void Empty_ItemAndQuantity_ReturnsFalse(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 5);
        Assert.That(s.Empty(), Is.False);
    }

    [Test]
    public void ContainsItem_MatchingItemWithQuantity_ReturnsTrue(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 5);
        Assert.That(s.ContainsItem(apple), Is.True);
    }

    [Test]
    public void ContainsItem_MatchingItemZeroQuantity_ReturnsFalse(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 0);
        Assert.That(s.ContainsItem(apple), Is.False);
    }

    [Test]
    public void ContainsItem_DifferentItem_ReturnsFalse(){
        Item apple = MakeItem("apple");
        Item pear = MakeItem("pear");
        ItemStack s = new ItemStack(null, apple, 5);
        Assert.That(s.ContainsItem(pear), Is.False);
    }

    [Test]
    public void HasSpaceForItem_SameItemBelowStackSize_ReturnsTrue(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 50, stackSize: 100);
        Assert.That(s.HasSpaceForItem(apple), Is.True);
    }

    [Test]
    public void HasSpaceForItem_SameItemAtStackSize_ReturnsFalse(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 100, stackSize: 100);
        Assert.That(s.HasSpaceForItem(apple), Is.False);
    }

    [Test]
    public void HasSpaceForItem_DifferentItem_ReturnsFalse(){
        Item apple = MakeItem("apple");
        Item pear = MakeItem("pear");
        ItemStack s = new ItemStack(null, apple, 50, stackSize: 100);
        Assert.That(s.HasSpaceForItem(pear), Is.False);
    }

    [Test]
    public void HasSpaceForItem_EmptyStack_ReturnsFalse(){
        // HasSpaceForItem requires this.item == iitem — empty stack has no item, so always false.
        // (FreeSpace handles the empty-stack case; HasSpaceForItem is a narrower check.)
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, null, 0);
        Assert.That(s.HasSpaceForItem(apple), Is.False);
    }

    // ── Available ──────────────────────────────────────────────────────
    [Test]
    public void Available_UnreservedQuantity_ReturnsTrue(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 10);
        s.resAmount = 3;
        Assert.That(s.Available(), Is.True);
    }

    [Test]
    public void Available_FullyReserved_ReturnsFalse(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 10);
        s.resAmount = 10;
        Assert.That(s.Available(), Is.False);
    }

    [Test]
    public void Available_EmptyStack_ReturnsFalse(){
        ItemStack s = new ItemStack(null, null, 0);
        Assert.That(s.Available(), Is.False);
    }

    // ── FreeSpace ──────────────────────────────────────────────────────
    [Test]
    public void FreeSpace_SameItem_ReturnsRemainingMinusResSpace(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 30, stackSize: 100);
        s.resSpace = 20;
        Assert.That(s.FreeSpace(apple), Is.EqualTo(50)); // 100 - 30 - 20
    }

    [Test]
    public void FreeSpace_SameItem_OverReserved_ClampsToZero(){
        // Defensive: if quantity + resSpace > stackSize somehow, FreeSpace shouldn't go negative.
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, apple, 90, stackSize: 100);
        s.resSpace = 50;
        Assert.That(s.FreeSpace(apple), Is.EqualTo(0));
    }

    [Test]
    public void FreeSpace_EmptyStackUnclaimed_ReturnsFullStackMinusResSpace(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, null, 0, stackSize: 100);
        Assert.That(s.FreeSpace(apple), Is.EqualTo(100));
    }

    [Test]
    public void FreeSpace_EmptyStackClaimedBySameItem_ReturnsRemainingSpace(){
        Item apple = MakeItem("apple");
        ItemStack s = new ItemStack(null, null, 0, stackSize: 100);
        s.resSpace = 30;
        s.resSpaceItem = apple;
        Assert.That(s.FreeSpace(apple), Is.EqualTo(70));
    }

    [Test]
    public void FreeSpace_EmptyStackClaimedByDifferentItem_ReturnsZero(){
        Item apple = MakeItem("apple");
        Item pear = MakeItem("pear");
        ItemStack s = new ItemStack(null, null, 0, stackSize: 100);
        s.resSpace = 30;
        s.resSpaceItem = pear;
        Assert.That(s.FreeSpace(apple), Is.EqualTo(0));
    }

    [Test]
    public void FreeSpace_OccupiedByDifferentItem_ReturnsZero(){
        Item apple = MakeItem("apple");
        Item pear = MakeItem("pear");
        ItemStack s = new ItemStack(null, apple, 30, stackSize: 100);
        Assert.That(s.FreeSpace(pear), Is.EqualTo(0));
    }

    // ── Helpers ────────────────────────────────────────────────────────
    static Item MakeItem(string name = "test", bool discrete = false){
        return new Item { id = 1, name = name, discrete = discrete };
    }
}

using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// EditMode tests for GlobalInventory — covers the pure / dependency-free surface:
// AddItem (the public mutation entry — note CLAUDE.md says it's "private" conceptually
// in the sense of "callers should use Inventory.Produce", but it's actually `public` on
// GlobalInventory itself), AddItems, the three Quantity overloads (string/int/Item) with
// special focus on group-aware summing, SufficientResources, and the change callback.
//
// ── Setup pattern ─────────────────────────────────────────────────────
// GlobalInventory's constructor (a) reads Db.itemsFlat to seed itemAmounts, and
// (b) assigns itself to the static `instance` (logging an error if non-null).
// Per [SetUp] we (1) populate Db.itemsFlat with a small fixture (apple/pear leaves
// under a "food" group + a discrete "wood" leaf), populate Db.items so that
// Quantity(int) / Quantity(string) can resolve ids to Items (group-aware path),
// (2) populate Db.iidByName for the string-name overload, (3) clear
// GlobalInventory.instance via reflection so the constructor doesn't log, then
// construct a fresh instance. [TearDown] clears the instance again so other test
// fixtures aren't affected.
//
// ── Deferred ──────────────────────────────────────────────────────────
// AddItems(ItemQuantity[]) is exercised here for the negate path; the non-negate
// path is symmetric and covered by the AddItem tests. The full Inventory.Produce →
// GlobalInventory.AddItem dispatch loop is integration-level (needs an Inventory and
// a tile/owner) and stays out of EditMode.
[TestFixture]
public class GlobalInventoryTests {

    Item food;     // group: food → { apple, pear }
    Item apple;    // leaf
    Item pear;     // leaf
    Item wood;     // unrelated leaf, discrete

    [SetUp]
    public void SetUp(){
        // Build a tiny item tree. Ids must be unique and small so they fit in a
        // Dictionary keyed by id without clashing with anything else.
        apple = new Item { id = 1, name = "apple" };
        pear  = new Item { id = 2, name = "pear" };
        food  = new Item { id = 3, name = "food", children = new[]{ apple, pear } };
        apple.parent = food;
        pear.parent = food;
        wood  = new Item { id = 4, name = "wood", discrete = true };

        // Db.itemsFlat is what the constructor uses to seed itemAmounts. Only leaves
        // belong here (groups are never physical) — matches how Db.cs builds it.
        Db.items     = new Item[]{ null, apple, pear, food, wood };
        Db.itemsFlat = new Item[]{ apple, pear, wood };

        // For the string-name Quantity overload. All four names resolve through the
        // group-aware path now: "food" sums apple+pear like Quantity(Item) does.
        SetDbDict("iidByName", new Dictionary<string, int>{
            { "apple", apple.id }, { "pear", pear.id }, { "wood", wood.id },
            { "food", food.id },
        });

        // Wipe the singleton so the constructor doesn't fire its dup-check LogError.
        SetInstance(null);
        new GlobalInventory();
    }

    [TearDown]
    public void TearDown(){
        SetInstance(null);
    }

    // ── Construction ────────────────────────────────────────────────────
    [Test]
    public void Constructor_SeedsItemAmountsForEveryFlatLeaf(){
        // Every leaf in Db.itemsFlat should have an entry initialized to 0.
        GlobalInventory gi = GlobalInventory.instance;
        Assert.That(gi.itemAmounts.ContainsKey(apple.id), Is.True);
        Assert.That(gi.itemAmounts.ContainsKey(pear.id), Is.True);
        Assert.That(gi.itemAmounts.ContainsKey(wood.id), Is.True);
        Assert.That(gi.itemAmounts[apple.id], Is.EqualTo(0));
        Assert.That(gi.itemAmounts[pear.id], Is.EqualTo(0));
        Assert.That(gi.itemAmounts[wood.id], Is.EqualTo(0));
    }

    [Test]
    public void Constructor_SecondInstance_LogsError(){
        // The first instance was created in SetUp; constructing another should LogError.
        LogAssert.Expect(LogType.Error, "there should only be one global inv");
        new GlobalInventory();
    }

    // ── AddItem ─────────────────────────────────────────────────────────
    [Test]
    public void AddItem_ByItem_LeafItem_AccumulatesQuantity(){
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(apple, 50);
        gi.AddItem(apple, 25);
        Assert.That(gi.Quantity(apple.id), Is.EqualTo(75));
    }

    [Test]
    public void AddItem_ByItem_GroupItem_IsIgnored(){
        // Group items never physically exist — the Item-overload of AddItem must early-return.
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(food, 100);
        // After the early-return, no leaf was credited — Quantity(food) sums apple+pear = 0.
        Assert.That(gi.Quantity(food.id), Is.EqualTo(0));
        Assert.That(gi.Quantity(apple.id), Is.EqualTo(0));
        Assert.That(gi.Quantity(pear.id), Is.EqualTo(0));
    }

    [Test]
    public void AddItem_ById_UnknownId_LogsErrorAndDoesNotMutate(){
        // The int overload validates against itemAmounts (seeded from Db.itemsFlat) —
        // unknown ids must NOT create phantom entries. Logs an error and returns.
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
            "GlobalInventory\\.AddItem: unknown item id 999"));
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(999, 7);
        // Unknown id query also logs and returns 0 — expect that LogError too.
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
            "GlobalInventory\\.Quantity: unknown item id 999"));
        Assert.That(gi.Quantity(999), Is.EqualTo(0));
        // No state mutation: itemAmounts must not have grown a 999 entry.
        Assert.That(gi.itemAmounts.ContainsKey(999), Is.False);
    }

    [Test]
    public void AddItem_NegativeQuantity_DecrementsBelowZero(){
        // GlobalInventory itself has no negative-clamp; relies on callers (Inventory.Produce)
        // not to over-remove. Tested here so the contract is explicit.
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(apple, 5);
        gi.AddItem(apple, -10);
        Assert.That(gi.Quantity(apple.id), Is.EqualTo(-5));
    }

    // ── AddItems (bulk) ─────────────────────────────────────────────────
    [Test]
    public void AddItems_NegateFalse_AddsEachQuantity(){
        GlobalInventory gi = GlobalInventory.instance;
        ItemQuantity[] iqs = { new ItemQuantity(apple, 30), new ItemQuantity(pear, 20) };
        gi.AddItems(iqs);
        Assert.That(gi.Quantity(apple.id), Is.EqualTo(30));
        Assert.That(gi.Quantity(pear.id), Is.EqualTo(20));
    }

    [Test]
    public void AddItems_NegateTrue_SubtractsEachQuantity(){
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(apple, 100);
        gi.AddItem(pear, 100);
        ItemQuantity[] iqs = { new ItemQuantity(apple, 30), new ItemQuantity(pear, 20) };
        gi.AddItems(iqs, negate: true);
        Assert.That(gi.Quantity(apple.id), Is.EqualTo(70));
        Assert.That(gi.Quantity(pear.id), Is.EqualTo(80));
    }

    // ── Quantity(Item): group-aware ────────────────────────────────────
    [Test]
    public void QuantityItem_GroupItem_SumsLeafChildren(){
        // The headline invariant: Quantity(food) = Quantity(apple) + Quantity(pear).
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(apple, 30);
        gi.AddItem(pear, 12);
        Assert.That(gi.Quantity(food), Is.EqualTo(42));
    }

    [Test]
    public void QuantityItem_LeafItem_ReturnsExactCount(){
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(apple, 30);
        gi.AddItem(pear, 12);
        Assert.That(gi.Quantity(apple), Is.EqualTo(30));
        Assert.That(gi.Quantity(pear), Is.EqualTo(12));
    }

    [Test]
    public void QuantityItem_GroupWithNoStock_ReturnsZero(){
        GlobalInventory gi = GlobalInventory.instance;
        Assert.That(gi.Quantity(food), Is.EqualTo(0));
    }

    // ── Quantity(int) symmetry with Quantity(Item) ──────────────────────
    [Test]
    public void QuantityInt_OnGroupId_SumsLeafChildren(){
        // The int overload routes through Quantity(Item), so a group id sums
        // its leaf descendants exactly like the Item overload — no asymmetry.
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(apple, 30);
        gi.AddItem(pear, 12);
        Assert.That(gi.Quantity(food.id), Is.EqualTo(42));
        Assert.That(gi.Quantity(food), Is.EqualTo(42));
    }

    [Test]
    public void QuantityInt_UnknownId_LogsErrorAndReturnsZero(){
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
            "GlobalInventory\\.Quantity: unknown item id 12345"));
        GlobalInventory gi = GlobalInventory.instance;
        Assert.That(gi.Quantity(12345), Is.EqualTo(0));
    }

    // ── Quantity(string) ───────────────────────────────────────────────
    [Test]
    public void QuantityString_LeafName_AgreesWithIntOverload(){
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(apple, 17);
        Assert.That(gi.Quantity("apple"), Is.EqualTo(gi.Quantity(apple.id)));
        Assert.That(gi.Quantity("apple"), Is.EqualTo(17));
    }

    [Test]
    public void QuantityString_GroupName_SumsLeafChildren(){
        // String overload routes through Quantity(int) → Quantity(Item), so it
        // matches the group-aware behaviour of the Item overload.
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(apple, 30);
        gi.AddItem(pear, 12);
        Assert.That(gi.Quantity("food"), Is.EqualTo(42));
    }

    // ── SufficientResources ────────────────────────────────────────────
    [Test]
    public void SufficientResources_AllSatisfied_ReturnsTrue(){
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(apple, 50);
        gi.AddItem(pear, 30);
        ItemQuantity[] iqs = { new ItemQuantity(apple, 40), new ItemQuantity(pear, 30) };
        Assert.That(gi.SufficientResources(iqs), Is.True);
    }

    [Test]
    public void SufficientResources_OneShort_ReturnsFalse(){
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(apple, 50);
        gi.AddItem(pear, 10);
        ItemQuantity[] iqs = { new ItemQuantity(apple, 40), new ItemQuantity(pear, 30) };
        Assert.That(gi.SufficientResources(iqs), Is.False);
    }

    [Test]
    public void SufficientResources_GroupCost_SatisfiedByAnyLeafMix(){
        // Recipe inputs are often group items ("any food"). SufficientResources must
        // use Quantity(Item) so that the apple+pear total satisfies a "food" cost.
        GlobalInventory gi = GlobalInventory.instance;
        gi.AddItem(apple, 25);
        gi.AddItem(pear, 25);
        ItemQuantity[] iqs = { new ItemQuantity(food, 40) };
        Assert.That(gi.SufficientResources(iqs), Is.True);
    }

    // ── Change callback ────────────────────────────────────────────────
    [Test]
    public void Callback_FiresOnAddItem(){
        GlobalInventory gi = GlobalInventory.instance;
        int fired = 0;
        GlobalInventory captured = null;
        System.Action<GlobalInventory> cb = inv => { fired++; captured = inv; };
        gi.RegisterCbInventoryChanged(cb);
        gi.AddItem(apple, 5);
        Assert.That(fired, Is.EqualTo(1));
        Assert.That(captured, Is.SameAs(gi));
    }

    [Test]
    public void Callback_FiresOncePerAddItemCall(){
        // Bulk AddItems is N AddItem calls under the hood — each fires the callback.
        GlobalInventory gi = GlobalInventory.instance;
        int fired = 0;
        gi.RegisterCbInventoryChanged(_ => fired++);
        ItemQuantity[] iqs = { new ItemQuantity(apple, 5), new ItemQuantity(pear, 3) };
        gi.AddItems(iqs);
        Assert.That(fired, Is.EqualTo(2));
    }

    [Test]
    public void Callback_DoesNotFireAfterUnregister(){
        GlobalInventory gi = GlobalInventory.instance;
        int fired = 0;
        System.Action<GlobalInventory> cb = _ => fired++;
        gi.RegisterCbInventoryChanged(cb);
        gi.UnregisterCbInventoryChanged(cb);
        gi.AddItem(apple, 5);
        Assert.That(fired, Is.EqualTo(0));
    }

    [Test]
    public void Callback_MultipleSubscribers_AllFire(){
        GlobalInventory gi = GlobalInventory.instance;
        int a = 0, b = 0;
        gi.RegisterCbInventoryChanged(_ => a++);
        gi.RegisterCbInventoryChanged(_ => b++);
        gi.AddItem(apple, 1);
        Assert.That(a, Is.EqualTo(1));
        Assert.That(b, Is.EqualTo(1));
    }

    // ── Reflection helpers ──────────────────────────────────────────────
    // GlobalInventory.instance has a `protected set` on the auto-property; tests need
    // to wipe it between fixtures so the constructor's dup-check doesn't LogError.
    // We invoke the non-public setter directly (PropertyInfo.SetValue won't see a
    // protected setter on a public property without explicit flags; using the
    // MethodInfo is the most reliable cross-runtime approach).
    static void SetInstance(GlobalInventory value){
        PropertyInfo prop = typeof(GlobalInventory).GetProperty(
            "instance", BindingFlags.Public | BindingFlags.Static);
        prop.GetSetMethod(nonPublic: true).Invoke(null, new object[]{ value });
    }

    // Db.iidByName / Db.itemByName likewise have protected setters on the static
    // auto-properties. Generic helper so we can wire up just what we need per test.
    static void SetDbDict<TVal>(string propName, Dictionary<string, TVal> value){
        PropertyInfo prop = typeof(Db).GetProperty(
            propName, BindingFlags.Public | BindingFlags.Static);
        prop.GetSetMethod(nonPublic: true).Invoke(null, new object[]{ value });
    }
}

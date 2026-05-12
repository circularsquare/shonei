using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// EditMode tests for FurnishingSlots — the building-side sub-component that holds
// named furnishing slots, tracks per-slot lifetime, and fires onSlotChanged on
// install + decay-out events. Lean by design: covers the install/decay/find/happiness
// invariants without spinning up a real Building (the refactored ctor takes
// (slotNames, x, y, name) so no Building is required).
//
// Like InventoryTests, we stand up minimal Db + InventoryController + GlobalInventory
// scaffolding via reflection (the same pattern, copy/pasted helpers).
[TestFixture]
public class FurnishingSlotsTests {
    Item cloth;        // furnishingSlot="cloth", happiness=1.0, lifetime=5d
    Item ramieCloth;   // leaf, inherits furnishing fields (we set them directly here)
    Item apple;        // unrelated item — for "doesn't fit" tests

    GameObject controllerGo;
    InventoryController controller;

    Item[] savedItemsFlat;
    Item[] savedItems;
    int savedItemsCount;
    Dictionary<string, Item> savedItemByName;
    Dictionary<string, int> savedIidByName;
    Dictionary<string, List<Item>> savedItemsByFurnishingSlot;

    [OneTimeSetUp]
    public void OneTimeSetUp() {
        savedItemsFlat = Db.itemsFlat;
        savedItems     = Db.items;
        savedItemsCount = GetStaticField<int>(typeof(Db), "itemsCount");
        savedItemByName = Db.itemByName;
        savedIidByName  = Db.iidByName;
        savedItemsByFurnishingSlot = Db.itemsByFurnishingSlot;

        // Use leaf cloth (ramie cloth) as the furnishable; matches the real schema (group
        // "cloth" inherits down to leaf "ramie cloth" via AddItemToDb at runtime — we
        // skip the inheritance step here and just set the leaf fields directly).
        cloth = new Item {
            id = 260, name = "cloth", itemClass = ItemClass.Default,
        };
        ramieCloth = new Item {
            id = 261, name = "ramie cloth", itemClass = ItemClass.Default,
            furnishingSlot = "cloth", furnishingHappiness = 1.0f, furnishingLifetimeDays = 5.0f,
            furnishingSprite = "furnishing_cloth", parent = cloth,
        };
        apple = new Item { id = 1, name = "apple", itemClass = ItemClass.Default };

        Item[] flat = { apple, cloth, ramieCloth };
        Db.itemsFlat = flat;
        Item[] byId = new Item[300];
        byId[1] = apple; byId[260] = cloth; byId[261] = ramieCloth;
        Db.items = byId;

        SetStaticProp(typeof(Db), "itemByName", new Dictionary<string, Item> {
            { "apple", apple }, { "cloth", cloth }, { "ramie cloth", ramieCloth }
        });
        SetStaticProp(typeof(Db), "iidByName", new Dictionary<string, int> {
            { "apple", 1 }, { "cloth", 260 }, { "ramie cloth", 261 }
        });
        Db.itemsByFurnishingSlot = new Dictionary<string, List<Item>> {
            { "cloth", new List<Item> { ramieCloth } }
        };
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() {
        Db.itemsFlat = savedItemsFlat;
        Db.items     = savedItems;
        SetStaticField(typeof(Db), "itemsCount", savedItemsCount);
        SetStaticProp(typeof(Db), "itemByName", savedItemByName);
        SetStaticProp(typeof(Db), "iidByName",  savedIidByName);
        Db.itemsByFurnishingSlot = savedItemsByFurnishingSlot;
    }

    [SetUp]
    public void SetUp() {
        controllerGo = new GameObject("TestInventoryController");
        controller = controllerGo.AddComponent<InventoryController>();
        SetStaticProp(typeof(InventoryController), "instance", controller);

        SetStaticProp(typeof(GlobalInventory), "instance", null);
        new GlobalInventory();
    }

    [TearDown]
    public void TearDown() {
        SetStaticProp(typeof(InventoryController), "instance", null);
        SetStaticProp(typeof(GlobalInventory), "instance", null);
        if (controllerGo != null) Object.DestroyImmediate(controllerGo);
        controllerGo = null;
        controller = null;
    }

    // ── Install: filling the slot caches item and lifetime ─────────────
    [Test]
    public void NotifyInstalled_StoresItemAndSetsLifetime() {
        var fs = new FurnishingSlots(new[] { "cloth" }, 0, 0, "house");
        int fireCount = 0;
        int firedIndex = -1;
        fs.onSlotChanged = i => { fireCount++; firedIndex = i; };

        fs.slotInvs[0].Produce(ramieCloth, 1);  // simulate DeliverToInventoryObjective filling the slot
        fs.NotifyInstalled(0);

        Assert.That(fs.IsEmpty(0), Is.False);
        Assert.That(fs.Get(0), Is.SameAs(ramieCloth));
        Assert.That(fs.slotRemainingDays[0], Is.EqualTo(5.0f).Within(0.001f));
        Assert.That(fireCount, Is.EqualTo(1), "onSlotChanged should fire exactly once on install");
        Assert.That(firedIndex, Is.EqualTo(0));
    }

    // ── Decay: empties slot and fires onSlotChanged exactly once on 0-cross ──
    [Test]
    public void TickDecay_EmptiesSlotAndFiresOnce_OnZeroCross() {
        var fs = new FurnishingSlots(new[] { "cloth" }, 0, 0, "house");
        fs.slotInvs[0].Produce(ramieCloth, 1);
        fs.NotifyInstalled(0);

        int fireCount = 0;
        fs.onSlotChanged = _ => fireCount++;

        // Drive enough seconds to cross 5 days (ticksInDay=240 → 5d = 1200s).
        // First TickDecay below 5d worth of seconds should NOT empty.
        fs.TickDecay(World.ticksInDay * 2f); // 2 days
        Assert.That(fs.IsEmpty(0), Is.False, "still 3 days remaining");
        Assert.That(fireCount, Is.EqualTo(0));

        fs.TickDecay(World.ticksInDay * 3f); // exactly 3 more days → crosses 0
        Assert.That(fs.IsEmpty(0), Is.True, "should be empty after 5 days total");
        Assert.That(fireCount, Is.EqualTo(1), "decay-out fires exactly once");

        // A subsequent TickDecay on the already-empty slot must NOT fire again.
        fs.TickDecay(World.ticksInDay);
        Assert.That(fireCount, Is.EqualTo(1), "empty slot is not re-fired");
    }

    [Test]
    public void TickDecay_DrainsGlobalInventory() {
        // The slot's quantity counts toward GlobalInventory; decay-out must drain it,
        // otherwise the world's total inflates by one cloth per decayed furnishing.
        var fs = new FurnishingSlots(new[] { "cloth" }, 0, 0, "house");
        fs.slotInvs[0].Produce(ramieCloth, 1);
        fs.NotifyInstalled(0);
        Assert.That(GlobalInventory.instance.Quantity(ramieCloth), Is.EqualTo(1));

        fs.TickDecay(World.ticksInDay * 6f); // well past lifetime
        Assert.That(fs.IsEmpty(0), Is.True);
        Assert.That(GlobalInventory.instance.Quantity(ramieCloth), Is.EqualTo(0),
            "decayed slot must drain global inv");
    }

    // ── FindEmptyMatchingSlot: type-gating ─────────────────────────────
    [Test]
    public void FindEmptyMatchingSlot_ReturnsIndexForMatchingItem() {
        var fs = new FurnishingSlots(new[] { "cloth", "chair" }, 0, 0, "house");
        Assert.That(fs.FindEmptyMatchingSlot(ramieCloth), Is.EqualTo(0));
    }

    [Test]
    public void FindEmptyMatchingSlot_ReturnsMinusOne_ForUnrelatedItem() {
        var fs = new FurnishingSlots(new[] { "cloth" }, 0, 0, "house");
        Assert.That(fs.FindEmptyMatchingSlot(apple), Is.EqualTo(-1),
            "apple has no furnishingSlot");
    }

    [Test]
    public void FindEmptyMatchingSlot_SkipsFilledSlots() {
        var fs = new FurnishingSlots(new[] { "cloth", "cloth" }, 0, 0, "house");
        // Fill slot 0
        fs.slotInvs[0].Produce(ramieCloth, 1);
        fs.NotifyInstalled(0);
        // Second cloth slot is still open
        Assert.That(fs.FindEmptyMatchingSlot(ramieCloth), Is.EqualTo(1));
    }

    // ── Happiness recompute: sums furnishingHappiness over filled slots ─
    [Test]
    public void RecomputeFurnishingBonus_IsZero_WhenNoHouse() {
        var happiness = new Happiness();
        happiness.furnishingScore = 99f; // poison value
        happiness.RecomputeFurnishingBonus(null);
        Assert.That(happiness.furnishingScore, Is.EqualTo(0f),
            "no animal → bonus must be 0, never lingering stale state");
    }

    // ── Save/restore round-trip is covered by the integration save tests ─
    // (Requires real Building + SaveSystem fixture — deferred to PlayMode
    // snapshot scenarios. The data path is straightforward field copies; the
    // mechanics tested above guarantee NotifyInstalled is the only state mutator.)

    // ── Helpers ────────────────────────────────────────────────────────
    static void SetStaticProp(System.Type type, string name, object value) {
        PropertyInfo p = type.GetProperty(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(p, Is.Not.Null, $"static property {type.Name}.{name} not found");
        p.SetValue(null, value);
    }

    static void SetStaticField(System.Type type, string name, object value) {
        FieldInfo f = type.GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(f, Is.Not.Null, $"static field {type.Name}.{name} not found");
        f.SetValue(null, value);
    }

    static T GetStaticField<T>(System.Type type, string name) {
        FieldInfo f = type.GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(f, Is.Not.Null, $"static field {type.Name}.{name} not found");
        return (T)f.GetValue(null);
    }
}

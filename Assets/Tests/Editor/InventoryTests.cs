using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// EditMode tests for Inventory — focuses on the data-side invariants:
// Produce updates GlobalInventory, MoveItemTo doesn't double-count, Quantity sums
// across stacks, ItemTypeCompatible enforces ItemClass match, and stack
// auto-creation/cleanup behaves on Produce in/drain.
//
// Setup pattern: Inventory's constructor reaches for InventoryController.instance
// (registry), GlobalInventory.instance (totals), and Db.itemsFlat (allow-list keys).
// We stand up a minimal Unity-side scaffold:
//   - A throwaway GameObject hosting an InventoryController component, with its
//     static `instance` set via reflection (the setter is protected).
//   - A fresh GlobalInventory, with its static `instance` reset via reflection
//     each test for isolation (the ctor LogErrors on duplicate).
//   - A small Db.itemsFlat / Db.items snapshot built per-fixture, restored in
//     [OneTimeTearDown] so the suite doesn't poison sibling tests.
// We use InvType.Animal for most tests to skip the constructor's GameObject /
// SpriteRenderer creation path — it's irrelevant to the invariants being tested
// and avoids the Lighting/Resources dependencies.
//
// ── Deferred to later tiers ────────────────────────────────────
// - Floor/Storage haul side effects (RegisterHaul/RemoveHaulForStack/
//   RegisterStorageEvictionHaul) — needs WorkOrderManager fixture.
// - Decay() — uses World.ticksInDay; needs World fixture.
// - TickUpdate / ExpireIfStale — needs World.instance.timer.
// - UpdateSprite paths (Floor/Storage) — touches Resources.Load and SpriteRenderer
//   plumbing; not load-bearing for the model-level invariants.
// - Restack — pure-ish but tangential to the public Produce/Move contract.
// - ReserveSpace plumbing — already covered for ItemStack; the inventory-level
//   distribution loop is best tested alongside Task reservation tests.
[TestFixture]
public class InventoryTests {
    sealed class TestTask : Task {
        public bool completed;

        public TestTask(Animal animal) : base(animal) {}

        public override bool Initialize() => true;

        public override void Complete() {
            completed = true;
            base.Complete();
        }
    }

    // ── Test items (rebuilt per-fixture, restored in OneTimeTearDown) ───
    Item apple;        // Default class, leaf
    Item pear;         // Default class, leaf
    Item water;        // Liquid class, leaf
    Item book;         // Book class, leaf

    GameObject controllerGo;
    InventoryController controller;

    // Snapshots so the suite leaves Db in the state it found it (other test
    // fixtures may rely on Db being empty or populated by a prior bootstrap).
    Item[] savedItemsFlat;
    Item[] savedItems;
    int savedItemsCount;
    Dictionary<string, Item> savedItemByName;
    Dictionary<string, int> savedIidByName;

    [OneTimeSetUp]
    public void OneTimeSetUp(){
        // Snapshot Db state.
        savedItemsFlat   = Db.itemsFlat;
        savedItems       = Db.items;
        savedItemsCount  = GetStaticField<int>(typeof(Db), "itemsCount");
        savedItemByName  = Db.itemByName;
        savedIidByName   = Db.iidByName;

        // Build a small item set. ids must match array indices so Db.items[id] works.
        apple = new Item { id = 1, name = "apple", itemClass = ItemClass.Default, discrete = false };
        pear  = new Item { id = 2, name = "pear",  itemClass = ItemClass.Default, discrete = false };
        water = new Item { id = 3, name = "water", itemClass = ItemClass.Liquid,  discrete = false };
        book  = new Item { id = 4, name = "book",  itemClass = ItemClass.Book,    discrete = false };

        Item[] flat = { apple, pear, water, book };
        Db.itemsFlat = flat;
        // Db.items is indexed by id with possible nulls — size to max id + 1.
        Item[] byId = new Item[5];
        byId[1] = apple; byId[2] = pear; byId[3] = water; byId[4] = book;
        Db.items = byId;

        SetStaticProp(typeof(Db), "itemByName", new Dictionary<string, Item>{
            { "apple", apple }, { "pear", pear }, { "water", water }, { "book", book }
        });
        SetStaticProp(typeof(Db), "iidByName", new Dictionary<string, int>{
            { "apple", 1 }, { "pear", 2 }, { "water", 3 }, { "book", 4 }
        });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown(){
        Db.itemsFlat   = savedItemsFlat;
        Db.items       = savedItems;
        SetStaticField(typeof(Db), "itemsCount", savedItemsCount);
        SetStaticProp(typeof(Db), "itemByName", savedItemByName);
        SetStaticProp(typeof(Db), "iidByName",  savedIidByName);
    }

    [SetUp]
    public void SetUp(){
        // Fresh InventoryController on a throwaway GameObject. We don't call Start()
        // (that path expects ItemDisplay prefabs, etc.), we just inject the static
        // instance directly. inventories / byType are inline-initialized so no
        // further wiring is needed for AddInventory.
        controllerGo = new GameObject("TestInventoryController");
        controller = controllerGo.AddComponent<InventoryController>();
        SetStaticProp(typeof(InventoryController), "instance", controller);

        // Fresh GlobalInventory — its ctor self-installs into the static `instance`
        // but LogErrors if one is already there, so clear first.
        SetStaticProp(typeof(GlobalInventory), "instance", null);
        new GlobalInventory();
    }

    [TearDown]
    public void TearDown(){
        SetStaticProp(typeof(InventoryController), "instance", null);
        SetStaticProp(typeof(GlobalInventory), "instance", null);
        if (controllerGo != null) Object.DestroyImmediate(controllerGo);
        controllerGo = null;
        controller = null;
    }

    // ── Produce: adds to inventory AND updates GlobalInventory ─────────
    // The "no double-count" rule from CLAUDE.md says Produce is the only path that
    // touches global inv on creation. These tests verify both halves of that.
    [Test]
    public void Produce_AddsToInventoryAndGlobal(){
        Inventory inv = MakeAnimal();
        inv.Produce(apple, 50);
        Assert.That(inv.Quantity(apple), Is.EqualTo(50), "local inv");
        Assert.That(GlobalInventory.instance.Quantity(apple), Is.EqualTo(50), "global inv");
    }

    [Test]
    public void Produce_NegativeAmount_DrainsBoth(){
        // Produce is also the drain path (Decay calls Produce(-x)). Verify global
        // mirrors the drop.
        Inventory inv = MakeAnimal();
        inv.Produce(apple, 100);
        inv.Produce(apple, -40);
        Assert.That(inv.Quantity(apple), Is.EqualTo(60));
        Assert.That(GlobalInventory.instance.Quantity(apple), Is.EqualTo(60));
    }

    [Test]
    public void Produce_OverflowingStack_OnlyCreditsAcceptedAmount(){
        // The single Animal stack has stackSize 100 by default. Producing 250 fits
        // 100 and returns 150 leftover; only the accepted 100 should hit global inv,
        // otherwise we'd inflate world totals beyond what physically exists.
        Inventory inv = MakeAnimal(stackSize: 100);
        int leftover = inv.Produce(apple, 250);
        Assert.That(leftover, Is.EqualTo(150));
        Assert.That(inv.Quantity(apple), Is.EqualTo(100));
        Assert.That(GlobalInventory.instance.Quantity(apple), Is.EqualTo(100));
    }

    // ── MoveItemTo: transfer must conserve global total ────────────────
    [Test]
    public void MoveItemTo_TransfersWithoutDoubleCounting(){
        Inventory src  = MakeAnimal();
        Inventory dst  = MakeAnimal();
        src.Produce(apple, 80);
        int globalBefore = GlobalInventory.instance.Quantity(apple);

        int moved = src.MoveItemTo(dst, apple, 30);

        Assert.That(moved, Is.EqualTo(30));
        Assert.That(src.Quantity(apple), Is.EqualTo(50));
        Assert.That(dst.Quantity(apple), Is.EqualTo(30));
        Assert.That(GlobalInventory.instance.Quantity(apple), Is.EqualTo(globalBefore),
            "MoveItemTo must not change global totals — items already exist in the world");
    }

    [Test]
    public void MoveItemTo_FullStack_DrainsSourceCreatesDestStack(){
        Inventory src = MakeAnimal();
        Inventory dst = MakeAnimal();
        src.Produce(apple, 50);

        int moved = src.MoveItemTo(dst, apple, 50);

        Assert.That(moved, Is.EqualTo(50));
        Assert.That(src.Quantity(apple), Is.EqualTo(0));
        Assert.That(dst.Quantity(apple), Is.EqualTo(50));
        // Source stack should be cleared (item == null) once drained.
        Assert.That(src.itemStacks[0].item, Is.Null);
    }

    [Test]
    public void MoveItemTo_DestFull_ReturnsLeftoverToSource(){
        // Dest stack capped at 50; source has 100. Try to move all 100 — only 50
        // should land in dest, the other 50 should bounce back to source so the
        // global total is unchanged.
        Inventory src = MakeAnimal(stackSize: 200);
        Inventory dst = MakeAnimal(stackSize: 50);
        src.Produce(apple, 100);
        int globalBefore = GlobalInventory.instance.Quantity(apple);

        int moved = src.MoveItemTo(dst, apple, 100);

        Assert.That(moved, Is.EqualTo(50));
        Assert.That(dst.Quantity(apple), Is.EqualTo(50));
        Assert.That(src.Quantity(apple), Is.EqualTo(50), "leftover bounced back");
        Assert.That(GlobalInventory.instance.Quantity(apple), Is.EqualTo(globalBefore));
    }

    // ── Quantity: sums across all stacks of that item ──────────────────
    [Test]
    public void Quantity_SumsAcrossStacks(){
        Inventory inv = MakeAnimal(nStacks: 3, stackSize: 100);
        inv.Produce(apple, 80);  // fills stack 0
        inv.Produce(apple, 80);  // overflows into stack 1 (60 in 0 used... actually stack 0 already at 80, +20 fills it, +60 spills)
        // Note: AddItem consolidation tops off the existing stack first, so:
        // After Produce(apple, 80): stack0=80
        // After Produce(apple, 80): stack0 maxes at 100 (took 20), stack1=60
        Assert.That(inv.Quantity(apple), Is.EqualTo(160));
    }

    [Test]
    public void Quantity_DifferentItemsTracked_Separately(){
        Inventory inv = MakeAnimal(nStacks: 3);
        inv.Produce(apple, 50);
        inv.Produce(pear, 30);
        Assert.That(inv.Quantity(apple), Is.EqualTo(50));
        Assert.That(inv.Quantity(pear), Is.EqualTo(30));
    }

    [Test]
    public void Quantity_ItemNotPresent_ReturnsZero(){
        Inventory inv = MakeAnimal();
        inv.Produce(apple, 50);
        Assert.That(inv.Quantity(pear), Is.EqualTo(0));
    }

    // ── ItemTypeCompatible: ItemClass matching ─────────────────────────
    [Test]
    public void ItemTypeCompatible_NonStorageDefaultClass_AcceptsAnything(){
        // Non-storage inv with storageClass=Default ignores class — see Inventory.cs:24.
        Inventory inv = MakeAnimal();
        Assert.That(inv.ItemTypeCompatible(apple), Is.True);
        Assert.That(inv.ItemTypeCompatible(water), Is.True);
        Assert.That(inv.ItemTypeCompatible(book),  Is.True);
    }

    [Test]
    public void ItemTypeCompatible_StorageDefault_OnlyDefaultItems(){
        Inventory inv = MakeStorage(ItemClass.Default);
        Assert.That(inv.ItemTypeCompatible(apple), Is.True);
        Assert.That(inv.ItemTypeCompatible(pear),  Is.True);
        Assert.That(inv.ItemTypeCompatible(water), Is.False);
        Assert.That(inv.ItemTypeCompatible(book),  Is.False);
    }

    [Test]
    public void ItemTypeCompatible_StorageLiquid_OnlyLiquidItems(){
        Inventory inv = MakeStorage(ItemClass.Liquid);
        Assert.That(inv.ItemTypeCompatible(apple), Is.False);
        Assert.That(inv.ItemTypeCompatible(water), Is.True);
        Assert.That(inv.ItemTypeCompatible(book),  Is.False);
    }

    [Test]
    public void ItemTypeCompatible_StorageBook_OnlyBookItems(){
        Inventory inv = MakeStorage(ItemClass.Book);
        Assert.That(inv.ItemTypeCompatible(apple), Is.False);
        Assert.That(inv.ItemTypeCompatible(water), Is.False);
        Assert.That(inv.ItemTypeCompatible(book),  Is.True);
    }

    [Test]
    public void ItemTypeCompatible_NonStorageWithRestrictedClass_EnforcesMatch(){
        // The bookSlotInv pattern: an Equip/Animal inv constructed with a non-Default
        // storageClass gates by class even though it isn't Storage. See Inventory.cs:23.
        Inventory inv = new Inventory(n: 1, stackSize: 100,
            invType: Inventory.InvType.Equip, storageClass: ItemClass.Book);
        Assert.That(inv.ItemTypeCompatible(book),  Is.True);
        Assert.That(inv.ItemTypeCompatible(apple), Is.False);
    }

    // ── Stack auto-creation / cleanup ──────────────────────────────────
    [Test]
    public void Produce_OnEmptyInv_CreatesStack(){
        Inventory inv = MakeAnimal();
        Assert.That(inv.itemStacks[0].item, Is.Null, "starts empty");
        inv.Produce(apple, 50);
        Assert.That(inv.itemStacks[0].item, Is.SameAs(apple));
        Assert.That(inv.itemStacks[0].quantity, Is.EqualTo(50));
    }

    [Test]
    public void Produce_DrainToZero_ClearsStackItem(){
        // Stack should null out its item slot when drained — otherwise a different
        // item couldn't claim that slot later. ItemStack.AddItem handles this; we
        // verify the Inventory-level path also reaches the cleanup.
        Inventory inv = MakeAnimal();
        inv.Produce(apple, 50);
        inv.Produce(apple, -50);
        Assert.That(inv.itemStacks[0].item, Is.Null,
            "drained stack must release its item slot for reuse");
        Assert.That(inv.itemStacks[0].quantity, Is.EqualTo(0));

        // And it should be reusable for a different item.
        inv.Produce(pear, 20);
        Assert.That(inv.itemStacks[0].item, Is.SameAs(pear));
        Assert.That(inv.itemStacks[0].quantity, Is.EqualTo(20));
    }

    // ── InvType behavior ───────────────────────────────────────────────
    // The full decay multiplier table is owned by Decay() which needs World; we
    // can still verify the type flag is plumbed through and read by Quantity etc.
    [Test]
    public void InvType_StoredOnConstruct(){
        Inventory animal = MakeAnimal();
        Inventory storage = MakeStorage(ItemClass.Default);
        Assert.That(animal.invType, Is.EqualTo(Inventory.InvType.Animal));
        Assert.That(storage.invType, Is.EqualTo(Inventory.InvType.Storage));
    }

    [Test]
    public void StorageInv_DefaultsAllItemsDisallowed(){
        // Storage inventories start with allowed[id]=false for everything (player
        // opts in via filter UI). Non-storage inventories accept everything by default.
        Inventory storage = MakeStorage(ItemClass.Default);
        Assert.That(storage.allowed[apple.id], Is.False);
        Assert.That(storage.allowed[pear.id],  Is.False);

        Inventory animal = MakeAnimal();
        Assert.That(animal.allowed[apple.id], Is.True);
    }

    [Test]
    public void StorageBook_AutoAllowsBookItems(){
        // Bookshelves auto-allow every book on construct (see Inventory.cs:101-104) —
        // the only opt-in storage case, because tanks deliberately stay manual.
        Inventory shelf = MakeStorage(ItemClass.Book);
        Assert.That(shelf.allowed[book.id], Is.True);
    }

    [Test]
    public void FetchObjective_OnArrival_OnlyTakesReservedAmountFromCurrentSource(){
        Inventory source = MakeAnimal(stackSize: 100);
        Inventory dest = MakeAnimal(stackSize: 100);
        source.Produce(apple, 50);

        GameObject animalGo = new GameObject("TestAnimal");
        try {
            Animal animal = animalGo.AddComponent<Animal>();
            animal.aName = "Tester";
            animal.inv = dest;

            TestTask task = new TestTask(animal);
            animal.task = task;

            int reserved = task.ReserveStack(source.itemStacks[0], 17);
            Assert.That(reserved, Is.EqualTo(17), "fixture sanity: reservation should succeed");

            FetchObjective objective = new FetchObjective(
                task,
                new ItemQuantity(apple, 100),
                softFetch: true,
                sourceInv: source,
                sourceLimit: reserved);

            objective.OnArrival();

            Assert.That(dest.Quantity(apple), Is.EqualTo(17),
                "fetch visit should only move the amount reserved from this source");
            Assert.That(source.Quantity(apple), Is.EqualTo(33),
                "unreserved remainder should stay in the source inventory");
            Assert.That(task.completed, Is.True);
            Assert.That(animal.task, Is.Null, "successful completion should cleanly release the task");
            Assert.That(source.itemStacks[0].resAmount, Is.EqualTo(0),
                "task cleanup should release the source reservation after completion");
        } finally {
            Object.DestroyImmediate(animalGo);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────
    // Animal type: skips GameObject/SpriteRenderer creation in the ctor, so we
    // don't need a Resources/Lighting fixture. Default storageClass.
    Inventory MakeAnimal(int nStacks = 1, int stackSize = 100){
        return new Inventory(n: nStacks, stackSize: stackSize,
            invType: Inventory.InvType.Animal);
    }

    // Storage type with a chosen class. Note: Storage *does* create a GameObject in
    // the ctor — but we have InventoryController.instance set up so the SetParent
    // call lands somewhere valid. Resources.Load returns null in tests (no asset
    // bundle); SpriteRenderer.sprite = null is fine.
    Inventory MakeStorage(ItemClass cls){
        return new Inventory(n: 1, stackSize: 100,
            invType: Inventory.InvType.Storage, storageClass: cls);
    }

    static void SetStaticProp(System.Type type, string name, object value){
        PropertyInfo p = type.GetProperty(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(p, Is.Not.Null, $"static property {type.Name}.{name} not found");
        p.SetValue(null, value);
    }

    static void SetStaticField(System.Type type, string name, object value){
        FieldInfo f = type.GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(f, Is.Not.Null, $"static field {type.Name}.{name} not found");
        f.SetValue(null, value);
    }

    static T GetStaticField<T>(System.Type type, string name){
        FieldInfo f = type.GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(f, Is.Not.Null, $"static field {type.Name}.{name} not found");
        return (T)f.GetValue(null);
    }
}

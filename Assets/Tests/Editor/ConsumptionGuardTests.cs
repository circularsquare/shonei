using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// EditMode tests for the "consume" mechanic — the pure, nav-free core:
//   - GlobalInventory.CanCraft           (crafting is ALWAYS allowed: a protected INPUT never blocks;
//                                         only protected FUEL does, for deadlock avoidance)
//   - GlobalInventory.PickFuel           (protected fuel is never burned)
//   - GlobalInventory.ConsumableFuelEnergy
//   - InventoryController.SetConsumptionDisabled (group rows fan to leaf descendants)
//
// The flag now gates only direct END-USE channels (eat / drink / equip / fuel / furnish), all of
// which need an Animal + Navigation to exercise (Animal.FindFood/ChooseTonic/FindEquipment,
// DrinkTask, SupplyFuelTask, SupplyFurnishingTask) — those are integration-level, verified in
// playtest. Crafting, construction, processor-fill, and repair ignore the flag entirely.
//
// ── Setup pattern (mirrors GlobalInventoryTests / RecipeScoringTests) ──
// We swap a tiny item fixture into Db.items / Db.itemsFlat, construct a fresh GlobalInventory,
// and attach an InventoryController whose protected-set `instance` static is forced via
// reflection (its Start() never runs in EditMode, but the consumptionDisabled field initializer
// does, so the set is a live empty HashSet ready to mutate). Everything is restored on teardown.
[TestFixture]
public class ConsumptionGuardTests {

    Item apple;     // leaf, food child
    Item pear;      // leaf, food child
    Item food;      // group: food → { apple, pear }
    Item coal;      // fuel leaf
    Item charcoal;  // fuel leaf

    static Item[]          savedItems;
    static Item[]          savedItemsFlat;
    static GlobalInventory savedGi;

    GameObject  icGO;
    readonly List<int> dirtyIids = new List<int>();

    [SetUp]
    public void SetUp(){
        savedItems     = Db.items;
        savedItemsFlat = Db.itemsFlat;
        savedGi        = GlobalInventory.instance;

        apple    = new Item { id = 1, name = "apple" };
        pear     = new Item { id = 2, name = "pear" };
        food     = new Item { id = 3, name = "food", children = new[]{ apple, pear } };
        apple.parent = food; pear.parent = food;
        coal     = new Item { id = 4, name = "coal",     fuelValue = 10f };
        charcoal = new Item { id = 5, name = "charcoal", fuelValue = 10f };

        // Only leaves go in itemsFlat (groups are never physical); Db.items is id-indexed.
        Db.itemsFlat = new[]{ apple, pear, coal, charcoal };
        Db.items     = new Item[]{ null, apple, pear, food, coal, charcoal };

        SetSingletonInstance<GlobalInventory>(null);
        new GlobalInventory();

        icGO = new GameObject("InventoryControllerHost_ConsumptionGuardTests");
        var ic = icGO.AddComponent<InventoryController>();
        SetSingletonInstance(ic); // Start() doesn't run in EditMode; force the singleton
    }

    [TearDown]
    public void TearDown(){
        if (icGO != null) Object.DestroyImmediate(icGO);
        SetSingletonInstance<InventoryController>(null);
        SetSingletonInstance<GlobalInventory>(savedGi);
        Db.items     = savedItems;
        Db.itemsFlat = savedItemsFlat;
        dirtyIids.Clear();
    }

    // ── CanCraft: crafting is ALWAYS allowed (inputs ignore the flag) ───
    [Test]
    public void CanCraft_LeafInputProtected_StillCraftable(){
        // Crafting is a transformation use, not a "consume" channel — protecting an ingredient
        // must NOT make its recipe uncraftable (this is the re-scope: the old code returned false).
        SetGlobal(apple, 500);
        var gi = GlobalInventory.instance;
        Recipe r = new Recipe { id = 1, inputs = new[]{ new ItemQuantity(apple, 100) }, outputs = new ItemQuantity[0] };
        Assert.That(gi.CanCraft(r), Is.True);

        InventoryController.instance.SetConsumptionDisabled(apple, true);
        Assert.That(gi.CanCraft(r), Is.True, "protected ingredient still craftable — crafting always allowed");
    }

    [Test]
    public void CanCraft_AllGroupLeavesProtected_StillCraftable(){
        SetGlobal(apple, 500);
        SetGlobal(pear, 500);
        var gi = GlobalInventory.instance;
        Recipe r = new Recipe { id = 1, inputs = new[]{ new ItemQuantity(food, 100) }, outputs = new ItemQuantity[0] };

        InventoryController.instance.SetConsumptionDisabled(apple, true);
        InventoryController.instance.SetConsumptionDisabled(pear, true);
        Assert.That(gi.CanCraft(r), Is.True, "even all leaves protected → still craftable from the group input");
    }

    [Test]
    public void CanCraft_FuelOnlyProtected_IsFalse(){
        // Fuel IS a gated consume channel — so a recipe whose only fuel is protected stays
        // uncraftable (else PickFuel returns null and the craft stalls).
        SetGlobal(apple, 500);
        SetGlobal(coal, 500); // 5 liang × 10 = 50 energy
        var gi = GlobalInventory.instance;
        Recipe r = new Recipe {
            id = 1, inputs = new[]{ new ItemQuantity(apple, 100) }, outputs = new ItemQuantity[0], fuelCost = 20f
        };
        Assert.That(gi.CanCraft(r), Is.True);

        InventoryController.instance.SetConsumptionDisabled(coal, true);
        Assert.That(gi.CanCraft(r), Is.False, "only fuel in stock is protected → can't fuel the craft");
    }

    // ── PickFuel / ConsumableFuelEnergy ─────────────────────────────────
    [Test]
    public void PickFuel_SkipsProtected_NullWhenAllProtected(){
        SetGlobal(coal, 500);
        SetGlobal(charcoal, 500);
        var gi = GlobalInventory.instance;
        Assert.That(gi.PickFuel(), Is.Not.Null);

        InventoryController.instance.SetConsumptionDisabled(coal, true);
        Assert.That(gi.PickFuel(), Is.EqualTo(charcoal), "protected coal skipped, charcoal chosen");

        InventoryController.instance.SetConsumptionDisabled(charcoal, true);
        Assert.That(gi.PickFuel(), Is.Null, "no unprotected fuel left");
    }

    [Test]
    public void ConsumableFuelEnergy_ExcludesProtected(){
        SetGlobal(coal, 500);     // 50 energy
        SetGlobal(charcoal, 500); // 50 energy
        var gi = GlobalInventory.instance;
        Assert.That(gi.ConsumableFuelEnergy(), Is.EqualTo(100f).Within(0.001f));
        Assert.That(gi.TotalFuelEnergy(),      Is.EqualTo(100f).Within(0.001f));

        InventoryController.instance.SetConsumptionDisabled(coal, true);
        Assert.That(gi.ConsumableFuelEnergy(), Is.EqualTo(50f).Within(0.001f), "protected coal drops out");
        Assert.That(gi.TotalFuelEnergy(),      Is.EqualTo(100f).Within(0.001f), "total still counts protected fuel");
    }

    // ── Group-fan setter ────────────────────────────────────────────────
    [Test]
    public void SetConsumptionDisabled_Group_FansToLeafDescendants(){
        var ic = InventoryController.instance;
        ic.SetConsumptionDisabled(food, true);
        Assert.That(ic.IsConsumptionDisabled(apple), Is.True);
        Assert.That(ic.IsConsumptionDisabled(pear),  Is.True);

        ic.SetConsumptionDisabled(food, false);
        Assert.That(ic.IsConsumptionDisabled(apple), Is.False);
        Assert.That(ic.IsConsumptionDisabled(pear),  Is.False);
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    void SetGlobal(Item item, int quantity){
        int cur = GlobalInventory.instance.Quantity(item.id);
        GlobalInventory.instance.AddItem(item.id, quantity - cur);
        if (!dirtyIids.Contains(item.id)) dirtyIids.Add(item.id);
    }

    // Sets a class's protected-set static `instance` auto-property via its compiler-generated
    // backing field (same approach as RecipeScoringTests). Null clears it.
    static void SetSingletonInstance<T>(T value) where T : class {
        FieldInfo backing = typeof(T).GetField(
            "<instance>k__BackingField",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(backing, Is.Not.Null, $"no backing field for {typeof(T).Name}.instance");
        backing.SetValue(null, value);
    }
}

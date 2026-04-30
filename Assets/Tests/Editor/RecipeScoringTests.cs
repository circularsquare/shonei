using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// EditMode tests for Recipe (defined in Db.cs). Focuses on the three picking-related
// methods that drive recipe selection in WorkOrderManager / Animal.PickRecipe*:
//   - Score(Dictionary<int, int> targets)
//   - AllOutputsSatisfied(Dictionary<int, int> targets)
//   - IsEligibleForPicking()
//
// Score and AllOutputsSatisfied both read GlobalInventory.instance.Quantity(item),
// so the fixture stands up a minimal GlobalInventory once in [OneTimeSetUp]:
// Db.itemsFlat is temporarily swapped for an empty array (GlobalInventory's ctor
// calls Db.itemsFlat.ToDictionary), the singleton is constructed, and then both
// statics are restored on tear-down to avoid polluting other fixtures.
//
// IsEligibleForPicking is fully null-safe — both RecipePanel.instance and
// ResearchSystem.instance are checked for null before dereference. We exercise
// the both-null path directly, and the RecipePanel-disabled gate by attaching
// a RecipePanel MonoBehaviour to a temporary GameObject.
//
// ── Deferred to integration tests ────────────────────────────────────
// The ResearchSystem-locked branch of IsEligibleForPicking requires
// ResearchSystem.LoadNodes to read researchDb.json on Awake plus a populated
// recipeToTechNode index — that's an integration concern. Group-item outputs,
// recipe ratio scoring with dependencies between Score calls (e.g. dynamically
// shifting target values mid-loop), and JSON [OnDeserialized] wire-up are
// likewise out of scope; we construct Recipe instances directly via field
// initializers per the audit guidance.
[TestFixture]
public class RecipeScoringTests {

    // ── Test fixture state ─────────────────────────────────────────────
    // Saved off in OneTimeSetUp and restored in OneTimeTearDown so we don't
    // leak our minimal Db / GlobalInventory state to sibling fixtures.
    static Item[]          savedItemsFlat;
    static Item[]          savedItems;
    static GlobalInventory savedGlobalInventoryInstance;

    // Tracks which item ids we've touched in GlobalInventory this test, so
    // [TearDown] can zero them back out without reaching into the protected
    // itemAmounts setter. Cheaper than rebuilding the singleton per test.
    readonly List<int> dirtyIids = new List<int>();

    // Items we reuse across tests. id values picked to be distinct from real
    // game items (the Db static arrays aren't loaded in this fixture, so any
    // ids work — but high values keep stack traces obvious if something leaks).
    static Item itemA;
    static Item itemB;

    // RecipePanel host GameObject for IsEligibleForPicking gate tests.
    GameObject recipePanelGO;

    [OneTimeSetUp]
    public void OneTimeSetUp(){
        savedItemsFlat = Db.itemsFlat;
        savedItems     = Db.items;
        savedGlobalInventoryInstance = GlobalInventory.instance;

        itemA = new Item { id = 401, name = "test_a" };
        itemB = new Item { id = 402, name = "test_b" };

        // GlobalInventory's ctor does Db.itemsFlat.ToDictionary(i => i.id, ...).
        // The default Db.itemsFlat is new Item[500] (mostly nulls), which would
        // NRE; we swap in our test items so the dict initializes cleanly.
        Db.itemsFlat = new[] { itemA, itemB };
        // Quantity(int) now resolves iid → Db.items[iid] → Quantity(Item). Populate
        // those slots so SetGlobal/TearDown's Quantity(iid) calls don't log errors.
        Db.items = new Item[500];
        Db.items[itemA.id] = itemA;
        Db.items[itemB.id] = itemB;

        // GlobalInventory.instance has a protected setter, so we can't null it
        // before constructing a fresh one. The ctor logs an error if instance is
        // already set, but only if the existing static was ever populated — in
        // a clean test run it isn't.
        SetGlobalInventoryInstance(null);
        new GlobalInventory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown(){
        Db.itemsFlat = savedItemsFlat;
        Db.items     = savedItems;
        SetGlobalInventoryInstance(savedGlobalInventoryInstance);
        if (recipePanelGO != null) {
            Object.DestroyImmediate(recipePanelGO);
            // Belt-and-suspenders: null the static so any later test that reads
            // RecipePanel.instance reference-equality (rather than Unity's `== null`
            // overload) sees a clean slate.
            SetSingletonInstance<RecipePanel>(null);
        }
    }

    [TearDown]
    public void TearDown(){
        // Reset any quantities the test set so the next test starts at 0 across the board.
        foreach (int iid in dirtyIids){
            int cur = GlobalInventory.instance.Quantity(iid);
            if (cur != 0) GlobalInventory.instance.AddItem(iid, -cur);
        }
        dirtyIids.Clear();
        // Re-enable any recipe ids the test disabled in RecipePanel.
        if (RecipePanel.instance != null) RecipePanel.instance.ClearDisabled();
    }

    // ── Score: null targets ─────────────────────────────────────────────
    [Test]
    public void Score_NullTargets_ReturnsOne(){
        // Documented invariant: a null targets dict means "no preferences set" — the recipe
        // is treated as default-eligible with neutral score 1, never NaN.
        Recipe r = MakeRecipe(
            inputs:  new[] { IQ(itemA, 100) },
            outputs: new[] { IQ(itemB, 100) }
        );
        Assert.That(r.Score(null), Is.EqualTo(1f));
    }

    [Test]
    public void Score_NullTargets_NoIOPaths_StillReturnsOne(){
        // Even an inputless+outputless recipe takes the early-out — never inspects inputs/outputs.
        Recipe r = MakeRecipe();
        Assert.That(r.Score(null), Is.EqualTo(1f));
    }

    // ── Score: target == 0 is skipped ──────────────────────────────────
    // The most important invariant per project memory: a per-item target of 0
    // must NOT cause a divide-by-zero (output) or zero-multiply (input). It's
    // treated as neutral so recipes touching that item aren't penalised.
    [Test]
    public void Score_OutputTargetZero_IsNeutral_NoDivideByZero(){
        SetGlobal(itemA, 50);
        var targets = new Dictionary<int, int> { { itemA.id, 0 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10) });
        // No targeted inputs/outputs after the skip → score stays at the seed value of 1.
        Assert.That(r.Score(targets), Is.EqualTo(1f));
    }

    [Test]
    public void Score_InputTargetZero_IsNeutral_NoZeroMultiply(){
        SetGlobal(itemA, 50);
        var targets = new Dictionary<int, int> { { itemA.id, 0 } };
        Recipe r = MakeRecipe(inputs: new[] { IQ(itemA, 10) });
        Assert.That(r.Score(targets), Is.EqualTo(1f));
    }

    [Test]
    public void Score_AllTargetsZero_ReturnsOne(){
        SetGlobal(itemA, 50);
        SetGlobal(itemB, 30);
        var targets = new Dictionary<int, int> {
            { itemA.id, 0 },
            { itemB.id, 0 },
        };
        Recipe r = MakeRecipe(
            inputs:  new[] { IQ(itemA, 10) },
            outputs: new[] { IQ(itemB, 5) }
        );
        Assert.That(r.Score(targets), Is.EqualTo(1f));
    }

    [Test]
    public void Score_MixedZeroAndNonZeroTargets_OnlyNonZeroAffectScore(){
        // itemA target 0 (skipped), itemB target 100 (counted as input ratio 50/100 = 0.5).
        SetGlobal(itemA, 50);
        SetGlobal(itemB, 50);
        var targets = new Dictionary<int, int> {
            { itemA.id, 0 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(inputs: new[] { IQ(itemA, 10), IQ(itemB, 10) });
        Assert.That(r.Score(targets), Is.EqualTo(0.5f).Within(1e-6f));
    }

    // ── Score: input/output ratio behaviour ────────────────────────────
    // Inputs multiply by quantity/target — a recipe is favoured when its inputs are abundant.
    // Outputs divide by quantity/target — a recipe is favoured when its outputs are scarce.
    [TestCase(50, 100, 0.5f)]   // half stocked → score 0.5
    [TestCase(100, 100, 1f)]    // exactly at target → score 1
    [TestCase(200, 100, 2f)]    // double target → score 2
    [TestCase(0, 100, 0f)]      // empty → score 0 (recipe unattractive)
    public void Score_SingleInput_RatioIsQuantityOverTarget(int qty, int target, float expected){
        SetGlobal(itemA, qty);
        var targets = new Dictionary<int, int> { { itemA.id, target } };
        Recipe r = MakeRecipe(inputs: new[] { IQ(itemA, 10) });
        Assert.That(r.Score(targets), Is.EqualTo(expected).Within(1e-6f));
    }

    [TestCase(50, 100, 2f)]     // half stocked → recipe doubly attractive
    [TestCase(100, 100, 1f)]    // at target → neutral
    [TestCase(200, 100, 0.5f)]  // overstocked → score halved
    public void Score_SingleOutput_RatioIsTargetOverQuantity(int qty, int target, float expected){
        SetGlobal(itemA, qty);
        var targets = new Dictionary<int, int> { { itemA.id, target } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10) });
        Assert.That(r.Score(targets), Is.EqualTo(expected).Within(1e-6f));
    }

    [Test]
    public void Score_OutputZeroQuantity_DivByZeroProducesInfinity(){
        // Documenting the contract: with output target>0 and current qty=0, Score does
        // 1 / (0/target) = +inf. AllOutputsSatisfied wouldn't gate this recipe out (since
        // 0<target), so an infinite score is the intended "produce this NOW" signal.
        SetGlobal(itemA, 0);
        var targets = new Dictionary<int, int> { { itemA.id, 100 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10) });
        Assert.That(float.IsPositiveInfinity(r.Score(targets)), Is.True);
    }

    [Test]
    public void Score_InputAndOutput_MultiplicativeRatio(){
        // Recipe: A → B. 200 A in stock, 50 B in stock, both targeted at 100.
        // Score = (200/100) * (100/50) = 2 * 2 = 4.
        SetGlobal(itemA, 200);
        SetGlobal(itemB, 50);
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(
            inputs:  new[] { IQ(itemA, 10) },
            outputs: new[] { IQ(itemB, 10) }
        );
        Assert.That(r.Score(targets), Is.EqualTo(4f).Within(1e-6f));
    }

    [Test]
    public void Score_NoInputsOrOutputs_NonNullTargets_ReturnsOne(){
        // The score-1 seed survives an empty IO loop. A recipe with no targeted material
        // flow is exactly as eligible as the neutral baseline — never NaN, never 0.
        var targets = new Dictionary<int, int> { { itemA.id, 100 } };
        Recipe r = MakeRecipe();
        Assert.That(r.Score(targets), Is.EqualTo(1f));
    }

    [Test]
    public void Score_RecipeIgnoresQuantityField_OnlyItemIdMatters(){
        // Documentation: Score does NOT consult ItemQuantity.quantity — it only uses item.id
        // to look up the global stock and the target. Two recipes with the same items but
        // different recipe quantities should score identically.
        SetGlobal(itemA, 100);
        var targets = new Dictionary<int, int> { { itemA.id, 100 } };
        Recipe r1 = MakeRecipe(inputs: new[] { IQ(itemA, 1) });
        Recipe r2 = MakeRecipe(inputs: new[] { IQ(itemA, 9999) });
        Assert.That(r1.Score(targets), Is.EqualTo(r2.Score(targets)));
    }

    // ── AllOutputsSatisfied ────────────────────────────────────────────
    [Test]
    public void AllOutputsSatisfied_NullTargets_ReturnsFalse(){
        // Null targets → can't determine satisfaction → fall through and produce.
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10) });
        Assert.That(r.AllOutputsSatisfied(null), Is.False);
    }

    [Test]
    public void AllOutputsSatisfied_AllOutputsBelowTarget_ReturnsFalse(){
        SetGlobal(itemA, 5);
        SetGlobal(itemB, 10);
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 1), IQ(itemB, 1) });
        Assert.That(r.AllOutputsSatisfied(targets), Is.False);
    }

    [Test]
    public void AllOutputsSatisfied_OneOutputBelow_RestAbove_ReturnsFalse(){
        // Recipe is still useful — that one shortfall trips the check.
        SetGlobal(itemA, 5);    // below
        SetGlobal(itemB, 200);  // above
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 1), IQ(itemB, 1) });
        Assert.That(r.AllOutputsSatisfied(targets), Is.False);
    }

    [Test]
    public void AllOutputsSatisfied_AllOutputsAtOrAboveTarget_ReturnsTrue(){
        SetGlobal(itemA, 100);  // exactly at target — counts as satisfied (Quantity < target is the gate)
        SetGlobal(itemB, 200);  // above
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 1), IQ(itemB, 1) });
        Assert.That(r.AllOutputsSatisfied(targets), Is.True);
    }

    [Test]
    public void AllOutputsSatisfied_TargetZero_QuantityZero_CountsAsSatisfied(){
        // 0 < 0 is false — so the output is treated as satisfied. This is the
        // intended "produce none" semantics: an explicit 0 target stops production.
        SetGlobal(itemA, 0);
        var targets = new Dictionary<int, int> { { itemA.id, 0 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10) });
        Assert.That(r.AllOutputsSatisfied(targets), Is.True);
    }

    [Test]
    public void AllOutputsSatisfied_EmptyOutputs_ReturnsFalse(){
        // No tracked outputs → anyTracked stays false → return false. Suppression
        // requires at least one output that's actually being tracked.
        var targets = new Dictionary<int, int> { { itemA.id, 100 } };
        Recipe r = MakeRecipe();
        Assert.That(r.AllOutputsSatisfied(targets), Is.False);
    }

    [Test]
    public void AllOutputsSatisfied_NoOutputIdsTracked_ReturnsFalse(){
        // Output exists but its id isn't in targets → TryGetValue skips it →
        // anyTracked stays false → safe fallback returns false (don't suppress).
        SetGlobal(itemA, 999);  // would dwarf any target if it were tracked
        var targets = new Dictionary<int, int> { { itemB.id, 100 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 1) });
        Assert.That(r.AllOutputsSatisfied(targets), Is.False);
    }

    [Test]
    public void AllOutputsSatisfied_MixedTrackedAndUntracked_OnlyTrackedMatter(){
        // itemA tracked and over-target; itemB untracked. Only itemA's status decides → true.
        SetGlobal(itemA, 200);
        var targets = new Dictionary<int, int> { { itemA.id, 100 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 1), IQ(itemB, 1) });
        Assert.That(r.AllOutputsSatisfied(targets), Is.True);
    }

    [Test]
    public void AllOutputsSatisfied_IgnoresInputs(){
        // Method only inspects outputs — inputs being below target shouldn't affect it.
        SetGlobal(itemA, 0);    // input, far below target
        SetGlobal(itemB, 200);  // output, above target
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(
            inputs:  new[] { IQ(itemA, 10) },
            outputs: new[] { IQ(itemB, 10) }
        );
        Assert.That(r.AllOutputsSatisfied(targets), Is.True);
    }

    // ── IsEligibleForPicking ───────────────────────────────────────────
    // Null-safe path: when both gating singletons are null (early startup / unit
    // test environments), the gate defaults to "eligible". Tested first because
    // it requires no extra setup — and it's the contract relied on by the rest
    // of the Score / AllOutputsSatisfied tests above.
    [Test]
    public void IsEligibleForPicking_NoSingletonsActive_ReturnsTrue(){
        // Force both singletons to null for this test, regardless of test order or
        // environmental state — IsEligibleForPicking's null-safe contract is precisely
        // about working when the UI/research stack hasn't been brought up.
        RecipePanel    savedRecipePanel    = RecipePanel.instance;
        ResearchSystem savedResearchSystem = ResearchSystem.instance;
        try {
            SetSingletonInstance<RecipePanel>(null);
            SetSingletonInstance<ResearchSystem>(null);
            Recipe r = MakeRecipe(id: 7777);
            Assert.That(r.IsEligibleForPicking(), Is.True);
        } finally {
            SetSingletonInstance(savedRecipePanel);
            SetSingletonInstance(savedResearchSystem);
        }
    }

    [Test]
    public void IsEligibleForPicking_RecipePanelDisabled_ReturnsFalse(){
        EnsureRecipePanel();
        Recipe r = MakeRecipe(id: 7778);
        RecipePanel.instance.SetAllowed(r.id, false);
        Assert.That(r.IsEligibleForPicking(), Is.False);
    }

    [Test]
    public void IsEligibleForPicking_RecipePanelEnabled_NoResearchSystem_ReturnsTrue(){
        EnsureRecipePanel();
        Recipe r = MakeRecipe(id: 7779);
        // Default state is "allowed" — IsAllowed(id) returns true for ids never disabled.
        Assert.That(RecipePanel.instance.IsAllowed(r.id), Is.True);
        Assert.That(r.IsEligibleForPicking(), Is.True);
    }

    [Test]
    public void IsEligibleForPicking_RecipePanelToggledBackOn_ReturnsTrue(){
        // SetAllowed(true) after a disable should restore eligibility — guards against
        // state-management bugs in the disabled-recipes set.
        EnsureRecipePanel();
        Recipe r = MakeRecipe(id: 7780);
        RecipePanel.instance.SetAllowed(r.id, false);
        Assume.That(r.IsEligibleForPicking(), Is.False);
        RecipePanel.instance.SetAllowed(r.id, true);
        Assert.That(r.IsEligibleForPicking(), Is.True);
    }

    // ── Helpers ────────────────────────────────────────────────────────
    static Recipe MakeRecipe(int id = 1, ItemQuantity[] inputs = null, ItemQuantity[] outputs = null){
        // Constructed directly via field initializers — bypasses the JSON
        // [OnDeserialized] path so we don't need ninputs/noutputs or Db lookup.
        return new Recipe {
            id      = id,
            inputs  = inputs  ?? new ItemQuantity[0],
            outputs = outputs ?? new ItemQuantity[0],
        };
    }

    // ItemQuantity has an (int id, int q) ctor that reads Db.items[id] — we avoid
    // it because Db.items isn't populated in this fixture. The (Item, int) ctor
    // takes the Item directly and is safe.
    static ItemQuantity IQ(Item item, int quantity){
        return new ItemQuantity(item, quantity);
    }

    // Force a global-inventory quantity to an exact value, bypassing the
    // additive-only AddItem API. Tracks the iid so [TearDown] can zero it back.
    void SetGlobal(Item item, int quantity){
        int cur = GlobalInventory.instance.Quantity(item.id);
        GlobalInventory.instance.AddItem(item.id, quantity - cur);
        if (!dirtyIids.Contains(item.id)) dirtyIids.Add(item.id);
    }

    // Lazily attaches a RecipePanel to a host GameObject. Awake registers it as
    // the singleton; UI.RegisterExclusive only appends to a static list (no UI
    // singleton dereference) so this is safe in EditMode without world setup.
    // The reflection-set is belt-and-suspenders: AddComponent.Awake should set
    // the singleton, but if a prior fixture left RecipePanel.instance pointing
    // at a Unity-destroyed object, Awake's `instance = this` may race with the
    // already-set field and tests then see the stale ref. Forcing the assignment
    // post-AddComponent guarantees instance points at our live panel.
    void EnsureRecipePanel(){
        if (RecipePanel.instance != null) return;
        recipePanelGO = new GameObject("RecipePanelHost_RecipeScoringTests");
        var panel = recipePanelGO.AddComponent<RecipePanel>();
        SetSingletonInstance<RecipePanel>(panel);
    }

    // GlobalInventory.instance has a protected setter; the only stable way to
    // null/restore it from outside the class hierarchy is via reflection on the
    // compiler-generated backing field for the auto-property.
    static void SetGlobalInventoryInstance(GlobalInventory value){
        SetSingletonInstance(value);
    }

    // Generic version — works for any class with a protected-set static `instance`
    // auto-property (RecipePanel, ResearchSystem, GlobalInventory all follow the pattern).
    // The argument-less overload nulls the static; the value overload sets it.
    static void SetSingletonInstance<T>(T value) where T : class {
        FieldInfo backing = typeof(T).GetField(
            "<instance>k__BackingField",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (backing == null){
            Debug.LogError($"RecipeScoringTests: couldn't find {typeof(T).Name}.instance backing field — fixture may leak state to other tests.");
            return;
        }
        backing.SetValue(null, value);
    }
}

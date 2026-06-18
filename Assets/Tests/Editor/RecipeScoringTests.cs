using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// EditMode tests for Recipe (defined in Db.cs). Focuses on the picking- and
// batch-sizing methods that drive recipe selection and craft-round counts in
// WorkOrderManager / Animal.PickRecipe* / Animal.CalculateWorkPossible:
//   - Score(Dictionary<int, int> targets)
//   - AllOutputsSatisfied(Dictionary<int, int> targets)
//   - IsEligibleForPicking()
//   - CapRoundsByTarget(int rounds, Dictionary<int, int> targets)
//   - AllOutputsScarce(Dictionary<int, int> targets)
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
// recipeToTechNode index — that's an integration concern. Group-item OUTPUTS are
// invalid by Db.ValidateNoGroupOutputs, so untested here; group INPUTS (wildcards)
// ARE exercised — we build throwaway group items via the Group() helper. Recipe
// scoring with dependencies between Score calls (e.g. dynamically shifting target
// values mid-loop) and JSON [OnDeserialized] wire-up remain out of scope; we
// construct Recipe instances directly via field initializers per the audit guidance.
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

    // ── Score: target == 0 semantics ───────────────────────────────────
    // A per-item target of 0 means "keep none". NaN-safe by construction — SurplusRatio
    // special-cases target==0 and never divides. The two sides differ:
    //   OUTPUT: a 0-target item is always at/above target → skipped (never produced).
    //   INPUT:  a 0-target item we hold is "fully disposable" → max surplus (the cap), so its
    //           recipe is favoured and the stock gets consumed — matching Task.ResolveConsumeLeaf.
    [Test]
    public void Score_OutputTargetZero_Skipped_NoDivideByZero(){
        SetGlobal(itemA, 50);
        var targets = new Dictionary<int, int> { { itemA.id, 0 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10) });
        // qty (50) >= target (0) → surplus output skipped → no scored outputs → score stays 1.
        Assert.That(r.Score(targets), Is.EqualTo(1f));
    }

    [Test]
    public void Score_InputTargetZero_Held_IsMaxSurplus(){
        SetGlobal(itemA, 50);
        var targets = new Dictionary<int, int> { { itemA.id, 0 } };
        Recipe r = MakeRecipe(inputs: new[] { IQ(itemA, 10) });
        // target 0 + held → SurplusRatio caps at MaxSurplusRatio; no outputs → that IS the score.
        Assert.That(r.Score(targets), Is.EqualTo(Recipe.MaxSurplusRatio).Within(1e-4f));
    }

    [Test]
    public void Score_InputTargetZero_NoStock_IsZero(){
        // target 0 but we hold none → SurplusRatio 0 → gmIn 0 → recipe unmakeable.
        SetGlobal(itemA, 0);
        var targets = new Dictionary<int, int> { { itemA.id, 0 } };
        Recipe r = MakeRecipe(inputs: new[] { IQ(itemA, 10) });
        Assert.That(r.Score(targets), Is.EqualTo(0f));
    }

    [Test]
    public void Score_AllTargetsZero_InputDisposable_OutputSkipped(){
        SetGlobal(itemA, 50);  // input, target 0, held → cap
        SetGlobal(itemB, 30);  // output, target 0 → skipped
        var targets = new Dictionary<int, int> {
            { itemA.id, 0 },
            { itemB.id, 0 },
        };
        Recipe r = MakeRecipe(
            inputs:  new[] { IQ(itemA, 10) },
            outputs: new[] { IQ(itemB, 5) }
        );
        // gmIn = cap, gmOut = 1 (output skipped) → score = cap.
        Assert.That(r.Score(targets), Is.EqualTo(Recipe.MaxSurplusRatio).Within(1e-4f));
    }

    [Test]
    public void Score_MixedZeroAndNonZeroInputTargets_ZeroTargetContributesMaxSurplus(){
        // itemA target 0 + held → cap; itemB target 100, 50 in stock → 0.5.
        SetGlobal(itemA, 50);
        SetGlobal(itemB, 50);
        var targets = new Dictionary<int, int> {
            { itemA.id, 0 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(inputs: new[] { IQ(itemA, 10), IQ(itemB, 10) });
        // gmIn = sqrt(MaxSurplusRatio * 0.5).
        float expected = Mathf.Sqrt(Recipe.MaxSurplusRatio * 0.5f);
        Assert.That(r.Score(targets), Is.EqualTo(expected).Within(1e-4f));
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

    // Only SCARCE outputs (qty < target) count, contributing target/qty (scarcer → higher score).
    // An output at/above target is skipped entirely — it can't suppress the recipe (the byproduct
    // fix), so the score falls back to the neutral 1.
    [TestCase(50, 100, 2f)]     // scarce (half stocked) → doubly attractive
    [TestCase(100, 100, 1f)]    // at target → skipped → neutral 1
    [TestCase(200, 100, 1f)]    // over target → skipped → neutral 1 (was 0.5 before the byproduct fix)
    public void Score_SingleOutput_ScarceCounts_SurplusSkipped(int qty, int target, float expected){
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

    // ── Score: geometric-mean combination ──────────────────────────────
    // Inputs and outputs each fold to a geometric mean, so multi-item recipes aren't
    // crushed by raw product compounding. The core regression these guard: 2 inputs at
    // ratio 0.5 must score 0.5 (geomean), not 0.25 (old product).
    [Test]
    public void Score_MultipleInputs_GeometricMean_NotProduct(){
        SetGlobal(itemA, 50);
        SetGlobal(itemB, 50);
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(inputs: new[] { IQ(itemA, 10), IQ(itemB, 10) });
        // GM(0.5, 0.5) = sqrt(0.25) = 0.5 — old multiplicative product would give 0.25.
        Assert.That(r.Score(targets), Is.EqualTo(0.5f).Within(1e-6f));
    }

    [Test]
    public void Score_MultipleOutputs_GeometricMean(){
        // Two scarce outputs at ratio 0.5: GM_out = 0.5, GM_in = 1 (none) → score 1/0.5 = 2.
        SetGlobal(itemA, 50);
        SetGlobal(itemB, 50);
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10), IQ(itemB, 10) });
        Assert.That(r.Score(targets), Is.EqualTo(2f).Within(1e-6f));
    }

    [Test]
    public void Score_EqualPerItemRatios_InputCountInvariant(){
        // The "don't punish more inputs" contract: a 1-input and a 2-input recipe whose
        // per-item ratios all equal 0.5 must score identically (both 0.5).
        SetGlobal(itemA, 50);
        SetGlobal(itemB, 50);
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe oneInput = MakeRecipe(inputs: new[] { IQ(itemA, 10) });
        Recipe twoInput = MakeRecipe(inputs: new[] { IQ(itemA, 10), IQ(itemB, 10) });
        Assert.That(twoInput.Score(targets), Is.EqualTo(oneInput.Score(targets)).Within(1e-6f));
    }

    [Test]
    public void Score_ZeroInputAndZeroOutput_NotNaN_ReturnsZero(){
        // NaN-safety guard: an empty input (GM_in = 0) is checked before the empty-output
        // +Infinity branch, so a recipe with both never produces 0/0 = NaN. gmIn==0 wins → 0.
        SetGlobal(itemA, 0); // input empty
        SetGlobal(itemB, 0); // output never produced
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(
            inputs:  new[] { IQ(itemA, 10) },
            outputs: new[] { IQ(itemB, 10) }
        );
        float score = r.Score(targets);
        Assert.That(float.IsNaN(score), Is.False);
        Assert.That(score, Is.EqualTo(0f));
    }

    // ── SurplusRatio: cap + target-zero ────────────────────────────────
    // The shared scalar behind input scoring and Task.ResolveConsumeLeaf.
    [TestCase(50, 100, 0.5f)]
    [TestCase(150, 100, 1.5f)]
    [TestCase(5000, 100, 20f)]   // ratio 50 clamped to the cap
    [TestCase(50, 0, 20f)]       // target 0 + held → fully disposable → cap
    [TestCase(0, 0, 0f)]         // target 0 + none → nothing to consume → 0
    [TestCase(0, 100, 0f)]       // empty → 0
    public void SurplusRatio_CapAndTargetZero(int qty, int target, float expected){
        Assert.That(Recipe.SurplusRatio(qty, target), Is.EqualTo(expected).Within(1e-4f));
    }

    [Test]
    public void SurplusRatio_CapMatchesConstant(){
        // The clamp uses MaxSurplusRatio — assert the literal 20 in the [TestCase]s tracks it.
        Assert.That(Recipe.SurplusRatio(1_000_000, 100), Is.EqualTo(Recipe.MaxSurplusRatio));
        Assert.That(Recipe.MaxSurplusRatio, Is.EqualTo(20f)); // update SurplusRatio_CapAndTargetZero if this changes
    }

    // ── Score: byproduct (the sawdust fix) ─────────────────────────────
    [Test]
    public void Score_OverTargetByproduct_DoesNotSuppressScarcePrimary(){
        // A multi-output recipe [scarce primary, flooded byproduct] must score identically to the
        // same recipe without the byproduct: surplus outputs are skipped, so sawdust piling up no
        // longer drags plank-cutting down. Core regression behind the byproduct fix.
        SetGlobal(itemA, 50);     // primary — scarce (below target)
        SetGlobal(itemB, 100000); // byproduct — flooded, far over target
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe withByproduct = MakeRecipe(outputs: new[] { IQ(itemA, 10), IQ(itemB, 10) });
        Recipe primaryOnly   = MakeRecipe(outputs: new[] { IQ(itemA, 10) });
        Assert.That(withByproduct.Score(targets), Is.EqualTo(primaryOnly.Score(targets)).Within(1e-6f));
        Assert.That(withByproduct.Score(targets), Is.EqualTo(2f).Within(1e-6f)); // 1 / (50/100)
    }

    // ── Score: group inputs resolve to the max-surplus leaf ────────────
    [Test]
    public void Score_GroupInput_UsesMaxSurplusLeaf(){
        // A wildcard group input scores by its MOST over-target leaf — the leaf
        // Task.ResolveConsumeLeaf would actually consume.
        SetGlobal(itemA, 50);   // leaf A: 0.5
        SetGlobal(itemB, 300);  // leaf B: 3.0  ← max
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(inputs: new[] { IQ(Group(itemA, itemB), 10) });
        Assert.That(r.Score(targets), Is.EqualTo(3f).Within(1e-6f));
    }

    [Test]
    public void Score_GroupInput_MaxLeafObeysCap(){
        SetGlobal(itemA, 100);      // 1.0
        SetGlobal(itemB, 1000000);  // huge → capped
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(inputs: new[] { IQ(Group(itemA, itemB), 10) });
        Assert.That(r.Score(targets), Is.EqualTo(Recipe.MaxSurplusRatio).Within(1e-4f));
    }

    [Test]
    public void Score_GroupInput_TargetZeroLeafHeld_IsMaxSurplus(){
        // A held target-0 leaf is fully disposable → max surplus → makes the whole group input
        // look abundant (and ResolveConsumeLeaf will burn that leaf first).
        SetGlobal(itemA, 10);   // target 0, held → cap
        SetGlobal(itemB, 50);   // 0.5
        var targets = new Dictionary<int, int> {
            { itemA.id, 0 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(inputs: new[] { IQ(Group(itemA, itemB), 10) });
        Assert.That(r.Score(targets), Is.EqualTo(Recipe.MaxSurplusRatio).Within(1e-4f));
    }

    [Test]
    public void Score_GroupInput_AllLeavesEmpty_Unmakeable(){
        // No stock in any leaf → max surplus 0 → gmIn 0 → score 0 (recipe unmakeable).
        SetGlobal(itemA, 0);
        SetGlobal(itemB, 0);
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(inputs: new[] { IQ(Group(itemA, itemB), 10) });
        Assert.That(r.Score(targets), Is.EqualTo(0f));
    }

    // ── Item.LeafDescendants ───────────────────────────────────────────
    [Test]
    public void LeafDescendants_GroupReturnsAllLeavesDepthFirst(){
        Item leaf1 = new Item { id = 430, name = "l1" };
        Item leaf2 = new Item { id = 431, name = "l2" };
        Item sub   = new Item { id = 432, name = "sub", children = new[] { leaf2 } };
        Item group = new Item { id = 433, name = "g", children = new[] { leaf1, sub } };
        CollectionAssert.AreEqual(new[] { leaf1, leaf2 }, group.LeafDescendants());
    }

    [Test]
    public void LeafDescendants_LeafReturnsSelf(){
        Item leaf = new Item { id = 434, name = "solo" };
        CollectionAssert.AreEqual(new[] { leaf }, leaf.LeafDescendants());
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

    // ── CapRoundsByTarget ──────────────────────────────────────────────
    // Caps a craft session's round count so production stops near the player's
    // per-output target. Ceiling division is the off-by-one-prone part — covered
    // explicitly. The method only ever lowers the incoming count, never raises it.
    [Test]
    public void CapRoundsByTarget_NullTargets_ReturnsRoundsUnchanged(){
        // No targets dict → no opinion on production volume → pass the cap through.
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10) });
        Assert.That(r.CapRoundsByTarget(8, null), Is.EqualTo(8));
    }

    [Test]
    public void CapRoundsByTarget_UntrackedOutput_NoCap(){
        // itemA is produced but absent from the targets dict — even sitting at 0
        // (maximum headroom, if it were tracked) it imposes no cap.
        SetGlobal(itemA, 0);
        var targets = new Dictionary<int, int> { { itemB.id, 100 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10) });
        Assert.That(r.CapRoundsByTarget(8, targets), Is.EqualTo(8));
    }

    [TestCase(100)]  // qty == target → headroom 0 → no cap
    [TestCase(150)]  // qty above target → negative headroom → no cap
    public void CapRoundsByTarget_OutputAtOrAboveTarget_NoCap(int qty){
        // Already-satisfied outputs are accepted as collateral overshoot — see the
        // method comment. The recipe only reached here because some *other* output
        // was still under target.
        SetGlobal(itemA, qty);
        var targets = new Dictionary<int, int> { { itemA.id, 100 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10) });
        Assert.That(r.CapRoundsByTarget(8, targets), Is.EqualTo(8));
    }

    [TestCase(70, 10, 3)]   // headroom 30, exact multiple → 3 rounds
    [TestCase(75, 10, 3)]   // headroom 25 → ceil(25/10) = 3, not 2
    [TestCase(99, 10, 1)]   // headroom 1 → ceil(1/10) = 1, always ≥ one round
    public void CapRoundsByTarget_CeilingDivisionToTarget(int qty, int perRound, int expected){
        SetGlobal(itemA, qty);
        var targets = new Dictionary<int, int> { { itemA.id, 100 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, perRound) });
        // Generous incoming cap so the target is the binding constraint.
        Assert.That(r.CapRoundsByTarget(99, targets), Is.EqualTo(expected));
    }

    [Test]
    public void CapRoundsByTarget_NeverRaisesRounds(){
        // Target leaves room for far more rounds, but the incoming cap (2) is
        // smaller — CapRoundsByTarget only ever tightens the count.
        SetGlobal(itemA, 0);
        var targets = new Dictionary<int, int> { { itemA.id, 100 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 1) });
        Assert.That(r.CapRoundsByTarget(2, targets), Is.EqualTo(2));
    }

    [Test]
    public void CapRoundsByTarget_MultipleOutputs_TakesTightestCap(){
        // itemA has room for 5 rounds to target, itemB only 2 → the tighter cap wins.
        SetGlobal(itemA, 50);   // headroom 50, /10 → 5 rounds
        SetGlobal(itemB, 80);   // headroom 20, /10 → 2 rounds
        var targets = new Dictionary<int, int> {
            { itemA.id, 100 },
            { itemB.id, 100 },
        };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10), IQ(itemB, 10) });
        Assert.That(r.CapRoundsByTarget(99, targets), Is.EqualTo(2));
    }

    // ── AllOutputsScarce ───────────────────────────────────────────────
    // Decides whether a mouse may ignore the storage cap and batch-craft onto the
    // floor. An output is "scarce" when global qty is strictly below
    // min(ScarcityRoundsThreshold * perRound, target); the bypass fires only when
    // every output is scarce. Tests derive quantities from the live constant so
    // they survive a retune of ScarcityRoundsThreshold.
    const int ScarcityT = Recipe.ScarcityRoundsThreshold;

    [Test]
    public void AllOutputsScarce_AllOutputsBelowRoundsThreshold_ReturnsTrue(){
        // perRound 10 → rounds threshold = 10 * ScarcityT. Both outputs sit one
        // below it. Null targets → the rounds threshold is used on its own.
        SetGlobal(itemA, 10 * ScarcityT - 1);
        SetGlobal(itemB, 10 * ScarcityT - 1);
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10), IQ(itemB, 10) });
        Assert.That(r.AllOutputsScarce(null), Is.True);
    }

    [Test]
    public void AllOutputsScarce_OneOutputAtThreshold_ReturnsFalse(){
        // qty == threshold is NOT scarce (the check is `>=`) — a single such output
        // sinks the whole bypass.
        SetGlobal(itemA, 10 * ScarcityT - 1);  // scarce
        SetGlobal(itemB, 10 * ScarcityT);      // exactly at threshold → not scarce
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10), IQ(itemB, 10) });
        Assert.That(r.AllOutputsScarce(null), Is.False);
    }

    [Test]
    public void AllOutputsScarce_LowTargetClampsThreshold(){
        // Target 50 is well below the rounds threshold (100 * ScarcityT) → threshold
        // clamps to 50. qty 60 is under the rounds threshold but at/above target,
        // so the clamp makes it not scarce. Without the clamp it would read scarce.
        SetGlobal(itemA, 60);
        var targets = new Dictionary<int, int> { { itemA.id, 50 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 100) });
        Assert.That(r.AllOutputsScarce(targets), Is.False);
    }

    [Test]
    public void AllOutputsScarce_TargetZero_NeverScarce(){
        // target 0 clamps the threshold to 0; qty ≥ 0 always holds → the bypass can
        // never fire for a "produce none" item.
        SetGlobal(itemA, 0);
        var targets = new Dictionary<int, int> { { itemA.id, 0 } };
        Recipe r = MakeRecipe(outputs: new[] { IQ(itemA, 10) });
        Assert.That(r.AllOutputsScarce(targets), Is.False);
    }

    [Test]
    public void AllOutputsScarce_EmptyOutputs_ReturnsTrue(){
        // Vacuous truth — no output violates scarcity. Harmless: the storage-cap
        // loop it gates is also a no-op for an outputless recipe.
        Recipe r = MakeRecipe();
        Assert.That(r.AllOutputsScarce(null), Is.True);
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

    // Builds a throwaway group item over the given leaves for group-input scoring tests.
    // id is arbitrary: Score iterates the group's LeafDescendants and looks each LEAF up in
    // GlobalInventory/targets — the group item itself is never looked up in Db.
    static Item Group(params Item[] children){
        return new Item { id = 409, name = "test_group", children = children };
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

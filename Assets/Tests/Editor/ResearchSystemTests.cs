using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// EditMode tests for ResearchSystem — focuses on the pure / data-driven surface:
// progress accumulation (AddPassiveProgress, AddScientistProgress), unlock /
// forget transitions (CheckTransitions), prerequisite gating, the recipe-gating
// reverse index (IsRecipeUnlocked), per-tick decay (TickUpdate), study toggles,
// PickStudyTarget priority, and ResetAll.
//
// ResearchSystem is a MonoBehaviour singleton. We instantiate it via AddComponent
// so Awake runs and seeds `instance`, then in SetUp we wipe the collections that
// LoadNodes populated from researchDb.json and rebuild a tiny in-memory tree:
//
//     A (id=1, cost=1, no prereqs)
//     B (id=2, cost=2, prereqs=[A])
//     C (id=3, cost=1, no prereqs, gates recipeId=42)
//
// Start() is intentionally NOT called: EditMode tests don't run the play loop, so
// the index-building / Db-touching paths in Start are skipped. We populate the
// recipe lock index manually via reflection where needed.
//
// ── Deferred to integration tests ────────────────────────────────────
// InjectBookRecipeUnlocks / BuildJobLockIndex / BuildBuildingLockIndex /
// ValidateJobUnlocks all need a live Db plus AnimalController/BuildPanel/
// InventoryController, so the unlock-side-effects (ApplyEffect routing to
// UnlockBuilding/UnlockJob/DiscoverItem) live in PlayMode. The active-research
// query (IsActivelyResearched) reads AnimalController state and is also deferred.
[TestFixture]
public class ResearchSystemTests {

    GameObject host;
    ResearchSystem rs;

    ResearchNodeData nodeA; // id=1, cost=1, no prereqs
    ResearchNodeData nodeB; // id=2, cost=2, prereqs=[A]
    ResearchNodeData nodeC; // id=3, cost=1, no prereqs, gates recipeId=42

    const int GatedRecipeId   = 42;
    const int UngatedRecipeId = 99;

    [SetUp]
    public void SetUp(){
        // Make absolutely sure no stale instance survives from a prior fixture/test.
        SetInstance(null);

        host = new GameObject("ResearchSystemTestHost");
        rs = host.AddComponent<ResearchSystem>();
        // Awake has now run: LoadNodes pulled real researchDb.json into rs.nodes/nodeById/progress.
        // Wipe and reseed with a tiny deterministic tree so tests don't depend on data churn.

        rs.nodes.Clear();
        rs.nodeById.Clear();
        rs.progress.Clear();
        rs.unlockedIds.Clear();
        rs.studiedIds.Clear();
        rs.unlockTimestamps.Clear();
        rs.unlockCounter = 0;

        nodeA = MakeNode(1, "alpha", cost: 1f, prereqs: new int[0]);
        nodeB = MakeNode(2, "beta",  cost: 2f, prereqs: new[]{ 1 });
        nodeC = MakeNode(3, "gamma", cost: 1f, prereqs: new int[0],
                         unlocks: new[]{ new UnlockEntry { type = "recipe", target = GatedRecipeId.ToString() } });
        AddNode(nodeA);
        AddNode(nodeB);
        AddNode(nodeC);

        // Manually wire the recipe→tech reverse index. BuildRecipeLockIndex normally runs
        // from Start() (which we don't trigger in EditMode), so we set it directly.
        var recipeIndex = GetRecipeIndex();
        recipeIndex.Clear();
        recipeIndex[GatedRecipeId] = nodeC.id;
    }

    [TearDown]
    public void TearDown(){
        if (host != null) Object.DestroyImmediate(host);
        SetInstance(null);
    }

    // ResearchSystem.instance has a protected setter; tests need reflection to wipe it.
    static void SetInstance(ResearchSystem value){
        PropertyInfo p = typeof(ResearchSystem).GetProperty(
            "instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        p.SetValue(null, value);
    }

    // ── AddPassiveProgress: below threshold ──────────────────────────────
    [Test]
    public void AddPassiveProgress_BelowCost_AccumulatesWithoutUnlocking(){
        rs.AddPassiveProgress("alpha", 0.5f);
        Assert.That(rs.GetProgress(nodeA.id), Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(rs.IsUnlocked(nodeA.id), Is.False);
    }

    // ── AddPassiveProgress: reaches threshold ────────────────────────────
    [Test]
    public void AddPassiveProgress_ReachesCost_UnlocksNodeAndStampsTimestamp(){
        rs.AddPassiveProgress("alpha", 1f);
        Assert.That(rs.IsUnlocked(nodeA.id), Is.True);
        Assert.That(rs.unlockTimestamps.ContainsKey(nodeA.id), Is.True);
        Assert.That(rs.unlockCounter, Is.EqualTo(1));
    }

    // ── AddPassiveProgress: caps at 2× cost ───────────────────────────────
    [Test]
    public void AddPassiveProgress_OverCap_ClampsToTwiceCost(){
        // alpha cost = 1, cap = 2. Pour in 10× and verify clamp.
        rs.AddPassiveProgress("alpha", 10f);
        Assert.That(rs.GetProgress(nodeA.id), Is.EqualTo(2f).Within(1e-5f));
        Assert.That(rs.IsUnlocked(nodeA.id), Is.True);
    }

    // ── Unlock idempotency: re-crossing threshold doesn't double-stamp ───
    [Test]
    public void AddPassiveProgress_AlreadyUnlocked_DoesNotRestamp(){
        rs.AddPassiveProgress("alpha", 1f);
        int firstStamp = rs.unlockTimestamps[nodeA.id];
        int firstCounter = rs.unlockCounter;

        // More progress while already unlocked — should not change unlock state or restamp.
        rs.AddPassiveProgress("alpha", 0.5f);

        Assert.That(rs.IsUnlocked(nodeA.id), Is.True);
        Assert.That(rs.unlockTimestamps[nodeA.id], Is.EqualTo(firstStamp));
        Assert.That(rs.unlockCounter, Is.EqualTo(firstCounter));
    }

    // ── Prerequisite gating ──────────────────────────────────────────────
    [Test]
    public void AddPassiveProgress_PrereqLocked_DoesNotUnlockEvenAtCost(){
        // beta cost = 2, prereq = alpha (still locked).
        rs.AddPassiveProgress("beta", 5f);
        // Progress accumulates (capped at 2×cost=4) but unlock is gated on prereqs.
        Assert.That(rs.GetProgress(nodeB.id), Is.EqualTo(4f).Within(1e-5f));
        Assert.That(rs.IsUnlocked(nodeB.id), Is.False);
    }

    [Test]
    public void AddPassiveProgress_PrereqUnlocked_BetaUnlocksWhenItHitsCost(){
        rs.AddPassiveProgress("alpha", 1f); // unlock prereq
        Assert.That(rs.IsUnlocked(nodeA.id), Is.True);

        rs.AddPassiveProgress("beta", 2f);
        Assert.That(rs.IsUnlocked(nodeB.id), Is.True);
    }

    [Test]
    public void PrereqsMet_ReflectsUnlockedSet(){
        Assert.That(rs.PrereqsMet(nodeB), Is.False);
        rs.unlockedIds.Add(nodeA.id);
        Assert.That(rs.PrereqsMet(nodeB), Is.True);
    }

    // ── IsRecipeUnlocked ─────────────────────────────────────────────────
    [Test]
    public void IsRecipeUnlocked_UngatedRecipe_ReturnsTrue(){
        // Recipe id 99 is not in the reverse index → unlocked by default.
        Assert.That(rs.IsRecipeUnlocked(UngatedRecipeId), Is.True);
    }

    [Test]
    public void IsRecipeUnlocked_GatedAndLocked_ReturnsFalse(){
        // gamma gates recipe 42, gamma not yet unlocked.
        Assert.That(rs.IsRecipeUnlocked(GatedRecipeId), Is.False);
    }

    [Test]
    public void IsRecipeUnlocked_GatedAndUnlocked_ReturnsTrue(){
        rs.AddPassiveProgress("gamma", 1f); // unlocks gamma
        Assert.That(rs.IsUnlocked(nodeC.id), Is.True);
        Assert.That(rs.IsRecipeUnlocked(GatedRecipeId), Is.True);
    }

    // ── AddScientistProgress ──────────────────────────────────────────────
    [Test]
    public void AddScientistProgress_AddsScaledProgressAndCanUnlock(){
        // ScientistRate = 0.05. workEfficiency=20 → 1.0 progress per call. alpha cost=1 → unlocks in one tick.
        rs.AddScientistProgress(20f, nodeA.id);
        Assert.That(rs.GetProgress(nodeA.id), Is.EqualTo(1f).Within(1e-5f));
        Assert.That(rs.IsUnlocked(nodeA.id), Is.True);
    }

    [Test]
    public void AddScientistProgress_NegativeTarget_NoOp(){
        rs.AddScientistProgress(20f, -1);
        Assert.That(rs.GetProgress(nodeA.id), Is.EqualTo(0f));
    }

    [Test]
    public void AddScientistProgress_UnknownNode_NoOp(){
        rs.AddScientistProgress(20f, 999);
        Assert.That(rs.IsUnlocked(999), Is.False);
    }

    // ── TickUpdate: decay + transition check ─────────────────────────────
    [Test]
    public void TickUpdate_DecaysAllNodesWithProgress(){
        rs.AddPassiveProgress("alpha", 0.5f);
        rs.AddPassiveProgress("beta", 0.5f);
        // DecayRate = 0.01 per tick.
        rs.TickUpdate();
        Assert.That(rs.GetProgress(nodeA.id), Is.EqualTo(0.49f).Within(1e-5f));
        Assert.That(rs.GetProgress(nodeB.id), Is.EqualTo(0.49f).Within(1e-5f));
    }

    [Test]
    public void TickUpdate_FloorsAtZero_NoNegativeProgress(){
        // Tiny progress, then enough ticks to take it below zero — must clamp to 0.
        rs.AddPassiveProgress("alpha", 0.005f);
        for (int i = 0; i < 5; i++) rs.TickUpdate();
        Assert.That(rs.GetProgress(nodeA.id), Is.EqualTo(0f));
    }

    [Test]
    public void TickUpdate_ZeroProgressNode_StaysZero(){
        // Sanity: nodes at 0 don't get pushed negative by the decay step.
        rs.TickUpdate();
        foreach (var n in rs.nodes)
            Assert.That(rs.GetProgress(n.id), Is.EqualTo(0f));
    }

    // ── Forget transition ────────────────────────────────────────────────
    [Test]
    public void TickUpdate_DecayBelowSeventyFivePercent_ForgetsAndFiresEvent(){
        // alpha cost=1, forget threshold = 0.75. Set progress to just above cost,
        // then decay enough ticks to drop below 0.75.
        rs.AddPassiveProgress("alpha", 1f);
        Assert.That(rs.IsUnlocked(nodeA.id), Is.True);

        bool fired = false;
        ResearchNodeData forgotten = null;
        System.Action<ResearchNodeData> handler = n => { fired = true; forgotten = n; };
        ResearchSystem.OnTechForgotten += handler;
        try {
            // RevertEffect logs a Debug.Log for the forget event.
            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("Technology forgotten: alpha"));
            // Need to drop from ~1.0 below 0.75 → at 0.01/tick, 26 ticks suffices.
            for (int i = 0; i < 30; i++) rs.TickUpdate();
        } finally {
            ResearchSystem.OnTechForgotten -= handler;
        }

        Assert.That(rs.IsUnlocked(nodeA.id), Is.False);
        Assert.That(fired, Is.True);
        Assert.That(forgotten, Is.SameAs(nodeA));
    }

    // ── Study API ────────────────────────────────────────────────────────
    [Test]
    public void ToggleStudy_FlipsMembership(){
        Assert.That(rs.IsStudied(nodeA.id), Is.False);
        rs.ToggleStudy(nodeA.id);
        Assert.That(rs.IsStudied(nodeA.id), Is.True);
        rs.ToggleStudy(nodeA.id);
        Assert.That(rs.IsStudied(nodeA.id), Is.False);
    }

    [Test]
    public void SetStudy_SetsExplicitState(){
        rs.SetStudy(nodeA.id, true);
        Assert.That(rs.IsStudied(nodeA.id), Is.True);
        rs.SetStudy(nodeA.id, true); // idempotent
        Assert.That(rs.IsStudied(nodeA.id), Is.True);
        rs.SetStudy(nodeA.id, false);
        Assert.That(rs.IsStudied(nodeA.id), Is.False);
    }

    [Test]
    public void CanStudy_RequiresPrereqs(){
        Assert.That(rs.CanStudy(nodeA), Is.True);
        Assert.That(rs.CanStudy(nodeB), Is.False);
        rs.unlockedIds.Add(nodeA.id);
        Assert.That(rs.CanStudy(nodeB), Is.True);
    }

    // ── PickStudyTarget priority ─────────────────────────────────────────
    [Test]
    public void PickStudyTarget_NoStudied_ReturnsMinusOne(){
        Assert.That(rs.PickStudyTarget(), Is.EqualTo(-1));
    }

    [Test]
    public void PickStudyTarget_PrefersBelowCostOverAboveCost(){
        // alpha studied, already past cost (reinforcement candidate).
        // gamma studied, below cost (real-research candidate). Below-cost wins.
        rs.AddPassiveProgress("alpha", 1.5f); // unlocks alpha, progress past cost
        rs.SetStudy(nodeA.id, true);
        rs.SetStudy(nodeC.id, true);

        Assert.That(rs.PickStudyTarget(), Is.EqualTo(nodeC.id));
    }

    [Test]
    public void PickStudyTarget_AmongAboveCost_PicksLowestRatio(){
        // alpha cap=2, gamma cap=2. Push alpha to 1.8 (90%) and gamma to 1.2 (60%).
        // Both are studied and above cost — expect gamma (lower ratio).
        rs.AddPassiveProgress("alpha", 1.8f);
        rs.AddPassiveProgress("gamma", 1.2f);
        rs.SetStudy(nodeA.id, true);
        rs.SetStudy(nodeC.id, true);

        Assert.That(rs.PickStudyTarget(), Is.EqualTo(nodeC.id));
    }

    [Test]
    public void PickStudyTarget_SkipsNodesWithUnmetPrereqs(){
        // beta studied but its prereq alpha is locked → not eligible.
        rs.SetStudy(nodeB.id, true);
        Assert.That(rs.PickStudyTarget(), Is.EqualTo(-1));
    }

    // ── GetCap ───────────────────────────────────────────────────────────
    [Test]
    public void GetCap_IsTwiceNodeCost(){
        Assert.That(rs.GetCap(nodeA), Is.EqualTo(2f));
        Assert.That(rs.GetCap(nodeB), Is.EqualTo(4f));
    }

    // ── ResetAll ─────────────────────────────────────────────────────────
    [Test]
    public void ResetAll_ClearsProgressUnlocksStudyAndCounters(){
        rs.AddPassiveProgress("alpha", 1f);
        rs.SetStudy(nodeC.id, true);
        Assert.That(rs.IsUnlocked(nodeA.id), Is.True);
        Assert.That(rs.IsStudied(nodeC.id), Is.True);

        rs.ResetAll();

        Assert.That(rs.unlockedIds, Is.Empty);
        Assert.That(rs.studiedIds, Is.Empty);
        Assert.That(rs.unlockTimestamps, Is.Empty);
        Assert.That(rs.unlockCounter, Is.EqualTo(0));
        foreach (var n in rs.nodes)
            Assert.That(rs.GetProgress(n.id), Is.EqualTo(0f));
    }

    // ── Helpers ──────────────────────────────────────────────────────────
    static ResearchNodeData MakeNode(int id, string name, float cost, int[] prereqs, UnlockEntry[] unlocks = null){
        return new ResearchNodeData {
            id          = id,
            name        = name,
            description = "",
            prereqs     = prereqs ?? new int[0],
            cost        = cost,
            unlocks     = unlocks ?? new UnlockEntry[0],
        };
    }

    void AddNode(ResearchNodeData node){
        rs.nodes.Add(node);
        rs.nodeById[node.id] = node;
        rs.progress[node.id] = 0f;
    }

    // recipeToTechNode is private; reflection lets tests populate it directly so we can
    // exercise IsRecipeUnlocked without running Start()/BuildRecipeLockIndex (which would
    // pull in Db). Tested-class internals leaking into tests is an accepted trade-off here.
    Dictionary<int, int> GetRecipeIndex(){
        var field = typeof(ResearchSystem).GetField("recipeToTechNode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "recipeToTechNode field not found — has it been renamed?");
        return (Dictionary<int, int>)field.GetValue(rs);
    }
}

using NUnit.Framework;
using System.Collections.Generic;

// EditMode tests for JobSwapper.
//
// Scope: WeightedScore — the pure scoring function that drives swap eligibility.
// We can build SkillSet and Job instances directly (Job is a plain POCO with a
// resolvedSkillWeights dictionary populated at Db load time; we populate it
// manually for tests).
//
// ── Deferred to integration tests ────────────────────────────────────
// TrySwap depends on AnimalController.instance (singleton set in Unity Awake)
// and Animal.SetJob → Refresh, both of which require live MonoBehaviours.
// Once we have an Animal/AnimalController test fixture (Tier 2), TrySwap
// behaviour can be covered directly.
//
// ── Note on the "construction-job-vs-craft-job" distinction ─────────
// That invariant lives in WorkOrderManager.RegisterWorkstation (line ~408,
// the canDo lambda for Craft orders): eligibility is determined by recipe
// match, not by structType.job. JobSwapper is intentionally agnostic to
// that distinction — it scores purely on resolvedSkillWeights and never
// touches Job.recipes. We assert that invariant below as a regression
// guard: a job can have recipes set without affecting WeightedScore.
[TestFixture]
public class JobSwapperTests {

    // ── WeightedScore: empty / null cases ──────────────────────────────
    [Test]
    public void WeightedScore_NullResolvedWeights_ReturnsZero(){
        // Job with no skillWeights at all (resolvedSkillWeights stays null in Db).
        Job job = MakeJob("hauler", null);
        SkillSet ss = MakeSkills((Skill.Farming, 5));
        Assert.That(JobSwapper.WeightedScore(ss, job), Is.EqualTo(0f));
    }

    [Test]
    public void WeightedScore_EmptyResolvedWeights_ReturnsZero(){
        // Distinct from null: an empty dict means "no skills weighted" (also 0).
        Job job = MakeJob("hauler", new Dictionary<Skill, float>());
        SkillSet ss = MakeSkills((Skill.Farming, 5), (Skill.Mining, 9));
        Assert.That(JobSwapper.WeightedScore(ss, job), Is.EqualTo(0f));
    }

    [Test]
    public void WeightedScore_AllZeroLevels_ReturnsZero(){
        // Default SkillSet (everything lv0) → score 0 regardless of weights.
        Job job = MakeJob("farmer", new Dictionary<Skill, float>{ { Skill.Farming, 1f } });
        SkillSet ss = new SkillSet();
        Assert.That(JobSwapper.WeightedScore(ss, job), Is.EqualTo(0f));
    }

    // ── WeightedScore: single-skill weighting ─────────────────────────
    [TestCase(0, 0f)]
    [TestCase(1, 0.8f)]
    [TestCase(5, 4.0f)]
    [TestCase(10, 8.0f)]
    public void WeightedScore_SingleSkillWeight_LinearInLevel(int level, float expected){
        Job farmer = MakeJob("farmer", new Dictionary<Skill, float>{ { Skill.Farming, 0.8f } });
        SkillSet ss = MakeSkills((Skill.Farming, level));
        Assert.That(JobSwapper.WeightedScore(ss, farmer), Is.EqualTo(expected).Within(1e-5f));
    }

    // ── WeightedScore: multi-skill weighting (sum across) ─────────────
    [Test]
    public void WeightedScore_MultiSkill_SumsWeightedLevels(){
        // farmer with farming:0.8, woodworking:0.2 — animal lv5 farming, lv2 woodworking
        // → 0.8*5 + 0.2*2 = 4.0 + 0.4 = 4.4
        Job farmer = MakeJob("farmer", new Dictionary<Skill, float>{
            { Skill.Farming,     0.8f },
            { Skill.Woodworking, 0.2f },
        });
        SkillSet ss = MakeSkills((Skill.Farming, 5), (Skill.Woodworking, 2));
        Assert.That(JobSwapper.WeightedScore(ss, farmer), Is.EqualTo(4.4f).Within(1e-5f));
    }

    [Test]
    public void WeightedScore_OnlyWeightedSkillsContribute(){
        // Animal has Mining lv10 — but the farmer job doesn't weight Mining at all.
        // The unweighted skill must NOT bleed in.
        Job farmer = MakeJob("farmer", new Dictionary<Skill, float>{ { Skill.Farming, 1f } });
        SkillSet ss = MakeSkills((Skill.Farming, 0), (Skill.Mining, 10));
        Assert.That(JobSwapper.WeightedScore(ss, farmer), Is.EqualTo(0f));
    }

    [Test]
    public void WeightedScore_NegativeWeight_Subtracts(){
        // Defensive: nothing in JobSwapper.cs forbids a negative weight in JSON.
        // Document current behaviour — negative weights subtract linearly.
        Job job = MakeJob("oddball", new Dictionary<Skill, float>{
            { Skill.Farming,  1f },
            { Skill.Mining,  -0.5f },
        });
        SkillSet ss = MakeSkills((Skill.Farming, 4), (Skill.Mining, 6));
        // 1*4 + (-0.5)*6 = 4 - 3 = 1.0
        Assert.That(JobSwapper.WeightedScore(ss, job), Is.EqualTo(1f).Within(1e-5f));
    }

    // ── WeightedScore: relative ordering drives swap decisions ─────────
    // The "gain > 0" check inside TrySwap depends on relative score deltas.
    // Pin down the canonical "swap is beneficial" case so future refactors don't
    // silently flip the sign.
    [Test]
    public void WeightedScore_BeneficialSwap_HasPositiveGain(){
        // farmer values farming heavily; mason values mining heavily.
        // Animal A is farming-heavy; animal B is mining-heavy. They start mismatched
        // (A=mason, B=farmer) and a swap should improve combined score.
        Job farmer = MakeJob("farmer", new Dictionary<Skill, float>{ { Skill.Farming, 1f } });
        Job mason  = MakeJob("mason",  new Dictionary<Skill, float>{ { Skill.Mining,  1f } });
        SkillSet a = MakeSkills((Skill.Farming, 8), (Skill.Mining, 1));
        SkillSet b = MakeSkills((Skill.Farming, 1), (Skill.Mining, 8));

        // Current: a=mason (score 1), b=farmer (score 1) → total 2.
        float current = JobSwapper.WeightedScore(a, mason) + JobSwapper.WeightedScore(b, farmer);
        // Swapped: a=farmer (score 8), b=mason (score 8) → total 16.
        float swapped = JobSwapper.WeightedScore(a, farmer) + JobSwapper.WeightedScore(b, mason);
        Assert.That(current, Is.EqualTo(2f).Within(1e-5f));
        Assert.That(swapped, Is.EqualTo(16f).Within(1e-5f));
        Assert.That(swapped - current, Is.GreaterThan(0f));
    }

    [Test]
    public void WeightedScore_NeutralSwap_HasZeroGain(){
        // Two identical animals doing identical jobs swap to no benefit.
        Job farmer = MakeJob("farmer", new Dictionary<Skill, float>{ { Skill.Farming, 1f } });
        Job mason  = MakeJob("mason",  new Dictionary<Skill, float>{ { Skill.Mining,  1f } });
        SkillSet a = MakeSkills((Skill.Farming, 5), (Skill.Mining, 5));
        SkillSet b = MakeSkills((Skill.Farming, 5), (Skill.Mining, 5));

        float current = JobSwapper.WeightedScore(a, farmer) + JobSwapper.WeightedScore(b, mason);
        float swapped = JobSwapper.WeightedScore(a, mason)  + JobSwapper.WeightedScore(b, farmer);
        Assert.That(swapped - current, Is.EqualTo(0f).Within(1e-5f));
    }

    // ── Construction-job-vs-craft-job invariant ────────────────────────
    // Project memory: "Do NOT use structType.job for craft eligibility — that's
    // the construction job." JobSwapper must score by skill weights only and
    // ignore Job.recipes entirely; otherwise a hauler with the construction
    // role for a sawmill could be wrongly considered "good at sawmill crafts"
    // for swap purposes. Regression guard: populating recipes does not change
    // WeightedScore.
    [Test]
    public void WeightedScore_IgnoresJobRecipes_ConstructionVsCraftDistinctionPreserved(){
        // A hauler whose `recipes[]` references a sawmill (because sawmill's
        // *construction* logistics job is "hauler") must not score higher on
        // a Woodworking-weighted axis than its actual skill levels imply.
        Job hauler = MakeJob("hauler", new Dictionary<Skill, float>{ { Skill.Construction, 1f } });
        // Stuff the recipes array with a sawmill recipe to simulate Db's load-time wiring.
        // (In real Db this would be a sawmill-construction recipe, but JobSwapper never
        // looks at recipes — so the contents don't matter, only that they're present.)
        hauler.recipes[0] = new Recipe { id = 999, job = "hauler", tile = "sawmill" };
        hauler.nRecipes   = 1;

        SkillSet noviceConstructor = MakeSkills((Skill.Construction, 1), (Skill.Woodworking, 0));
        float scoreBefore = JobSwapper.WeightedScore(noviceConstructor, hauler);

        // Even though recipes[0].tile == "sawmill" (a woodworking-ish building),
        // the score must be derived purely from Construction:1 * level:1 = 1.0,
        // unaffected by the woodworking-flavoured recipe wiring.
        Assert.That(scoreBefore, Is.EqualTo(1f).Within(1e-5f));

        // Sanity: bumping woodworking level changes nothing (not weighted).
        SkillSet noviceWithWoodworking = MakeSkills((Skill.Construction, 1), (Skill.Woodworking, 10));
        float scoreAfter = JobSwapper.WeightedScore(noviceWithWoodworking, hauler);
        Assert.That(scoreAfter, Is.EqualTo(1f).Within(1e-5f));
    }

    // ── Helpers ────────────────────────────────────────────────────────
    // Build a Job with the resolved-skill-weights dict pre-populated, mimicking
    // what Db does after JSON load. Pass null for the "no weights configured" case.
    static Job MakeJob(string name, Dictionary<Skill, float> resolvedWeights){
        return new Job {
            id   = 0,
            name = name,
            resolvedSkillWeights = resolvedWeights,
        };
    }

    // Build a SkillSet with the given (skill, level) pairs. We can't set level
    // directly, so we round-trip through Deserialize using the int[] level array.
    static SkillSet MakeSkills(params (Skill skill, int level)[] entries){
        SkillSet ss = new SkillSet();
        float[] xp = new float[SkillSet.Count];
        int[]   lv = new int[SkillSet.Count];
        foreach (var (skill, level) in entries) lv[(int)skill] = level;
        ss.Deserialize(xp, lv);
        return ss;
    }
}

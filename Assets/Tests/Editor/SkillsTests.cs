using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// EditMode tests for the Skills module: the Skill enum domain, SkillSet's
// XP/level/bonus accounting, and serialize/deserialize round-trips. The whole
// surface is pure C# — no MonoBehaviours, no World — so we can construct
// SkillSet directly and exercise it without any Unity scaffolding.
[TestFixture]
public class SkillsTests {

    // ── XpThreshold ─────────────────────────────────────────────────────
    // Doubles each level: 10, 20, 40, 80, 160, ...  i.e. 10 * 2^level.
    [TestCase(0,   10f)]
    [TestCase(1,   20f)]
    [TestCase(2,   40f)]
    [TestCase(3,   80f)]
    [TestCase(4,  160f)]
    [TestCase(5,  320f)]
    [TestCase(10, 10240f)]
    public void XpThreshold_DoublesPerLevel(int level, float expected){
        Assert.That(SkillSet.XpThreshold(level), Is.EqualTo(expected));
    }

    // ── Count / enum domain ────────────────────────────────────────────
    [Test]
    public void Count_MatchesEnumLength(){
        // Defensive: if a Skill is added without bumping any code paths, this
        // catches the mismatch quickly. Actual enum values listed for clarity.
        Assert.That(SkillSet.Count, Is.EqualTo(System.Enum.GetValues(typeof(Skill)).Length));
        Assert.That(SkillSet.Count, Is.GreaterThanOrEqualTo(5)); // Farming, Mining, Construction, Science, Woodworking
    }

    // ── Defaults ────────────────────────────────────────────────────────
    [Test]
    public void Defaults_AllSkillsZeroXpZeroLevel(){
        SkillSet ss = new SkillSet();
        foreach (Skill s in System.Enum.GetValues(typeof(Skill))){
            Assert.That(ss.GetXp(s),    Is.EqualTo(0f), $"{s} xp default");
            Assert.That(ss.GetLevel(s), Is.EqualTo(0),  $"{s} level default");
            // Level 0 → bonus 1.0 (no multiplier).
            Assert.That(ss.GetBonus(s), Is.EqualTo(1f), $"{s} bonus default");
        }
    }

    // ── GetBonus: level → +5% per level ────────────────────────────────
    // Level → bonus mapping. Constructed by gaining XP since level isn't directly settable.
    [TestCase(0, 1.00f)]
    [TestCase(1, 1.05f)]
    [TestCase(2, 1.10f)]
    [TestCase(3, 1.15f)]
    [TestCase(5, 1.25f)]
    [TestCase(10, 1.50f)]
    public void GetBonus_AdditiveFivePercentPerLevel(int targetLevel, float expectedBonus){
        SkillSet ss = LevelUpTo(Skill.Farming, targetLevel);
        Assert.That(ss.GetLevel(Skill.Farming), Is.EqualTo(targetLevel));
        Assert.That(ss.GetBonus(Skill.Farming), Is.EqualTo(expectedBonus).Within(1e-5f));
    }

    // ── GainXp: threshold-crossing & carry-over ────────────────────────
    [Test]
    public void GainXp_BelowThreshold_AccumulatesNoLevel(){
        SkillSet ss = new SkillSet();
        ss.GainXp(Skill.Mining, 5f);
        Assert.That(ss.GetXp(Skill.Mining),    Is.EqualTo(5f));
        Assert.That(ss.GetLevel(Skill.Mining), Is.EqualTo(0));
    }

    [Test]
    public void GainXp_ExactlyAtThreshold_LevelsUpAndZeroesXp(){
        // 10 xp == lv0 threshold → level becomes 1, xp resets to 0.
        LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("Skill up!.*Mining.*lv1"));
        SkillSet ss = new SkillSet();
        ss.GainXp(Skill.Mining, 10f);
        Assert.That(ss.GetLevel(Skill.Mining), Is.EqualTo(1));
        Assert.That(ss.GetXp(Skill.Mining),    Is.EqualTo(0f).Within(1e-5f));
    }

    [Test]
    public void GainXp_OverThreshold_CarriesRemainder(){
        // 15 xp at lv0 (threshold 10) → lv1 + 5 leftover xp.
        LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("Skill up!.*Mining.*lv1"));
        SkillSet ss = new SkillSet();
        ss.GainXp(Skill.Mining, 15f);
        Assert.That(ss.GetLevel(Skill.Mining), Is.EqualTo(1));
        Assert.That(ss.GetXp(Skill.Mining),    Is.EqualTo(5f).Within(1e-5f));
    }

    [Test]
    public void GainXp_LargeAmount_LevelsUpMultipleTimes(){
        // 70 xp from lv0: spend 10 → lv1, 20 → lv2, 40 → lv3, leftover 0.
        // Total used: 10+20+40 = 70 exactly, lands on lv3 with 0 xp.
        LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("Skill up!.*Construction.*lv1"));
        LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("Skill up!.*Construction.*lv2"));
        LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("Skill up!.*Construction.*lv3"));
        SkillSet ss = new SkillSet();
        ss.GainXp(Skill.Construction, 70f);
        Assert.That(ss.GetLevel(Skill.Construction), Is.EqualTo(3));
        Assert.That(ss.GetXp(Skill.Construction),    Is.EqualTo(0f).Within(1e-5f));
    }

    [TestCase(0f)]
    [TestCase(-1f)]
    [TestCase(-1000f)]
    public void GainXp_NonPositive_NoOp(float amount){
        // GainXp early-outs on amount <= 0; protects against negative XP poisoning the bank.
        SkillSet ss = new SkillSet();
        ss.GainXp(Skill.Science, 7f); // seed something so we can detect erroneous changes
        ss.GainXp(Skill.Science, amount);
        Assert.That(ss.GetXp(Skill.Science),    Is.EqualTo(7f));
        Assert.That(ss.GetLevel(Skill.Science), Is.EqualTo(0));
    }

    [Test]
    public void GainXp_DoesNotBleedAcrossSkills(){
        // Domain isolation: gaining Farming XP must not affect Mining / Science / etc.
        SkillSet ss = new SkillSet();
        ss.GainXp(Skill.Farming, 5f);
        Assert.That(ss.GetXp(Skill.Farming),     Is.EqualTo(5f));
        Assert.That(ss.GetXp(Skill.Mining),      Is.EqualTo(0f));
        Assert.That(ss.GetXp(Skill.Construction),Is.EqualTo(0f));
        Assert.That(ss.GetXp(Skill.Science),     Is.EqualTo(0f));
        Assert.That(ss.GetXp(Skill.Woodworking), Is.EqualTo(0f));
    }

    // ── Serialize / Deserialize ────────────────────────────────────────
    [Test]
    public void Serialize_ReturnsClonesNotAliases(){
        // Mutating the returned arrays must not corrupt internal state — they're
        // public arrays so a careless caller will try.
        SkillSet ss = new SkillSet();
        ss.GainXp(Skill.Farming, 5f); // 5 xp, lv0, below threshold
        float[] xp = ss.SerializeXp();
        int[]   lv = ss.SerializeLevel();
        xp[(int)Skill.Farming] = 9999f;
        lv[(int)Skill.Farming] = 99;
        Assert.That(ss.GetXp(Skill.Farming),    Is.EqualTo(5f));
        Assert.That(ss.GetLevel(Skill.Farming), Is.EqualTo(0));
    }

    [Test]
    public void Deserialize_RoundTrip_PreservesXpAndLevel(){
        // Build a non-trivial state, snapshot, then load into a fresh SkillSet.
        LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("Skill up!.*Woodworking.*lv1"));
        SkillSet a = new SkillSet();
        a.GainXp(Skill.Woodworking, 13f); // crosses lv0→lv1, 3 leftover

        SkillSet b = new SkillSet();
        b.Deserialize(a.SerializeXp(), a.SerializeLevel());
        Assert.That(b.GetXp(Skill.Woodworking),    Is.EqualTo(3f).Within(1e-5f));
        Assert.That(b.GetLevel(Skill.Woodworking), Is.EqualTo(1));
        Assert.That(b.GetBonus(Skill.Woodworking), Is.EqualTo(1.05f).Within(1e-5f));
    }

    [Test]
    public void Deserialize_ShorterSavedArrays_LeavesExtraDomainsAtDefault(){
        // Migration safety: an old save written before a new Skill was added
        // shouldn't crash; the missing tail simply stays at defaults.
        float[] shortXp = new float[]{ 1f, 2f, 3f }; // only first three skills
        int[]   shortLv = new int[]  { 0, 0, 0 };
        SkillSet ss = new SkillSet();
        ss.Deserialize(shortXp, shortLv);
        Assert.That(ss.GetXp(Skill.Farming),      Is.EqualTo(1f));
        Assert.That(ss.GetXp(Skill.Mining),       Is.EqualTo(2f));
        Assert.That(ss.GetXp(Skill.Construction), Is.EqualTo(3f));
        // Tail untouched at default 0.
        Assert.That(ss.GetXp(Skill.Science),      Is.EqualTo(0f));
        Assert.That(ss.GetXp(Skill.Woodworking),  Is.EqualTo(0f));
    }

    [Test]
    public void Deserialize_LongerSavedArrays_Truncates(){
        // The mirror case: a save from a future build with extra Skill entries
        // should clamp to current Count rather than throw.
        int n = SkillSet.Count;
        float[] longXp = new float[n + 3];
        int[]   longLv = new int[n + 3];
        for (int i = 0; i < longXp.Length; i++){ longXp[i] = i; longLv[i] = i; }
        SkillSet ss = new SkillSet();
        Assert.DoesNotThrow(() => ss.Deserialize(longXp, longLv));
        // Each in-range domain receives its corresponding index.
        for (int i = 0; i < n; i++){
            Skill s = (Skill)i;
            Assert.That(ss.GetXp(s),    Is.EqualTo((float)i));
            Assert.That(ss.GetLevel(s), Is.EqualTo(i));
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────
    // Levels a fresh SkillSet up to `target` by feeding the exact threshold sums.
    // Level-ups Debug.Log; we LogAssert.Expect each one so the test runner doesn't fail.
    static SkillSet LevelUpTo(Skill skill, int target){
        SkillSet ss = new SkillSet();
        for (int lv = 0; lv < target; lv++){
            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex(
                $"Skill up!.*{System.Enum.GetName(typeof(Skill), skill)}.*lv{lv + 1}"));
            ss.GainXp(skill, SkillSet.XpThreshold(lv));
        }
        return ss;
    }
}

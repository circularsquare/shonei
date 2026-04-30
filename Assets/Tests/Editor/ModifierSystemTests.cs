using NUnit.Framework;

// EditMode tests for ModifierSystem — the central registry of gameplay modifier
// constants and query methods. Covers the public constants (regression-pinned values),
// the trivial GetRecipeOutputMultiplier placeholder, and the pure compositional
// invariants between the constants.
//
// ── Deferred to integration tests ────────────────────────────────────
// GetWorkMultiplier, GetBaseWorkEfficiency, and GetTravelSpeedMultiplier all need
// a fully-wired Animal: toolSlotInv (Inventory ctor pulls in Db.itemsFlat and
// InventoryController.instance, which create real GameObjects), animal.skills
// (testable in isolation but coupled here), TileHere() reaching world.GetTileAt,
// and AnimalController.instance.HasMultipleAnimalsOnTile. These will be covered
// once we have a World/Animal test fixture (Tier 2 of the test plan), where the
// composition logic — efficiency × tool × skill × road × floor × crowding — can be
// exercised end-to-end against a real tile.
[TestFixture]
public class ModifierSystemTests {

    // ── Constants ──────────────────────────────────────────────────────
    // These are pinned values: changing them is a deliberate balance change, not a refactor.
    // Tests document the intended magnitudes so a stray edit triggers a failure.
    [Test]
    public void ToolWorkBonus_IsExpectedValue(){
        Assert.That(ModifierSystem.ToolWorkBonus, Is.EqualTo(1.25f));
    }

    [Test]
    public void BaseAnimalSpeed_IsExpectedValue(){
        Assert.That(ModifierSystem.BaseAnimalSpeed, Is.EqualTo(1.5f));
    }

    [Test]
    public void FloorItemSpeedPenalty_IsExpectedValue(){
        Assert.That(ModifierSystem.FloorItemSpeedPenalty, Is.EqualTo(0.8f));
    }

    [Test]
    public void CrowdingSpeedPenalty_IsExpectedValue(){
        Assert.That(ModifierSystem.CrowdingSpeedPenalty, Is.EqualTo(0.8f));
    }

    // ── Constant invariants ────────────────────────────────────────────
    // Compositional sanity: penalties are penalties (in (0,1)), bonuses are bonuses (>1).
    [Test]
    public void ToolWorkBonus_IsAboveOne(){
        // A tool should speed work up, not slow it down.
        Assert.That(ModifierSystem.ToolWorkBonus, Is.GreaterThan(1f));
    }

    [Test]
    public void BaseAnimalSpeed_IsPositive(){
        // Used as a multiplier on travel speed — must be > 0 or animals never move.
        Assert.That(ModifierSystem.BaseAnimalSpeed, Is.GreaterThan(0f));
    }

    [Test]
    public void FloorItemSpeedPenalty_IsBetweenZeroAndOne(){
        // Multiplicative slowdown: must reduce speed (< 1) but not stop movement (> 0).
        Assert.That(ModifierSystem.FloorItemSpeedPenalty, Is.GreaterThan(0f).And.LessThan(1f));
    }

    [Test]
    public void CrowdingSpeedPenalty_IsBetweenZeroAndOne(){
        // Same contract as FloorItemSpeedPenalty.
        Assert.That(ModifierSystem.CrowdingSpeedPenalty, Is.GreaterThan(0f).And.LessThan(1f));
    }

    [Test]
    public void StackedPenalties_StillPositive(){
        // Floor + crowding combined should leave a non-zero speed multiplier — animals
        // shouldn't get pinned to zero by tile conditions alone.
        float stacked = ModifierSystem.FloorItemSpeedPenalty * ModifierSystem.CrowdingSpeedPenalty;
        Assert.That(stacked, Is.GreaterThan(0f));
        Assert.That(stacked, Is.LessThan(1f));
    }

    // ── GetRecipeOutputMultiplier ──────────────────────────────────────
    [Test]
    public void GetRecipeOutputMultiplier_NoModifiers_ReturnsOne(){
        // Placeholder for future modifiers. While there are no contributors, must be
        // an exact 1f so callers see no output change.
        Assert.That(ModifierSystem.GetRecipeOutputMultiplier(), Is.EqualTo(1f));
    }

    [Test]
    public void GetRecipeOutputMultiplier_IsDeterministic(){
        // No hidden state — repeated calls return the same value.
        float a = ModifierSystem.GetRecipeOutputMultiplier();
        float b = ModifierSystem.GetRecipeOutputMultiplier();
        Assert.That(a, Is.EqualTo(b));
    }

    // ── SkillSet bonus interaction ─────────────────────────────────────
    // GetWorkMultiplier delegates skill scaling to SkillSet.GetBonus. The full path
    // through GetWorkMultiplier needs an Animal (deferred), but we can assert that
    // SkillSet's contribution is the canonical "1 + level × BonusPerLevel" — so any
    // future ModifierSystem refactor that breaks this contract will surface here.
    [Test]
    public void SkillSet_GetBonus_FreshSkillSet_ReturnsOne(){
        SkillSet ss = new SkillSet();
        Assert.That(ss.GetBonus(Skill.Farming), Is.EqualTo(1f));
    }

    [Test]
    public void SkillSet_GetBonus_AfterLevelUp_ReturnsExpectedMultiplier(){
        // Each level grants +5% — this is the multiplier ModifierSystem.GetWorkMultiplier
        // pulls into the work-speed product. Validating the formula here lets us
        // continue to defer the full Animal-dependent path without losing coverage of
        // the skill-side contribution.
        SkillSet ss = new SkillSet();
        // Level 0 → 1 takes 10 XP (XpThreshold(0) = 10).
        UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Log,
            new System.Text.RegularExpressions.Regex("Skill up!"));
        ss.GainXp(Skill.Mining, 10f);
        Assert.That(ss.GetLevel(Skill.Mining), Is.EqualTo(1));
        Assert.That(ss.GetBonus(Skill.Mining), Is.EqualTo(1f + SkillSet.BonusPerLevel));
    }
}

using NUnit.Framework;

// EditMode tests for Eating — per-animal hunger state. Pure C#: no Unity
// singletons, no Animal reference required (World.ticksInDay is a plain static
// field, not the singleton). Covers Eat, Update, SlowUpdate, Fullness/Hungry/
// Efficiency, the timeSinceLastAte/AteRecently bookkeeping, and the starvation
// countdown (starvingTicks / StarvedToDeath).
//
// ── Deferred to integration tests ────────────────────────────────────
// FindFood scoring and the seekFood/hungry-driven AI behaviours live on
// Animal and need a live World — covered separately.
[TestFixture]
public class EatingTests {

    // ── Construction ────────────────────────────────────────────────────
    [Test]
    public void Constructor_DefaultsAreSane(){
        // Defaults are public fields, so I'm pinning them here — if someone
        // tunes them, this test fails loudly so the change is intentional.
        Eating e = new Eating();
        Assert.That(e.maxFood, Is.EqualTo(100f));
        Assert.That(e.food, Is.EqualTo(90f));
        Assert.That(e.hungerRate, Is.EqualTo(0.4f));
        Assert.That(e.timeSinceLastAte, Is.EqualTo(9999f));
    }

    // ── Fullness / Hungry ───────────────────────────────────────────────
    [TestCase(100f, 1f)]
    [TestCase(50f,  0.5f)]
    [TestCase(0f,   0f)]
    public void Fullness_IsFoodOverMax(float food, float expected){
        Eating e = new Eating();
        e.food = food;
        Assert.That(e.Fullness(), Is.EqualTo(expected).Within(0.0001f));
    }

    [Test]
    public void Hungry_BelowSeekThreshold_ReturnsTrue(){
        // seekFoodThreshold is 0.6 — fullness 0.59 should register as hungry.
        Eating e = new Eating();
        e.food = 59f;
        Assert.That(e.Hungry(), Is.True);
    }

    [Test]
    public void Hungry_AtOrAboveSeekThreshold_ReturnsFalse(){
        Eating e = new Eating();
        e.food = 60f; // fullness == seekFoodThreshold; "<" → not hungry
        Assert.That(e.Hungry(), Is.False);
    }

    // ── Efficiency ──────────────────────────────────────────────────────
    [TestCase(100f, 1f)]   // full → no penalty
    [TestCase(60f,  1f)]   // above hungryThreshold (0.5) → still 1
    [TestCase(50.001f, 1f)] // just above → 1
    public void Efficiency_AboveHungryThreshold_IsOne(float food, float expected){
        Eating e = new Eating();
        e.food = food;
        Assert.That(e.Efficiency(), Is.EqualTo(expected).Within(0.0001f));
    }

    [TestCase(50f, 1f)]   // 0.5 fullness * 1.6 + 0.2 = 1.0 (boundary, since "> 0.5" is false at exactly 0.5)
    [TestCase(25f, 0.6f)] // 0.25 * 1.6 + 0.2 = 0.6
    [TestCase(0f,  0.2f)] // floor: 20%
    public void Efficiency_AtOrBelowHungryThreshold_LinearPenalty(float food, float expected){
        Eating e = new Eating();
        e.food = food;
        Assert.That(e.Efficiency(), Is.EqualTo(expected).Within(0.0001f));
    }

    // ── HungerUrgency ─────────────────────────────────────────────────────
    // Contract: 0 at/above seekFoodThreshold (0.6), convex rise to 1 at empty.
    // u = ((0.6 - fullness) / 0.6) ^ 1.5. Convex: slightly-hungry pull is low.
    [TestCase(100f, 0f)]        // full → no pull
    [TestCase(60f,  0f)]        // exactly at seek threshold → no pull
    [TestCase(0f,   1f)]        // empty → max pull (1^1.5 = 1)
    [TestCase(50f,  0.06804f)]  // fullness 0.5: t=(0.6-0.5)/0.6=0.1667, 0.1667^1.5 ≈ 0.0680
    [TestCase(30f,  0.35355f)]  // fullness 0.3: t=0.5, 0.5^1.5 ≈ 0.3536
    public void HungerUrgency_ZeroAboveThreshold_ConvexBelow(float food, float expected){
        Eating e = new Eating();
        e.food = food;
        Assert.That(e.HungerUrgency(), Is.EqualTo(expected).Within(0.001f));
    }

    // HungerUrgency and Hungry must agree on the trigger: urgency > 0 iff Hungry.
    [TestCase(59f)]  // hungry
    [TestCase(60f)]  // not hungry (boundary)
    [TestCase(90f)]  // not hungry
    public void HungerUrgency_PositiveIffHungry(float food){
        Eating e = new Eating();
        e.food = food;
        Assert.That(e.HungerUrgency() > 0f, Is.EqualTo(e.Hungry()));
    }

    // Convexity invariant: a slightly-hungry mouse's pull should be well below the
    // linear midpoint, so it prefers finishing nearby work over topping up.
    [Test]
    public void HungerUrgency_IsConvex_SlightlyHungryPullIsLow(){
        Eating e = new Eating();
        e.food = 45f; // fullness 0.45, t = 0.25 → 0.25^1.5 = 0.125, below the linear 0.25
        Assert.That(e.HungerUrgency(), Is.LessThan(0.25f));
    }

    // ── Eat ─────────────────────────────────────────────────────────────
    [Test]
    public void Eat_RestoresFood(){
        Eating e = new Eating();
        e.food = 50f;
        e.Eat(20f);
        Assert.That(e.food, Is.EqualTo(70f).Within(0.0001f));
    }

    [Test]
    public void Eat_ClampsToMaxFood(){
        Eating e = new Eating();
        e.food = 90f;
        e.Eat(50f);
        Assert.That(e.food, Is.EqualTo(e.maxFood));
    }

    [Test]
    public void Eat_ResetsTimeSinceLastAte(){
        Eating e = new Eating();
        e.timeSinceLastAte = 5000f;
        e.Eat(10f);
        Assert.That(e.timeSinceLastAte, Is.EqualTo(0f));
    }

    [Test]
    public void Eat_ZeroFoodValue_StillResetsTimer(){
        // Inedible items shouldn't be eaten in the first place, but if a 0-value
        // item somehow reaches Eat() it shouldn't crash — and the timer reset
        // is harmless on its own. Documenting the contract.
        Eating e = new Eating();
        float before = e.food;
        e.timeSinceLastAte = 999f;
        e.Eat(0f);
        Assert.That(e.food, Is.EqualTo(before));
        Assert.That(e.timeSinceLastAte, Is.EqualTo(0f));
    }

    // ── Update (hunger depletion) ───────────────────────────────────────
    [Test]
    public void Update_DepletesByHungerRate(){
        Eating e = new Eating();
        e.food = 50f;
        e.hungerRate = 0.4f;
        e.Update(1f);
        Assert.That(e.food, Is.EqualTo(49.6f).Within(0.0001f));
    }

    [Test]
    public void Update_ScalesByDt(){
        Eating e = new Eating();
        e.food = 50f;
        e.hungerRate = 0.5f;
        e.Update(4f);
        Assert.That(e.food, Is.EqualTo(48f).Within(0.0001f));
    }

    [Test]
    public void Update_ClampsAtZero(){
        // Defensive: hunger should never produce a negative food value, even
        // with absurd dt. AI logic elsewhere may divide by it.
        Eating e = new Eating();
        e.food = 1f;
        e.hungerRate = 1f;
        e.Update(100f);
        Assert.That(e.food, Is.EqualTo(0f));
    }

    // ── SlowUpdate (timer accumulation) ────────────────────────────────
    [Test]
    public void SlowUpdate_AccumulatesTimeSinceLastAte(){
        Eating e = new Eating();
        e.timeSinceLastAte = 0f;
        e.SlowUpdate(10f);
        e.SlowUpdate(10f);
        Assert.That(e.timeSinceLastAte, Is.EqualTo(20f));
    }

    [Test]
    public void AteRecently_WithinFiveMinutes_ReturnsTrue(){
        Eating e = new Eating();
        e.timeSinceLastAte = 299f;
        Assert.That(e.AteRecently(), Is.True);
    }

    [Test]
    public void AteRecently_BeyondFiveMinutes_ReturnsFalse(){
        Eating e = new Eating();
        e.timeSinceLastAte = 300f;
        Assert.That(e.AteRecently(), Is.False);
    }

    [Test]
    public void EatThenUpdate_RoundTrip(){
        // Eat fully, deplete a known amount, confirm fullness is what we expect.
        Eating e = new Eating();
        e.food = 0f;
        e.hungerRate = 0.4f;
        e.Eat(80f);                 // food = 80
        e.Update(10f);              // -4 → 76
        Assert.That(e.food, Is.EqualTo(76f).Within(0.0001f));
        Assert.That(e.timeSinceLastAte, Is.EqualTo(0f));
    }

    // ── Starvation ──────────────────────────────────────────────────────
    // A mouse held at zero food accumulates starvingTicks; a full in-game day
    // of them is fatal (StarvedToDeath). Eat() — or any tick with food left —
    // wipes the countdown.
    [Test]
    public void Constructor_StarvingTicksStartsAtZero(){
        Assert.That(new Eating().starvingTicks, Is.EqualTo(0));
    }

    [Test]
    public void Update_AtZeroFood_AccumulatesStarvingTicks(){
        Eating e = new Eating();
        e.food = 0.4f;
        e.hungerRate = 0.4f;
        e.Update(1f); // food → 0  → starvingTicks 1
        e.Update(1f); // stays 0   → 2
        e.Update(1f); // stays 0   → 3
        Assert.That(e.starvingTicks, Is.EqualTo(3));
    }

    [Test]
    public void Update_WithFoodRemaining_KeepsStarvingTicksZero(){
        Eating e = new Eating();
        e.food = 50f;
        e.Update(1f);
        Assert.That(e.starvingTicks, Is.EqualTo(0));
    }

    [Test]
    public void Update_FoodRestored_ResetsStarvingTicks(){
        // A mouse that gets food after starving has its countdown wiped on the
        // next tick that finds food remaining.
        Eating e = new Eating();
        e.food = 0f;
        e.Update(1f);  // starvingTicks → 1
        e.food = 50f;
        e.Update(1f);  // food remaining → reset
        Assert.That(e.starvingTicks, Is.EqualTo(0));
    }

    [Test]
    public void Eat_ResetsStarvingTicks(){
        Eating e = new Eating();
        e.food = 0f;
        e.Update(1f); // starvingTicks → 1
        e.Update(1f); // → 2
        e.Eat(30f);
        Assert.That(e.starvingTicks, Is.EqualTo(0));
    }

    [Test]
    public void StarvedToDeath_FalseBeforeAFullDay(){
        Eating e = new Eating();
        e.starvingTicks = World.ticksInDay - 1;
        Assert.That(e.StarvedToDeath(), Is.False);
    }

    [Test]
    public void StarvedToDeath_TrueAtAFullDayAtZeroFood(){
        Eating e = new Eating();
        e.starvingTicks = World.ticksInDay;
        Assert.That(e.StarvedToDeath(), Is.True);
    }
}

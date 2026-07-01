using NUnit.Framework;

// EditMode tests for Eeping — per-animal sleep/fatigue state. Pure C#: no
// Unity singletons or Animal reference required. Covers ShouldSleep across
// the day/night threshold split, Eep recovery (atHome vs outside) and its
// max clamp, Update depletion and its zero-floor clamp, and Efficiency at
// the threshold boundary.
//
// Note: tireRate / eepRate / outsideEepRate are STATIC fields. Tests that
// mutate them must restore the original value in [TearDown] so they don't
// leak between fixtures. (Currently no test mutates them — the defaults are
// what we exercise — but flagging it for future authors.)
[TestFixture]
public class EepingTests {

    // ── Eepness ────────────────────────────────────────────────────────
    [TestCase(100f, 1f)]
    [TestCase(50f,  0.5f)]
    [TestCase(0f,   0f)]
    public void Eepness_IsEepOverMax(float eep, float expected){
        Eeping e = new Eeping();
        e.eep = eep;
        Assert.That(e.Eepness(), Is.EqualTo(expected).Within(0.0001f));
    }

    // ── ShouldSleep ────────────────────────────────────────────────────
    // Contract: sleep when e/maxEep < exhaustedSleepThreshold (0.4) + urgency * bedtimeMaxBoost (0.5).
    // Range: [0.4, 0.9]. bedtimeUrgency ramps 0→1 across the evening so mice peel off to
    // bed at staggered moments; the 0.9 ceiling keeps fully-rested mice awake at deep night.
    [TestCase(39f, 0f,    true)]   // fatigued, daytime → emergency nap
    [TestCase(41f, 0f,    false)]  // mid-tired daytime → keep working (e=0.41 ≥ 0.4)
    [TestCase(90f, 0f,    false)]  // rested daytime → working
    [TestCase(60f, 0.5f,  true)]   // mid bedtime: threshold=0.65, e=0.60 → sleeps
    [TestCase(70f, 0.5f,  false)]  // mid bedtime: threshold=0.65, e=0.70 → stays up
    [TestCase(85f, 1f,    true)]   // deep night: threshold=0.9, e=0.85 → sleeps
    [TestCase(95f, 1f,    false)]  // deep night: rested mouse (e=0.95) stays up regardless
    // No exact-boundary cases (e == threshold): C# permits float intermediates at
    // higher precision, so `e < threshold` is unspecified at the bit level when the
    // two are equal. The off-boundary cases above pin the contract on both sides.
    public void ShouldSleep_AdditiveBedtimeAndExhaustion(float eep, float bedtimeUrgency, bool expected){
        Eeping e = new Eeping();
        e.eep = eep;
        Assert.That(e.ShouldSleep(bedtimeUrgency), Is.EqualTo(expected));
    }

    // ── SleepUrgency ───────────────────────────────────────────────────
    // Contract: 0 at/above the (bedtime-shifted) sleep threshold, linear pull below it.
    // threshold = 0.4 + bedtimeUrgency * 0.5. Mirrors ShouldSleep's trigger boundary.
    [TestCase(40f, 0f,   0f)]      // e=0.40 == daytime threshold → no pull
    [TestCase(90f, 0f,   0f)]      // rested daytime → no pull
    [TestCase(0f,  0f,   1f)]      // empty daytime: (0.4-0)/0.4 = 1
    [TestCase(20f, 0f,   0.5f)]    // e=0.20, threshold 0.4: (0.4-0.2)/0.4 = 0.5
    [TestCase(65f, 0.5f, 0f)]      // mid-bedtime threshold 0.65, e=0.65 → 0
    [TestCase(40f, 0.5f, 0.3846154f)] // threshold 0.65, e=0.40: (0.65-0.40)/0.65
    [TestCase(0f,  1f,   1f)]      // deep night, empty: (0.9-0)/0.9 = 1
    public void SleepUrgency_ZeroAboveThreshold_LinearBelow(float eep, float bedtime, float expected){
        Eeping e = new Eeping();
        e.eep = eep;
        Assert.That(e.SleepUrgency(bedtime), Is.EqualTo(expected).Within(0.0001f));
    }

    // SleepUrgency and ShouldSleep must agree on the trigger: urgency > 0 iff ShouldSleep.
    [TestCase(39f, 0f)]
    [TestCase(41f, 0f)]
    [TestCase(60f, 0.5f)]
    [TestCase(85f, 1f)]
    public void SleepUrgency_PositiveIffShouldSleep(float eep, float bedtime){
        Eeping e = new Eeping();
        e.eep = eep;
        Assert.That(e.SleepUrgency(bedtime) > 0f, Is.EqualTo(e.ShouldSleep(bedtime)));
    }

    // ── Efficiency ─────────────────────────────────────────────────────
    [TestCase(100f, 1f)]
    [TestCase(60f,  1f)]
    [TestCase(50.001f, 1f)]
    public void Efficiency_AboveHalf_IsOne(float eep, float expected){
        Eeping e = new Eeping();
        e.eep = eep;
        Assert.That(e.Efficiency(), Is.EqualTo(expected).Within(0.0001f));
    }

    // At or below half-eep, efficiency drops linearly from 1.0 (at the half boundary) to a floor at
    // empty. Asserts the *shape* — boundary continuity, linearity, and a sub-1-but-positive floor —
    // so it survives retuning the floor/slope (inline literals in Efficiency(), currently a 50% floor).
    [Test]
    public void Efficiency_AtOrBelowHalf_LinearPenaltyToFloor(){
        Eeping e = new Eeping();
        e.eep = 0f;  float atEmpty   = e.Efficiency();
        e.eep = 25f; float atQuarter = e.Efficiency(); // midpoint of [0, 0.5·maxEep]
        e.eep = 50f; float atHalf    = e.Efficiency(); // eep/maxEep == 0.5 boundary

        Assert.That(atHalf, Is.EqualTo(1f).Within(0.0001f), "continuous with the >0.5 branch");
        Assert.That(atEmpty, Is.LessThan(1f).And.GreaterThan(0f), "a penalty floor, but never zero");
        Assert.That(atQuarter, Is.EqualTo((atEmpty + 1f) / 2f).Within(0.0001f), "linear between floor and 1");
    }

    // ── Eep (recovery) ─────────────────────────────────────────────────
    // Expected values derive from the live rate constants so they verify the *behaviour*
    // (recovery = rate × t, at-home vs outside picks the right rate, additive onto current eep)
    // without re-breaking every time the rates are retuned.
    [Test]
    public void Eep_AtHome_RecoversAtEepRate(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Eep(t: 1f, atHome: true); // +eepRate
        Assert.That(e.eep, Is.EqualTo(50f + Eeping.eepRate).Within(0.0001f));
    }

    [Test]
    public void Eep_Outside_RecoversAtOutsideRate(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Eep(t: 1f, atHome: false); // +outsideEepRate
        Assert.That(e.eep, Is.EqualTo(50f + Eeping.outsideEepRate).Within(0.0001f));
    }

    [Test]
    public void Eep_ScalesByDt(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Eep(t: 5f, atHome: true); // +eepRate × 5
        Assert.That(e.eep, Is.EqualTo(50f + Eeping.eepRate * 5f).Within(0.0001f));
    }

    [Test]
    public void Eep_ClampsAtMax(){
        // Eep() clamps at maxEep. Recovery is ticked wall-clock (Animal.HandleNeeds) while
        // the wake-up check is energy-gated (HandleEeping); a low-efficiency sleeper's
        // recovery can outrun the wake check, so without the clamp eep would drift past cap.
        Eeping e = new Eeping();
        e.eep = 99f;
        e.Eep(t: 10f, atHome: true); // +10, clamped → 100
        Assert.That(e.eep, Is.EqualTo(e.maxEep).Within(0.0001f));
    }

    // ── Update (fatigue) ───────────────────────────────────────────────
    // Expected values derive from tireRate so they verify depletion = tireRate × t (and dt
    // scaling) without re-breaking on a retune.
    [Test]
    public void Update_DepletesAtTireRate(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Update(1f); // -tireRate
        Assert.That(e.eep, Is.EqualTo(50f - Eeping.tireRate).Within(0.0001f));
    }

    [Test]
    public void Update_ScalesByDt(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Update(10f); // -tireRate × 10
        Assert.That(e.eep, Is.EqualTo(50f - Eeping.tireRate * 10f).Within(0.0001f));
    }

    [Test]
    public void Update_ClampsAtZero(){
        Eeping e = new Eeping();
        e.eep = 0.05f;
        e.Update(1f); // 0.05 - tireRate < 0 → clamp 0
        Assert.That(e.eep, Is.EqualTo(0f));
    }

    [Test]
    public void Update_FromZero_StaysAtZero(){
        Eeping e = new Eeping();
        e.eep = 0f;
        e.Update(100f);
        Assert.That(e.eep, Is.EqualTo(0f));
    }

    // ── Round-trip ─────────────────────────────────────────────────────
    [Test]
    public void EepThenUpdate_RoundTrip(){
        Eeping e = new Eeping();
        e.eep = 0f;
        e.Eep(t: 20f, atHome: true);  // +eepRate × 20
        e.Update(50f);                 // -tireRate × 50
        Assert.That(e.eep, Is.EqualTo(Eeping.eepRate * 20f - Eeping.tireRate * 50f).Within(0.0001f));
    }
}

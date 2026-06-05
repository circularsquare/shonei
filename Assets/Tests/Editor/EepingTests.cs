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

    // ── Construction ────────────────────────────────────────────────────
    [Test]
    public void Constructor_DefaultsAreSane(){
        // Pinning defaults so accidental tuning shows up in CI.
        Eeping e = new Eeping();
        Assert.That(e.maxEep, Is.EqualTo(100f));
        Assert.That(e.eep, Is.EqualTo(90f));
        Assert.That(Eeping.tireRate, Is.EqualTo(0.2f));
        Assert.That(Eeping.eepRate, Is.EqualTo(1f));
        Assert.That(Eeping.outsideEepRate, Is.EqualTo(0.7f));
    }

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

    [TestCase(50f, 1f)]   // boundary: "> 0.5" false → linear branch returns 1.0 here
    [TestCase(25f, 0.6f)] // 0.25 * 1.6 + 0.2 = 0.6
    [TestCase(0f,  0.2f)] // 20% floor
    public void Efficiency_AtOrBelowHalf_LinearPenalty(float eep, float expected){
        Eeping e = new Eeping();
        e.eep = eep;
        Assert.That(e.Efficiency(), Is.EqualTo(expected).Within(0.0001f));
    }

    // ── Eep (recovery) ─────────────────────────────────────────────────
    [Test]
    public void Eep_AtHome_RecoversAtEepRate(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Eep(t: 1f, atHome: true); // +1
        Assert.That(e.eep, Is.EqualTo(51f).Within(0.0001f));
    }

    [Test]
    public void Eep_Outside_RecoversAtOutsideRate(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Eep(t: 1f, atHome: false); // +0.7
        Assert.That(e.eep, Is.EqualTo(50.7f).Within(0.0001f));
    }

    [Test]
    public void Eep_ScalesByDt(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Eep(t: 5f, atHome: true); // +5
        Assert.That(e.eep, Is.EqualTo(55f).Within(0.0001f));
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
    [Test]
    public void Update_DepletesAtTireRate(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Update(1f);
        Assert.That(e.eep, Is.EqualTo(49.8f).Within(0.0001f));
    }

    [Test]
    public void Update_ScalesByDt(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Update(10f); // -2.0
        Assert.That(e.eep, Is.EqualTo(48f).Within(0.0001f));
    }

    [Test]
    public void Update_ClampsAtZero(){
        Eeping e = new Eeping();
        e.eep = 0.05f;
        e.Update(1f); // 0.05 - 0.2 = -0.15 → clamp 0
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
        e.Eep(t: 20f, atHome: true);  // +20 → 20
        e.Update(50f);                 // -10 → 10
        Assert.That(e.eep, Is.EqualTo(10f).Within(0.0001f));
    }
}

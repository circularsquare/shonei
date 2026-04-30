using NUnit.Framework;

// EditMode tests for Eeping — per-animal sleep/fatigue state. Pure C#: no
// Unity singletons or Animal reference required. Covers ShouldSleep across
// the day/night threshold split, Eep recovery (atHome vs outside), Update
// depletion, the zero-floor clamp, and Efficiency at the threshold boundary.
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
        Assert.That(Eeping.tireRate, Is.EqualTo(0.1f));
        Assert.That(Eeping.eepRate, Is.EqualTo(2f));
        Assert.That(Eeping.outsideEepRate, Is.EqualTo(1f));
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
    [Test]
    public void ShouldSleep_BelowExhaustedThreshold_AlwaysSleeps(){
        // Below 0.5 fullness, sleep regardless of time of day.
        Eeping e = new Eeping();
        e.eep = 49f;
        Assert.That(e.ShouldSleep(isNighttime: false), Is.True);
        Assert.That(e.ShouldSleep(isNighttime: true), Is.True);
    }

    [Test]
    public void ShouldSleep_AtNightBelowNightThreshold_Sleeps(){
        // Tired-ish (0.84) at night → bed time.
        Eeping e = new Eeping();
        e.eep = 84f;
        Assert.That(e.ShouldSleep(isNighttime: true), Is.True);
    }

    [Test]
    public void ShouldSleep_DuringDayAboveExhaustedThreshold_DoesNotSleep(){
        // Tired-ish but daytime → keep working until exhausted.
        Eeping e = new Eeping();
        e.eep = 60f; // above 0.5, below 0.85
        Assert.That(e.ShouldSleep(isNighttime: false), Is.False);
    }

    [Test]
    public void ShouldSleep_AtNightAboveNightThreshold_DoesNotSleep(){
        // Fully rested mouse stays awake even at night.
        Eeping e = new Eeping();
        e.eep = 90f; // above 0.85
        Assert.That(e.ShouldSleep(isNighttime: true), Is.False);
    }

    [TestCase(85f, true,  false)] // exactly at 0.85 night threshold — "<" is strict, so not sleeping
    [TestCase(85f, false, false)] // daytime: above exhausted threshold, no sleep
    [TestCase(50f, false, false)] // exactly at 0.5 exhausted threshold (daytime) — "<" is strict
    [TestCase(50f, true,  true)]  // exactly at 0.5 daytime → false; but at NIGHT, 0.5 < 0.85 fires
    public void ShouldSleep_AtThresholdBoundary_IsStrictLessThan(float eep, bool night, bool expected){
        Eeping e = new Eeping();
        e.eep = eep;
        Assert.That(e.ShouldSleep(night), Is.EqualTo(expected));
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
        e.Eep(t: 1f, atHome: true); // +2
        Assert.That(e.eep, Is.EqualTo(52f).Within(0.0001f));
    }

    [Test]
    public void Eep_Outside_RecoversAtOutsideRate(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Eep(t: 1f, atHome: false); // +1
        Assert.That(e.eep, Is.EqualTo(51f).Within(0.0001f));
    }

    [Test]
    public void Eep_ScalesByDt(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Eep(t: 5f, atHome: true); // +10
        Assert.That(e.eep, Is.EqualTo(60f).Within(0.0001f));
    }

    [Test]
    public void Eep_DoesNotClampAboveMax(){
        // Surprising: Eep() lets eep exceed maxEep. ShouldSleep still works
        // (it just becomes "false" forever), but this is worth pinning so we
        // notice if a clamp is added or required later.
        Eeping e = new Eeping();
        e.eep = 99f;
        e.Eep(t: 10f, atHome: true); // +20 → 119
        Assert.That(e.eep, Is.GreaterThan(e.maxEep));
    }

    // ── Update (fatigue) ───────────────────────────────────────────────
    [Test]
    public void Update_DepletesAtTireRate(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Update(1f);
        Assert.That(e.eep, Is.EqualTo(49.9f).Within(0.0001f));
    }

    [Test]
    public void Update_ScalesByDt(){
        Eeping e = new Eeping();
        e.eep = 50f;
        e.Update(10f); // -1.0
        Assert.That(e.eep, Is.EqualTo(49f).Within(0.0001f));
    }

    [Test]
    public void Update_ClampsAtZero(){
        Eeping e = new Eeping();
        e.eep = 0.05f;
        e.Update(1f); // 0.05 - 0.1 = -0.05 → clamp 0
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
        e.Eep(t: 10f, atHome: true);  // +20 → 20
        e.Update(50f);                 // -5 → 15
        Assert.That(e.eep, Is.EqualTo(15f).Within(0.0001f));
    }
}

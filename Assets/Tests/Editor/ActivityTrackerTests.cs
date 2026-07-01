using NUnit.Framework;

// EditMode tests for ActivityTracker: the recency-weighted per-mouse time-per-state
// record behind the population panel's activity bar. Pure C# — ActivityTracker
// constructs directly and reads only World.ticksInDay (a static default), so no Unity
// scaffolding is needed.
//
// Half-life is ~2 in-game days = 2 * World.ticksInDay = 960 ticks (ticksInDay default 480).
[TestFixture]
public class ActivityTrackerTests {

    const int HalfLifeTicks = 960; // 2 * World.ticksInDay (default 480)

    static void TickN(ActivityTracker t, Animal.AnimalState state, int n){
        for (int i = 0; i < n; i++) t.Tick(state);
    }

    // ── Count / enum domain ────────────────────────────────────────────
    [Test]
    public void Count_MatchesEnumLength(){
        Assert.That(ActivityTracker.Count, Is.EqualTo(System.Enum.GetValues(typeof(ActivityGroup)).Length));
        Assert.That(ActivityTracker.Count, Is.EqualTo(5)); // Working, Walking, Leisure, Idle, Sleep
    }

    // ── GroupFor: raw state → display bucket ────────────────────────────
    [TestCase(Animal.AnimalState.Working,   ActivityGroup.Working)]
    [TestCase(Animal.AnimalState.Moving,    ActivityGroup.Walking)]
    [TestCase(Animal.AnimalState.Traveling, ActivityGroup.Walking)]  // off-screen journey folds into walking
    [TestCase(Animal.AnimalState.Leisuring, ActivityGroup.Leisure)]
    [TestCase(Animal.AnimalState.Eeping,    ActivityGroup.Sleep)]
    [TestCase(Animal.AnimalState.Idle,      ActivityGroup.Idle)]
    [TestCase(Animal.AnimalState.Falling,   ActivityGroup.Idle)]     // involuntary/brief folds into idle
    public void GroupFor_MapsStateToBucket(Animal.AnimalState state, ActivityGroup expected){
        Assert.That(ActivityTracker.GroupFor(state), Is.EqualTo(expected));
    }

    [Test]
    public void GroupFor_CoversEveryState(){
        // Every AnimalState must map without throwing — guards against a new state
        // silently hitting the default branch unnoticed.
        foreach (Animal.AnimalState s in System.Enum.GetValues(typeof(Animal.AnimalState)))
            Assert.DoesNotThrow(() => ActivityTracker.GroupFor(s), $"{s} unmapped");
    }

    // ── Defaults ────────────────────────────────────────────────────────
    [Test]
    public void FreshTracker_AllFractionsZero(){
        // Sum is 0 before any ticks; Fraction must guard the divide and return 0, not NaN.
        ActivityTracker t = new ActivityTracker();
        foreach (ActivityGroup g in System.Enum.GetValues(typeof(ActivityGroup)))
            Assert.That(t.Fraction(g), Is.EqualTo(0f), $"{g} fresh");
    }

    // ── Convergence under a constant state ──────────────────────────────
    [Test]
    public void ConstantState_ConvergesToFullOccupancy(){
        // Many half-lives of pure Working → Working fraction ≈ 1, the rest ≈ 0.
        ActivityTracker t = new ActivityTracker();
        TickN(t, Animal.AnimalState.Working, HalfLifeTicks * 12);
        Assert.That(t.Fraction(ActivityGroup.Working), Is.GreaterThan(0.99f));
        Assert.That(t.Fraction(ActivityGroup.Idle),    Is.LessThan(0.01f));
        Assert.That(t.Fraction(ActivityGroup.Sleep),   Is.LessThan(0.01f));
    }

    // ── Self-normalization ──────────────────────────────────────────────
    [Test]
    public void Fractions_SumToOne_OnceWarmed(){
        // Mixed history; the five fractions should still partition ≈ 1.
        ActivityTracker t = new ActivityTracker();
        TickN(t, Animal.AnimalState.Working,   500);
        TickN(t, Animal.AnimalState.Idle,      300);
        TickN(t, Animal.AnimalState.Eeping,    400);
        TickN(t, Animal.AnimalState.Moving,    200);
        TickN(t, Animal.AnimalState.Leisuring, 100);
        float sum = 0f;
        foreach (ActivityGroup g in System.Enum.GetValues(typeof(ActivityGroup)))
            sum += t.Fraction(g);
        Assert.That(sum, Is.EqualTo(1f).Within(1e-4f));
    }

    // ── Recency weighting ───────────────────────────────────────────────
    [Test]
    public void RecentSwitch_NewStateDominates(){
        // Long idle history, then ~2 half-lives of work: recent work should outweigh the
        // decayed idle, demonstrating the metric tracks "lately" not "lifetime".
        ActivityTracker t = new ActivityTracker();
        TickN(t, Animal.AnimalState.Idle,    HalfLifeTicks * 5); // saturate idle
        Assert.That(t.Fraction(ActivityGroup.Idle), Is.GreaterThan(0.99f));
        TickN(t, Animal.AnimalState.Working, HalfLifeTicks * 2); // ~2 half-lives of work
        Assert.That(t.Fraction(ActivityGroup.Working), Is.GreaterThan(t.Fraction(ActivityGroup.Idle)));
        Assert.That(t.Fraction(ActivityGroup.Working), Is.GreaterThan(0.5f));
    }

    // ── Serialize / Deserialize ────────────────────────────────────────
    [Test]
    public void Serialize_ReturnsCloneNotAlias(){
        ActivityTracker t = new ActivityTracker();
        TickN(t, Animal.AnimalState.Working, 100);
        float[] snap = t.Serialize();
        float before = t.Fraction(ActivityGroup.Working);
        snap[(int)ActivityGroup.Working] = 9999f; // mutate the returned array
        Assert.That(t.Fraction(ActivityGroup.Working), Is.EqualTo(before).Within(1e-6f));
    }

    [Test]
    public void Deserialize_RoundTrip_PreservesFractions(){
        ActivityTracker a = new ActivityTracker();
        TickN(a, Animal.AnimalState.Working, 600);
        TickN(a, Animal.AnimalState.Idle,    200);
        ActivityTracker b = new ActivityTracker();
        b.Deserialize(a.Serialize());
        foreach (ActivityGroup g in System.Enum.GetValues(typeof(ActivityGroup)))
            Assert.That(b.Fraction(g), Is.EqualTo(a.Fraction(g)).Within(1e-6f), $"{g}");
    }

    [Test]
    public void Deserialize_Null_NoOp(){
        // Old saves have no activity array → stays zeroed, no throw.
        ActivityTracker t = new ActivityTracker();
        Assert.DoesNotThrow(() => t.Deserialize(null));
        Assert.That(t.Fraction(ActivityGroup.Idle), Is.EqualTo(0f));
    }

    [Test]
    public void Deserialize_ShorterArray_LeavesTailAtDefault(){
        float[] shortArr = new float[]{ 0.5f, 0.5f }; // only Working, Walking
        ActivityTracker t = new ActivityTracker();
        t.Deserialize(shortArr);
        // Working : Walking is 1:1, the rest 0 → each of the two is 0.5.
        Assert.That(t.Fraction(ActivityGroup.Working), Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(t.Fraction(ActivityGroup.Walking), Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(t.Fraction(ActivityGroup.Sleep),   Is.EqualTo(0f));
    }

    [Test]
    public void Deserialize_LongerArray_Truncates(){
        int n = ActivityTracker.Count;
        float[] longArr = new float[n + 3];
        for (int i = 0; i < longArr.Length; i++) longArr[i] = 1f; // uniform
        ActivityTracker t = new ActivityTracker();
        Assert.DoesNotThrow(() => t.Deserialize(longArr));
        // Only the first n buckets are kept; uniform → each fraction = 1/n.
        Assert.That(t.Fraction(ActivityGroup.Working), Is.EqualTo(1f / n).Within(1e-6f));
    }
}

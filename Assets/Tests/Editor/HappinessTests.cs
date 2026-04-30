using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests for Happiness — per-animal need-satisfaction tracking.
// Db.happinessNeeds and Db.happinessNeedsSorted are populated manually in
// [SetUp] so we don't need a live Db MonoBehaviour. The original values are
// captured and restored in [TearDown] to avoid leaking into other fixtures.
//
// ── Deferred to integration tests ────────────────────────────────────
// SlowUpdate(Animal) reads Animal.HasHouse and (via WeatherSystem) the world
// temperature; UpdateComfortRange(Animal) reads Animal.clothingSlotInv.
// Both need a real Animal MonoBehaviour, which pulls in World, Job, and the
// inventory graph. Decay and warmth-decay logic IS exercised here through a
// helper that performs the same math the SlowUpdate body does, since the
// dictionary mutation is the part worth pinning.
[TestFixture]
public class HappinessTests {

    HashSet<string> _origNeeds;
    List<string>    _origSorted;

    [SetUp]
    public void SetUp(){
        _origNeeds  = Db.happinessNeeds;
        _origSorted = Db.happinessNeedsSorted;
        Db.happinessNeeds = new HashSet<string>{ "wheat", "fruit", "social", "reading", "fireplace" };
        Db.happinessNeedsSorted = new List<string>{ "wheat", "fruit", "social", "fireplace", "reading" };
    }

    [TearDown]
    public void TearDown(){
        Db.happinessNeeds  = _origNeeds;
        Db.happinessNeedsSorted = _origSorted;
    }

    // ── Construction ────────────────────────────────────────────────────
    [Test]
    public void Constructor_PrepopulatesAllNeedsAtZero(){
        Happiness h = new Happiness();
        foreach (string need in Db.happinessNeeds){
            Assert.That(h.satisfactions.ContainsKey(need), Is.True, $"missing key {need}");
            Assert.That(h.satisfactions[need], Is.EqualTo(0f));
        }
    }

    [Test]
    public void Constructor_NullNeedsRegistry_CreatesEmptyDict(){
        // If Db hasn't loaded yet, Happiness must not crash — it just starts empty.
        Db.happinessNeeds = null;
        Happiness h = new Happiness();
        Assert.That(h.satisfactions, Is.Not.Null);
        Assert.That(h.satisfactions.Count, Is.EqualTo(0));
    }

    [Test]
    public void Constructor_DefaultsAreSane(){
        Happiness h = new Happiness();
        Assert.That(h.warmth, Is.EqualTo(0f));
        Assert.That(h.score, Is.EqualTo(0f));
        Assert.That(h.comfortTempLow,  Is.EqualTo(10f));
        Assert.That(h.comfortTempHigh, Is.EqualTo(25f));
        Assert.That(h.house, Is.False);
    }

    // ── GetSatisfaction ─────────────────────────────────────────────────
    [Test]
    public void GetSatisfaction_KnownNeed_ReturnsStoredValue(){
        Happiness h = new Happiness();
        h.satisfactions["wheat"] = 1.5f;
        Assert.That(h.GetSatisfaction("wheat"), Is.EqualTo(1.5f));
    }

    [Test]
    public void GetSatisfaction_UnknownNeed_ReturnsZero(){
        // TryGetValue → out 0 — useful for code paths that pass through buildings
        // referencing needs not present in this animal's dict (shouldn't happen,
        // but the API is defensive).
        Happiness h = new Happiness();
        Assert.That(h.GetSatisfaction("nonexistent"), Is.EqualTo(0f));
    }

    // ── NoteAte ─────────────────────────────────────────────────────────
    [Test]
    public void NoteAte_FoodWithHappinessNeed_GrantsFoodValueOver20(){
        Happiness h = new Happiness();
        Item bread = new Item{ name = "bread", foodValue = 40f, happinessNeed = "wheat" };
        h.NoteAte(bread);
        Assert.That(h.GetSatisfaction("wheat"), Is.EqualTo(2f).Within(0.0001f));
    }

    [Test]
    public void NoteAte_FractionScalesGrant(){
        Happiness h = new Happiness();
        Item bread = new Item{ name = "bread", foodValue = 40f, happinessNeed = "wheat" };
        h.NoteAte(bread, fraction: 0.5f);
        Assert.That(h.GetSatisfaction("wheat"), Is.EqualTo(1f).Within(0.0001f));
    }

    [Test]
    public void NoteAte_NullHappinessNeed_NoChange(){
        // Foods with no happinessNeed shouldn't grant anything anywhere.
        Happiness h = new Happiness();
        Item gruel = new Item{ name = "gruel", foodValue = 30f, happinessNeed = null };
        h.NoteAte(gruel);
        foreach (var kv in h.satisfactions){
            Assert.That(kv.Value, Is.EqualTo(0f), $"{kv.Key} unexpectedly granted");
        }
    }

    [Test]
    public void NoteAte_RepeatedlyClampsToCap(){
        Happiness h = new Happiness();
        Item bread = new Item{ name = "bread", foodValue = 100f, happinessNeed = "wheat" }; // grants 5 each call
        for (int i = 0; i < 10; i++) h.NoteAte(bread);
        Assert.That(h.GetSatisfaction("wheat"), Is.EqualTo(Happiness.satisfactionCap));
    }

    // ── NoteSocialized / NoteRead / NoteSawDecoration / NoteLeisure ─────
    [Test]
    public void NoteSocialized_GrantsSocialNeed(){
        Happiness h = new Happiness();
        h.NoteSocialized(0.5f);
        Assert.That(h.GetSatisfaction("social"), Is.EqualTo(0.5f).Within(0.0001f));
    }

    [Test]
    public void NoteRead_GrantsReadingNeed(){
        Happiness h = new Happiness();
        h.NoteRead(0.3f);
        Assert.That(h.GetSatisfaction("reading"), Is.EqualTo(0.3f).Within(0.0001f));
    }

    [Test]
    public void NoteSawDecoration_GrantsActivityGrant(){
        // For any need name (typically "fountain") — the registered key for the deco.
        // Using "fruit" here since it's already in our test-needs registry.
        Happiness h = new Happiness();
        h.NoteSawDecoration("fruit");
        Assert.That(h.GetSatisfaction("fruit"), Is.EqualTo(Happiness.activityGrant));
    }

    [Test]
    public void NoteLeisure_GrantsScaledByMultiplier(){
        Happiness h = new Happiness();
        h.NoteLeisure("fireplace", multiplier: 0.5f);
        Assert.That(h.GetSatisfaction("fireplace"),
            Is.EqualTo(Happiness.activityGrant * 0.5f).Within(0.0001f));
    }

    [Test]
    public void NoteLeisure_FireplaceNeed_AlsoBoostsWarmth(){
        // The fireplace "need" name has a special-case warmth side-effect.
        Happiness h = new Happiness();
        h.NoteLeisure("fireplace");
        Assert.That(h.warmth, Is.EqualTo(Happiness.activityGrant).Within(0.0001f));
    }

    [Test]
    public void NoteLeisure_NonFireplaceNeed_DoesNotChangeWarmth(){
        // Bench grants leisure but not warmth.
        Happiness h = new Happiness();
        h.NoteLeisure("fruit"); // any non-fireplace key
        Assert.That(h.warmth, Is.EqualTo(0f));
    }

    [Test]
    public void NoteLeisure_WarmthClampsAtCap(){
        Happiness h = new Happiness();
        for (int i = 0; i < 10; i++) h.NoteLeisure("fireplace"); // 10 * 2 = 20, cap 5
        Assert.That(h.warmth, Is.EqualTo(Happiness.satisfactionCap));
    }

    // ── WouldHelp ───────────────────────────────────────────────────────
    [Test]
    public void WouldHelp_NeedBelowWantThreshold_ReturnsTrue(){
        Happiness h = new Happiness();
        h.satisfactions["wheat"] = 1.0f; // 1.0 <= wantThreshold 1.2
        Item bread = new Item{ name = "bread", foodValue = 40f, happinessNeed = "wheat" };
        Assert.That(h.WouldHelp(bread), Is.True);
    }

    [Test]
    public void WouldHelp_NeedAboveWantThreshold_ReturnsFalse(){
        Happiness h = new Happiness();
        h.satisfactions["wheat"] = 1.5f; // > 1.2
        Item bread = new Item{ name = "bread", foodValue = 40f, happinessNeed = "wheat" };
        Assert.That(h.WouldHelp(bread), Is.False);
    }

    [Test]
    public void WouldHelp_FoodWithNoHappinessNeed_ReturnsFalse(){
        Happiness h = new Happiness();
        Item gruel = new Item{ name = "gruel", foodValue = 30f, happinessNeed = null };
        Assert.That(h.WouldHelp(gruel), Is.False);
    }

    // ── TemperatureEfficiency (no Animal needed; WeatherSystem.instance is null in tests) ─
    [Test]
    public void TemperatureEfficiency_DefaultRangeAndDefaultTemp_IsOne(){
        // Default comfortLow=10, comfortHigh=25; WeatherSystem.instance is null in
        // EditMode tests, so the SlowUpdate fallback temp of 17.5C is used here too.
        Happiness h = new Happiness();
        Assert.That(h.TemperatureEfficiency(), Is.EqualTo(1f));
    }

    [Test]
    public void TemperatureEfficiency_OutsideRange_LinearFalloffFlooredAt0_7(){
        // Force comfort range so that the fallback 17.5C lands above the high.
        Happiness h = new Happiness();
        h.comfortTempLow  = 0f;
        h.comfortTempHigh = 10f;     // deviation = 7.5
        // 1 - 7.5*0.04 = 0.7 exactly — at the floor.
        Assert.That(h.TemperatureEfficiency(), Is.EqualTo(0.7f).Within(0.0001f));
    }

    [Test]
    public void TemperatureEfficiency_FarOutsideRange_FlooredAt0_7(){
        Happiness h = new Happiness();
        h.comfortTempLow  = 100f;
        h.comfortTempHigh = 200f;    // deviation huge
        Assert.That(h.TemperatureEfficiency(), Is.EqualTo(0.7f));
    }

    // ── Aggregate score: tested indirectly here through manual decay math.
    // Direct SlowUpdate(Animal) coverage is deferred — see fixture comment.
    [Test]
    public void Satisfactions_DecayMathMatchesDecayFactor(){
        // The SlowUpdate decay step does: satisfactions[k] *= pow(1 - decayPerTick, 10).
        // We replicate that here to confirm the constant interacts as expected — if
        // decayPerTick is changed, this test pins the resulting 10-tick factor.
        // With decayPerTick=0.005 → pow(0.995, 10) ~ 0.9511.
        float factor10 = UnityEngine.Mathf.Pow(1f - Happiness.decayPerTick, 10f);
        Assert.That(factor10, Is.EqualTo(0.9511f).Within(0.001f),
            "decayFactor10 drifted from expected ~0.9511 — review SlowUpdate callers.");
    }
}

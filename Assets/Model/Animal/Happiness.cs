using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class Happiness {
    public bool house;
    public float score;

    public const float satisfiedThreshold = 1.0f;
    public const float wantThreshold = 1.2f;
    public const float satisfactionCap = 5.0f;
    public const float decayPerTick = 0.005f; // proportion of current amount that decays per tick
    public const float activityGrant = 2.0f;

    // Precomputed decay factor for 10 ticks (one SlowUpdate): pow(0.99, 10) ~ 0.9044
    private static readonly float decayFactor10 = Mathf.Pow(1f - decayPerTick, 10f);

    // All satisfaction values keyed by need name (e.g. "wheat", "fruit", "fountain", "social").
    // Initialized from Db.happinessNeeds at construction — all keys start at 0.
    public Dictionary<string, float> satisfactions;

    // Warmth: decaying cold tolerance buff from sitting by a fireplace.
    // Widens comfortTempLow by up to warmth * 1C (max 5C at cap).
    // Decays ~2 days: 0.94^48 ~ 0.046 (24 SlowUpdates/day x 2 days).
    public float warmth = 0f;
    private const float warmthDecayFactor10 = 0.94f;

    // Comfortable temperature range (C). Updated by UpdateComfortRange().
    public float comfortTempLow  = 10f;
    public float comfortTempHigh = 25f;
    public float temperatureScore;  // cached for display

    public Happiness() {
        satisfactions = new Dictionary<string, float>();
        if (Db.happinessNeeds != null) {
            foreach (string need in Db.happinessNeeds)
                satisfactions[need] = 0f;
        }
    }

    // ── Grant / query helpers ────────────────────────────────────────

    private void Grant(string need, float amount) {
        if (string.IsNullOrEmpty(need)) return;
        satisfactions.TryGetValue(need, out float cur);
        satisfactions[need] = Mathf.Min(satisfactionCap, cur + amount);
    }

    public float GetSatisfaction(string need) {
        satisfactions.TryGetValue(need, out float val);
        return val;
    }

    // ── Event callbacks ──────────────────────────────────────────────

    public void NoteAte(Item food, float fraction = 1f) {
        if (food.happinessNeed == null) return;
        Grant(food.happinessNeed, activityGrant * fraction);
    }

    // Called when a nearby decoration building is spotted.
    public void NoteSawDecoration(string need) {
        Grant(need, activityGrant);
    }

    public void NoteSocialized() {
        Grant("social", activityGrant);
    }

    // Granted when a mouse finishes leisuring at a fireplace (or other leisure building).
    // Special side-effects (warmth) are handled here; the satisfaction grant is generic.
    public void NoteLeisure(string need) {
        Grant(need, activityGrant);
        if (need == "fireplace") {
            warmth = Mathf.Min(satisfactionCap, warmth + activityGrant);
        }
    }

    // Returns the current satisfaction value for a named leisure need.
    // Used by TryPickLeisure to rank options by lowest satisfaction.
    public float GetLeisureSatisfaction(string need) {
        return GetSatisfaction(need);
    }

    // True if eating this food would satisfy a currently-wanting category
    public bool WouldHelp(Item food) {
        if (food.happinessNeed == null) return false;
        return GetSatisfaction(food.happinessNeed) <= wantThreshold;
    }

    // Adjusts comfort temperature range based on equipped clothing and warmth buff.
    // Called from Animal.SlowUpdate() before SlowUpdate(). Currently hardcoded
    // to +/-3C for any clothing item; can be made data-driven later.
    // Warmth buff widens cold tolerance by up to 5C (1C per warmth point).
    public void UpdateComfortRange(Animal a) {
        bool hasClothing = a.clothingSlotInv.itemStacks[0].item != null;
        float clothingBonus = hasClothing ? 3f : 0f;
        float warmthBonus = warmth * 1f; // up to 5C at max warmth
        comfortTempLow  = 10f - clothingBonus - warmthBonus;
        comfortTempHigh = 25f + clothingBonus;
    }

    public void SlowUpdate(Animal a) {
        // Exponential decay: each SlowUpdate (10 ticks) multiplies by ~0.9044
        var keys = new List<string>(satisfactions.Keys);
        foreach (string key in keys)
            satisfactions[key] *= decayFactor10;

        // Warmth decays much slower (~2 days from full to negligible)
        warmth *= warmthDecayFactor10;

        house = a.HasHouse;

        // Temperature comfort: +2 in range, smoothly decreasing outside (2 - deviation/5).
        float temp = WeatherSystem.instance?.temperature ?? 17.5f;
        if (temp >= comfortTempLow && temp <= comfortTempHigh) {
            temperatureScore = 2f;
        } else {
            float deviation = temp < comfortTempLow ? comfortTempLow - temp : temp - comfortTempHigh;
            temperatureScore = 2f - deviation / 5f;
        }

        // Score: +1 per satisfied need + housing + temperature
        score = (house ? 1f : 0f) + temperatureScore;
        foreach (var kv in satisfactions) {
            if (kv.Value >= satisfiedThreshold) score += 1f;
        }
    }

    // Efficiency multiplier from temperature comfort. 1.0 when comfortable,
    // linear falloff outside range, floored at 0.7 (never slower than 70%).
    public float TemperatureEfficiency() {
        float temp = WeatherSystem.instance?.temperature ?? 17.5f;
        if (temp >= comfortTempLow && temp <= comfortTempHigh) return 1f;
        float deviation = temp < comfortTempLow ? comfortTempLow - temp : temp - comfortTempHigh;
        return Mathf.Max(0.7f, 1f - deviation * 0.04f);
    }

    public override string ToString() {
        var sb = new StringBuilder();
        foreach (string need in Db.happinessNeedsSorted) {
            float val = GetSatisfaction(need);
            bool sat = val >= satisfiedThreshold;
            sb.Append($"{need}: {(sat?1:0)} ({val:0.0}), ");
        }
        sb.Append($"housing: {(house?1:0)}/1, ");
        sb.Append($"warmth: {warmth:0.0}, ");
        sb.Append($"temp: {temperatureScore:0.0}/2  ({score:0.0})");
        return sb.ToString();
    }
}

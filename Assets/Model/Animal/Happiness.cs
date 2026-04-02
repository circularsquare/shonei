using UnityEngine;

public class Happiness {
    public bool house;
    public float score;

    public const float satisfiedThreshold = 1.0f;
    public const float wantThreshold = 1.2f;
    public const float satisfactionCap = 5.0f;
    public const float decayPerTick = 0.005f; // proportion of current amount that decays per tick
    public const float activityGrant = 2.0f;

    // Precomputed decay factor for 10 ticks (one SlowUpdate): pow(0.99, 10) ≈ 0.9044
    private static readonly float decayFactor10 = Mathf.Pow(1f - decayPerTick, 10f);

    public float satWheat = 0f;
    public float satFruit = 0f;
    public float satSoymilk = 0f;
    public float satFountain = 0f;
    public float satSocial = 0f;
    public float satFireplace = 0f;

    // Warmth: decaying cold tolerance buff from sitting by a fireplace.
    // Widens comfortTempLow by up to warmth * 1°C (max 5°C at cap).
    // Decays ~2 days: 0.94^48 ≈ 0.046 (24 SlowUpdates/day × 2 days).
    public float warmth = 0f;
    private const float warmthDecayFactor10 = 0.94f;

    // Comfortable temperature range (°C). Updated by UpdateComfortRange().
    public float comfortTempLow  = 10f;
    public float comfortTempHigh = 25f;
    public float temperatureScore;  // cached for display

    public Happiness(){}

    public void NoteAte(Item food, float fraction = 1f) {
        if      (food.name == "wheat")   satWheat   = Mathf.Min(satisfactionCap, satWheat   + activityGrant * fraction);
        else if (food.name == "apple")   satFruit   = Mathf.Min(satisfactionCap, satFruit   + activityGrant * fraction);
        else if (food.name == "soymilk" || food.name == "tofu") satSoymilk = Mathf.Min(satisfactionCap, satSoymilk + activityGrant * fraction);
        // add more mappings here as new foods are added
    }

    // Called when a nearby decoration building is spotted. Dispatches to the correct field by name.
    // Add more mappings here as new decoration types are introduced.
    public void NoteSawDecoration(string decorType) {
        if (decorType == "fountain") satFountain = Mathf.Min(satisfactionCap, satFountain + activityGrant);
        // else if (decorType == "garden") satGarden = Mathf.Min(satisfactionCap, satGarden + activityGrant);
    }

    public void NoteSocialized() {
        satSocial = Mathf.Min(satisfactionCap, satSocial + activityGrant);
    }

    // Granted when a mouse finishes leisuring at a fireplace.
    // Boosts both the happiness satisfaction and the warmth cold-tolerance buff.
    public void NoteWarmedByFire() {
        satFireplace = Mathf.Min(satisfactionCap, satFireplace + activityGrant);
        warmth = Mathf.Min(satisfactionCap, warmth + activityGrant);
    }

    // Returns the current satisfaction value for a named leisure need.
    // Used by TryPickLeisure to rank options by lowest satisfaction.
    public float GetLeisureSatisfaction(string need) {
        if (need == "social")    return satSocial;
        if (need == "fireplace") return satFireplace;
        Debug.LogError($"Happiness.GetLeisureSatisfaction: unknown need '{need}'");
        return 0f;
    }

    // Grants the satisfaction bonus for a named leisure need.
    // Dispatches to the specific Note* method so special side effects (e.g. warmth) are preserved.
    public void NoteLeisure(string need) {
        if (need == "fireplace") { NoteWarmedByFire(); return; }
        // Future leisure buildings: add cases here.
        Debug.LogError($"Happiness.NoteLeisure: unknown building need '{need}'");
    }

    // True if eating this food would satisfy a currently-wanting category
    public bool WouldHelp(Item food) {
        if (food.name == "wheat")   return satWheat   <= wantThreshold;
        if (food.name == "apple")   return satFruit   <= wantThreshold;
        if (food.name == "soymilk" || food.name == "tofu") return satSoymilk <= wantThreshold;
        return false;
    }

    // Adjusts comfort temperature range based on equipped clothing and warmth buff.
    // Called from Animal.SlowUpdate() before SlowUpdate(). Currently hardcoded
    // to +/-3°C for any clothing item; can be made data-driven later.
    // Warmth buff widens cold tolerance by up to 5°C (1°C per warmth point).
    public void UpdateComfortRange(Animal a) {
        bool hasClothing = a.clothingSlotInv.itemStacks[0].item != null;
        float clothingBonus = hasClothing ? 3f : 0f;
        float warmthBonus = warmth * 1f; // up to 5°C at max warmth
        comfortTempLow  = 10f - clothingBonus - warmthBonus;
        comfortTempHigh = 25f + clothingBonus;
    }

    public void SlowUpdate(Animal a) {
        // Exponential decay: each SlowUpdate (10 ticks) multiplies by ~0.9044
        satWheat    *= decayFactor10;
        satFruit    *= decayFactor10;
        satSoymilk  *= decayFactor10;
        satFountain *= decayFactor10;
        satSocial   *= decayFactor10;
        satFireplace *= decayFactor10;

        // Warmth decays much slower (~2 days from full to negligible)
        warmth *= warmthDecayFactor10;

        bool wheat      = satWheat    >= satisfiedThreshold;
        bool fruit      = satFruit    >= satisfiedThreshold;
        bool soymilk    = satSoymilk  >= satisfiedThreshold;
        bool fountain   = satFountain >= satisfiedThreshold;
        bool socialized = satSocial   >= satisfiedThreshold;
        bool fireplace  = satFireplace >= satisfiedThreshold;
        house = a.HasHouse;

        // Temperature comfort: +2 if in range, else -1 per 5°C outside range.
        float temp = WeatherSystem.instance?.temperature ?? 17.5f;
        if (temp >= comfortTempLow && temp <= comfortTempHigh) {
            temperatureScore = 2f;
        } else {
            float deviation = temp < comfortTempLow ? comfortTempLow - temp : temp - comfortTempHigh;
            temperatureScore = -deviation / 5f;
        }

        score = (wheat ? 1f : 0f) + (fruit ? 1f : 0f) + (soymilk ? 1f : 0f) + (house ? 1f : 0f) + (fountain ? 1f : 0f) + (socialized ? 1f : 0f) + (fireplace ? 1f : 0f) + temperatureScore;
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
        bool wheat      = satWheat    >= satisfiedThreshold;
        bool fruit      = satFruit    >= satisfiedThreshold;
        bool soymilk    = satSoymilk  >= satisfiedThreshold;
        bool fountain   = satFountain >= satisfiedThreshold;
        bool socialized = satSocial   >= satisfiedThreshold;
        bool fireplace  = satFireplace >= satisfiedThreshold;
        return $"wheat: {(wheat?1:0)} ({satWheat:0.0}), fruit: {(fruit?1:0)} ({satFruit:0.0}), soy: {(soymilk?1:0)} ({satSoymilk:0.0}), housing: {(house?1:0)}/1, fountain: {(fountain?1:0)} ({satFountain:0.0}), social: {(socialized?1:0)} ({satSocial:0.0}), fireplace: {(fireplace?1:0)} ({satFireplace:0.0}), warmth: {warmth:0.0}, temp: {temperatureScore:0.0}/2  ({score:0.0})";
    }
}

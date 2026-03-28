using UnityEngine;

public class Happiness {
    public bool house;
    public float score;

    public const float recentThreshold = 120f;
    public const float soonThreshold = 30f;
    public const float maxTime = recentThreshold * 1.5f;
    public float timeSinceAteWheat   = maxTime;
    public float timeSinceAteFruit   = maxTime;
    public float timeSinceAteSoymilk = maxTime;
    public float timeSinceSawFountain = maxTime;

    // Comfortable temperature range (°C). Updated by UpdateClothingBonus().
    public float comfortTempLow  = 10f;
    public float comfortTempHigh = 25f;
    public float temperatureScore;  // cached for display

    public Happiness(){}

    public void NoteAte(Item food, float fraction = 1f) {
        if      (food.name == "wheat")   timeSinceAteWheat   = Mathf.Max(0f, timeSinceAteWheat   - fraction * recentThreshold);
        else if (food.name == "apple")   timeSinceAteFruit   = Mathf.Max(0f, timeSinceAteFruit   - fraction * recentThreshold);
        else if (food.name == "soymilk" || food.name == "tofu") timeSinceAteSoymilk = Mathf.Max(0f, timeSinceAteSoymilk - fraction * recentThreshold);
        // add more mappings here as new foods are added
    }

    // Called when a nearby decoration building is spotted. Dispatches to the correct timer by name.
    // Add more mappings here as new decoration types are introduced.
    public void NoteSawDecoration(string decorType) {
        if (decorType == "fountain") timeSinceSawFountain = 0f;
        // else if (decorType == "garden") timeSinceSawGarden = 0f;
    }

    // True if eating this food would satisfy a currently-unhappy category
    public bool WouldHelp(Item food) {
        if (food.name == "wheat")   return timeSinceAteWheat   >= recentThreshold - soonThreshold;
        if (food.name == "apple")   return timeSinceAteFruit   >= recentThreshold - soonThreshold;
        if (food.name == "soymilk" || food.name == "tofu") return timeSinceAteSoymilk >= recentThreshold - soonThreshold;
        return false;
    }

    // Adjusts comfort temperature range based on equipped clothing.
    // Called from Animal.SlowUpdate() before SlowUpdate(). Currently hardcoded
    // to +/-3°C for any clothing item; can be made data-driven later.
    public void UpdateClothingBonus(Animal a) {
        bool hasClothing = a.clothingSlotInv.itemStacks[0].item != null;
        float bonus = hasClothing ? 3f : 0f;
        comfortTempLow  = 10f - bonus;
        comfortTempHigh = 25f + bonus;
    }

    public void SlowUpdate(Animal a){
        timeSinceAteWheat    = Mathf.Min(timeSinceAteWheat    + 10f, maxTime);
        timeSinceAteFruit    = Mathf.Min(timeSinceAteFruit    + 10f, maxTime);
        timeSinceAteSoymilk  = Mathf.Min(timeSinceAteSoymilk  + 10f, maxTime);
        timeSinceSawFountain = Mathf.Min(timeSinceSawFountain + 10f, maxTime);
        bool wheat    = timeSinceAteWheat    < recentThreshold;
        bool fruit    = timeSinceAteFruit    < recentThreshold;
        bool soymilk  = timeSinceAteSoymilk  < recentThreshold;
        bool fountain = timeSinceSawFountain < recentThreshold;
        house = a.HasHouse;

        // Temperature comfort: +2 if in range, else -1 per 5°C outside range.
        float temp = WeatherSystem.instance?.temperature ?? 17.5f;
        if (temp >= comfortTempLow && temp <= comfortTempHigh) {
            temperatureScore = 2f;
        } else {
            float deviation = temp < comfortTempLow ? comfortTempLow - temp : temp - comfortTempHigh;
            temperatureScore = -deviation / 5f;
        }

        score = (wheat ? 1f : 0f) + (fruit ? 1f : 0f) + (soymilk ? 1f : 0f) + (house ? 1f : 0f) + (fountain ? 1f : 0f) + temperatureScore;
    }

    // Efficiency multiplier from temperature comfort. 1.0 when comfortable,
    // linear falloff outside range, floored at 0.7 (never slower than 70%).
    public float TemperatureEfficiency() {
        float temp = WeatherSystem.instance?.temperature ?? 17.5f;
        if (temp >= comfortTempLow && temp <= comfortTempHigh) return 1f;
        float deviation = temp < comfortTempLow ? comfortTempLow - temp : temp - comfortTempHigh;
        return Mathf.Max(0.7f, 1f - deviation * 0.04f);
    }

    public override string ToString(){
        bool wheat    = timeSinceAteWheat    < recentThreshold;
        bool fruit    = timeSinceAteFruit    < recentThreshold;
        bool soymilk  = timeSinceAteSoymilk  < recentThreshold;
        bool fountain = timeSinceSawFountain < recentThreshold;
        return $"wheat: {(wheat?1:0)}/1, fruit: {(fruit?1:0)}/1, soy: {(soymilk?1:0)}/1, housing: {(house?1:0)}/1, fountain: {(fountain?1:0)}/1, temp: {temperatureScore:0.0}/2  ({score:0.0})";
    }
}

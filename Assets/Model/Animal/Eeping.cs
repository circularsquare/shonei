// Per-animal sleep state. Tracks current tiredness ("eep") and the work-efficiency
// penalty when exhausted. Eep() restores it (faster at home, slower outside);
// Update() depletes it each tick.
public class Eeping {
    public float maxEep = 100f;
    public float eep = 90f;
    // In steady state a mouse sleeps tireRate/eepRate of the day — the tireRate ticked during
    // sleep cancels out, so the fraction is purely the ratio. Home: 0.2/1 = 20%. Outside:
    // 0.2/0.7 ~ 29%. Raising tireRate also lowers the energy a mouse reaches before bed (it
    // depletes faster across the day), so it's the knob that makes mice actually get tired.
    public static float tireRate = 0.2f;
    public static float eepRate = 1f;          // home recovery
    public static float outsideEepRate = 0.7f; // slower when caught out

    // Sleep thresholds. exhaustedSleepThreshold is the daytime baseline — mice this tired
    // sleep regardless of time. At bedtime, BedtimeUrgency (0→1 across the bedtime window)
    // scales bedtimeMaxBoost on top: a mouse sleeps when
    //   eep/maxEep < exhaustedSleepThreshold + bedtimeUrgency * bedtimeMaxBoost
    // The deep-night ceiling is exhaustedSleepThreshold + bedtimeMaxBoost = 0.9, so
    // fully-rested mice (e ≥ 0.9) never sleep even at the dead of night. Effect: low-energy
    // mice peel off to bed earlier and higher-energy mice stay up later, so a houseful
    // doesn't all rush home on the same tick, but no one wastes a productive night in bed.
    // nightSleepThreshold is retained as a constant for EstimateDailyWorkFraction's
    // analytical estimate (which expects a single sleep-cutoff value).
    public const float nightSleepThreshold = 0.85f;
    public const float exhaustedSleepThreshold = 0.4f;
    public const float bedtimeMaxBoost = 0.5f;

    public Eeping(){}
    public bool ShouldSleep(float bedtimeUrgency){
        float e = eep / maxEep;
        return e < exhaustedSleepThreshold + bedtimeUrgency * bedtimeMaxBoost;
    }

    // Smooth 0..1 urgency to sleep, for the unified ChooseTask picker (see
    // plans/urgency-system.md). Same trigger boundary as ShouldSleep — 0 at/above the
    // (bedtime-shifted) threshold — but linear pull below it: the more exhausted past the
    // threshold, the stronger the draw to bed. ShouldSleep is retained for binary callers.
    public float SleepUrgency(float bedtimeUrgency){
        float e = eep / maxEep;
        float threshold = exhaustedSleepThreshold + bedtimeUrgency * bedtimeMaxBoost;
        if (e >= threshold) return 0f;
        return (threshold - e) / threshold;
    }
    public float Efficiency(){
        if (eep / maxEep > 0.5f){
            return 1f;
        } else {
            return eep / maxEep * 2f * 0.8f + 0.2f; // 20% at worst.
        }
    }
    public float Eepness(){ return eep / maxEep; }
    public void Eep(float t, bool atHome){
        eep += t * (atHome ? eepRate : outsideEepRate);
        // Clamp at the cap: recovery is ticked wall-clock (Animal.HandleNeeds) while the
        // wake-up check is energy-gated (AnimalStateManager.HandleEeping). For a low-efficiency
        // sleeper the recovery can outrun the wake check by a few ticks — without this clamp
        // eep would drift above maxEep in that window.
        if (eep > maxEep){ eep = maxEep; }
    }
    public void Update(float t = 1f){
        eep -= tireRate * t;
        if (eep < 0f){eep = 0f;}
    }

}

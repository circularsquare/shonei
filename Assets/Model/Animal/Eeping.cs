// Per-animal sleep state. Tracks current tiredness ("eep") and the work-efficiency
// penalty when exhausted. Eep() restores it (faster at home, slower outside);
// Update() depletes it each tick.
public class Eeping {
    public float maxEep = 100f;
    public float eep = 90f;
    // In steady state a mouse sleeps tireRate/eepRate of the day — the tireRate ticked during
    // sleep cancels out, so the fraction is purely the ratio. Home: 0.1/0.5 = 20%. Outside:
    // 0.1/0.35 ~ 29%. Raising tireRate also lowers the energy a mouse reaches before bed (it
    // depletes faster across the day), so it's the knob that makes mice actually get tired.
    public static float tireRate = 0.1f;
    public static float eepRate = 0.5f;          // home recovery
    public static float outsideEepRate = 0.35f; // slower when caught out

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
    // Wake-up thresholds. A sleeping mouse used to wake only at 100% eep, ignoring the clock,
    // so a late-to-bed mouse slept all the way to full and into the morning ("sleeping in").
    // Now, once rested past wakeConsiderEepness, it rolls each sleep-tick to wake — see WakeChance.
    public const float wakeConsiderEepness = 0.8f; // below this, never wake early (still genuinely tired)
    public const float wakeStickiness      = 0.3f; // bias to keep sleeping past the floor, so mice don't
                                                    // all pop awake the instant they cross 80%

    // Per-sleep-tick probability of waking. 0 below the wake floor; above it, ramps with how rested
    // the mouse is, reduced by a stickiness bias AND by any remaining time-of-day sleep pull
    // (SleepUrgency). Net effect: deep sleep through the night (high threshold keeps them down to
    // ~0.95), but a morning wake-up around ~0.86–0.95 instead of grinding to 100%.
    public float WakeChance(float bedtimeUrgency){
        float e = eep / maxEep;
        if (e >= 1f) return 1f;
        if (e < wakeConsiderEepness) return 0f;
        float rested = (e - wakeConsiderEepness) / (1f - wakeConsiderEepness); // 0 at floor → 1 at full
        float keepSleeping = wakeStickiness + SleepUrgency(bedtimeUrgency);
        return UnityEngine.Mathf.Clamp01(rested - keepSleeping);
    }
    public float Efficiency(){
        if (eep / maxEep > 0.5f){
            return 1f;
        } else {
            return eep / maxEep * 2f * 0.5f + 0.5f; // 50% at worst.
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

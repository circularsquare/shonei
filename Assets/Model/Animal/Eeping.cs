// Per-animal sleep state. Tracks current tiredness ("eep") and the work-efficiency
// penalty when exhausted. Eep() restores it (faster at home, slower outside);
// Update() depletes it each tick.
public class Eeping {
    public float maxEep = 100f;
    public float eep = 90f;
    public static float tireRate = 0.1f;
    public static float eepRate = 2f;
    public static float outsideEepRate = 1f;

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
    public float Efficiency(){
        if (eep / maxEep > 0.5f){
            return 1f;
        } else {
            return eep / maxEep * 2f * 0.8f + 0.2f; // 20% at worst.
        }
    }
    public float Eepness(){ return eep / maxEep; }
    public void Eep(float t, bool atHome){
        if (atHome){ eep += t * eepRate; }
        else { eep += t * outsideEepRate; }
    }
    public void Update(float t = 1f){
        eep -= tireRate * t;
        if (eep < 0f){eep = 0f;}
    }

}

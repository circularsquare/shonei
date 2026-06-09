// Per-animal hunger state. Tracks current food, rate of depletion, and the
// work-efficiency penalty when hungry. Eat() restores food; Update() depletes it.
// A mouse held at zero food accumulates starvingTicks; once that reaches a full
// in-game day (World.ticksInDay), StarvedToDeath() trips and AnimalController
// removes the mouse.
public class Eating {
    public float maxFood = 100f;
    public float food = 90f;
    public float hungerRate = 0.2f;
    public float timeSinceLastAte = 9999f;

    // Consecutive ticks spent at food == 0. Incremented by Update() while empty,
    // zeroed by Eat() or any tick with food left. Fatal once it hits a full day.
    public int starvingTicks = 0;

public Eating(){ }

    public const float hungryThreshold   = 0.5f; // below this fullness, work efficiency starts to drop
    public const float seekFoodThreshold = 0.6f; // below this fullness, mice actively seek food
                                                 // (decoupled from hungryThreshold so we can have mice
                                                 // top up before they actually start losing efficiency)

    // Food-selection tuning (see Animal.FindFood). Score = foodValue * cravingMult * 1/(1 + dist*urgency),
    // urgency = (1 - fullness) / starvingHalfDistance.
    public const float starvingHalfDistance = 3.0f; // tiles of travel cost that halve food's appeal at fullness=0.
                                                    // Smaller = a starving mouse less willing to walk for distant food.
    public const float cravingMultiplier    = 2.0f; // appeal bonus for foods that satisfy an unhappy food category.
                                                    // ~"willing to walk 2x farther for craved food at equal hunger".

    public float Fullness(){ return food / maxFood; }
    public bool Hungry(){ return Fullness() < seekFoodThreshold; }
    public bool AteRecently(){ return timeSinceLastAte < 300f; } // within 5 min

    public float Efficiency(){
        if (Fullness() > hungryThreshold){
            return 1f;
        } else {
            return Fullness() * 2f * 0.6f + 0.4f; // 40% at worst.
        }
    }

    // Smooth 0..1 urgency to seek food, for the unified ChooseTask picker (see
    // plans/urgency-system.md). Replaces the hard Hungry() cliff. Two regimes:
    //   • seekFoodThreshold (0.6) → HungerDominateThreshold (0.3): a CONCAVE curve (exponent < 1) so
    //     urgency rises fast as soon as the mouse is hungry — a mouse is already clearly seeking
    //     food by ~0.5 fullness (≈0.41), not loitering until near-empty.
    //   • below HungerDominateThreshold (0.3): ramp from the dominate floor up to 1.0 at empty. The
    //     floor sits ABOVE the realistic work ceiling (p1 order at distance 0 ≈ 0.70), so once a
    //     mouse is genuinely low it is essentially impossible for work or leisure to out-score eating.
    // Tuning constants live in UrgencyConfig. Hungry() is retained for any binary callers.
    public float HungerUrgency(){
        float f = Fullness();
        if (f >= seekFoodThreshold) return 0f;
        float dominate = UrgencyConfig.HungerDominateThreshold;
        float floor = UrgencyConfig.HungerDominateFloor;
        if (f < dominate) {
            // Linear ramp floor → 1.0 as fullness goes dominate → 0.
            float d = (dominate - f) / dominate; // 0 at threshold → 1 at empty
            return floor + (1f - floor) * d;
        }
        // Concave curve across [dominate, seekFoodThreshold), ending at the floor.
        float t = (seekFoodThreshold - f) / (seekFoodThreshold - dominate); // 0 at seek → 1 at dominate
        return UnityEngine.Mathf.Pow(t, UrgencyConfig.HungerConcavity) * floor;
    }
    public void Eat(float nFood){
        food = UnityEngine.Mathf.Min(food + nFood, maxFood);
        timeSinceLastAte = 0f;
        starvingTicks = 0; // any meal clears the starvation countdown
    }
    public void SlowUpdate(float t = 10f){
        timeSinceLastAte += t;
    }
    public void Update(float t = 1f){
        food -= hungerRate * t;
        if (food <= 0f){
            food = 0f;
            starvingTicks++; // counts ticks at empty — a full day of these is fatal
        } else {
            starvingTicks = 0;
        }
    }

    // True once the mouse has spent a full in-game day at zero food. Checked each
    // tick by Animal.TickUpdate, which flags the mouse for removal when it trips.
    public bool StarvedToDeath(){ return starvingTicks >= World.ticksInDay; }
}

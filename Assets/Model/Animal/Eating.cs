// Per-animal hunger state. Tracks current food, rate of depletion, and the
// work-efficiency penalty when hungry. Eat() restores food; Update() depletes it.
public class Eating {
    public float maxFood = 100f;
    public float food = 90f;
    public float hungerRate = 0.4f;
    public float timeSinceLastAte = 9999f;

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
            return Fullness() * 2f * 0.8f + 0.2f; // 20% at worst.
        }
    }
    public void Eat(float nFood){
        food = UnityEngine.Mathf.Min(food + nFood, maxFood);
        timeSinceLastAte = 0f;
    }
    public void SlowUpdate(float t = 10f){
        timeSinceLastAte += t;
    }
    public void Update(float t = 1f){
        food -= hungerRate * t;
        if (food < 0f){food = 0f;}
    }
}

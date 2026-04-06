public class Eating {
    public float maxFood = 100f;
    public float food = 90f;
    public float hungerRate = 0.4f;
    public float timeSinceLastAte = 9999f;

public Eating(){ }

    public const float hungryThreshold = 0.5f; // below this fullness, efficiency starts to drop

    public float Fullness(){ return food / maxFood; }
    public bool Hungry(){ return Fullness() < hungryThreshold; }
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

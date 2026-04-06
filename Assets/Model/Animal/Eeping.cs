public class Eeping {
    public float maxEep = 100f;
    public float eep = 90f;
    public static float tireRate = 0.1f;
    public static float eepRate = 2f;
    public static float outsideEepRate = 1f;

    public Eeping(){}
    public bool Eepy(){
        return eep / maxEep < 0.7f;
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

using UnityEngine;
using UnityEngine.UI;

// Drives a full-screen multiply-blend overlay to produce a day/night cycle.
// Phase is derived directly from World.timer:
//   phase = (timer % ticksInDay) / ticksInDay
//   0.0        = midnight
//   0.2-0.25   = dawn  (night → dusk color)
//   0.25-0.3   = dawn  (dusk color → day)
//   0.3-0.7    = day   (no tint)
//   0.7-0.75   = dusk  (day → dusk color)
//   0.75-0.8   = dusk  (dusk color → night)
//   0.8-1.0    = night
//
// Scene setup: assign overlayImage in the Inspector.
// Canvas must use Screen Space - Camera (not Overlay) so the multiply blend
// composites against the actual game pixels rendered by the camera.
public class DayNightController : MonoBehaviour {
    public static DayNightController instance { get; private set; }

    [SerializeField] RawImage overlayImage;

    static readonly Color DayColor   = Color.white;
    static readonly Color DuskColor  = new Color(0.98f, 0.80f, 0.7f, 1f);
    static readonly Color NightColor = new Color(0.50f, 0.50f, 0.70f, 1f);

    Material overlayMaterial;

    void Awake(){
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    void Start(){
        if (overlayImage != null)
            overlayMaterial = overlayImage.material; // auto-instanced by Unity
    }

    void Update(){
        if (overlayMaterial == null) return;
        if (World.instance == null) return;

        float phase = GetDayPhase();
        Color tint;

        if (phase < 0.2f){
            tint = NightColor;
        } else if (phase < 0.25f){
            tint = Color.Lerp(NightColor, DuskColor, (phase - 0.2f) / 0.05f);
        } else if (phase < 0.3f){
            tint = Color.Lerp(DuskColor, DayColor, (phase - 0.25f) / 0.05f);
        } else if (phase < 0.7f){
            tint = DayColor;
        } else if (phase < 0.75f){
            tint = Color.Lerp(DayColor, DuskColor, (phase - 0.7f) / 0.05f);
        } else if (phase < 0.8f){
            tint = Color.Lerp(DuskColor, NightColor, (phase - 0.75f) / 0.05f);
        } else {
            tint = NightColor;
        }

        overlayMaterial.color = tint;
    }

    // Returns current day phase [0,1] where 0=midnight, 0.5=noon.
    public static float GetDayPhase(){
        if (World.instance == null) return 0f;
        return World.instance.timer % Db.ticksInDay / Db.ticksInDay;
    }

    // Returns true during night hours (phase < 0.2 or phase >= 0.8).
    public static bool IsNight(){
        float phase = GetDayPhase();
        return phase < 0.2f || phase >= 0.8f;
    }
}

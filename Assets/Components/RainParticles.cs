using UnityEngine;

// Rain. Falls fast, bluish, splashes on impact.
//
// All the camera-tracking, viewport-fill, lifetime, and density logic lives in
// PrecipitationParticles. This subclass adds the rain-specific pieces:
//
//  • Reads WeatherSystem.rainAmount (gated on temperature ≥ snowThresholdC).
//  • Emits a splash burst from a sibling ParticleSystem on each impact.
//  • Tracks splash alpha to the same maxAlpha × intensity curve as the rain
//    drops, so splashes fade together with the storm.
public class RainParticles : PrecipitationParticles {
    [SerializeField] ParticleSystem splashPS;
    [SerializeField] int            splashCountPerDrop  = 3;
    [SerializeField] float          splashMinSpeed      = 0f;
    [SerializeField] float          splashMaxSpeed      = 0f;
    [SerializeField] float          splashSpreadDegrees = 70f;

    ParticleSystem.EmitParams splashEmit;

    protected override float GetIntensity() => WeatherSystem.instance?.rainAmount ?? 0f;

    protected override void Update() {
        base.Update();

        // Splash alpha tracks rain alpha so the splashes fade with the storm.
        if (splashPS != null) {
            var   splashMain = splashPS.main;
            Color sc         = splashMain.startColor.color;
            sc.a             = maxAlpha * intensity;
            splashMain.startColor = sc;
        }
    }

    protected override void OnParticleHit(Vector3 pos, Tile tile, float impactY) {
        if (splashPS == null) return;

        Vector3 splashPos = new Vector3(pos.x, impactY, pos.z);
        for (int j = 0; j < Random.Range(1, splashCountPerDrop + 1); j++) {
            float angle = Random.Range(-splashSpreadDegrees, splashSpreadDegrees) * Mathf.Deg2Rad;
            float speed = Random.Range(splashMinSpeed, splashMaxSpeed);
            splashEmit.position = splashPos;
            splashEmit.velocity = new(Mathf.Sin(angle) * speed, Mathf.Cos(angle) * speed, 0f);
            splashPS.Emit(splashEmit, 1);
        }
    }
}

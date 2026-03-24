using UnityEngine;

// Drives rain particle emission, opacity, and tile-based splash collisions.
//
// Collision works by reading live particle positions each frame and checking
// them against World tile data — no physics colliders needed.
// When a raindrop lands on a standable tile it is killed and a splash burst
// is emitted from a sibling ParticleSystem (splashPS).
[RequireComponent(typeof(ParticleSystem))]
public class RainParticles : MonoBehaviour {
    [SerializeField] float         maxEmissionRate          = 200f;
    [SerializeField] float         maxAlpha                 = 0.7f;
    [SerializeField] float         emitterHeightAboveCamera = 12f;
    [SerializeField] float         windSpeedScale           = 3f;   // world-units/s per unit of wind
    [SerializeField] ParticleSystem splashPS;
    [SerializeField] int            splashCountPerDrop      = 3;
    [SerializeField] float          splashMinSpeed          = 0f;
    [SerializeField] float          splashMaxSpeed          = 0f;
    [SerializeField] float          splashSpreadDegrees     = 70f;

    ParticleSystem                 ps;
    Camera                         mainCam;
    ParticleSystem.Particle[]      particles;             // reused each frame — no GC alloc
    ParticleSystem.EmitParams      splashEmit;

    void Awake() {
        ps        = GetComponent<ParticleSystem>();
        mainCam   = Camera.main;
        particles = new ParticleSystem.Particle[ps.main.maxParticles];
    }

    void Update() {
        // Keep emitter centred on camera so rain always covers the viewport.
        if (mainCam != null) {
            Vector3 pos = mainCam.transform.position;
            pos.y += emitterHeightAboveCamera;
            pos.z  = transform.position.z;
            transform.position = pos;
        }

        float amount = WeatherSystem.instance?.rainAmount ?? 0f;
        float wind   = WeatherSystem.instance?.wind      ?? 0f;

        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.x = wind * windSpeedScale;

        var emission = ps.emission;
        emission.rateOverTime = maxEmissionRate * amount;

        var main = ps.main;
        Color c = main.startColor.color;
        c.a = maxAlpha * amount;
        main.startColor = c;

        if (splashPS != null) {
            var splashMain = splashPS.main;
            Color sc = splashMain.startColor.color;
            sc.a = maxAlpha * amount;
            splashMain.startColor = sc;
        }

        CheckCollisions();
    }

    // Reads all live particles, kills any that have entered a solid tile or crossed
    // a water surface, and emits a splash burst at each impact point.
    void CheckCollisions() {
        if (splashPS == null || World.instance == null) return;

        int count    = ps.GetParticles(particles);
        bool anyKilled = false;

        for (int i = 0; i < count; i++) {
            Vector3 pos  = particles[i].position;
            Tile    tile = World.instance.GetTileAt(pos.x, pos.y);
            if (tile == null) continue;

            float splashY;
            if (tile.type.solid) {
                splashY = tile.y + 0.5f;
            } else if (tile.water > 0 && pos.y <= tile.y - 0.5f + tile.water / (float)WaterController.WaterMax) {
                // Raindrop has crossed the water surface; splash at the surface height.
                // tile.y is the tile centre, so the tile bottom is tile.y - 0.5.
                splashY = tile.y - 0.5f + tile.water / (float)WaterController.WaterMax;
            } else {
                continue;
            }

            Vector3 splashPos = new Vector3(pos.x, splashY, pos.z);
            for (int j = 0; j < Random.Range(1, splashCountPerDrop + 1); j++) {
                float angle = Random.Range(-splashSpreadDegrees, splashSpreadDegrees) * Mathf.Deg2Rad;
                float speed = Random.Range(splashMinSpeed, splashMaxSpeed);
                splashEmit.position = splashPos;
                splashEmit.velocity = new(Mathf.Sin(angle) * speed, Mathf.Cos(angle) * speed, 0f);
                splashPS.Emit(splashEmit, 1);
            }

            particles[i].remainingLifetime = -1f; // kill the raindrop
            anyKilled = true;
        }

        if (anyKilled) ps.SetParticles(particles, count);
    }
}

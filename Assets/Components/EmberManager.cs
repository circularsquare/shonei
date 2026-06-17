using System.Collections.Generic;
using UnityEngine;

// Rising fire embers — sparse glowing sparks that drift up from lit fires
// (torches, fireplaces) at night. Pure ambience; never touches the sim.
//
// One global manager drives a single pooled ParticleSystem — no per-fire
// GameObject, no per-spawn GC — analogous to the rain system (RainParticles).
// Each frame it walks LightSource.emitters (the same lit-fire registry the
// lighting pass uses for emission) and, for every fire whose
// StructType.emberRate > 0 and whose flame is currently glowing
// (CurrentEmissionScale above GlowThreshold), emits embers at the flame's wick,
// scaled by the glow and the player's particle-density setting.
//
// Rendering: this GameObject lives on the Unlit layer, drawn by the
// OverlayCamera as additive sparks on top of the fully-lit scene. Pure glow —
// it skips NormalsCapture entirely (no ghost-silhouette risk) and never feeds
// the lightmap. Because emission is gated on CurrentEmissionScale, which is 0
// by day and ramps up through twilight, embers only show when fires are lit.
//
// Pixel-perfect: don't manually snap particle positions. The PixelPerfectCamera
// already rasterizes the additive sparks to native pixels, so they stay crisp.
// Rounding each particle's position and writing it back via SetParticles would
// corrupt the sim — at riseSpeed ~0.23px/frame the rounded base never accumulates
// past 0.5px, freezing the rise (looked like it randomly turned on/off).
//
// Determinism: spawn timing and jitter use a private System.Random, never
// UnityEngine.Random — embers must not perturb the sim's shared random stream.
[RequireComponent(typeof(ParticleSystem))]
public class EmberManager : MonoBehaviour {
    public static EmberManager instance;

    [Header("Motion (world units, seconds)")]
    [SerializeField] float emberLifetime  = 1.3f;   // how long a spark lives
    [SerializeField] float riseSpeed      = 0.85f;  // base upward speed
    [SerializeField] float riseJitter     = 0.35f;  // ± on riseSpeed per spark
    [SerializeField] float driftSpeed     = 0.08f;  // ± random horizontal wander per spark
    [SerializeField] float windDriftScale = 0.2f;   // horizontal world-units/s per unit of wind — keep
                                                     // well below riseSpeed so gusts only lean the sparks,
                                                     // never blow them flat (wind re-rolls every game-hour ≈ 5s)
    [SerializeField] float spawnXSpread   = 0.07f;  // ± horizontal spawn spread around the flame centre
    [SerializeField] float spawnYSpread   = 0.06f;  // ± vertical spawn spread around the flame centre

    [Header("Look")]
    [SerializeField] Color emberColor     = new Color(0.88f, 0.38f, 0.09f, 1f);
    [SerializeField] float startSize      = 0.12f;  // ~2px at 16 PPU
    [SerializeField] float endSizeFactor  = 0.2f;   // shrinks to this fraction by death

    [Header("Budget")]
    [SerializeField] float globalRateScale = 1f;    // master multiplier on every emberRate
    [SerializeField] int   maxParticles    = 400;

    ParticleSystem            ps;
    ParticleSystem.EmitParams emit;
    System.Random             rng;

    // Per-emitter fractional-spawn accumulator → even spacing (no Poisson gaps).
    // Keyed by the fire's LightSource; pruned when it stops glowing and on Clear().
    readonly Dictionary<LightSource, float> spawnAccum = new Dictionary<LightSource, float>();

    // Matches LightSource's own fire-visibility gate (fireGO active above this).
    const float GlowThreshold = 0.05f;

    void Awake() {
        instance = this;
        ps  = GetComponent<ParticleSystem>();
        rng = new System.Random();

        // World space so sparks keep rising independent of camera pan / this GO.
        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.loop            = true;     // never-ending ambient system — don't auto-stop after duration
        main.maxParticles    = maxParticles;
        main.startLifetime   = emberLifetime;
        main.startSpeed      = 0f;        // velocity is set per-emit
        main.startSize       = startSize;
        main.gravityModifier = 0f;
        main.playOnAwake     = true;

        // We emit by hand from Update — no shape, no timed emission.
        var emission = ps.emission; emission.enabled = false;
        var shape    = ps.shape;    shape.enabled    = false;

        // Fade in fast, hold, fade out; colour stays the per-emit ember tint.
        var col = ps.colorOverLifetime;
        col.enabled = true;
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 0.18f),
                new GradientAlphaKey(1f, 0.55f),
                new GradientAlphaKey(0f, 1f),
            });
        col.color = g;

        // Shrink toward a point so the spark winks out rather than vanishing whole.
        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, endSizeFactor));

        emit = new ParticleSystem.EmitParams();

        // Emission is manual, but the system must be playing for emitted
        // particles to simulate (rise + age out).
        ps.Play();
    }

    void OnDestroy() { if (instance == this) instance = null; }

    // Flush live embers on world reload — mirrors PrecipitationParticles.ClearAll.
    // Called from WorldController.ClearWorld.
    public void Clear() { if (ps != null) ps.Clear(); spawnAccum.Clear(); }

    void Update() {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        float densityMul = SettingsManager.instance != null ? SettingsManager.instance.particleDensity : 1f;
        if (densityMul <= 0f) return;   // particles disabled in settings

        float wind = WeatherSystem.instance != null ? WeatherSystem.instance.wind : 0f;

        foreach (LightSource ls in LightSource.emitters) {
            if (ls == null || ls.building == null) continue;
            StructType st = ls.building.structType;
            if (st.emberRate <= 0f) continue;

            float glow = ls.CurrentEmissionScale;
            SpriteRenderer rr = ls.EmissionReceiver;
            if (glow <= GlowThreshold || rr == null || !rr.gameObject.activeInHierarchy) {
                spawnAccum.Remove(ls);   // not lit → forget its phase so it restarts clean
                continue;
            }

            // Even spacing via a per-emitter accumulator (deterministic, ~1/rate apart)
            // rather than independent per-frame trials — a Poisson process clusters and
            // leaves conspicuous gaps, this keeps a steady trickle. Same cost (one float
            // add + compare per fire), and the per-spark position/velocity jitter still
            // makes the stream look organic despite the regular timing.
            float a;
            spawnAccum.TryGetValue(ls, out a);
            a += st.emberRate * glow * densityMul * globalRateScale * dt;
            int n = (int)a;
            spawnAccum[ls] = a - n;
            if (n <= 0) continue;

            // Spawn at the flame's painted centre. emberOffsetX mirrors with the fire
            // sprite's flipX so side torches' sparks track the flame to whichever wall.
            float offX = rr.flipX ? -st.emberOffsetX : st.emberOffsetX;
            Vector3 center = rr.transform.position + new Vector3(offX, st.emberOffsetY, 0f);
            float xSpread = spawnXSpread * st.emberSpreadMult;
            for (int i = 0; i < n; i++) EmitEmber(center, wind, xSpread);
        }
    }

    void EmitEmber(Vector3 center, float wind, float xSpread) {
        emit.position      = new Vector3(center.x + Rand(xSpread), center.y + Rand(spawnYSpread), center.z);
        // Rise, plus a wind-biased horizontal drift and a little random wander.
        float vx = wind * windDriftScale + Rand(driftSpeed);
        emit.velocity      = new Vector3(vx, riseSpeed + Rand(riseJitter), 0f);
        emit.startColor    = emberColor;
        emit.startLifetime = emberLifetime;
        emit.startSize     = startSize;
        ps.Emit(emit, 1);
    }

    // Uniform in [-mag, +mag] from the private stream.
    float Rand(float mag) => (float)(rng.NextDouble() * 2.0 - 1.0) * mag;
}

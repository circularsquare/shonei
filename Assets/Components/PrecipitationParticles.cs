using UnityEngine;

// Abstract base for falling-precipitation particle effects (rain, snow, …).
//
// Owns the bits that don't care which kind of precipitation is falling:
//
//  • Sizing & lifetime adapt to camera zoom — emitter sits just above the
//    visible top edge, the box-shape width matches the viewport width, and
//    particle lifetime is just long enough for drops to traverse the visible
//    vertical span. Density (drops per second per world-unit of viewport
//    width) stays constant as you zoom.
//
//  • Viewport-fill bursts on fast pan / zoom-out. Computed as
//    (newRect ∖ oldRect) and decomposed into up to four non-overlapping
//    strips so the periphery isn't visibly empty. Density of the burst
//    matches the steady-state on-screen drop count.
//
//  • Tile-based collision read directly from World tile data — no physics
//    colliders. Particles that enter a solid tile or cross a water surface
//    are killed. The subclass's OnParticleHit() runs first so it can fire
//    splashes, leave snow marks, etc. before the kill.
//
// Subclasses provide:
//
//  • GetIntensity()   — read the right WeatherSystem channel (rainAmount /
//                       snowAmount). 0 silences emission and skips collisions.
//  • OnParticleHit()  — optional hook for impact side-effects (rain splashes;
//                       snow no-ops today, accumulation could go here later).
//
// The fallSpeed serialized field must mirror the prefab's
// velocityOverLifetime.y magnitude — particle lifetime is computed from it.
[RequireComponent(typeof(ParticleSystem))]
public abstract class PrecipitationParticles : MonoBehaviour {
    [SerializeField] protected float densityPerUnitWidth = 4f;     // drops/s per world-unit of viewport width at full intensity
    [SerializeField] protected float maxAlpha            = 0.7f;
    [SerializeField] protected float topMargin           = 1f;     // emitter Y above visible top edge (world units)
    [SerializeField] protected float sideMargin          = 1f;     // emitter X overhang past visible left/right edges; absorbs wind drift
    [SerializeField] protected float fallSpeed           = 7f;     // fall speed (world units/s); script drives velocityOverLifetime.y, so the prefab value is overwritten each frame
    [SerializeField] protected float horizontalJitter    = 0f;     // ± random sideways deviation around the wind-driven base X velocity (world units/s); 0 = uniform sideways motion, useful (e.g. 0.5 for snow) so flakes don't move as a single sheet
    [SerializeField] protected float windSpeedScale      = 3f;     // world-units/s per unit of wind
    [SerializeField] protected float panFillThreshold    = 0.002f; // pan delta as fraction of viewport width below which natural emission keeps up
    [SerializeField] protected int   maxParticlesCap     = 6000;   // bumped above prefab default to cover wide viewports at zoom-out

    protected ParticleSystem            ps;
    protected Camera                    mainCam;
    protected ParticleSystem.Particle[] particles;            // reused each frame — no GC alloc
    protected ParticleSystem.EmitParams fillEmit;             // for viewport-fill bursts
    protected float                     intensity;            // last frame's GetIntensity result; subclasses may read in their own Update overrides

    Vector2 prevMin, prevMax;                                 // last frame's visible-rect corners (world units)
    bool    hasPrevRect;

    // Subclass hook: returns 0..1 intensity from the appropriate WeatherSystem
    // channel. 0 → no emission, no fill, no collisions.
    protected abstract float GetIntensity();

    // Subclass hook: a particle just hit `tile` at world position `pos`, with
    // `impactY` being the splash-origin Y (top of solid tile, or water surface).
    // The particle is killed immediately after this returns. Default no-op.
    protected virtual void OnParticleHit(Vector3 pos, Tile tile, float impactY) {}

    protected virtual void Awake() {
        ps      = GetComponent<ParticleSystem>();
        mainCam = Camera.main;

        var main = ps.main;
        if (main.maxParticles < maxParticlesCap) main.maxParticles = maxParticlesCap;
        particles = new ParticleSystem.Particle[main.maxParticles];
    }

    protected virtual void Update() {
        if (mainCam == null) return;

        float   ortho  = mainCam.orthographicSize;
        float   halfH  = ortho;
        float   halfW  = ortho * mainCam.aspect;
        Vector3 camPos = mainCam.transform.position;

        intensity   = GetIntensity();
        float wind  = WeatherSystem.instance?.wind ?? 0f;
        float baseX = wind * windSpeedScale;

        float fallDistance = 2f * halfH + topMargin;
        float lifetime     = fallDistance / fallSpeed;

        // Wind slants particles sideways at baseX, so over their lifetime they
        // drift horizontally by windDrift = baseX * lifetime. With a fixed
        // viewport-width spawn box, that drift leaves a triangular gap on the
        // upwind side of the bottom of the viewport — particles from the upwind
        // spawn edge drift downwind before reaching the floor, and nothing
        // backfills behind them. Extend the spawn box upwind by |windDrift| so
        // the slanted band still covers the full viewport, and bump emission
        // rate proportionally so on-screen density doesn't thin with wind.
        float windDrift    = baseX * lifetime;
        float windDriftAbs = Mathf.Abs(windDrift);

        // ── Track emitter to viewport ───────────────────────────────────────
        Vector3 pos = camPos;
        pos.y += halfH + topMargin;
        pos.z  = transform.position.z;
        transform.position = pos;

        var shape = ps.shape;
        Vector3 sc = shape.scale;
        sc.x = 2f * halfW + 2f * sideMargin + windDriftAbs;
        shape.scale = sc;
        Vector3 sp = shape.position;
        sp.x = -0.5f * windDrift;                  // baseX>0 (wind right) → shift center left so extension covers the upwind edge
        shape.position = sp;

        var vel = ps.velocityOverLifetime;
        vel.enabled = true;

        // Unity requires all three axes of velocityOverLifetime to share a
        // CurveMode — mixing Constant and TwoConstants raises a runtime
        // error. So when horizontalJitter > 0 we promote *all three* axes
        // to TwoConstants (y/z collapsed to a single value) and otherwise
        // keep all three as plain Constant.
        var velX = vel.x;
        var velY = vel.y;
        var velZ = vel.z;
        if (horizontalJitter > 0f) {
            velX.mode = velY.mode = velZ.mode = ParticleSystemCurveMode.TwoConstants;
            velX.constantMin = baseX - horizontalJitter;
            velX.constantMax = baseX + horizontalJitter;
            velY.constantMin = velY.constantMax = -fallSpeed;
            velZ.constantMin = velZ.constantMax = 0f;
        } else {
            velX.mode = velY.mode = velZ.mode = ParticleSystemCurveMode.Constant;
            velX.constant = baseX;
            velY.constant = -fallSpeed;
            velZ.constant = 0f;
        }
        vel.x = velX;
        vel.y = velY;
        vel.z = velZ;

        var main = ps.main;
        main.startLifetime = lifetime;

        var   emission      = ps.emission;
        float viewportWidth = 2f * halfW;
        float desiredRate   = densityPerUnitWidth * (viewportWidth + windDriftAbs) * intensity;
        float maxSafeRate   = main.maxParticles * 0.85f / Mathf.Max(0.0001f, lifetime);
        float rate          = Mathf.Min(desiredRate, maxSafeRate);
        emission.rateOverTime = rate;

        Color c = main.startColor.color;
        c.a = maxAlpha * intensity;
        main.startColor = c;

        // ── Viewport-fill ───────────────────────────────────────────────────
        Vector2 curMin = new Vector2(camPos.x - halfW, camPos.y - halfH);
        Vector2 curMax = new Vector2(camPos.x + halfW, camPos.y + halfH);
        if (hasPrevRect && intensity > 0f) {
            EmitViewportFill(curMin, curMax, halfW, halfH, lifetime, rate);
        }
        prevMin     = curMin;
        prevMax     = curMax;
        hasPrevRect = true;

        if (intensity > 0f) CheckCollisions();
    }

    // Fills (curRect ∖ prevRect) — handles pans, zoom-outs, and combined moves
    // in one pass; zoom-ins yield empty strips and emit nothing. Strips clamp
    // to the current viewport, so a teleport-sized delta (save-load) fills one
    // viewport's worth instead of an unbounded region.
    void EmitViewportFill(Vector2 curMin, Vector2 curMax, float halfW, float halfH, float lifetime, float rate) {
        float vpW       = 2f * halfW;
        float vpH       = 2f * halfH;
        float threshold = vpW * panFillThreshold;

        bool changed = Mathf.Abs(curMin.x - prevMin.x) > threshold
                    || Mathf.Abs(curMax.x - prevMax.x) > threshold
                    || Mathf.Abs(curMin.y - prevMin.y) > threshold
                    || Mathf.Abs(curMax.y - prevMax.y) > threshold;
        if (!changed) return;

        float dropsPerArea = rate * lifetime / (vpW * vpH);
        if (dropsPerArea <= 0f) return;

        int budget = ps.main.maxParticles - ps.particleCount;
        if (budget <= 0) return;

        // Top — full new X, y in [max(curMin.y, prevMax.y), curMax.y].
        if (curMax.y > prevMax.y) {
            budget -= EmitFillRect(curMin.x, curMax.x,
                                   Mathf.Max(curMin.y, prevMax.y), curMax.y,
                                   dropsPerArea, budget);
            if (budget <= 0) return;
        }
        // Bottom — full new X, y in [curMin.y, min(curMax.y, prevMin.y)].
        if (curMin.y < prevMin.y) {
            budget -= EmitFillRect(curMin.x, curMax.x,
                                   curMin.y, Mathf.Min(curMax.y, prevMin.y),
                                   dropsPerArea, budget);
            if (budget <= 0) return;
        }
        // Side strips use the Y overlap so corners aren't double-filled.
        float overlapY0 = Mathf.Max(curMin.y, prevMin.y);
        float overlapY1 = Mathf.Min(curMax.y, prevMax.y);
        if (overlapY1 <= overlapY0) return;

        if (curMin.x < prevMin.x) {
            budget -= EmitFillRect(curMin.x, Mathf.Min(curMax.x, prevMin.x),
                                   overlapY0, overlapY1,
                                   dropsPerArea, budget);
            if (budget <= 0) return;
        }
        if (curMax.x > prevMax.x) {
            EmitFillRect(Mathf.Max(curMin.x, prevMax.x), curMax.x,
                         overlapY0, overlapY1,
                         dropsPerArea, budget);
        }
    }

    int EmitFillRect(float x0, float x1, float y0, float y1, float dropsPerArea, int budget) {
        float w = x1 - x0;
        float h = y1 - y0;
        if (w <= 0f || h <= 0f) return 0;

        int n = Mathf.Min(budget, Mathf.CeilToInt(dropsPerArea * w * h));
        for (int i = 0; i < n; i++) {
            fillEmit.position = new Vector3(
                Random.Range(x0, x1),
                Random.Range(y0, y1),
                transform.position.z);
            ps.Emit(fillEmit, 1);
        }
        return n;
    }

    // Reads live particles, calls OnParticleHit for any that have hit a solid
    // tile, crossed a water surface, or entered a tile with a solidTop/blocksRain
    // structure (building roof, platform, tarp), then kills them. The structure
    // check mirrors World.IsExposedAbove's blocker set so the visuals agree with
    // the moisture / snow / rain-catch model.
    void CheckCollisions() {
        if (World.instance == null) return;

        int  count     = ps.GetParticles(particles);
        bool anyKilled = false;

        for (int i = 0; i < count; i++) {
            Vector3 pos  = particles[i].position;
            Tile    tile = World.instance.GetTileAt(pos.x, pos.y);
            if (tile == null) continue;

            float impactY;
            if (tile.type.solid) {
                impactY = tile.y + 0.5f;
            } else if (tile.water > 0 && pos.y <= tile.y - 0.5f + tile.water / (float)WaterController.WaterMax) {
                // Particle has crossed the water surface; impact at the surface height.
                // tile.y is the tile centre, so the tile bottom is tile.y - 0.5.
                impactY = tile.y - 0.5f + tile.water / (float)WaterController.WaterMax;
            } else if (HasRainBlocker(tile)) {
                // solidTop / blocksRain structure occupies this tile — splash on its top edge.
                impactY = tile.y + 0.5f;
            } else {
                continue;
            }

            OnParticleHit(pos, tile, impactY);
            particles[i].remainingLifetime = -1f;
            anyKilled = true;
        }

        if (anyKilled) ps.SetParticles(particles, count);
    }

    static bool HasRainBlocker(Tile tile) {
        for (int d = 0; d < tile.structs.Length; d++) {
            Structure s = tile.structs[d];
            if (s != null && (s.structType.solidTop || s.structType.blocksRain)) return true;
        }
        return false;
    }
}

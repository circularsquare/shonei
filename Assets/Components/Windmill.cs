using System.Collections.Generic;
using UnityEngine;

// A passive surface power producer. Output scales with the magnitude of WeatherSystem.wind
// (an Ornstein-Uhlenbeck float in roughly [-1, 1], hourly random walk).
//
// Footprint: 2 wide × 3 tall (anchor bottom-left). Power port: one tile below the
// horizontal centre — so a vertical shaft running underneath the windmill connects.
//
// Placement constraint: the tile directly above the windmill's top must be exposed
// to the sky (no solid ground or solidTop structures above). Uses the existing
// World.IsExposedAbove helper, the same check rain-collection tanks use.
//
// Animation hook (deferred): IsCurrentlyActive + the wind value can drive a spinning
// blade overlay later.
public class Windmill : Building, PowerSystem.IPowerProducer {
    // Maximum power output at full wind (|wind| = 1). Tunable. Sized so a single
    // windmill at decent wind powers ~one or two consumers.
    public const float MaxOutput = 3.0f;

    // Below this wind magnitude the blades stall — output is exactly zero, not just
    // a tiny number that would still mark consumers as "powered".
    public const float StallThreshold = 0.05f;

    public Windmill(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    public override void OnPlaced() {
        base.OnPlaced();
        PowerSystem.instance?.RegisterProducer(this);
    }

    public override void Destroy() {
        PowerSystem.instance?.UnregisterProducer(this);
        base.Destroy();
    }

    // True iff sky access is unobstructed across the windmill's top row. Re-checked
    // on every tick / output query so a roof built after placement immediately kills
    // production. Mirrors the placement-time `mustBeOpenSkyAbove` check on the same tiles.
    bool HasOpenSky() {
        World world = World.instance;
        if (world == null) return false;
        int topY = y + structType.ny - 1;
        for (int dx = 0; dx < structType.nx; dx++)
            if (!world.IsExposedAbove(x + dx, topY)) return false;
        return true;
    }

    // True iff the windmill is currently producing usable power. Drives future blade animation
    // and the InfoPanel display. A broken windmill still spins visually if this is true — the
    // output cutoff happens in CurrentOutput, not here.
    public bool IsCurrentlyActive {
        get {
            if (!HasOpenSky()) return false;
            float w = WeatherSystem.instance?.wind ?? 0f;
            return Mathf.Abs(w) >= StallThreshold;
        }
    }

    // Blade FPS scales with |wind|, so the windmill "lazy-turns" in light breeze and
    // spins up in a gust. baseFps × |wind| ≈ baseFps at full wind, ~0 below stall.
    public override void AttachAnimations() {
        AttachFrameAnimator("windmill",
            () => IsCurrentlyActive,
            baseFps: 6f,
            speedMul: () => Mathf.Abs(WeatherSystem.instance?.wind ?? 0f));
    }

    // ── IPowerProducer ────────────────────────────────────────────────
    public Structure Structure => this;

    public float CurrentOutput {
        get {
            if (IsBroken) return 0f;
            if (!HasOpenSky()) return 0f;
            float w = Mathf.Abs(WeatherSystem.instance?.wind ?? 0f);
            if (w < StallThreshold) return 0f;
            return Mathf.Min(1f, w) * MaxOutput;
        }
    }

    public IEnumerable<PowerSystem.PowerPort> Ports {
        get {
            // Three connection points so the player has flexibility routing shafts:
            //   - Horizontal at the base on the left (dx=-1, dy=0): a shaft running into the
            //     left side of the windmill at ground level.
            //   - Vertical below either base tile (dx=0/1, dy=-1): a shaft dropping straight
            //     down through the foundation.
            // Mirroring flips X offsets via PowerSystem.FindAttachedNetwork, so a mirrored
            // windmill gets its horizontal port on the right side automatically.
            yield return new PowerSystem.PowerPort(-1, 0, PowerSystem.Axis.Horizontal);
            yield return new PowerSystem.PowerPort(0, -1, PowerSystem.Axis.Vertical);
            yield return new PowerSystem.PowerPort(1, -1, PowerSystem.Axis.Vertical);
        }
    }
}

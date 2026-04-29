using System.Collections.Generic;
using UnityEngine;

// A passive surface power producer. Output scales with the magnitude of WeatherSystem.wind
// (an Ornstein-Uhlenbeck float in roughly [-1, 1], hourly random walk).
//
// Footprint: 2 wide × 4 tall (anchor bottom-left). Three connection points — see Ports.
//
// Placement constraint: the tile directly above the windmill's top must be exposed
// to the sky (no solid ground or solidTop structures above). Uses the existing
// World.IsExposedAbove helper, the same check rain-collection tanks use.
//
// Visuals: the static tower is `windmill.png` (port stub pixels stripped — that visual
// is now provided conditionally by PortStubVisuals when a shaft actually connects).
// The blades are a separate child GameObject driven by WindmillBlades, rotating via
// transform.localRotation at a wind-proportional speed. Replaces an earlier 2-frame
// FrameAnimator that snapped between two static blade poses.
public class Windmill : Building, PowerSystem.IPowerProducer {
    // Maximum power output at full wind (|wind| = 1). Tunable. Sized so a single
    // windmill at decent wind powers ~one or two consumers.
    public const float MaxOutput = 3.0f;

    // Below this wind magnitude the blades stall — output is exactly zero, not just
    // a tiny number that would still mark consumers as "powered".
    public const float StallThreshold = 0.05f;

    // Rotation pivot for the wheel child GameObject, in tiles from the windmill anchor's
    // bottom-left CORNER (edge-aligned). 0 = left/bottom edge of the anchor tile; nx = right
    // edge of the rightmost tile. So WheelHubX = 1.0 sits on the boundary between column 0
    // and column 1 — the horizontal centre of a 2-wide windmill. Tweak to match the new
    // windmill.png artwork if the hub doesn't visually sit at this point.
    public const float WheelHubX = 1.0f;
    public const float WheelHubY = 2.375f;

    // Degrees per second the wheel sweeps at full wind magnitude (|wind| = 1). Linear
    // interpolation: actual speed = wind × WheelDegPerSecAtMaxWind, signed so positive wind
    // spins one way and negative wind the other.
    public const float WheelDegPerSecAtMaxWind = 180f;

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

    // Spawns a child GameObject for the wheel (blades) and rotates it via RotatingPart,
    // signed by wind direction. Replaces the previous 2-frame FrameAnimator approach so the
    // motion is smooth and spin direction tracks WeatherSystem.wind's sign. The static
    // `windmill.png` no longer contains the wheel — it lives in `windmill_wheel.png` (square,
    // centred pivot) so rotation doesn't translate the visual.
    public override void AttachAnimations() {
        Sprite wheelSprite = Resources.Load<Sprite>("Sprites/Buildings/windmill_wheel");
        if (wheelSprite != null) {
            GameObject wheelGO = new GameObject("wheel");
            wheelGO.transform.SetParent(go.transform, true);
            // World position of the rotation hub. WheelHubX/Y are edge-aligned (measured
            // from the anchor tile's bottom-left CORNER, which sits at world (x-0.5, y-0.5)
            // since tiles are centred at integer coords). The mirror formula `nx - hub`
            // reflects across the building's horizontal centre, which is also edge-aligned.
            float hubX = mirrored ? (structType.nx - WheelHubX) : WheelHubX;
            wheelGO.transform.position = new Vector3(x - 0.5f + hubX, y - 0.5f + WheelHubY, 0f);

            SpriteRenderer wsr = wheelGO.AddComponent<SpriteRenderer>();
            wsr.sprite = wheelSprite;
            wsr.flipX = mirrored;
            wsr.sortingOrder = (sr != null ? sr.sortingOrder : 10) + 1;
            LightReceiverUtil.SetSortBucket(wsr);

            RotatingPart rot = wheelGO.AddComponent<RotatingPart>();
            // Unsigned magnitude — direction is fixed (clockwise) regardless of wind sign,
            // matching the flywheel and any future rotating power machinery.
            rot.speedSource         = () => Mathf.Abs(WeatherSystem.instance?.wind ?? 0f);
            rot.isActive            = () => IsCurrentlyActive;
            rot.degPerSecAtMaxSpeed = WheelDegPerSecAtMaxWind;
            rot.stallThreshold      = StallThreshold;
            rot.directionSign       = -1f; // negative deg = clockwise in Unity 2D
        } else {
            Debug.LogWarning("windmill_wheel sprite missing at Resources/Sprites/Buildings/windmill_wheel — windmill wheel will not render.");
        }

        AttachPortStubs(Ports);
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
            // Four connection points so the player has flexibility routing shafts:
            //   - Horizontal at the base on the LEFT (dx=-1, dy=0).
            //   - Horizontal at the base on the RIGHT (dx=nx, dy=0).
            //   - Vertical below either base tile (dx=0/1, dy=-1): a shaft dropping straight
            //     down through the foundation.
            // Stubs only render where a shaft is actually attached, so the unused sides stay
            // clean. Mirroring is symmetric across the horizontal pair, so the F flip leaves
            // routing options unchanged.
            yield return new PowerSystem.PowerPort(-1, 0, PowerSystem.Axis.Horizontal);
            yield return new PowerSystem.PowerPort(structType.nx, 0, PowerSystem.Axis.Horizontal);
            yield return new PowerSystem.PowerPort(0, -1, PowerSystem.Axis.Vertical);
            yield return new PowerSystem.PowerPort(1, -1, PowerSystem.Axis.Vertical);
        }
    }
}
